using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public class WingetMatcherTests
{
    private static readonly WingetPackage[] Packages =
    [
        new("Mozilla Firefox", "Mozilla.Firefox", "121.0", null),
        new("Google Chrome", "Google.Chrome", "120.0", null),
        new("Microsoft Visual Studio Code", "Microsoft.VisualStudioCode", "1.85", null),
        new("7-Zip", "7zip.7zip", "23.01", null),
        new("Steam", "Valve.Steam", "2.10", null),
        new("Notepad++", "Notepad++.Notepad++", "8.6", null)
    ];

    [Theory]
    [InlineData("Mozilla Firefox 121.0 (x64)", "Mozilla.Firefox")]
    [InlineData("Visual Studio Code", "Microsoft.VisualStudioCode")]
    [InlineData("7-Zip 23.01 (x64 edition)", "7zip.7zip")]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++.Notepad++")]
    public void Matches_common_installed_app_names(string installedName, string expectedId)
    {
        var match = WingetMatcher.Match(installedName, null, null, Packages);
        Assert.Equal(expectedId, match.PackageId);
        Assert.True(match.Score >= 75, $"score was {match.Score}");
    }

    [Fact]
    public void Rejects_unrelated_names()
    {
        var match = WingetMatcher.Match("Dell SupportAssist Remediation", "Dell Inc.", null, Packages);
        Assert.Null(match.PackageId);
        Assert.Equal(WingetMatchKind.None, match.Kind);
    }

    [Fact]
    public void NormalizeName_removes_versions_architecture_and_vendor_noise()
    {
        Assert.Equal("visual studio code", WingetMatcher.NormalizeName("Microsoft Visual Studio Code (User) 1.85.2 x64"));
        Assert.Equal("7 zip", WingetMatcher.NormalizeName("7-Zip 23.01 (x64)"));
    }

    [Fact]
    public void Exact_names_are_strong_or_exact()
    {
        var match = WingetMatcher.Match("Google Chrome", "Google LLC", "120.0", Packages);
        Assert.Equal("Google.Chrome", match.PackageId);
        Assert.True(match.Kind is WingetMatchKind.Exact or WingetMatchKind.Strong);
    }
}

public class WingetTableParserTests
{
    private static string Table(bool includeAvailable)
    {
        const int nameWidth = 32;
        const int idWidth = 38;
        const int versionWidth = 14;
        const int availableWidth = 14;
        var header = "Name".PadRight(nameWidth) + "Id".PadRight(idWidth) + "Version".PadRight(versionWidth);
        if (includeAvailable)
        {
            header += "Available".PadRight(availableWidth);
        }

        header += "Source";
        var separator = new string('-', header.Length);
        var rows = new[]
        {
            "Mozilla Firefox".PadRight(nameWidth) + "Mozilla.Firefox".PadRight(idWidth) + "121.0".PadRight(versionWidth) +
            (includeAvailable ? "122.0".PadRight(availableWidth) : string.Empty) + "winget",
            "Microsoft Edge".PadRight(nameWidth) + "Microsoft.Edge".PadRight(idWidth) + "120.0".PadRight(versionWidth) +
            (includeAvailable ? string.Empty.PadRight(availableWidth) : string.Empty) + "winget",
            "Calculator".PadRight(nameWidth) + "9NBLGGH4NNS1".PadRight(idWidth) + "11.0".PadRight(versionWidth) +
            (includeAvailable ? string.Empty.PadRight(availableWidth) : string.Empty) + "msstore"
        };
        return string.Join(Environment.NewLine, new[] { header, separator }.Concat(rows));
    }

    [Fact]
    public void Parses_list_table_with_available_column()
    {
        var packages = WingetTableParser.ParseList(Table(includeAvailable: true));
        Assert.Equal(3, packages.Count);
        Assert.Equal("Mozilla.Firefox", packages[0].Id);
        Assert.Equal("121.0", packages[0].Version);
        Assert.Equal("122.0", packages[0].AvailableVersion);
        Assert.Equal("msstore", packages[2].Source);
    }

    [Fact]
    public void Parses_search_table_without_available_column()
    {
        var packages = WingetTableParser.ParseSearch(Table(includeAvailable: false));
        Assert.Equal(3, packages.Count);
        Assert.Equal("Microsoft.Edge", packages[1].Id);
        Assert.Equal("winget", packages[1].Source);
    }

    [Fact]
    public void Ignores_progress_and_empty_output()
    {
        Assert.Empty(WingetTableParser.ParseList("No installed package found matching input criteria."));
    }
}
