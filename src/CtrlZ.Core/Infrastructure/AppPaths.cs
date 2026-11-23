// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Infrastructure;

/// <summary>
/// Central path policy. Portable mode is opt-in with a <c>portable.flag</c> file next to
/// CTRL+Z.exe (or CTRLZ_DATA_DIR). Normal installs keep user data under LocalAppData.
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "CTRL+Z";

    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
        Profiles = Path.Combine(Root, "profiles");
        Logs = Path.Combine(Root, "logs");
        State = Path.Combine(Root, "state");
        Downloads = Path.Combine(Root, "downloads");
        Cache = Path.Combine(Root, "cache");
        Apps = Path.Combine(Root, "apps");
        Backups = Path.Combine(Root, "backups");
        SettingsFile = Path.Combine(Root, "settings.json");
    }

    public string Root { get; }
    public string Profiles { get; }
    public string Logs { get; }
    public string State { get; }
    public string Downloads { get; }
    public string Cache { get; }
    public string Apps { get; }
    public string Backups { get; }
    public string SettingsFile { get; }

    public string InstallStateFile => Path.Combine(State, "install-state.json");
    public string CatalogCacheFile => Path.Combine(Cache, "winutil-applications.json");
    public string CatalogMetadataFile => Path.Combine(Cache, "winutil-applications.meta.json");
    public string BrowserBackupStateFile => Path.Combine(State, "browser-backup-state.json");

    public static AppPaths CreateDefault() => CreateFor(Environment.ProcessPath);

    /// <summary>
    /// The data folders of the CTRL+Z at <paramref name="exePath"/>. The update helper runs from a
    /// temp copy, so it asks for the app it is updating rather than for itself.
    /// </summary>
    public static AppPaths CreateFor(string? exePath)
    {
        var overrideRoot = Environment.GetEnvironmentVariable("CTRLZ_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return new AppPaths(overrideRoot);
        }

        var exeFolder = (string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath)) ?? AppContext.BaseDirectory;
        var portableFlag = Path.Combine(exeFolder, "portable.flag");
        if (File.Exists(portableFlag))
        {
            return new AppPaths(Path.Combine(exeFolder, "Data"));
        }

        return new AppPaths(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName));
    }

    public void EnsureCreated()
    {
        foreach (var directory in new[] { Root, Profiles, Logs, State, Downloads, Cache, Apps, Backups })
        {
            Directory.CreateDirectory(directory);
        }
    }

    public string DefaultBackupFolder
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(documents, "CTRL+Z Backups");
        }
    }
}
