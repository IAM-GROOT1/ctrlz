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

    public AppProfile Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Id = Id,
        Name = Name,
        Description = Description,
        CreatedUtc = CreatedUtc,
        UpdatedUtc = UpdatedUtc,
        Apps = Apps.Select(a => a.Clone()).ToList()
    };

    public void Touch() => UpdatedUtc = DateTimeOffset.UtcNow;

    /// <summary>Repairs harmless inconsistencies after import. Never silently drops apps.</summary>
    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        Name = string.IsNullOrWhiteSpace(Name) ? "Imported profile" : Name.Trim();
        Apps ??= new List<AppEntry>();
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
