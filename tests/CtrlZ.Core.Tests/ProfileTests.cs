using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public class ProfileTests
{
    [Fact]
    public void Profile_roundtrips_with_multiple_apps_and_sources()
    {
        using var temp = new TestDirectory();
        var paths = new AppPaths(temp.PathOf("data"));
        paths.EnsureCreated();
        var store = new ProfileStore(paths, TestFactory.Log);

        var profile = new AppProfile { Name = "Work PC" };
        profile.Apps.Add(new AppEntry
        {
            Name = "Firefox",
            Source = AppSource.Winget,
            PackageId = "Mozilla.Firefox",
            Category = AppCategory.Browser
        });
        profile.Apps.Add(new AppEntry
        {
            Name = "Notion",
            Source = AppSource.DirectUrl,
            DownloadUrl = "https://example.com/notion.exe",
            SilentArgs = "/S"
        });

        store.SaveToStore(profile);
        var summaries = store.List();
        Assert.Single(summaries);
        var loaded = store.Load(summaries[0].Path);
        Assert.Equal("Work PC", loaded.Name);
        Assert.Equal(2, loaded.Apps.Count);
        Assert.Equal("Mozilla.Firefox", loaded.Apps[0].PackageId);
        Assert.Equal("/S", loaded.Apps[1].SilentArgs);
    }

    [Fact]
    public void Import_creates_unique_profile_when_id_already_exists()
    {
        using var temp = new TestDirectory();
        var paths = new AppPaths(temp.PathOf("data"));
        paths.EnsureCreated();
        var store = new ProfileStore(paths, TestFactory.Log);
        var profile = store.Create("Gaming PC");
        var exportPath = temp.PathOf("gaming.json");
        store.SaveAs(profile, exportPath);

        var imported = store.Import(exportPath);
        Assert.NotEqual(profile.Id, imported.Id);
        Assert.Contains("imported", imported.Name);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void External_export_is_valid_portable_json()
    {
        using var temp = new TestDirectory();
        var paths = new AppPaths(temp.PathOf("data"));
        var store = new ProfileStore(paths, TestFactory.Log);
        var profile = new AppProfile { Name = "Laptop" };
        profile.Apps.Add(new AppEntry { Name = "7-Zip", Source = AppSource.Winget, PackageId = "7zip.7zip" });
        var file = temp.PathOf("laptop.json");
        store.SaveAs(profile, file);

        var json = File.ReadAllText(file);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"source\": \"winget\"", json);
        Assert.Contains("7zip.7zip", json);
    }
}
