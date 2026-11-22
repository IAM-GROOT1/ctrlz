// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;
using CtrlZ.Core.Services;
using Xunit;

namespace CtrlZ.Core.Tests;

public sealed class PackageListImporterTests
{
    private static readonly IReadOnlyList<WinUtilCatalogItem> Catalog =
    [
        new("git", "Git", "Development", "Git.Git", "git", null, null, true),
        new("vscode", "VS Code", "Development", "Microsoft.VisualStudioCode", "vscode", null, null, false),
        new("7zip", "7-Zip", "Utilities", "7zip.7zip", "7zip", null, null, true),
        new("firefox", "Firefox", "Browsers", "Mozilla.Firefox", "firefox", null, null, true),
        new("chocoonly", "Choco Only", "Utilities", null, "chocoonly", null, null, true)
    ];

    private const string WingetExport = """
        {
          "$schema" : "https://aka.ms/winget-packages.schema.2.0.json",
          "CreationDate" : "2026-11-22T10:00:00.000-00:00",
          "Sources" : [
            {
              "Packages" : [
                { "PackageIdentifier" : "Git.Git" },
                { "PackageIdentifier" : "Some.Unlisted" },
                { "PackageIdentifier" : "Git.Git" },
                { "PackageIdentifier" : "bad id; rm -rf" }
              ],
              "SourceDetails" : { "Argument" : "https://cdn.winget.microsoft.com/cache", "Identifier" : "Microsoft.Winget.Source_8wekyb3d8bbwe", "Name" : "winget", "Type" : "Microsoft.PreIndexed.Package" }
            },
            {
              "Packages" : [ { "PackageIdentifier" : "9NBLGGH4NNS1" } ],
              "SourceDetails" : { "Name" : "msstore" }
            },
            {
              "Packages" : [ { "PackageIdentifier" : "Corp.Tool" } ],
              "SourceDetails" : { "Name" : "company-private" }
            }
          ],
          "WinGetVersion" : "1.9.2411"
        }
        """;

    [Fact]
    public void Winget_export_keeps_winget_and_store_ids_and_reports_the_rest()
    {
        var result = PackageListImporter.FromText(WingetExport, Catalog);

        Assert.Equal(PackageListKind.Winget, result.Kind);
        Assert.Collection(result.Apps,
            app => { Assert.Equal("Git", app.Name); Assert.Equal("Git.Git", app.PackageId); Assert.Equal(AppSource.Winget, app.Source); Assert.Equal(AppCategory.Development, app.Category); },
            app => { Assert.Equal("Some.Unlisted", app.Name); Assert.Equal(AppSource.Winget, app.Source); },
            app => { Assert.Equal("9NBLGGH4NNS1", app.PackageId); Assert.Equal(AppSource.MicrosoftStore, app.Source); });
        Assert.Equal(["Corp.Tool"], result.Unmatched);
    }

    [Fact]
    public void Scoop_json_matches_through_the_catalog()
    {
        const string json = """
            { "buckets": [ { "Name": "main" } ],
              "apps": [
                { "Name": "git", "Source": "main", "Version": "2.47.0" },
                { "Name": "7zip", "Source": "main" },
                { "Name": "firefox", "Source": "extras" },
                { "Name": "visualstudiocode", "Source": "extras" },
                { "Name": "obscure-tool", "Source": "main" }
              ] }
            """;

        var result = PackageListImporter.FromText(json, Catalog);

        Assert.Equal(PackageListKind.Scoop, result.Kind);
        Assert.Equal(["Git.Git", "7zip.7zip", "Mozilla.Firefox", "Microsoft.VisualStudioCode"], result.Apps.Select(app => app.PackageId));
        Assert.Equal(["obscure-tool"], result.Unmatched);
    }

    [Fact]
    public void Old_scoop_text_lines_are_read()
    {
        const string text = "git (v2.43.0) [main]\r\n7zip (v23.01) [main] {64bit}\r\nnothing-like-it (v1) [extras]\r\n";

        var result = PackageListImporter.FromText(text, Catalog);

        Assert.Equal(PackageListKind.Scoop, result.Kind);
        Assert.Equal(["Git.Git", "7zip.7zip"], result.Apps.Select(app => app.PackageId));
        Assert.Equal(["nothing-like-it"], result.Unmatched);
    }

    [Fact]
    public void Chocolatey_packages_config_matches_by_choco_id()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="git" version="2.47.0" />
              <package id="vscode" />
              <package id="chocoonly" />
              <package id="unknownpkg" />
            </packages>
            """;

        var result = PackageListImporter.FromText(xml, Catalog);

        Assert.Equal(PackageListKind.Chocolatey, result.Kind);
        Assert.Equal(["Git.Git", "Microsoft.VisualStudioCode"], result.Apps.Select(app => app.PackageId));
        Assert.Equal(["chocoonly", "unknownpkg"], result.Unmatched);
    }

    [Fact]
    public void Xml_with_a_dtd_is_refused()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE packages [ <!ENTITY x SYSTEM "file:///c:/windows/win.ini"> ]>
            <packages><package id="&x;" /></packages>
            """;

        Assert.Throws<FormatException>(() => PackageListImporter.FromText(xml, Catalog));
    }

    [Theory]
    [InlineData("""{ "Id": "x", "Apps": [] }""")]
    [InlineData("<config><package id=\"git\" /></config>")]
    [InlineData("this is just some notes\nnot a list")]
    [InlineData("{ not json")]
    [InlineData("")]
    public void Unknown_files_are_refused(string text) =>
        Assert.Throws<FormatException>(() => PackageListImporter.FromText(text, Catalog));

    [Fact]
    public void Imported_apps_carry_only_an_id_and_a_name()
    {
        var result = PackageListImporter.FromText(WingetExport, Catalog);

        Assert.All(result.Apps, app =>
        {
            Assert.Null(app.PostInstall);
            Assert.Null(app.Notes);
            Assert.Null(app.DownloadUrl);
            Assert.Null(app.LocalPath);
            Assert.Null(app.SilentArgs);
            Assert.False(app.IsAutoDependency);
        });
    }

    [Fact]
    public void A_bom_before_the_json_is_accepted()
    {
        var result = PackageListImporter.FromText((char)0xFEFF + WingetExport, Catalog);

        Assert.Equal(3, result.Apps.Count);
    }

    [Fact]
    public void Too_many_packages_are_refused()
    {
        var lines = string.Join("\n", Enumerable.Range(0, PackageListImporter.MaxPackages + 1).Select(i => $"app{i} (v1) [main]"));

        Assert.Throws<FormatException>(() => PackageListImporter.FromText(lines, Catalog));
    }
}
