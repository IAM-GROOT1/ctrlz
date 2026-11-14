namespace CtrlZ.Core.Services;

/// <summary>
/// Firefox-family backup whitelist. Gecko keeps bookmarks/history in places.sqlite,
/// sessions in sessionstore files, preferences in prefs.js, extensions in extensions/,
/// and extension storage under storage/*/moz-extension*.
/// </summary>
public static class FirefoxDataRules
{
    public static readonly string[] ProfileFiles =
    [
        "prefs.js",
        "user.js",
        "places.sqlite",
        "favicons.sqlite",
        "permissions.sqlite",
        "content-prefs.sqlite",
        "handlers.json",
        "mimeTypes.rdf",
        "search.json.mozlz4",
        "search.json",
        "search.sqlite",
        "sessionstore.jsonlz4",
        "sessionstore.js",
        "extensions.json",
        "extension-preferences.json",
        "extension-settings.json",
        "addons.json",
        "addonStartup.json.lz4",
        "times.json",
        "containers.json"
    ];

    public static readonly string[] ProfileDirectories =
    [
        "sessionstore-backups",
        "bookmarkbackups",
        "extensions",
        "chrome",
        "browser-extension-data"
    ];

    public static readonly string[] ExcludedDirectoryNames =
    [
        "cache2",
        "startupCache",
        "shader-cache",
        "crashes",
        "minidumps",
        "datareporting",
        "saved-telemetry-pings",
        "thumbnails",
        "storage/temporary",
        "offlineCache",
        "jumpListCache",
        "parentlock"
    ];

    /// <summary>Credential and cookie stores that must never be archived for Firefox.</summary>
    public static readonly string[] ForbiddenFileNames =
    [
        "cookies.sqlite",
        "cookies.sqlite-wal",
        "cookies.sqlite-shm",
        "cookies.sqlite-journal",
        "logins.json",
        "logins-backup.json",
        "signons.sqlite",
        "key3.db",
        "key4.db",
        "cert9.db",
        "cert8.db",
        "formhistory.sqlite",
        "downloads.sqlite",
        "pkcs11.txt",
        "sessionstore.bak"
    ];

    public static bool IsForbiddenFile(string name)
    {
        if (ForbiddenFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExcludedDirectory(string name)
    {
        return ExcludedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
               name.Equals("cache", StringComparison.OrdinalIgnoreCase);
    }
}
