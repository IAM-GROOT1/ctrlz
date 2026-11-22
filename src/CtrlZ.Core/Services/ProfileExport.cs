// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>What exporting a profile for another PC carries, and what needs attention there.</summary>
public sealed record ProfileExportSummary(
    int AppCount,
    int EnabledCount,
    IReadOnlyList<string> LocalInstallers,
    int CommandCount,
    int NoteCount)
{
    public bool HasNotes => NoteCount > 0;

    /// <summary>Plain text for the confirmation before the file is written.</summary>
    public string Describe()
    {
        var text = new StringBuilder()
            .Append($"{AppCount} app{Plural(AppCount)} ({EnabledCount} turned on) will be saved to one file. ")
            .Append("On the other PC, open My Apps → Import and choose it.");

        if (LocalInstallers.Count > 0)
        {
            text.Append("\n\n")
                .Append($"{LocalInstallers.Count} app{Plural(LocalInstallers.Count)} install{(LocalInstallers.Count == 1 ? "s" : string.Empty)} from a file on this PC: ")
                .Append(string.Join(", ", LocalInstallers.Take(5)))
                .Append(LocalInstallers.Count > 5 ? ", …" : string.Empty)
                .Append(". Copy those installers too and point each app at its new location there.");
        }

        if (CommandCount > 0)
        {
            text.Append("\n\n")
                .Append($"{CommandCount} app{Plural(CommandCount)} ha{(CommandCount == 1 ? "s" : "ve")} a post-install command. ")
                .Append("On the other PC they stay blocked until you review and approve each one there.");
        }

        if (HasNotes)
        {
            text.Append("\n\n")
                .Append($"{NoteCount} app{Plural(NoteCount)} ha{(NoteCount == 1 ? "s" : "ve")} a note. Notes can hold licence keys; ")
                .Append("leave them out if you will share this file.");
        }

        return text.ToString();
    }

    private static string Plural(int count) => count == 1 ? string.Empty : "s";
}

/// <summary>Prepares a profile to move to another PC: a summary first, then the copy to write.</summary>
public static class ProfileExport
{
    public static ProfileExportSummary Summarize(AppProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var apps = profile.Apps ?? [];
        return new ProfileExportSummary(
            apps.Count,
            apps.Count(app => app.Enabled),
            apps.Where(app => app.Source == AppSource.LocalFile).Select(app => app.Name).ToList(),
            apps.Count(app => app.PostInstall is not null),
            apps.Count(app => app.HasNotes));
    }

    /// <summary>
    /// The copy that is written. Notes are removed unless the user chose to include them; nothing
    /// else changes, so the file imports exactly as the profile is on this PC. The profile in the app
    /// is never modified.
    /// </summary>
    public static AppProfile ForExport(AppProfile profile, bool includeNotes)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var copy = profile.Clone();
        if (!includeNotes)
        {
            foreach (var app in copy.Apps)
            {
                app.Notes = null;
            }
        }

        return copy;
    }
}
