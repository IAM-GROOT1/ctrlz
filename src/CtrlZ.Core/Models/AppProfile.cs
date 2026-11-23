// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Models;

/// <summary>
/// Portable profile document. It deliberately contains no machine identifiers and no
/// transient state; copying the JSON between PCs is enough to recreate a setup.
/// </summary>
public sealed class AppProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "My apps";
    public string? Description { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<AppEntry> Apps { get; set; } = new();

    /// <summary>
    /// Ids of Windows tweaks chosen with this profile (see <c>TweakCatalog</c>), so they can be offered
    /// again after a reinstall. Only ids: what each one changes is fixed in CTRL+Z, never read from the
    /// file, and nothing is applied until the user asks. Null when there are none, so older profiles
    /// are written unchanged.
    /// </summary>
    public List<string>? Tweaks { get; set; }

    public AppProfile Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Id = Id,
        Name = Name,
        Description = Description,
        CreatedUtc = CreatedUtc,
        UpdatedUtc = UpdatedUtc,
        Apps = Apps.Select(a => a.Clone()).ToList(),
        Tweaks = Tweaks?.ToList()
    };

    public void Touch() => UpdatedUtc = DateTimeOffset.UtcNow;

    /// <summary>Repairs harmless inconsistencies after import. Never silently drops apps.</summary>
    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        Name = string.IsNullOrWhiteSpace(Name) ? "Imported profile" : Name.Trim();
        Apps ??= new List<AppEntry>();
        // Unknown ids are kept (a newer CTRL+Z may know them); blanks and repeats are not.
        Tweaks = Tweaks?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() is { Count: > 0 } tweaks ? tweaks : null;
        foreach (var app in Apps)
        {
            app.Id = string.IsNullOrWhiteSpace(app.Id) ? Guid.NewGuid().ToString("n") : app.Id;
            app.Name = app.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(app.SilentArgs))
            {
                app.SilentArgs = null;
            }
        }
    }
}

public sealed record ProfileSummary(string Path, string Id, string Name, int AppCount, DateTimeOffset UpdatedUtc)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}
