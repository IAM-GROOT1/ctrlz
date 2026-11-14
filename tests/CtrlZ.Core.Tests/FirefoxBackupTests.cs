using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;
using ICSharpCode.SharpZipLib.Zip;

namespace CtrlZ.Core.Tests;

public class FirefoxBackupTests
{
    private static (TestDirectory Temp, string Root, DetectedBrowser Browser) CreateSource()
    {
        var temp = new TestDirectory();
        var root = temp.CreateDirectory("Firefox");
        temp.Write("Firefox/profiles.ini",
            "[Profile0]\nName=Work\nIsRelative=1\nPath=Profiles/abc.default-release\nDefault=1\n");
        var profile = "Firefox/Profiles/abc.default-release";
        temp.Write($"{profile}/prefs.js", "user_pref(\"x\", 1);");
        temp.Write($"{profile}/places.sqlite", "places");
        temp.Write($"{profile}/sessionstore.jsonlz4", "session");
        temp.Write($"{profile}/sessionstore-backups/recovery.jsonlz4", "recovery");
        temp.Write($"{profile}/search.json.mozlz4", "search");
        temp.Write($"{profile}/permissions.sqlite", "permissions");
        temp.Write($"{profile}/extensions/demo@example.com.xpi", "xpi");
        temp.Write($"{profile}/extensions.json", "{}");
        temp.Write($"{profile}/storage/default/moz-extension+++abc/idb/data", "extension-data");
        temp.Write($"{profile}/bookmarkbackups/bookmarks.jsonlz4", "bookmarks");
        temp.Write($"{profile}/cookies.sqlite", "COOKIE-SECRET");
        temp.Write($"{profile}/logins.json", "PASSWORD-SECRET");
        temp.Write($"{profile}/key4.db", "KEY-SECRET");
        temp.Write($"{profile}/formhistory.sqlite", "FORM-SECRET");
        temp.Write($"{profile}/cache2/data", "CACHE");
        temp.Write($"{profile}/places.sqlite-wal", "WAL");

        var definition = ChromiumBrowserCatalog.CreateCustomDefinition(
            "firefox-test", "Test Firefox", BrowserFamily.Firefox);
        var browser = new DetectedBrowser
        {
            Definition = definition,
            DisplayName = "Test Firefox",
            UserDataPath = root,
            Version = "130.0",
            Profiles = FirefoxProfileReader.ReadProfiles(root).ToList()
        };
        return (temp, root, browser);
    }

    private static BrowserBackupService CreateService(TestDirectory temp)
    {
        var paths = new AppPaths(temp.PathOf("appdata"));
        paths.EnsureCreated();
        return new BrowserBackupService(paths, TestFactory.Log, new BrowserProcessService(TestFactory.Log));
    }

    [Fact]
    public void Firefox_plan_includes_profile_data_and_excludes_credentials()
    {
        var (temp, _, browser) = CreateSource();
        using (temp)
        {
            var plan = BrowserBackupPlanner.Build(browser, browser.Profiles);
            var paths = plan.Entries.Select(e => e.EntryPath).ToArray();

            Assert.Contains("User Data/Profiles/abc.default-release/prefs.js", paths);
            Assert.Contains("User Data/Profiles/abc.default-release/places.sqlite", paths);
            Assert.Contains("User Data/Profiles/abc.default-release/sessionstore.jsonlz4", paths);
            Assert.Contains("User Data/Profiles/abc.default-release/extensions/demo@example.com.xpi", paths);
            Assert.Contains("User Data/Profiles/abc.default-release/storage/default/moz-extension+++abc/idb/data", paths);
            Assert.DoesNotContain(paths, p => p.Contains("cookies.sqlite", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(paths, p => p.Contains("logins.json", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(paths, p => p.Contains("key4.db", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(paths, p => p.Contains("formhistory", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(paths, p => p.Contains("cache2", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Firefox_backup_is_family_tagged_and_sanitized()
    {
        var (temp, _, browser) = CreateSource();
        using (temp)
        {
            var service = CreateService(temp);
            var result = await service.BackupAsync(new BrowserBackupRequest(
                browser, browser.Profiles, temp.PathOf("backups"), Password: null));

            using var zip = new ZipFile(result.ArchivePath);
            var names = new List<string>();
            for (var i = 0; i < zip.Count; i++)
            {
                names.Add(zip[i].Name);
            }

            Assert.Equal(BrowserFamily.Firefox, result.Manifest.Family);
            Assert.Equal("browser-profile-backup", result.Manifest.Format);
            Assert.Contains("User Data/Profiles/abc.default-release/places.sqlite", names);
            Assert.DoesNotContain(names, n => n.Contains("cookies.sqlite", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("logins.json", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("key4.db", StringComparison.OrdinalIgnoreCase));
            Assert.True(result.Manifest.Contents.Bookmarks);
            Assert.True(result.Manifest.Contents.Sessions);
            Assert.True(result.Manifest.Contents.Extensions);
            Assert.True(result.Manifest.Contents.SearchEngines);
        }
    }

    [Fact]
    public async Task Single_profile_firefox_bundle_restores_into_its_actual_profile_folder()
    {
        using var temp = new TestDirectory();
        var sourceRoot = temp.CreateDirectory("TorSource");
        temp.Write("TorSource/profile.default/prefs.js", "source");
        temp.Write("TorSource/profile.default/places.sqlite", "source-places");
        temp.Write("TorSource/profile.default/cookies.sqlite", "COOKIE-SECRET");
        var sourceBrowser = new DetectedBrowser
        {
            Definition = ChromiumBrowserCatalog.CreateCustomDefinition(
                "tor-source", "Tor source", BrowserFamily.Firefox),
            DisplayName = "Tor source",
            UserDataPath = sourceRoot,
            Profiles = FirefoxProfileReader.ReadProfiles(sourceRoot).ToList()
        };
        var service = CreateService(temp);
        var backup = await service.BackupAsync(new BrowserBackupRequest(
            sourceBrowser, sourceBrowser.Profiles, temp.PathOf("backups"), Password: null));

        var targetRoot = temp.CreateDirectory("TorTarget");
        temp.Write("TorTarget/profile.default/prefs.js", "target");
        temp.Write("TorTarget/profile.default/places.sqlite", "target-places");
        temp.Write("TorTarget/profile.default/cookies.sqlite", "KEEP-COOKIE");
        var target = new DetectedBrowser
        {
            Definition = ChromiumBrowserCatalog.CreateCustomDefinition(
                "tor-target", "Tor target", BrowserFamily.Firefox),
            DisplayName = "Tor target",
            UserDataPath = targetRoot,
            Profiles = FirefoxProfileReader.ReadProfiles(targetRoot).ToList()
        };

        var restore = new BrowserRestoreService(
            new AppPaths(temp.PathOf("appdata")),
            TestFactory.Log,
            new BrowserProcessService(TestFactory.Log),
            new BrowserArchiveReader());
        await restore.RestoreAsync(new BrowserRestoreRequest(
            backup.ArchivePath, null, target, ["."], RestoreAsNewProfiles: true));

        Assert.Equal("source", File.ReadAllText(Path.Combine(targetRoot, "profile.default/prefs.js")));
        Assert.Equal("source-places", File.ReadAllText(Path.Combine(targetRoot, "profile.default/places.sqlite")));
        Assert.Equal("KEEP-COOKIE", File.ReadAllText(Path.Combine(targetRoot, "profile.default/cookies.sqlite")));
        Assert.False(File.Exists(Path.Combine(targetRoot, "Profiles/Profile 1/prefs.js")));
    }

    [Fact]
    public async Task Firefox_restore_as_new_profile_updates_profiles_ini_and_keeps_credentials()
    {
        var (temp, _, sourceBrowser) = CreateSource();
        using (temp)
        {
            var service = CreateService(temp);
            var backup = await service.BackupAsync(new BrowserBackupRequest(
                sourceBrowser, sourceBrowser.Profiles, temp.PathOf("backups"), Password: null));

            var targetRoot = temp.CreateDirectory("TargetFirefox");
            temp.Write("TargetFirefox/profiles.ini",
                "[Profile0]\nName=Existing\nIsRelative=1\nPath=Profiles/existing\nDefault=1\n");
            temp.Write("TargetFirefox/Profiles/existing/prefs.js", "existing");
            temp.Write("TargetFirefox/Profiles/existing/cookies.sqlite", "KEEP-COOKIE");

            var definition = ChromiumBrowserCatalog.CreateCustomDefinition(
                "firefox-target", "Target Firefox", BrowserFamily.Firefox);
            var target = new DetectedBrowser
            {
                Definition = definition,
                DisplayName = "Target Firefox",
                UserDataPath = targetRoot,
                Profiles = FirefoxProfileReader.ReadProfiles(targetRoot).ToList()
            };

            var restore = new BrowserRestoreService(
                new AppPaths(temp.PathOf("appdata")),
                TestFactory.Log,
                new BrowserProcessService(TestFactory.Log),
                new BrowserArchiveReader());
            var result = await restore.RestoreAsync(new BrowserRestoreRequest(
                backup.ArchivePath, null, target, ["Profiles/abc.default-release"], RestoreAsNewProfiles: true));

            Assert.Contains("Profiles/Profile 1", result.RestoredProfiles);
            Assert.True(File.Exists(Path.Combine(targetRoot, "Profiles/Profile 1/prefs.js")));
            Assert.True(File.Exists(Path.Combine(targetRoot, "Profiles/Profile 1/places.sqlite")));
            Assert.True(File.Exists(Path.Combine(targetRoot, "Profiles/Profile 1/extensions/demo@example.com.xpi")));
            Assert.False(File.Exists(Path.Combine(targetRoot, "Profiles/Profile 1/cookies.sqlite")));
            Assert.Equal("KEEP-COOKIE",
                File.ReadAllText(Path.Combine(targetRoot, "Profiles/existing/cookies.sqlite")));

            var ini = File.ReadAllText(Path.Combine(targetRoot, "profiles.ini"));
            Assert.Contains("Path=Profiles/Profile 1", ini);
            Assert.Contains("Name=Work (restored)", ini);
        }
    }
}
