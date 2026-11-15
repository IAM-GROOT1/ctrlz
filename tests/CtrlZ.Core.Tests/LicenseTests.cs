using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Licensing;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public class LicenseTests
{
    private const string Machine = "sha256:test-machine";

    private static string Sign(LicenseTokenPayload payload, ECDsa key)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonDefaults.Compact);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256);
        return $"CTRLZ1.{LicenseTokenCodec.Base64UrlEncode(bytes)}.{LicenseTokenCodec.Base64UrlEncode(signature)}";
    }

    private static (ECDsa Key, string PublicKey) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    private static LicenseTokenPayload Payload(
        string machineId = Machine,
        DateTimeOffset? expires = null,
        string licenseId = "license-1") => new()
    {
        LicenseId = licenseId,
        Plan = "pro",
        Features = ["pro.example"],
        MachineId = machineId,
        IssuedUtc = DateTimeOffset.UtcNow.AddDays(-1),
        ExpiresUtc = expires ?? DateTimeOffset.UtcNow.AddDays(365),
        Nonce = Guid.NewGuid().ToString("n")
    };

    [Fact]
    public void Valid_signed_token_is_accepted()
    {
        var (key, publicKey) = NewKey();
        using (key)
        {
            var token = Sign(Payload(), key);
            var result = LicenseTokenCodec.Verify(token, publicKey);

            Assert.True(result.IsValid);
            Assert.Equal("license-1", result.Payload!.LicenseId);
            Assert.Contains("pro.example", result.Payload.Features);
        }
    }

    [Fact]
    public void Tampered_token_is_rejected()
    {
        var (key, publicKey) = NewKey();
        using (key)
        {
            var token = Sign(Payload(), key);
            var parts = token.Split('.');
            var bytes = LicenseTokenCodec.Base64UrlDecode(parts[1]);
            var text = Encoding.UTF8.GetString(bytes).Replace("license-1", "license-2");
            var tampered = $"{parts[0]}.{LicenseTokenCodec.Base64UrlEncode(Encoding.UTF8.GetBytes(text))}.{parts[2]}";

            var result = LicenseTokenCodec.Verify(tampered, publicKey);

            Assert.False(result.IsValid);
            Assert.Contains("signature", result.Error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Wrong_machine_is_rejected()
    {
        var (key, publicKey) = NewKey();
        using (key)
        {
            var token = Sign(Payload(machineId: "sha256:other"), key);
            var verified = LicenseTokenCodec.Verify(token, publicKey);

            var snapshot = LicenseEvaluator.Evaluate(
                verified.Payload, Machine, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 30);

            Assert.Equal(LicenseStatus.MachineMismatch, snapshot.Status);
            Assert.False(snapshot.IsProEnabled);
        }
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var (key, publicKey) = NewKey();
        using (key)
        {
            var token = Sign(Payload(expires: DateTimeOffset.UtcNow.AddDays(-1)), key);
            var verified = LicenseTokenCodec.Verify(token, publicKey);

            var snapshot = LicenseEvaluator.Evaluate(
                verified.Payload, Machine, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 30);

            Assert.Equal(LicenseStatus.Expired, snapshot.Status);
        }
    }

    [Fact]
    public void Offline_grace_allows_use_then_expires()
    {
        var payload = Payload();
        var now = DateTimeOffset.UtcNow;
        var tenDaysAgo = now.AddDays(-10);
        var fortyDaysAgo = now.AddDays(-40);

        var inGrace = LicenseEvaluator.Evaluate(payload, Machine, now, tenDaysAgo, 30);
        var expired = LicenseEvaluator.Evaluate(payload, Machine, now, fortyDaysAgo, 30);

        Assert.Equal(LicenseStatus.OfflineGrace, inGrace.Status);
        Assert.True(inGrace.IsProEnabled);
        Assert.True(inGrace.GraceDaysRemaining > 19);
        Assert.Equal(LicenseStatus.Expired, expired.Status);
        Assert.False(expired.IsProEnabled);
    }

    [Fact]
    public void Clock_rollback_is_flagged_but_not_used_to_extend_grace()
    {
        var now = DateTimeOffset.UtcNow;
        var check = now.AddDays(5);
        var snapshot = LicenseEvaluator.Evaluate(Payload(), Machine, now, check, 30);

        Assert.True(snapshot.ClockWarning);
        Assert.True(snapshot.IsProEnabled);
    }

    [Fact]
    public async Task Activation_stores_token_and_deactivation_clears_it()
    {
        using var temp = new TestDirectory();
        var (key, publicKey) = NewKey();
        using (key)
        {
            var machineId = MachineIdProvider.GetMachineId();
            var token = Sign(new LicenseTokenPayload
                {
                    LicenseId = "activate-1",
                    Plan = "pro",
                    Features = ["pro.example"],
                    MachineId = machineId,
                    IssuedUtc = DateTimeOffset.UtcNow,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(365)
                }, key);

            var handler = new FakeLicenseHandler(token);
            var paths = new AppPaths(temp.PathOf("data"));
            paths.EnsureCreated();
            var settings = new SettingsStore(paths.SettingsFile, TestFactory.Log);
            settings.Save(new AppSettings
            {
                LicenseServerUrl = "https://license.test",
                ProPurchaseUrl = "https://buy.test"
            });
            var service = new LicenseService(
                paths, TestFactory.Log, settings,
                new LicenseStore(paths.LicenseFile, TestFactory.Log),
                new HttpClient(handler), publicKey);

            var active = await service.ActivateAsync("CTRLZ-TEST-0001");
            Assert.True(active.IsProEnabled);
            Assert.Equal("activate-1", active.Payload!.LicenseId);
            Assert.True(File.Exists(paths.LicenseFile));

            await service.DeactivateAsync();
            Assert.Equal(LicenseStatus.Missing, service.Snapshot.Status);
            Assert.False(File.Exists(paths.LicenseFile));
        }
    }

    [Fact]
    public async Task Network_failure_does_not_disable_an_active_license()
    {
        using var temp = new TestDirectory();
        var (key, publicKey) = NewKey();
        using (key)
        {
            var machineId = MachineIdProvider.GetMachineId();
            var token = Sign(new LicenseTokenPayload
                {
                    LicenseId = "offline-1",
                    Plan = "pro",
                    Features = ["pro.example"],
                    MachineId = machineId,
                    IssuedUtc = DateTimeOffset.UtcNow,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(365)
                }, key);
            var handler = new FakeLicenseHandler(token);
            var paths = new AppPaths(temp.PathOf("data"));
            paths.EnsureCreated();
            var settings = new SettingsStore(paths.SettingsFile, TestFactory.Log);
            settings.Save(new AppSettings { LicenseServerUrl = "https://license.test" });
            var service = new LicenseService(
                paths, TestFactory.Log, settings,
                new LicenseStore(paths.LicenseFile, TestFactory.Log),
                new HttpClient(handler), publicKey);

            await service.ActivateAsync("CTRLZ-TEST-0001");
            handler.Fail = true;
            await service.RevalidateQuietlyAsync();

            Assert.True(service.Snapshot.IsProEnabled);
            Assert.Equal("offline-1", service.Snapshot.Payload!.LicenseId);
        }
    }

    private sealed class FakeLicenseHandler : HttpMessageHandler
    {
        private readonly string _token;
        public bool Fail { get; set; }

        public FakeLicenseHandler(string token) => _token = token;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new HttpRequestException("offline");
            }

            var body = request.Content?.ReadAsStringAsync(cancellationToken).Result ?? string.Empty;
            _ = body;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"token\":\"{_token}\"}}", Encoding.UTF8, "application/json")
            });
        }
    }
}
