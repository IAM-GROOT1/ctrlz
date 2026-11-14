using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public class CatalogTests
{
    [Fact]
    public void Parses_winutil_catalog_shape()
    {
        const string json = """
        {
          "7zip": {"category":"Utilities","content":"7-Zip","description":"Archiver","link":"https://7-zip.org","winget":"7zip.7zip","foss":true},
          "notion": {"category":"Productivity","content":"Notion","description":"Notes","link":"https://notion.so","winget":"Notion.Notion","foss":false}
        }
        """;

        var items = WinUtilCatalogService.Parse(json);
        Assert.Equal(2, items.Count);
        var sevenZip = Assert.Single(items, i => i.Key == "7zip");
        Assert.Equal("7zip.7zip", sevenZip.WingetId);
        Assert.True(sevenZip.IsFoss);
        Assert.True(sevenZip.CanInstall);
    }

    [Fact]
    public void Embedded_catalog_has_recommended_apps()
    {
        var paths = new CtrlZ.Core.Infrastructure.AppPaths(Path.Combine(Path.GetTempPath(), "CtrlZCatalogTest"));
        var service = new WinUtilCatalogService(paths, new DownloadService(paths, TestFactory.Log), TestFactory.Log);
        var catalog = service.LoadBundled();
        Assert.True(catalog.Items.Count > 50);
        Assert.Contains(catalog.Items, i => i.WingetId == "7zip.7zip");
    }
}

public class GitHubTests
{
    [Theory]
    [InlineData("github.com/owner/project", "owner/project")]
    [InlineData("https://github.com/owner/project/", "owner/project")]
    [InlineData("https://github.com/owner/project.git", "owner/project")]
    [InlineData("owner/project", "owner/project")]
    [InlineData("git@github.com:owner/project.git", "owner/project")]
    public void Parses_repository_forms(string input, string expected)
    {
        Assert.True(GitHubReleaseService.TryParseRepository(input, out var repository, out var error), error);
        Assert.Equal(expected, repository);
    }

    [Theory]
    [InlineData("https://gitlab.com/owner/project")]
    [InlineData("not a repo")]
    [InlineData("https://github.com/onlyowner")]
    public void Rejects_invalid_repositories(string input)
    {
        Assert.False(GitHubReleaseService.TryParseRepository(input, out _, out _));
    }
}
