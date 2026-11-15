using System.Net.Http;
using System.Text;
using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Licensing;

/// <summary>
/// Client-side license lifecycle. All failure paths preserve the last good license where
/// possible; network problems are never treated as revocation.
/// </summary>
public sealed class LicenseService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly IAppLog _log;
    private readonly SettingsStore _settingsStore;
    private readonly LicenseStore _store;
    private readonly HttpClient _http;
    private readonly string _publicKeyBase64;
    private readonly bool _ownsHttp;
    private LicenseSnapshot _snapshot;

    public LicenseService(
        AppPaths paths,
        IAppLog log,
        SettingsStore settingsStore,
        LicenseStore store,
        HttpClient? http = null,
        string? publicKeyBase64 = null)
    {
        _paths = paths;
        _log = log;
        _settingsStore = settingsStore;
        _store = store;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttp = http is null;
        _publicKeyBase64 = publicKeyBase64 ?? LicensePublicKey.Base64;
        _snapshot = ComputeSnapshot();
    }

    public LicenseSnapshot Snapshot => _snapshot;
    public event EventHandler? LicenseChanged;

    public void Refresh()
    {
        _snapshot = ComputeSnapshot();
        LicenseChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Refresh();
        var stored = _store.Load();
        if (stored is null || string.IsNullOrWhiteSpace(stored.Token))
        {
            return;
        }

        var server = Settings().LicenseServerUrl;
        if (string.IsNullOrWhiteSpace(server))
        {
            _log.Write(LogLevel.Info, "A license is stored but no license server URL is configured; using offline grace.");
            return;
        }

        await RevalidateQuietlyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LicenseSnapshot> ActivateAsync(
        string licenseKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            throw new LicenseException("Enter a license key.");
        }

        var server = RequireServer();
        var machineId = MachineIdProvider.GetMachineId();
        var request = new
        {
            licenseKey = licenseKey.Trim(),
            machineId,
            product = "ctrlz",
            appVersion = typeof(LicenseService).Assembly.GetName().Version?.ToString(3)
        };

        ActivationResponse response;
        try
        {
            response = await PostAsync<ActivationResponse>(
                server.TrimEnd('/') + "/activate", request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.Write(LogLevel.Warning, "License activation request failed.", ex);
            throw new LicenseException("Could not reach the license server. Check your connection and try again.", ex);
        }

        var verification = LicenseTokenCodec.Verify(response.Token, _publicKeyBase64);
        if (!verification.IsValid || verification.Payload is null)
        {
            _log.Write(LogLevel.Warning, "License activation returned an invalid token: " + verification.Error);
            throw new LicenseException(verification.Error ?? "The license server returned an invalid token.");
        }

        if (!string.Equals(verification.Payload.MachineId, machineId, StringComparison.Ordinal))
        {
            throw new LicenseException("The server returned a license for a different machine.");
        }

        var stored = new StoredLicense
        {
            Token = response.Token,
            LastSuccessfulCheckUtc = DateTimeOffset.UtcNow,
            LastAttemptUtc = DateTimeOffset.UtcNow,
            LastAttemptSucceeded = true,
            ServerUrl = server
        };
        _store.Save(stored);
        Refresh();
        _log.Write(LogLevel.Info, "License activated.");
        return _snapshot;
    }

    public async Task RevalidateQuietlyAsync(CancellationToken cancellationToken = default)
    {
        var stored = _store.Load();
        if (stored is null || string.IsNullOrWhiteSpace(stored.Token))
        {
            return;
        }

        var server = Settings().LicenseServerUrl;
        if (string.IsNullOrWhiteSpace(server))
        {
            return;
        }

        try
        {
            var response = await PostAsync<ActivationResponse>(
                server.TrimEnd('/') + "/revalidate",
                new { token = stored.Token, machineId = MachineIdProvider.GetMachineId() },
                cancellationToken).ConfigureAwait(false);

            var verification = LicenseTokenCodec.Verify(response.Token, _publicKeyBase64);
            if (!verification.IsValid || verification.Payload is null)
            {
                stored.LastAttemptUtc = DateTimeOffset.UtcNow;
                stored.LastAttemptSucceeded = false;
                stored.LastError = verification.Error;
                _store.Save(stored);
                _log.Write(LogLevel.Warning, "License revalidation returned an invalid token; keeping the stored license.");
                return;
            }

            stored.Token = response.Token;
            stored.LastSuccessfulCheckUtc = DateTimeOffset.UtcNow;
            stored.LastAttemptUtc = DateTimeOffset.UtcNow;
            stored.LastAttemptSucceeded = true;
            stored.LastError = null;
            stored.ServerUrl = server;
            _store.Save(stored);
            _log.Write(LogLevel.Info, "License revalidated.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            stored.LastAttemptUtc = DateTimeOffset.UtcNow;
            stored.LastAttemptSucceeded = false;
            stored.LastError = "The license server could not be reached.";
            _store.Save(stored);
            _log.Write(LogLevel.Warning, "License revalidation failed; keeping offline grace.", ex);
        }
        finally
        {
            Refresh();
        }
    }

    public async Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        var stored = _store.Load();
        if (stored is null || string.IsNullOrWhiteSpace(stored.Token))
        {
            throw new LicenseException("There is no activated license to deactivate.");
        }

        var server = RequireServer();
        try
        {
            await PostAsync<ActivationResponse>(
                server.TrimEnd('/') + "/deactivate",
                new { token = stored.Token, machineId = MachineIdProvider.GetMachineId() },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.Write(LogLevel.Warning, "License deactivation request failed.", ex);
            throw new LicenseException("Could not reach the license server. This PC was not deactivated.", ex);
        }

        _store.Clear();
        Refresh();
        _log.Write(LogLevel.Info, "License deactivated on this machine.");
    }

    public void OpenPurchasePage()
    {
        var url = Settings().ProPurchaseUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new LicenseException("No Pro purchase URL is configured.");
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
    }

    private AppSettings Settings() => _settingsStore.Load();

    private string RequireServer()
    {
        var server = Settings().LicenseServerUrl;
        if (string.IsNullOrWhiteSpace(server) ||
            !Uri.TryCreate(server, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new LicenseException("Configure a valid license server URL in Settings.");
        }

        return server;
    }

    private LicenseSnapshot ComputeSnapshot()
    {
        var stored = _store.Load();
        var machineId = MachineIdProvider.GetMachineId();
        if (stored is null || string.IsNullOrWhiteSpace(stored.Token))
        {
            return new LicenseSnapshot
            {
                Status = LicenseStatus.Missing,
                Message = "No Pro license activated on this PC.",
                MachineId = machineId
            };
        }

        var verification = LicenseTokenCodec.Verify(stored.Token, _publicKeyBase64);
        var grace = Math.Max(1, Settings().LicenseGraceDays);
        return LicenseEvaluator.Evaluate(
            verification.Payload,
            machineId,
            DateTimeOffset.UtcNow,
            stored.LastSuccessfulCheckUtc,
            grace,
            verification.Error,
            stored.LastAttemptUtc);
    }

    private async Task<T> PostAsync<T>(string url, object body, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(body, JsonDefaults.Compact);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"License server returned {(int)response.StatusCode}.");
        }

        return JsonSerializer.Deserialize<T>(text, JsonDefaults.Compact)
               ?? throw new JsonException("The license server response was empty.");
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private sealed class ActivationResponse
    {
        public string Token { get; set; } = string.Empty;
    }
}
