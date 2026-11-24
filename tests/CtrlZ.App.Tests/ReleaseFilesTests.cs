// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CtrlZ.App.Tests;

/// <summary>Release files that must agree with each other.</summary>
public class ReleaseFilesTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "installer", "CTRL+Z.iss")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root above the test output.");
    }

    [Fact]
    public void The_winget_product_code_is_the_installer_app_id()
    {
        // winget finds an installed copy by this code; a mismatch would install a second copy.
        var root = RepoRoot();
        var iss = File.ReadAllText(Path.Combine(root, "installer", "CTRL+Z.iss"));
        var manifest = File.ReadAllText(Path.Combine(root, "build", "winget-manifest.ps1"));

        var appId = Regex.Match(iss, @"^AppId=\{\{(?<id>[0-9A-F-]{36})\}", RegexOptions.Multiline).Groups["id"].Value;
        var productCode = Regex.Match(manifest, @"ProductCode: '\{(?<id>[0-9A-F-]{36})\}_is1'").Groups["id"].Value;

        Assert.NotEmpty(appId);
        Assert.Equal(appId, productCode);
        Assert.Contains("PrivilegesRequired=lowest", iss);
        Assert.Contains("Scope: user", manifest);
    }
}
