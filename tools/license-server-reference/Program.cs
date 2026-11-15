using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// EXAMPLE ONLY. This is not a production license server. The real server and Pro module
// belong in a private repository. Never put real signing keys or customer data in this file.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var signer = DemoSingingKey.LoadOrCreate(app.Environment.ContentRootPath);
var store = new DemoStore();

app.MapGet("/health", () => Results.Ok(new { status = "ok", publicKey = signer.PublicKeyBase64 }));

app.MapPost("/activate", (ActivationRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.LicenseKey) ||
        string.IsNullOrWhiteSpace(request.MachineId) ||
        !store.TryGet(request.LicenseKey, out var license))
    {
        return Results.BadRequest(new { error = "Invalid license key." });
    }

    if (!store.TryActivate(license, request.MachineId))
    {
        return Results.Conflict(new { error = "This license has reached its machine limit." });
    }

    return Results.Ok(new { token = signer.Sign(license, request.MachineId) });
});

app.MapPost("/revalidate", (RevalidateRequest request) =>
{
    if (!signer.TryRead(request.Token, request.MachineId, out var payload) ||
        !store.TryGetByLicenseId(payload.LicenseId, out var license))
    {
        return Results.BadRequest(new { error = "Invalid or revoked license." });
    }

    if (!store.IsActive(license, request.MachineId))
    {
        return Results.Conflict(new { error = "This machine is not active." });
    }

    return Results.Ok(new { token = signer.Sign(license, request.MachineId) });
});

app.MapPost("/deactivate", (DeactivateRequest request) =>
{
    if (!signer.TryRead(request.Token, request.MachineId, out var payload) ||
        !store.TryGetByLicenseId(payload.LicenseId, out var license))
    {
        return Results.BadRequest(new { error = "Invalid license." });
    }

    store.Deactivate(license, request.MachineId);
    return Results.Ok(new { deactivated = true });
});

app.Run("http://127.0.0.1:5080");

internal sealed class DemoStore
{
    private readonly Dictionary<string, DemoLicense> _byKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRLZ-DEMO-0001"] = new("demo-license-1", "pro", ["pro.demo"], 2),
        ["CTRLZ-DEMO-0002"] = new("demo-license-2", "pro", ["pro.demo", "pro.advanced"], 1)
    };

    private readonly Dictionary<string, HashSet<string>> _machines = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string key, out DemoLicense license) => _byKey.TryGetValue(key, out license!);

    public bool TryGetByLicenseId(string id, out DemoLicense license)
    {
        license = _byKey.Values.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))!;
        return license is not null;
    }

    public bool TryActivate(DemoLicense license, string machineId)
    {
        if (!_machines.TryGetValue(license.Id, out var machines))
        {
            machines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _machines[license.Id] = machines;
        }

        if (machines.Contains(machineId))
        {
            return true;
        }

        if (machines.Count >= license.MaxMachines)
        {
            return false;
        }

        machines.Add(machineId);
        return true;
    }

    public bool IsActive(DemoLicense license, string machineId) =>
        _machines.TryGetValue(license.Id, out var machines) && machines.Contains(machineId);

    public void Deactivate(DemoLicense license, string machineId)
    {
        if (_machines.TryGetValue(license.Id, out var machines))
        {
            machines.Remove(machineId);
        }
    }
}

internal sealed record DemoLicense(string Id, string Plan, string[] Features, int MaxMachines);

internal sealed class DemoSingingKey
{
    private readonly ECDsa _key;

    private DemoSingingKey(ECDsa key, string publicKeyBase64)
    {
        _key = key;
        PublicKeyBase64 = publicKeyBase64;
    }

    public string PublicKeyBase64 { get; }

    public static DemoSingingKey LoadOrCreate(string contentRoot)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var env = Environment.GetEnvironmentVariable("CTRLZ_LICENSE_PRIVATE_KEY");
        if (!string.IsNullOrWhiteSpace(env))
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(env), out _);
        }
        else
        {
            var keyFolder = Path.Combine(contentRoot, "keys");
            Directory.CreateDirectory(keyFolder);
            var path = Path.Combine(keyFolder, "demo-private.pem");
            if (File.Exists(path))
            {
                key.ImportFromPem(File.ReadAllText(path));
            }
            else
            {
                File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
                Console.WriteLine("DEMO signing key generated at " + path);
            }
        }

        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        Console.WriteLine("DEMO public key: " + publicKey);
        Console.WriteLine("Replace LicensePublicKey.Base64 in the app to test against this server.");
        return new DemoSingingKey(key, publicKey);
    }

    public string Sign(DemoLicense license, string machineId)
    {
        var payload = new TokenPayload
        {
            LicenseId = license.Id,
            Plan = license.Plan,
            Features = license.Features.ToList(),
            MachineId = machineId,
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(365),
            Nonce = Guid.NewGuid().ToString("n")
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var signature = _key.SignData(bytes, HashAlgorithmName.SHA256);
        return $"CTRLZ1.{Base64Url(bytes)}.{Base64Url(signature)}";
    }

    public bool TryRead(string token, string machineId, out TokenPayload payload)
    {
        payload = new TokenPayload();
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != "CTRLZ1")
        {
            return false;
        }

        try
        {
            var bytes = FromBase64Url(parts[1]);
            var signature = FromBase64Url(parts[2]);
            if (!_key.VerifyData(bytes, signature, HashAlgorithmName.SHA256))
            {
                return false;
            }

            payload = JsonSerializer.Deserialize<TokenPayload>(bytes, JsonOptions) ?? new TokenPayload();
            return payload.MachineId.Equals(machineId, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        return Convert.FromBase64String(normalized);
    }
}

internal sealed class ActivationRequest
{
    public string LicenseKey { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
}

internal sealed class RevalidateRequest
{
    public string Token { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
}

internal sealed class DeactivateRequest
{
    public string Token { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
}

internal sealed class TokenPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string LicenseId { get; set; } = string.Empty;
    public string Plan { get; set; } = "pro";
    public List<string> Features { get; set; } = new();
    public string MachineId { get; set; } = string.Empty;
    public DateTimeOffset IssuedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public string? Nonce { get; set; }
}
