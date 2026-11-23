// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;

namespace CtrlZ.Core.Tweaks;

public enum TweakState
{
    Off,
    On,

    /// <summary>Some of the tweak's values are set and others are not (changed outside CTRL+Z).</summary>
    Partly
}

/// <summary>What a value looked like before CTRL+Z changed it, so Undo can put it back exactly.</summary>
public sealed record TweakUndoValue(
    string SubKey,
    string Name,
    bool Existed,
    TweakValueKind Kind,
    string? Value,
    string DeepestExistingKey);

/// <summary>
/// Applies and undoes tweaks. Before the first change to a value it saves what was there
/// (state/tweak-undo.json), and Undo restores that exactly, deleting values and keys the tweak
/// created. Applying again never overwrites the saved original.
/// </summary>
public sealed class TweakService
{
    private readonly IUserRegistry _registry;
    private readonly string _undoPath;
    private readonly IAppLog _log;
    private readonly object _gate = new();

    public TweakService(IUserRegistry registry, string undoPath, IAppLog log)
    {
        _registry = registry;
        _undoPath = undoPath;
        _log = log;
    }

    public TweakState StateOf(TweakDefinition tweak)
    {
        ArgumentNullException.ThrowIfNull(tweak);
        var matching = tweak.Values.Count(value => Matches(_registry.Read(value.SubKey, value.Name), value));
        return matching == 0 ? TweakState.Off : matching == tweak.Values.Count ? TweakState.On : TweakState.Partly;
    }

    /// <summary>True when CTRL+Z holds the original values for this tweak, so Undo is exact.</summary>
    public bool CanUndo(TweakDefinition tweak)
    {
        lock (_gate)
        {
            return Load().ContainsKey(tweak.Id);
        }
    }

    public void Apply(TweakDefinition tweak)
    {
        ArgumentNullException.ThrowIfNull(tweak);
        lock (_gate)
        {
            var undo = Load();
            var saved = undo.TryGetValue(tweak.Id, out var existing) ? existing : [];

            // Catalog values with no saved original yet: all of them on a first Apply, or any the
            // catalog gained since an older entry was saved. Never overwrite an original already kept.
            var missing = tweak.Values.Where(value => FindOriginal(saved, value) is null).ToList();
            if (missing.Count > 0 || saved.Count != tweak.Values.Count)
            {
                // A value of a type CTRL+Z never writes could not be put back exactly: change nothing.
                if (missing.Any(value => _registry.Read(value.SubKey, value.Name).Unsupported))
                {
                    throw new InvalidOperationException(
                        $"\"{tweak.Title}\" was not changed: Windows holds this setting in a form CTRL+Z could not restore exactly. Change it in Windows Settings instead.");
                }

                // Record every original first, and save it, before anything is written. Saved
                // entries the catalog no longer has are dropped.
                undo[tweak.Id] = tweak.Values
                    .Select(value => FindOriginal(saved, value) ?? Capture(value))
                    .ToList();
                Save(undo);
            }

            foreach (var value in tweak.Values)
            {
                _registry.Write(value.SubKey, value.Name, value.Kind, value.Value);
            }
        }

        _log.Write(LogLevel.Info, $"Applied tweak {tweak.Id}.");
    }

    /// <summary>
    /// Restores the values saved before the tweak was first applied. Without a saved original
    /// (applied outside CTRL+Z), the tweak's values are removed, which returns Windows to its default.
    /// </summary>
    public void Undo(TweakDefinition tweak)
    {
        ArgumentNullException.ThrowIfNull(tweak);
        lock (_gate)
        {
            var undo = Load();
            var originals = undo.TryGetValue(tweak.Id, out var saved) ? saved : [];

            // Only the catalog says where to write. The saved file supplies the old value for each
            // catalog value, never a key or name of its own, so an edited file cannot reach any
            // other part of the registry.
            foreach (var value in tweak.Values)
            {
                var original = FindOriginal(originals, value);

                if (original is { Existed: true } && TryOriginalValue(original, out var kind, out var restored))
                {
                    _registry.Write(value.SubKey, value.Name, kind, restored);
                }
                else if (original is { Existed: false })
                {
                    _registry.Delete(value.SubKey, value.Name, KeepAbove(original.DeepestExistingKey, value));
                }
                else
                {
                    // No usable original (set outside CTRL+Z): remove the value. Keys stay unless the
                    // tweak owns them; Delete only ever removes empty ones.
                    _registry.Delete(value.SubKey, value.Name, value.OwnedBelow ?? value.SubKey);
                }
            }

            if (undo.Remove(tweak.Id))
            {
                Save(undo);
            }
        }

        _log.Write(LogLevel.Info, $"Undid tweak {tweak.Id}.");
    }

    /// <summary>The saved original for one catalog value; null-safe against a hand-edited file.</summary>
    private static TweakUndoValue? FindOriginal(IEnumerable<TweakUndoValue?> saved, TweakValue value) =>
        saved.FirstOrDefault(o =>
            o is not null &&
            string.Equals(o.SubKey, value.SubKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.Name, value.Name, StringComparison.OrdinalIgnoreCase));

    private static bool TryOriginalValue(TweakUndoValue original, out TweakValueKind kind, out object value)
    {
        kind = original.Kind;
        value = string.Empty;
        switch (original.Kind)
        {
            case TweakValueKind.DWord when int.TryParse(original.Value, out var number):
                value = number;
                return true;
            case TweakValueKind.String:
                value = original.Value ?? string.Empty;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The saved deepest key, if it is the value's own key or one of its parents; else the value's key.</summary>
    private static string KeepAbove(string? saved, TweakValue value)
    {
        if (!string.IsNullOrWhiteSpace(saved) && TweakCatalog.IsAllowedSubKey(saved) &&
            (saved.Equals(value.SubKey, StringComparison.OrdinalIgnoreCase) ||
             value.SubKey.StartsWith(saved + @"\", StringComparison.OrdinalIgnoreCase)))
        {
            return saved;
        }

        return value.SubKey;
    }

    private TweakUndoValue Capture(TweakValue value)
    {
        var current = _registry.Read(value.SubKey, value.Name);
        return new TweakUndoValue(value.SubKey, value.Name, current.Exists, current.Kind,
            current.Value?.ToString(), DeepestExistingKey(value.SubKey));
    }

    private string DeepestExistingKey(string subKey)
    {
        var path = subKey;
        while (path.Length > 0 && TweakCatalog.IsAllowedSubKey(path) && !_registry.KeyExists(path))
        {
            var index = path.LastIndexOf('\\');
            path = index <= 0 ? string.Empty : path[..index];
        }

        return path;
    }

    private static bool Matches(RegistrySnapshot current, TweakValue wanted) =>
        current.Exists && current.Kind == wanted.Kind &&
        string.Equals(current.Value?.ToString(), wanted.Value.ToString(), StringComparison.Ordinal);

    private Dictionary<string, List<TweakUndoValue>> Load()
    {
        try
        {
            return File.Exists(_undoPath)
                ? JsonSerializer.Deserialize<Dictionary<string, List<TweakUndoValue>>>(File.ReadAllText(_undoPath), JsonDefaults.Profile)
                  ?? new Dictionary<string, List<TweakUndoValue>>()
                : new Dictionary<string, List<TweakUndoValue>>();
        }
        catch (JsonException)
        {
            // A damaged record must not be overwritten silently: keep it aside and start fresh.
            File.Copy(_undoPath, _undoPath + ".damaged", overwrite: true);
            return new Dictionary<string, List<TweakUndoValue>>();
        }
    }

    private void Save(Dictionary<string, List<TweakUndoValue>> undo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_undoPath)!);
        FileUtil.AtomicWriteAllText(_undoPath, JsonSerializer.Serialize(undo, JsonDefaults.Profile));
    }
}
