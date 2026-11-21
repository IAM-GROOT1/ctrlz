// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Safety;

namespace CtrlZ.Core.Tests;

public class BrowserBackupInventoryTests
{
    private readonly BrowserBackupInventory _inventory = new();

    [Fact]
    public void Parses_browser_and_timestamp_from_the_file_name()
    {
        using var directory = new TestDirectory();
        directory.Write("CTRLZ-Chrome-20260601-1230.zip", "x");

        var backups = _inventory.Scan(directory.Root);

        var backup = Assert.Single(backups);
        Assert.Equal("Chrome", backup.BrowserName);
        Assert.Equal(2026, backup.CreatedUtc.Year);
        Assert.Equal(6, backup.CreatedUtc.Month);
        Assert.Equal(1, backup.CreatedUtc.Day);
        Assert.Equal(12, backup.CreatedUtc.Hour);
        Assert.Equal(30, backup.CreatedUtc.Minute);
    }

    [Fact]
    public void Handles_a_duplicate_suffix_and_a_dotted_browser_name()
    {
        using var directory = new TestDirectory();
        directory.Write("CTRLZ-Brave.Beta-20260601-1230-2.zip", "x");

        var backup = Assert.Single(_inventory.Scan(directory.Root));

        Assert.Equal("Brave.Beta", backup.BrowserName);
    }

    [Fact]
    public void Ignores_files_that_are_not_ctrlz_archives()
    {
        using var directory = new TestDirectory();
        directory.Write("holiday-photo.zip", "x");
        directory.Write("CTRLZ-Chrome-20260601-1230.txt", "x");

        Assert.Empty(_inventory.Scan(directory.Root));
    }

    [Fact]
    public void Missing_or_unset_backup_folder_is_empty_not_an_error()
    {
        using var directory = new TestDirectory();

        Assert.Empty(_inventory.Scan(null));
        Assert.Empty(_inventory.Scan(string.Empty));
        Assert.Empty(_inventory.Scan(directory.PathOf("does-not-exist")));
    }

    [Fact]
    public void Keeps_the_newest_archive_per_browser()
    {
        using var directory = new TestDirectory();
        directory.Write("CTRLZ-Chrome-20260101-1000.zip", "old");
        directory.Write("CTRLZ-Chrome-20260601-1000.zip", "new");
        directory.Write("CTRLZ-Firefox-20260301-1000.zip", "ff");

        var newest = BrowserBackupInventory.NewestPerBrowser(_inventory.Scan(directory.Root));

        Assert.Equal(2, newest.Count);
        var chrome = newest.Single(b => b.BrowserName == "Chrome");
        Assert.Equal(6, chrome.CreatedUtc.Month);
    }

    [Fact]
    public void Falls_back_to_the_file_timestamp_when_the_stamp_does_not_parse()
    {
        using var directory = new TestDirectory();
        var path = directory.Write("CTRLZ-Chrome-backup.zip", "x");
        var written = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        var backup = Assert.Single(_inventory.Scan(directory.Root));

        Assert.Equal("Chrome-backup", backup.BrowserName);
        Assert.Equal(written, backup.CreatedUtc.UtcDateTime);
    }

    // ---- backup age logic ---------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(13.9, 13)]
    [InlineData(14, 14)]
    public void Age_is_whole_days_and_never_negative(double ageDays, int expected)
    {
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var created = now.AddDays(-ageDays);

        Assert.Equal(expected, BrowserBackupInventory.AgeInDays(created, now));
        Assert.Equal(0, BrowserBackupInventory.AgeInDays(now.AddDays(3), now));
    }

    [Theory]
    [InlineData(13, 14, false)]   // younger than the threshold
    [InlineData(14, 14, true)]    // exactly at the threshold counts as stale
    [InlineData(30, 14, true)]
    [InlineData(365, 0, false)]   // zero disables the warning
    [InlineData(365, -1, false)]
    public void Staleness_follows_the_configured_threshold(int ageDays, int threshold, bool expected)
    {
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, BrowserBackupInventory.IsStale(now.AddDays(-ageDays), now, threshold));
    }
}

public class DeveloperItemsProbeTests
{
    [Fact]
    public void Reports_ssh_git_env_files_wsl_and_packages_by_presence_only()
    {
        using var directory = new TestDirectory();
        var home = directory.CreateDirectory("home");
        var appData = directory.CreateDirectory("appdata");
        directory.Write("home/.ssh/id_alpha", "PRIVATE");
        directory.Write("home/.ssh/known_hosts", "host");
        directory.Write("home/.gitconfig", "[user]");
        directory.Write("home/Projects/app/.env", "SECRET=1");
        directory.Write("home/Projects/app/prod.env", "SECRET=2");
        directory.Write("appdata/npm/node_modules/.keep", "x");

        var env = TestEnvironment(home, appData);
        var items = new DeveloperItemsProbe(() => 2).Probe(env);

        Assert.True(items.Single(i => i.Id == "ssh").Present);
        Assert.Equal(2, items.Single(i => i.Id == "ssh").Count);
        Assert.True(items.Single(i => i.Id == "gitconfig").Present);
        Assert.Equal(2, items.Single(i => i.Id == "env-files").Count);
        Assert.Equal(2, items.Single(i => i.Id == "wsl").Count);
        Assert.True(items.Single(i => i.Id == "packages").Present);
    }

    [Fact]
    public void Never_reports_the_names_of_private_files()
    {
        using var directory = new TestDirectory();
        var home = directory.CreateDirectory("home");
        directory.Write("home/.ssh/id_rsa_secret_project", "PRIVATE");
        directory.Write("home/Projects/secret-client/.env", "SECRET=1");

        var items = new DeveloperItemsProbe(() => 0).Probe(TestEnvironment(home, directory.CreateDirectory("appdata")));

        var text = string.Join(" | ", items.Select(i => $"{i.Label} {i.Note}"));
        Assert.DoesNotContain("id_rsa_secret_project", text);
        Assert.DoesNotContain("secret-client", text);
        Assert.DoesNotContain("SECRET=1", text);
    }

    [Fact]
    public void An_empty_home_reports_nothing_present()
    {
        using var directory = new TestDirectory();
        var home = directory.CreateDirectory("empty-home");

        var items = new DeveloperItemsProbe(() => 0).Probe(TestEnvironment(home, directory.CreateDirectory("appdata")));

        Assert.All(items, item => Assert.False(item.Present));
        Assert.Equal(0, items.Single(i => i.Id == "env-files").Count);
    }

    private static SafetyEnvironment TestEnvironment(string home, string appData) => new()
    {
        HomeDirectory = home,
        AppData = appData,
        LocalAppData = appData,
        ProgramData = home,
        ProgramFiles = home,
        ProgramFilesX86 = home,
        Documents = home,
        SavedGames = home,
        FixedDrives = []
    };
}

public class GameLibraryProbeTests
{
    [Fact]
    public void Detects_steam_install_and_its_save_root()
    {
        using var directory = new TestDirectory();
        var pf86 = directory.CreateDirectory("pf86");
        var home = directory.CreateDirectory("home");
        var documents = directory.CreateDirectory("Documents");
        directory.CreateDirectory("pf86/Steam/steamapps/common");
        directory.CreateDirectory("pf86/Steam/userdata");
        directory.CreateDirectory("Documents/My Games");

        var env = TestEnvironment(home, pf86, documents);
        var libraries = new GameLibraryProbe(steamInstallPath: () => null).Detect(env);

        var steam = libraries.Single(l => l.Name == "Steam");
        Assert.True(steam.Detected);
        Assert.True(steam.HasLibraryFiles);
        Assert.Contains(steam.SavePaths, path => path.EndsWith("userdata", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(steam.SavePaths, path => path.EndsWith("My Games", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Reports_nothing_detected_on_a_bare_machine()
    {
        using var directory = new TestDirectory();
        var home = directory.CreateDirectory("home");
        var pf86 = directory.CreateDirectory("pf86");
        var documents = directory.CreateDirectory("Documents");

        var libraries = new GameLibraryProbe(steamInstallPath: () => null, gogInstallPath: () => null)
            .Detect(TestEnvironment(home, pf86, documents));

        Assert.All(libraries, library => Assert.False(library.Detected));
        Assert.All(libraries, library => Assert.Empty(library.SavePaths));
    }

    [Fact]
    public void A_registry_path_that_points_at_a_real_folder_counts_as_detected()
    {
        using var directory = new TestDirectory();
        var home = directory.CreateDirectory("home");
        var pf86 = directory.CreateDirectory("pf86");
        var documents = directory.CreateDirectory("Documents");
        var steam = directory.CreateDirectory("elsewhere/Steam");
        directory.CreateDirectory("elsewhere/Steam/userdata");

        var libraries = new GameLibraryProbe(steamInstallPath: () => steam)
            .Detect(TestEnvironment(home, pf86, documents));

        Assert.True(libraries.Single(l => l.Name == "Steam").Detected);
    }

    private static SafetyEnvironment TestEnvironment(string home, string pf86, string documents) => new()
    {
        HomeDirectory = home,
        AppData = home,
        LocalAppData = home,
        ProgramData = home,
        ProgramFiles = pf86,
        ProgramFilesX86 = pf86,
        Documents = documents,
        // Deliberately a folder that does not exist, like a fresh profile.
        SavedGames = Path.Combine(home, "Saved Games"),
        FixedDrives = []
    };
}
