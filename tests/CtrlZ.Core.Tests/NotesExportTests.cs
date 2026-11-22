// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Models;
using CtrlZ.Core.Safety;

namespace CtrlZ.Core.Tests;

public class NotesExportTests
{
    private const string Secret = "licence key ABCD-1234";

    private static DryRunReport Report() => new()
    {
        ProfileName = "P",
        Items = [new DryRunItem { AppId = "a", Name = "Tool", Status = PlanCheckStatus.Ready, Message = "ok" }]
    };

    private static AppEntry[] Apps() => [new AppEntry { Id = "a", Name = "Tool", Notes = Secret }];

    [Fact]
    public void Dry_run_export_hides_notes_by_default()
    {
        var text = Report().WithExportNotes(Apps(), new AppSettings()).ToText();

        Assert.DoesNotContain(Secret, text);
        Assert.Contains(NotesPrivacy.Redacted, text);
    }

    [Fact]
    public void Dry_run_export_includes_notes_when_opted_in()
    {
        var text = Report().WithExportNotes(Apps(), new AppSettings { IncludeNotesInReports = true }).ToText();

        Assert.Contains(Secret, text);
    }

    [Fact]
    public void Plain_report_text_never_contains_notes()
    {
        Assert.DoesNotContain(Secret, Report().ToText());
    }

    [Theory]
    [InlineData(false, NotesPrivacy.Redacted)]
    [InlineData(true, Secret)]
    public void Export_chokepoint_follows_the_setting(bool optIn, string expected)
    {
        Assert.Equal(expected, NotesPrivacy.ForExport(Secret, new AppSettings { IncludeNotesInReports = optIn }));
        Assert.Null(NotesPrivacy.ForExport("  ", new AppSettings { IncludeNotesInReports = optIn }));
    }
}
