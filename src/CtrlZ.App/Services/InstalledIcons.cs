// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Logging;
using CtrlZ.Core.Services;

namespace CtrlZ.App.Services;

/// <summary>
/// The icons of programs installed on this PC, by name, so an app added from the catalog, typed in
/// or imported from a profile shows its real icon once it is installed here. Read once in the
/// background from the same Add/Remove Programs entries the Scan page uses; nothing leaves the PC.
/// </summary>
public static class InstalledIcons
{
    private static IReadOnlyDictionary<string, string>? _byName;
    private static int _started;

    /// <summary>Raised (on a background thread) once the list has been read.</summary>
    public static event EventHandler? Loaded;

    public static void StartLoading(IAppLog log)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Use(new RegistryAppScanner(log).Scan().Select(app => (app.Name, app.IconPath)));
            }
            catch (Exception ex)
            {
                log.Write(LogLevel.Warning, "Could not read installed programs for icons.", ex);
            }
        });
    }

    /// <summary>Replaces the list (startup, and tests).</summary>
    public static void Use(IEnumerable<(string Name, string? IconPath)> programs)
    {
        var withIcons = programs.Where(p => !string.IsNullOrWhiteSpace(p.IconPath)).ToList();
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        // Exact and version-free names first, so they win over the shorter form added after
        // ("Mozilla Firefox" also answers to "Firefox", unless a program is called just that).
        foreach (var (name, icon) in withIcons)
        {
            foreach (var key in BrandIcons.NameKeys(name, dropFirstWord: false))
            {
                byName.TryAdd(key, icon!);
            }
        }

        foreach (var (name, icon) in withIcons)
        {
            foreach (var key in BrandIcons.NameKeys(name))
            {
                byName.TryAdd(key, icon!);
            }
        }

        _byName = byName;
        Loaded?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The icon file of an installed program with this name, or null.</summary>
    public static string? Find(string? name)
    {
        var byName = _byName;
        if (byName is null)
        {
            return null;
        }

        foreach (var key in BrandIcons.NameKeys(name))
        {
            if (byName.TryGetValue(key, out var icon))
            {
                return icon;
            }
        }

        return null;
    }
}
