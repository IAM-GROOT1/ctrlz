// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using Microsoft.Win32;

namespace CtrlZ.Core.Tweaks;

/// <summary>A registry value as it was read: absent, or present with a kind and value.</summary>
public sealed record RegistrySnapshot(bool Exists, TweakValueKind Kind, object? Value, bool Unsupported = false)
{
    public static readonly RegistrySnapshot Absent = new(false, TweakValueKind.DWord, null);
}

/// <summary>The current user's registry hive, and nothing else. Replaceable in tests.</summary>
public interface IUserRegistry
{
    RegistrySnapshot Read(string subKey, string name);

    bool KeyExists(string subKey);

    void Write(string subKey, string name, TweakValueKind kind, object value);

    /// <summary>Removes the value, then any keys left empty up to (not including) <paramref name="keepAbove"/>.</summary>
    void Delete(string subKey, string name, string keepAbove);
}

/// <summary>HKEY_CURRENT_USER through Microsoft.Win32. Every path is checked before it is opened.</summary>
public sealed class WindowsUserRegistry : IUserRegistry
{
    public RegistrySnapshot Read(string subKey, string name)
    {
        Guard(subKey);
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value)
        {
            return RegistrySnapshot.Absent;
        }

        return key.GetValueKind(name) switch
        {
            RegistryValueKind.DWord => new RegistrySnapshot(true, TweakValueKind.DWord, Convert.ToInt32(value)),
            RegistryValueKind.String => new RegistrySnapshot(true, TweakValueKind.String, value.ToString()),
            // A kind CTRL+Z never writes (binary, QWORD, multi-string...): it could not be restored exactly.
            _ => new RegistrySnapshot(true, TweakValueKind.String, null, Unsupported: true)
        };
    }

    public bool KeyExists(string subKey)
    {
        Guard(subKey);
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key is not null;
    }

    public void Write(string subKey, string name, TweakValueKind kind, object value)
    {
        Guard(subKey);
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true)
                        ?? throw new InvalidOperationException("The registry key could not be opened.");
        key.SetValue(name, kind == TweakValueKind.DWord ? Convert.ToInt32(value) : value.ToString()!,
            kind == TweakValueKind.DWord ? RegistryValueKind.DWord : RegistryValueKind.String);
    }

    public void Delete(string subKey, string name, string keepAbove)
    {
        Guard(subKey);
        using (var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true))
        {
            key?.DeleteValue(name, throwOnMissingValue: false);
        }

        // Remove keys this tweak created, leaf first, stopping at the first one that still holds anything.
        var path = subKey;
        while (path.Length > keepAbove.Length && path.StartsWith(keepAbove, StringComparison.OrdinalIgnoreCase))
        {
            using (var key = Registry.CurrentUser.OpenSubKey(path, writable: false))
            {
                if (key is null)
                {
                    path = Parent(path);
                    continue;
                }

                if (key.ValueCount > 0 || key.SubKeyCount > 0)
                {
                    return;
                }
            }

            Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
            path = Parent(path);
        }
    }

    private static string Parent(string path)
    {
        var index = path.LastIndexOf('\\');
        return index <= 0 ? string.Empty : path[..index];
    }

    private static void Guard(string subKey)
    {
        if (!TweakCatalog.IsAllowedSubKey(subKey))
        {
            throw new ArgumentException("Only keys under HKEY_CURRENT_USER\\Software can be changed.", nameof(subKey));
        }
    }
}
