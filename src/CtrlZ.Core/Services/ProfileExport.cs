// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
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
            .Append(CoreText.Plural("ProfileExport_AppsSaved", AppCount, EnabledCount))
            .Append(CoreText.Get("ProfileExport_OnTheOtherPCOpen"));

        if (LocalInstallers.Count > 0)
        {
            text.Append("\n\n")
                .Append(CoreText.Plural(
                    "ProfileExport_LocalInstallers",
                    LocalInstallers.Count,
                    string.Join(", ", LocalInstallers.Take(5)) + (LocalInstallers.Count > 5 ? ", …" : string.Empty)));
        }

        if (CommandCount > 0)
        {
            text.Append("\n\n")
                .Append(CoreText.Plural("ProfileExport_AppsWithCommand", CommandCount))
                .Append(CoreText.Get("ProfileExport_OnTheOtherPCThey"));
        }

        if (HasNotes)
        {
            text.Append("\n\n")
                .Append(CoreText.Plural("ProfileExport_AppsWithNote", NoteCount))
                .Append(CoreText.Get("ProfileExport_LeaveThemOutIfYou"));
        }

        return text.ToString();
    }

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
