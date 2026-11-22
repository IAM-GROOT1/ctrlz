// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

public class NotesPrivacyTests
{
    [Fact]
    public void Notes_are_off_by_default()
    {
        Assert.False(new AppSettings().IncludeNotesInReports);
    }

    [Fact]
    public void A_report_hides_notes_until_the_user_opts_in()
    {
        const string note = "licence ABC-123";

        Assert.Equal(NotesPrivacy.Redacted, NotesPrivacy.ForReport(note, includeNotes: false));
        Assert.Equal(note, NotesPrivacy.ForReport(note, includeNotes: true));
    }

    [Fact]
    public void The_opt_in_setting_drives_the_same_decision_as_the_flag()
    {
        var off = new AppSettings();
        var on = new AppSettings { IncludeNotesInReports = true };

        Assert.Equal(NotesPrivacy.Redacted, NotesPrivacy.ForReport("secret", off.IncludeNotesInReports));
        Assert.Equal("secret", NotesPrivacy.ForReport("secret", on.IncludeNotesInReports));
    }

    [Fact]
    public void A_log_line_never_carries_a_note_even_when_reports_include_them()
    {
        const string note = "licence ABC-123";

        Assert.Equal("[note present]", NotesPrivacy.ForLog(note));
        Assert.DoesNotContain("ABC-123", NotesPrivacy.ForLog(note));
    }

    [Fact]
    public void An_app_knows_when_it_has_a_note()
    {
        Assert.False(new AppEntry().HasNotes);
        Assert.False(new AppEntry { Notes = "   " }.HasNotes);
        Assert.True(new AppEntry { Notes = "check the licence" }.HasNotes);
    }
}
