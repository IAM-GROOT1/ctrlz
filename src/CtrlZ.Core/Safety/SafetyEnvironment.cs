// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Safety;

/// <summary>
/// The machine locations a readiness scan needs, captured once so every probe reads the
/// same view and tests can point at a temporary tree instead of the real user profile.
/// </summary>
public sealed record SafetyEnvironment
{
    public required string HomeDirectory { get; init; }
    public required string AppData { get; init; }
    public required string LocalAppData { get; init; }
    public required string ProgramData { get; init; }
    public required string ProgramFiles { get; init; }
    public required string ProgramFilesX86 { get; init; }
    public required string Documents { get; init; }
    public required string SavedGames { get; init; }

    /// <summary>Ready fixed drives, including the drive holding Windows. Empty when unknown.</summary>
    public IReadOnlyList<string> FixedDrives { get; init; } = [];

    public static SafetyEnvironment Detect()
    {
        var home = SafeFolder(Environment.SpecialFolder.UserProfile);
        return new SafetyEnvironment
        {
            HomeDirectory = home,
            AppData = SafeFolder(Environment.SpecialFolder.ApplicationData),
            LocalAppData = SafeFolder(Environment.SpecialFolder.LocalApplicationData),
            ProgramData = SafeFolder(Environment.SpecialFolder.CommonApplicationData),
            ProgramFiles = SafeFolder(Environment.SpecialFolder.ProgramFiles),
            ProgramFilesX86 = SafeFolder(Environment.SpecialFolder.ProgramFilesX86),
            Documents = SafeFolder(Environment.SpecialFolder.MyDocuments),
            // .NET has no SavedGames special folder; it is a fixed child of the profile.
            SavedGames = Combine(home, "Saved Games"),
            FixedDrives = DetectFixedDrives()
        };
    }

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Combine(string root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        try
        {
            return Path.Combine(new[] { root }.Concat(parts).ToArray());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<string> DetectFixedDrives()
    {
        var drives = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                    {
                        drives.Add(drive.RootDirectory.FullName);
                    }
                }
                catch
                {
                    // A drive that disappears mid-enumeration is simply skipped.
                }
            }
        }
        catch
        {
            // No drive information available; the folder checks fall back to the known ones.
        }

        return drives;
    }
}
