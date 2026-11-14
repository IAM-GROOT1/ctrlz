using System.Text.Json;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>
/// Reads profile names from Local State rather than guessing from folder names. Falls back
/// to directory scanning if Local State is missing keys, which happens with some forks.
/// </summary>
public static class ChromiumProfileReader
{
    public static IReadOnlyList<BrowserProfile> ReadProfiles(string userDataPath, bool flatLayout = false)
    {
        if (!Directory.Exists(userDataPath))
        {
            return Array.Empty<BrowserProfile>();
        }

        var localStatePath = Path.Combine(userDataPath, "Local State");
        var names = new Dictionary<string, (string? Name, string? UserName)>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(localStatePath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(localStatePath));
                if (document.RootElement.TryGetProperty("profile", out var profileElement) &&
                    profileElement.TryGetProperty("info_cache", out var infoCache) &&
                    infoCache.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in infoCache.EnumerateObject())
                    {
                        var displayName = GetString(property.Value, "name")
                                          ?? GetString(property.Value, "shortcut_name");
                        var userName = GetString(property.Value, "user_name")
                                       ?? GetString(property.Value, "gaia_name");
                        names[property.Name] = (displayName, userName);
                    }
                }
            }
            catch (JsonException)
            {
                // The caller can still use the directory fallback.
            }
        }

        var directories = Directory.EnumerateDirectories(userDataPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => name!.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
            .Where(name => Directory.Exists(Path.Combine(userDataPath, name!)))
            .ToArray();

        if (directories.Length == 0)
        {
            if (flatLayout || File.Exists(Path.Combine(userDataPath, "Preferences")))
            {
                names.TryGetValue("Default", out var flatName);
                return
                [
                    new BrowserProfile(
                        ".",
                        string.IsNullOrWhiteSpace(flatName.Name) ? "Default profile" : flatName.Name!,
                        flatName.UserName,
                        IsDefault: true,
                        userDataPath)
                ];
            }

            return Array.Empty<BrowserProfile>();
        }

        var profiles = new List<BrowserProfile>();
        foreach (var directory in directories)
        {
            names.TryGetValue(directory!, out var metadata);
            var displayName = metadata.Name;
            if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("Person ", StringComparison.OrdinalIgnoreCase))
            {
                displayName = directory!.Equals("Default", StringComparison.OrdinalIgnoreCase)
                    ? "Default"
                    : directory!;
            }

            profiles.Add(new BrowserProfile(
                directory!,
                displayName!,
                metadata.UserName,
                directory!.Equals("Default", StringComparison.OrdinalIgnoreCase),
                Path.Combine(userDataPath, directory!)));
        }

        return profiles
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => ProfileOrder(p.DirectoryName))
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static bool ValidateUserDataFolder(string? path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Choose a User Data folder.";
            return false;
        }

        if (!Directory.Exists(path))
        {
            error = "That folder does not exist.";
            return false;
        }

        if (!File.Exists(Path.Combine(path, "Local State")))
        {
            error = "That folder does not contain a Local State file. Pick the browser's \"User Data\" folder, not its application folder.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "Local State")));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Local State is not a JSON object. The folder may be corrupted.";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"Local State could not be read: {ex.Message}";
            return false;
        }

        return true;
    }

    public static string? ReadBrowserDisplayNameHint(string userDataPath)
    {
        try
        {
            var parent = Directory.GetParent(userDataPath.TrimEnd(Path.DirectorySeparatorChar));
            return parent?.Name;
        }
        catch
        {
            return null;
        }
    }

    private static int ProfileOrder(string directoryName)
    {
        if (directoryName.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var parts = directoryName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && int.TryParse(parts[^1], out var number) ? number : 1000;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
