// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using Xunit;
using CtrlZ.App.ViewModels;
using CtrlZ.Core.Models;

namespace CtrlZ.App.Tests;

public class InstallRowNoteTests
{
    [Theory]
    [InlineData(InstallStatus.Failed, true)]
    [InlineData(InstallStatus.NeedsManualAction, true)]
    [InlineData(InstallStatus.Success, false)]
    [InlineData(InstallStatus.Queued, false)]
    [InlineData(InstallStatus.Installing, false)]
    public void Note_shows_only_when_the_user_has_to_act(InstallStatus status, bool expected)
    {
        var row = new InstallRowViewModel("Tool", AppSource.Winget, null) { Note = "activate with the key in my email" };

        row.Status = status;

        Assert.Equal(expected, row.ShowsNote);
        Assert.Equal(expected, row.NoteText.Contains("activate", StringComparison.Ordinal));
    }

    [Fact]
    public void No_note_means_nothing_is_shown_even_on_failure()
    {
        var row = new InstallRowViewModel("Tool", AppSource.Winget, null) { Status = InstallStatus.Failed };

        Assert.False(row.ShowsNote);
        Assert.Equal(string.Empty, row.NoteText);
    }
}
