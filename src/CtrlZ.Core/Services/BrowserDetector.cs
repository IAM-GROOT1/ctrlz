// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>
/// Finds Chromium- and Firefox-family browsers from known data locations and the
/// uninstall registry. A browser is backup-ready only when its profile root validates
/// (Local State for Chromium, profiles.ini or a profile folder for Firefox).
/// </summary>
public sealed class BrowserDetector
{
    private readonly IAppLog _log;

    public BrowserDetector(IAppLog log)
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

        foreach (var definition in BrowserCatalog.Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var registryInfo = BrowserRegistryLookup.Match(registry, definition);
                var userData = ChromiumBrowserCatalog.ExpandPaths(definition.UserDataCandidates)
                    .FirstOrDefault(path => ValidateRoot(path, definition.Family));
                var executable = ChromiumBrowserCatalog.ExpandPaths(definition.ExecutableCandidates)
                    .FirstOrDefault(File.Exists) ?? registryInfo?.ExecutablePath;

                if (userData is null && executable is null && registryInfo is null)
                {
                    continue;
                }

                var profiles = userData is null
                    ? new List<BrowserProfile>()
                    : ReadProfiles(userData, definition).ToList();
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

        _log.Write(LogLevel.Info, $"Detected {browsers.Count} browser(s) across Chromium and Firefox families.");
        return browsers;
    }

    public DetectedBrowser CreateCustom(string userDataPath)
    {
        var normalized = Path.GetFullPath(userDataPath.Trim().Trim('"'));
        // Users often pick the Profiles subfolder rather than its parent. Normalize to the
        // Firefox root so profiles.ini can be read and updated safely.
        var parent = Directory.GetParent(normalized)?.FullName;
        if (parent is not null &&
            Path.GetFileName(normalized).Equals("Profiles", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(parent, "profiles.ini")))
        {
            normalized = parent;
        }

        var hasChromium = File.Exists(Path.Combine(normalized, "Local State"));
        var hasFirefox = FirefoxProfileReader.ValidateProfileRoot(normalized, out _);
        if (!hasChromium && !hasFirefox)
        {
            throw new BrowserPathValidationException(
                "That folder is not a recognized browser profile root. Choose a Chromium User Data folder " +
                "(contains Local State) or a Firefox profile root (contains profiles.ini, or prefs.js for a single profile).");
        }

        var family = hasChromium ? BrowserFamily.Chromium : BrowserFamily.Firefox;
        var profiles = family == BrowserFamily.Chromium
            ? ChromiumProfileReader.ReadProfiles(normalized).ToList()
            : FirefoxProfileReader.ReadProfiles(normalized).ToList();
        if (profiles.Count == 0)
        {
            throw new BrowserPathValidationException(
                "The profile root is valid, but no profile folders were found. The path may be the wrong folder.");
        }

        var hint = family == BrowserFamily.Chromium
            ? ChromiumProfileReader.ReadBrowserDisplayNameHint(normalized)
            : BrowserDisplayNameFromFolder(normalized);
        var displayName = string.IsNullOrWhiteSpace(hint)
            ? "Custom browser"
            : $"{hint} (custom)";
        // A stable id keeps custom-browser archives matched across app restarts.
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{family}:{normalized.ToLowerInvariant()}")))[..10].ToLowerInvariant();
        var definition = ChromiumBrowserCatalog.CreateCustomDefinition(
            "custom-" + hash, displayName, family);
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

    public static bool ValidateRoot(string path, BrowserFamily family)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        return family switch
        {
            BrowserFamily.Firefox => FirefoxProfileReader.ValidateProfileRoot(path, out _),
            _ => File.Exists(Path.Combine(path, "Local State"))
        };
    }

    private static IReadOnlyList<BrowserProfile> ReadProfiles(string root, BrowserAppDefinition definition)
    {
        return definition.Family == BrowserFamily.Firefox
            ? FirefoxProfileReader.ReadProfiles(root, definition.FlatProfileLayout)
            : ChromiumProfileReader.ReadProfiles(root, definition.FlatProfileLayout);
    }

    private static string? BrowserDisplayNameFromFolder(string path)
    {
        try
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name) || name.Equals("User Data", StringComparison.OrdinalIgnoreCase))
            {
                return "Firefox";
            }

            return name;
        }
        catch
        {
            return null;
        }
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
