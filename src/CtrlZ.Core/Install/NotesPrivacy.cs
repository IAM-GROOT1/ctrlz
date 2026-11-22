// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Install;

/// <summary>
/// Per-app notes are free text and may hold licence keys or manual steps. They never leave the app
/// unless the user opts in, which is what every reporting path asks here first.
/// </summary>
public static class NotesPrivacy
{
    public const string Redacted = "(note hidden)";

    /// <summary>Returns the note for a report, or a placeholder when notes are excluded.</summary>
    public static string? ForReport(string? note, bool includeNotes)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        return includeNotes ? note : Redacted;
    }

    /// <summary>
    /// The single chokepoint for any export that leaves the app (dry-run export today, any future
    /// diagnostics bundle): the note is included only when the user turned the setting on.
    /// </summary>
    public static string? ForExport(string? note, CtrlZ.Core.Models.AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ForReport(note, settings.IncludeNotesInReports);
    }

    /// <summary>True when the app has a note worth showing an indicator for.</summary>
    public static bool HasNote(string? note) => !string.IsNullOrWhiteSpace(note);

    /// <summary>Text for a log line: the note is never included, before or after the opt-in.</summary>
    public static string ForLog(string? note) => HasNote(note) ? "[note present]" : string.Empty;
}
