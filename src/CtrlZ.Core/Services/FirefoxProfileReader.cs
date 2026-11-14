using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>
/// Reads Firefox-family profiles from profiles.ini. It also supports single-profile
/// layouts used by Tor Browser/Mullvad-style bundles where the root itself (or a
/// "profile"/"profile.default" child) is the profile.
/// </summary>
public static class FirefoxProfileReader
{
    public static IReadOnlyList<BrowserProfile> ReadProfiles(string profileRoot, bool flatLayoutHint = false)
    {
        if (!Directory.Exists(profileRoot))
        {
            return Array.Empty<BrowserProfile>();
        }

        var profiles = new List<BrowserProfile>();
        var iniPath = Path.Combine(profileRoot, "profiles.ini");
        if (File.Exists(iniPath))
        {
            var ini = FirefoxProfilesIni.Load(profileRoot);
            var installDefault = ini.FindInstallDefault();
            foreach (var section in ini.ProfileSections)
            {
                var path = section.Get("Path");
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var isRelative = section.Get("IsRelative") != "0";
                var resolved = isRelative
                    ? Path.GetFullPath(Path.Combine(profileRoot, path))
                    : Path.GetFullPath(path);
                if (!Directory.Exists(resolved) || !FileUtil.IsUnderDirectory(resolved, profileRoot))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(profileRoot, resolved).Replace('\\', '/');
                var name = section.Get("Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = Path.GetFileName(resolved);
                }

                var isDefault = section.Get("Default") == "1" ||
                                (!string.IsNullOrWhiteSpace(installDefault) &&
                                 FirefoxProfilesIni.NormalizePath(installDefault)
                                     .Equals(FirefoxProfilesIni.NormalizePath(path), StringComparison.OrdinalIgnoreCase));
                profiles.Add(new BrowserProfile(
                    relative,
                    name!,
                    null,
                    isDefault,
                    resolved));
            }
        }

        if (profiles.Count == 0)
        {
            // A profile root itself can be a single Firefox profile (some portable/Tor
            // layouts), or contain exactly one profile-like child.
            if (LooksLikeProfile(profileRoot))
            {
                profiles.Add(new BrowserProfile(
                    ".",
                    DisplayNameFromFolder(profileRoot),
                    null,
                    true,
                    profileRoot));
            }
            else
            {
                foreach (var child in new[] { "profile", "profile.default" })
                {
                    var candidate = Path.Combine(profileRoot, child);
                    if (Directory.Exists(candidate) && LooksLikeProfile(candidate))
                    {
                        // With no profiles.ini this is a single-profile bundle. Treat it as a
                        // flat profile whose actual root is the child folder; restore writes
                        // directly into that folder instead of inventing a Profiles/ layout
                        // the browser would not read.
                        profiles.Add(new BrowserProfile(
                            ".",
                            DisplayNameFromFolder(candidate),
                            null,
                            true,
                            candidate));
                    }
                }

                if (profiles.Count == 0)
                {
                    var profilesFolder = Path.Combine(profileRoot, "Profiles");
                    if (Directory.Exists(profilesFolder))
                    {
                        foreach (var directory in Directory.EnumerateDirectories(profilesFolder))
                        {
                            if (!LooksLikeProfile(directory))
                            {
                                continue;
                            }

                            var relative = Path.GetRelativePath(profileRoot, directory).Replace('\\', '/');
                            profiles.Add(new BrowserProfile(
                                relative,
                                Path.GetFileName(directory),
                                null,
                                profiles.Count == 0,
                                directory));
                        }
                    }
                }
            }
        }

        if (flatLayoutHint && profiles.Count > 1)
        {
            // Hint is advisory; profiles.ini still wins.
        }

        return profiles
            .OrderByDescending(profile => profile.IsDefault)
            .ThenBy(profile => profile.DirectoryName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool ValidateProfileRoot(string? path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Choose a Firefox profile root (the folder containing profiles.ini) or a single profile folder.";
            return false;
        }

        if (!Directory.Exists(path))
        {
            error = "That folder does not exist.";
            return false;
        }

        var profiles = ReadProfiles(path);
        if (profiles.Count == 0)
        {
            error = "No Firefox profiles were found. Choose the folder containing profiles.ini (usually " +
                    @"%APPDATA%\Mozilla\Firefox), or a profile folder containing prefs.js.";
            return false;
        }

        return true;
    }

    public static bool LooksLikeProfile(string directory)
    {
        return File.Exists(Path.Combine(directory, "prefs.js")) ||
               File.Exists(Path.Combine(directory, "places.sqlite")) ||
               File.Exists(Path.Combine(directory, "sessionstore.jsonlz4")) ||
               File.Exists(Path.Combine(directory, "sessionstore-backups"));
    }

    public static string DisplayNameFromFolder(string directory)
    {
        var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
        if (name.Equals("profile", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("profile.", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Profiles", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(directory);
            return parent is null ? "Default" : parent.Name;
        }

        return name;
    }
}
