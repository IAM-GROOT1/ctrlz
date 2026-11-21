// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using Microsoft.Win32;

namespace CtrlZ.Core.Safety;

/// <summary>A launcher or game library found on this PC.</summary>
public sealed record GameLibrary(
    string Name,
    bool Detected,
    string? InstallPath,
    bool HasLibraryFiles,
    IReadOnlyList<string> SavePaths);

/// <summary>
/// Detects the usual game launchers and the folders where saves usually live. It only
/// checks existence: nothing is opened, and no game library is ever backed up by CTRL+Z.
/// </summary>
public sealed class GameLibraryProbe
{
    private readonly Func<string?> _steamRegistryPath;
    private readonly Func<string?> _gogRegistryPath;

    public GameLibraryProbe(Func<string?>? steamInstallPath = null, Func<string?>? gogInstallPath = null)
    {
        _steamRegistryPath = steamInstallPath ?? ReadSteamInstallPath;
        _gogRegistryPath = gogInstallPath ?? ReadGogInstallPath;
    }

    public IReadOnlyList<GameLibrary> Detect(SafetyEnvironment env, CancellationToken cancellationToken = default)
    {
        var common = CommonSaveRoots(env);

        return
        [
            DetectSteam(env, common, cancellationToken),
            DetectEpic(env, common, cancellationToken),
            DetectGog(env, common, cancellationToken),
            DetectBattleNet(env, common, cancellationToken)
        ];
    }

    private GameLibrary DetectSteam(SafetyEnvironment env, string[] common, CancellationToken cancellationToken)
    {
        var install = FirstExisting(
            SafeRegistry(_steamRegistryPath),
            Combine(env.ProgramFilesX86, "Steam"),
            Combine(env.ProgramFiles, "Steam"));

        var saves = new List<string>();
        if (install is not null)
        {
            saves.AddIfExisting(Combine(install, "userdata"));
            saves.AddIfExisting(Combine(install, "steamapps", "common", "Steamworks Shared"));
        }

        saves.AddRange(common);
        return Build("Steam", install, install is not null && SafeDirectoryExists(Combine(install, "steamapps")), saves, cancellationToken);
    }

    private GameLibrary DetectEpic(SafetyEnvironment env, string[] common, CancellationToken cancellationToken)
    {
        var install = FirstExisting(
            Combine(env.ProgramData, "Epic", "UnrealEngineLauncher"),
            Combine(env.LocalAppData, "EpicGamesLauncher"),
            Combine(env.ProgramFilesX86, "Epic Games"));

        var saves = new List<string>();
        saves.AddIfExisting(Combine(env.LocalAppData, "EpicGamesLauncher", "Saved"));
        saves.AddRange(common);
        return Build("Epic Games", install, install is not null, saves, cancellationToken);
    }

    private GameLibrary DetectGog(SafetyEnvironment env, string[] common, CancellationToken cancellationToken)
    {
        var install = FirstExisting(
            SafeRegistry(_gogRegistryPath),
            Combine(env.ProgramFilesX86, "GOG Galaxy"),
            Combine(env.ProgramFiles, "GOG Galaxy"));

        var saves = new List<string>();
        saves.AddIfExisting(Combine(env.ProgramData, "GOG.com"));
        saves.AddRange(common);
        return Build("GOG", install, install is not null, saves, cancellationToken);
    }

    private GameLibrary DetectBattleNet(SafetyEnvironment env, string[] common, CancellationToken cancellationToken)
    {
        var install = FirstExisting(
            Combine(env.ProgramData, "Battle.net"),
            Combine(env.ProgramFilesX86, "Battle.net"),
            Combine(env.ProgramFiles, "Battle.net"));

        var saves = new List<string>();
        saves.AddIfExisting(Combine(env.ProgramData, "Blizzard Entertainment"));
        saves.AddRange(common);
        return Build("Battle.net", install, install is not null, saves, cancellationToken);
    }

    private static GameLibrary Build(
        string name,
        string? installPath,
        bool hasLibraryFiles,
        IEnumerable<string> savePaths,
        CancellationToken cancellationToken)
    {
        var saves = new List<string>();
        foreach (var path in savePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(path) && !saves.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                saves.Add(path);
            }
        }

        return new GameLibrary(name, installPath is not null, installPath, hasLibraryFiles, saves);
    }

    /// <summary>Folders where most Windows games keep their saves, used by every launcher.</summary>
    private static string[] CommonSaveRoots(SafetyEnvironment env)
    {
        var roots = new List<string>();
        roots.AddIfExisting(Combine(env.Documents, "My Games"));
        roots.AddIfExisting(env.SavedGames);
        return roots.ToArray();
    }

    private static string? FirstExisting(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && SafeDirectoryExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? SafeRegistry(Func<string?> read)
    {
        try
        {
            var value = read();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }
        catch
        {
            return false;
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

    private static string? ReadSteamInstallPath()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            return key?.GetValue("InstallPath") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadGogInstallPath()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            return key?.GetValue("path") as string;
        }
        catch
        {
            return null;
        }
    }
}

internal static class SavePathExtensions
{
    public static void AddIfExisting(this List<string> paths, string candidate)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                Directory.Exists(candidate) &&
                !paths.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(candidate);
            }
        }
        catch
        {
            // Unreadable candidate: simply not reported.
        }
    }
}
