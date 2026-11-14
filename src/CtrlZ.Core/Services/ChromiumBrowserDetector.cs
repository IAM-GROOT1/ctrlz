using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>
/// Finds browsers from known User Data locations and the uninstall registry. A browser is
/// only considered backup-ready when its User Data folder contains a valid Local State.
/// </summary>
public sealed class ChromiumBrowserDetector
{
    private readonly IAppLog _log;

    public ChromiumBrowserDetector(IAppLog log)
    {
        _log = log;
    }

    public Task<IReadOnlyList<DetectedBrowser>> DetectAsync(
        string? customUserDataPath = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Detect(customUserDataPath, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<DetectedBrowser> Detect(string? customUserDataPath, CancellationToken cancellationToken)
    {
        var registry = BrowserRegistryLookup.FindAll();
        var browsers = new List<DetectedBrowser>();
        var usedUserData = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in ChromiumBrowserCatalog.Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var registryInfo = BrowserRegistryLookup.Match(registry, definition);
                var userData = ChromiumBrowserCatalog.ExpandPaths(definition.UserDataCandidates)
                    .FirstOrDefault(path => File.Exists(Path.Combine(path, "Local State")));
                var executable = ChromiumBrowserCatalog.ExpandPaths(definition.ExecutableCandidates)
                    .FirstOrDefault(File.Exists) ?? registryInfo?.ExecutablePath;

                if (userData is null && executable is null && registryInfo is null)
                {
                    continue;
                }

                var profiles = userData is null
                    ? new List<BrowserProfile>()
                    : ChromiumProfileReader.ReadProfiles(userData, definition.FlatProfileLayout).ToList();
                var browser = new DetectedBrowser
                {
                    Definition = definition,
                    DisplayName = definition.DisplayName,
                    UserDataPath = userData,
                    ExecutablePath = executable,
                    Version = GetVersion(executable) ?? registryInfo?.Version,
                    FoundInRegistry = registryInfo is not null,
                    Profiles = profiles
                };
                if (userData is not null)
                {
                    usedUserData.Add(userData);
                }

                browsers.Add(browser);
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Warning, $"Could not inspect {definition.DisplayName}.", ex);
            }
        }

        if (!string.IsNullOrWhiteSpace(customUserDataPath))
        {
            try
            {
                var custom = CreateCustom(customUserDataPath);
                if (usedUserData.Add(custom.UserDataPath!))
                {
                    browsers.Add(custom);
                }
                else
                {
                    _log.Write(LogLevel.Info, "Custom browser path already belongs to a detected browser.");
                }
            }
            catch (BrowserPathValidationException ex)
            {
                _log.Write(LogLevel.Warning, $"Custom browser path rejected: {ex.Message}");
            }
        }

        _log.Write(LogLevel.Info, $"Detected {browsers.Count} Chromium browser(s).");
        return browsers;
    }

    public DetectedBrowser CreateCustom(string userDataPath)
    {
        var normalized = Path.GetFullPath(userDataPath.Trim().Trim('"'));
        if (!ChromiumProfileReader.ValidateUserDataFolder(normalized, out var error))
        {
            throw new BrowserPathValidationException(error);
        }

        var profiles = ChromiumProfileReader.ReadProfiles(normalized).ToList();
        if (profiles.Count == 0)
        {
            throw new BrowserPathValidationException(
                "Local State exists, but no Default/Profile folders were found. The path may be the wrong folder.");
        }

        var hint = ChromiumProfileReader.ReadBrowserDisplayNameHint(normalized);
        var displayName = string.IsNullOrWhiteSpace(hint) || hint.Equals("User Data", StringComparison.OrdinalIgnoreCase)
            ? "Custom Chromium browser"
            : $"{hint} (custom)";
        // A stable id keeps custom-browser archives matched across app restarts.
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(normalized.ToLowerInvariant())))[..10].ToLowerInvariant();
        var definition = ChromiumBrowserCatalog.CreateCustomDefinition("custom-" + hash, displayName);
        return new DetectedBrowser
        {
            Definition = definition,
            DisplayName = displayName,
            UserDataPath = normalized,
            ExecutablePath = null,
            Version = null,
            IsCustom = true,
            Profiles = profiles
        };
    }

    private static string? GetVersion(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
        }
        catch
        {
            return null;
        }
    }
}
