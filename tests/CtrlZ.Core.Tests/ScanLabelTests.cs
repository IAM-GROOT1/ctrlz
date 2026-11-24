// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

/// <summary>What the Scan page says about a program that winget did not match.</summary>
public class ScanLabelTests
{
    [Fact]
    public void Without_winget_the_reason_says_winget_is_missing_not_that_the_name_is()
    {
        var match = WingetMatcher.Match("CTRL+Z 1.4.0", "IAM-GROOT1", "1.4.0", []);

        Assert.Equal(WingetMatchKind.None, match.Kind);
        Assert.Equal("winget is not available, so nothing was matched.", match.Reason);
        Assert.Equal("No name to match.", WingetMatcher.Match(" ", null, null, [new WingetPackage("7-Zip", "7zip.7zip", "24", null)]).Reason);
    }

    [Fact]
    public void An_unmatched_registry_program_has_no_source_yet()
    {
        var unmatched = new ScannedApp { Name = "Tool", Source = AppSource.Winget };
        var matched = new ScannedApp { Name = "7-Zip", Source = AppSource.Winget, MatchedWingetId = "7zip.7zip" };
        var store = new ScannedApp { Name = "Calculator", Source = AppSource.MicrosoftStore, IsStoreApp = true };

        Assert.Equal("Not matched", unmatched.SourceLabel);
        Assert.Equal("winget", matched.SourceLabel);
        Assert.Equal("Store", store.SourceLabel);
    }
}
