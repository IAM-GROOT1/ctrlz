using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;
using ICSharpCode.SharpZipLib.Zip;

namespace CtrlZ.Core.Tests;

public class BrowserBackupTests
{
    private static (TestDirectory Temp, string Root, DetectedBrowser Browser) CreateProfileTree()
    {
        var temp = new TestDirectory();
        var root = temp.CreateDirectory("User Data");
        var profile = temp.CreateDirectory("User Data/Default");
        Directory.CreateDirectory(Path.Combine(profile, "Sessions"));
        Directory.CreateDirectory(Path.Combine(profile, "Extensions", "abcdef", "1.0"));
        Directory.CreateDirectory(Path.Combine(profile, "Cache"));
        Directory.CreateDirectory(Path.Combine(profile, "Code Cache"));
        temp.Write("User Data/Local State",
            "{\"os_crypt\":{\"encrypted_key\":\"secret\"},\"browser\":{\"x\":1},\"profile\":{\"info_cache\":{\"Default\":{\"name\":\"Work\"}}}}");
        temp.Write("User Data/Default/Bookmarks", "{\"roots\":{}}");
        temp.Write("User Data/Default/History", "history");
        temp.Write("User Data/Default/Preferences", "{\"profile\":{}}");
        temp.Write("User Data/Default/Web Data", "not-a-sqlite-database-in-this-fixture");
        temp.Write("User Data/Default/Sessions/Session_1", "tabs");
        temp.Write("User Data/Default/Extensions/abcdef/1.0/manifest.json", "{\"name\":\"ext\"}");
        temp.Write("User Data/Default/Cache/Cache_Data/data", "cache");
        temp.Write("User Data/Default/Code Cache/js/data", "cache");
        temp.Write("User Data/Default/Cookies", "cookie-secret");
        temp.Write("User Data/Default/Login Data", "password-secret");
        temp.Write("User Data/Default/Network/Cookies", "network-cookie");
        File.WriteAllText(Path.Combine(profile, "Local State"), "{}");

        var definition = ChromiumBrowserCatalog.CreateCustomDefinition("test", "Test Browser");
        var browser = new DetectedBrowser
        {
            Definition = definition,
            DisplayName = "Test Browser",
            UserDataPath = root,
            Profiles = ChromiumProfileReader.ReadProfiles(root).ToList()
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
    public void Planner_includes_core_data_and_excludes_secrets_and_caches()
    {
        var (temp, root, browser) = CreateProfileTree();
        using (temp)
        {
            var plan = BrowserBackupPlanner.Build(browser, browser.Profiles);
            var paths = plan.Entries.Select(e => e.EntryPath).ToArray();

            Assert.Contains("User Data/Local State", paths);
            Assert.Contains("User Data/Default/Bookmarks", paths);
            Assert.Contains("User Data/Default/History", paths);
            Assert.Contains("User Data/Default/Sessions/Session_1", paths);
            Assert.Contains("User Data/Default/Extensions/abcdef/1.0/manifest.json", paths);
            Assert.Contains("User Data/Default/Web Data", paths);
            Assert.DoesNotContain(paths, p => p.Contains("Cookies", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(paths, p => p.Contains("Login Data", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(paths, p => p.Contains("/Cache/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(paths, p => p.Contains("Code Cache", StringComparison.OrdinalIgnoreCase));

            var estimate = BrowserBackupPlanner.Estimate(plan);
            Assert.True(estimate.FileCount >= 7);
            Assert.True(estimate.TotalBytes > 0);
            Assert.Contains(estimate.Excluded, e => e.Contains("Cookies", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Backup_creates_archive_with_manifest_and_without_secrets()
    {
        var (temp, _, browser) = CreateProfileTree();
        using (temp)
        {
            var service = CreateService(temp);
            var result = await service.BackupAsync(new BrowserBackupRequest(
                browser, browser.Profiles, temp.PathOf("backups"), Password: null));

            Assert.True(File.Exists(result.ArchivePath));
            Assert.Equal("Test Browser", result.Manifest.BrowserName);
            Assert.Single(result.Manifest.Profiles);

            using var zip = new ZipFile(result.ArchivePath);
            var names = new List<string>();
            for (var i = 0; i < zip.Count; i++)
            {
                names.Add(zip[i].Name);
            }

            Assert.Contains("manifest.json", names);
            Assert.Contains("User Data/Default/Bookmarks", names);
            Assert.Contains("User Data/Local State", names);
            Assert.DoesNotContain(names, n => n.Contains("Cookies", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("Login Data", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => n.Contains("/Cache/", StringComparison.OrdinalIgnoreCase));

            var info = new BrowserArchiveReader().ReadInfo(result.ArchivePath, null);
            Assert.False(info.IsEncrypted);
            Assert.Equal("browser-profile-backup", info.Manifest.Format);
            Assert.Equal(BrowserFamily.Chromium, info.Manifest.Family);
            var localStateEntry = zip.GetEntry("User Data/Local State")!;
            using var reader = new StreamReader(zip.GetInputStream(localStateEntry));
            Assert.DoesNotContain("secret", reader.ReadToEnd());
        }
    }

    [Fact]
    public async Task Encrypted_archive_requires_password()
    {
        var (temp, _, browser) = CreateProfileTree();
        using (temp)
        {
            var service = CreateService(temp);
            var result = await service.BackupAsync(new BrowserBackupRequest(
                browser, browser.Profiles, temp.PathOf("backups"), Password: "correct horse"));

            var reader = new BrowserArchiveReader();
            Assert.Throws<BrowserArchivePasswordException>(() => reader.ReadInfo(result.ArchivePath, null));
            Assert.Throws<BrowserArchivePasswordException>(() => reader.ReadInfo(result.ArchivePath, "wrong"));
            var info = reader.ReadInfo(result.ArchivePath, "correct horse");
            Assert.True(info.IsEncrypted);
        }
    }

    [Fact]
    public async Task Restore_as_new_profile_keeps_existing_data_and_restores_files()
    {
        var (temp, _, sourceBrowser) = CreateProfileTree();
        using (temp)
        {
            var service = CreateService(temp);
            var backup = await service.BackupAsync(new BrowserBackupRequest(
                sourceBrowser, sourceBrowser.Profiles, temp.PathOf("backups"), Password: null));

            var targetRoot = temp.CreateDirectory("Target User Data");
            File.WriteAllText(Path.Combine(targetRoot, "Local State"),
                "{\"profile\":{\"info_cache\":{\"Default\":{\"name\":\"Existing\"}}},\"os_crypt\":{\"encrypted_key\":\"keep\"}}");
            var targetProfile = temp.CreateDirectory("Target User Data/Default");
            File.WriteAllText(Path.Combine(targetProfile, "Cookies"), "keep-my-cookie");
            File.WriteAllText(Path.Combine(targetProfile, "Login Data"), "keep-my-password");

            var definition = ChromiumBrowserCatalog.CreateCustomDefinition("target", "Test Target");
            var target = new DetectedBrowser
            {
                Definition = definition,
                DisplayName = "Test Target",
                UserDataPath = targetRoot,
                Profiles = ChromiumProfileReader.ReadProfiles(targetRoot).ToList()
            };

            var restore = new BrowserRestoreService(
                new AppPaths(temp.PathOf("appdata")),
                TestFactory.Log,
                new BrowserProcessService(TestFactory.Log),
                new BrowserArchiveReader());

            var result = await restore.RestoreAsync(new BrowserRestoreRequest(
                backup.ArchivePath, null, target, ["Default"], RestoreAsNewProfiles: true));

            Assert.Contains("Profile 1", result.RestoredProfiles);
            Assert.True(File.Exists(Path.Combine(targetRoot, "Profile 1", "Bookmarks")));
            Assert.True(File.Exists(Path.Combine(targetRoot, "Profile 1", "Extensions", "abcdef", "1.0", "manifest.json")));
            Assert.Equal("keep-my-cookie", File.ReadAllText(Path.Combine(targetProfile, "Cookies")));
            Assert.Equal("keep-my-password", File.ReadAllText(Path.Combine(targetProfile, "Login Data")));

            var localState = File.ReadAllText(Path.Combine(targetRoot, "Local State"));
            Assert.Contains("Profile 1", localState);
            Assert.Contains("Work (restored)", localState);
            Assert.Contains("keep", localState); // current os_crypt is preserved, not replaced by the archive.
            Assert.DoesNotContain("secret", localState);
        }
    }
}
