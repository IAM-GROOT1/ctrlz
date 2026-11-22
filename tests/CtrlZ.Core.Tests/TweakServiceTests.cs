// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;
using CtrlZ.Core.Services;
using CtrlZ.Core.Tweaks;

namespace CtrlZ.Core.Tests;

public sealed class TweakServiceTests : IDisposable
{
    private readonly TestDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>An in-memory HKCU: keys and their values, case-insensitive like the real one.</summary>
    private sealed class FakeRegistry : IUserRegistry
    {
        public Dictionary<string, Dictionary<string, (TweakValueKind Kind, object Value, bool Unsupported)>> Keys { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public RegistrySnapshot Read(string subKey, string name)
        {
            Assert.True(TweakCatalog.IsAllowedSubKey(subKey));
            return Keys.TryGetValue(subKey, out var values) && values.TryGetValue(name, out var value)
                ? new RegistrySnapshot(true, value.Kind, value.Unsupported ? null : value.Value, value.Unsupported)
                : RegistrySnapshot.Absent;
        }

        public bool KeyExists(string subKey) => Keys.ContainsKey(subKey);

        public void Write(string subKey, string name, TweakValueKind kind, object value)
        {
            Assert.True(TweakCatalog.IsAllowedSubKey(subKey));
            var path = subKey;
            while (path.Contains('\\'))
            {
                Keys.TryAdd(path, new Dictionary<string, (TweakValueKind, object, bool)>(StringComparer.OrdinalIgnoreCase));
                path = path[..path.LastIndexOf('\\')];
            }

            Keys[subKey][name] = (kind, value, false);
        }

        public void Delete(string subKey, string name, string keepAbove)
        {
            if (Keys.TryGetValue(subKey, out var values))
            {
                values.Remove(name);
            }

            var path = subKey;
            while (path.Length > keepAbove.Length && path.StartsWith(keepAbove, StringComparison.OrdinalIgnoreCase))
            {
                var hasChildren = Keys.Keys.Any(key => key.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase));
                if (Keys.TryGetValue(path, out var held) && (held.Count > 0 || hasChildren))
                {
                    return;
                }

                Keys.Remove(path);
                path = path[..path.LastIndexOf('\\')];
            }
        }

        public void Seed(string subKey, string name, TweakValueKind kind, object value) => Write(subKey, name, kind, value);
    }

    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private TweakService Service(FakeRegistry registry) =>
        new(registry, _temp.PathOf(Path.Combine("state", "tweak-undo.json")), TestFactory.Log);

    private static TweakDefinition Tweak(string id) => TweakCatalog.Find(id)!;

    [Fact]
    public void Apply_then_undo_restores_the_previous_value_exactly()
    {
        var registry = new FakeRegistry();
        registry.Seed(Advanced, "HideFileExt", TweakValueKind.DWord, 1);
        var service = Service(registry);

        Assert.Equal(TweakState.Off, service.StateOf(Tweak("show-file-extensions")));
        service.Apply(Tweak("show-file-extensions"));
        Assert.Equal(TweakState.On, service.StateOf(Tweak("show-file-extensions")));
        Assert.True(service.CanUndo(Tweak("show-file-extensions")));

        service.Undo(Tweak("show-file-extensions"));

        Assert.Equal(1, registry.Read(Advanced, "HideFileExt").Value);
        Assert.False(service.CanUndo(Tweak("show-file-extensions")));
    }

    [Fact]
    public void A_value_that_did_not_exist_is_removed_on_undo_along_with_keys_the_tweak_created()
    {
        var registry = new FakeRegistry();
        registry.Seed(@"Software\Classes\CLSID\{other}", "x", TweakValueKind.DWord, 1);
        var tweak = Tweak("classic-context-menu");
        var service = Service(registry);

        service.Apply(tweak);
        Assert.True(registry.KeyExists(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32"));

        service.Undo(tweak);

        Assert.False(registry.KeyExists(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"));
        Assert.True(registry.KeyExists(@"Software\Classes\CLSID"));
        Assert.True(registry.KeyExists(@"Software\Classes\CLSID\{other}"));
    }

    [Fact]
    public void Applying_twice_keeps_the_first_original()
    {
        var registry = new FakeRegistry();
        registry.Seed(Advanced, "LaunchTo", TweakValueKind.DWord, 2);
        var service = Service(registry);
        var tweak = Tweak("explorer-opens-this-pc");

        service.Apply(tweak);
        service.Apply(tweak);
        service.Undo(tweak);

        Assert.Equal(2, registry.Read(Advanced, "LaunchTo").Value);
    }

    [Fact]
    public void A_tweak_with_two_values_reports_partly_on()
    {
        var registry = new FakeRegistry();
        registry.Seed(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", TweakValueKind.DWord, 0);

        Assert.Equal(TweakState.Partly, Service(registry).StateOf(Tweak("dark-mode")));
    }

    [Fact]
    public void A_value_of_another_type_is_never_overwritten()
    {
        var registry = new FakeRegistry();
        registry.Seed(Advanced, "Hidden", TweakValueKind.DWord, 2);
        registry.Keys[Advanced]["Hidden"] = (TweakValueKind.String, string.Empty, true);
        var service = Service(registry);

        var ex = Assert.Throws<InvalidOperationException>(() => service.Apply(Tweak("show-hidden-files")));

        Assert.Contains("was not changed", ex.Message);
        Assert.True(registry.Read(Advanced, "Hidden").Unsupported);
        Assert.False(service.CanUndo(Tweak("show-hidden-files")));
    }

    [Fact]
    public void The_original_survives_a_restart_of_the_app()
    {
        var registry = new FakeRegistry();
        registry.Seed(Advanced, "HideFileExt", TweakValueKind.DWord, 1);
        Service(registry).Apply(Tweak("show-file-extensions"));

        Service(registry).Undo(Tweak("show-file-extensions"));

        Assert.Equal(1, registry.Read(Advanced, "HideFileExt").Value);
    }

    [Fact]
    public void Undo_without_a_saved_original_returns_the_setting_to_the_windows_default()
    {
        var registry = new FakeRegistry();
        registry.Seed(Advanced, "HideFileExt", TweakValueKind.DWord, 0);

        Service(registry).Undo(Tweak("show-file-extensions"));

        Assert.False(registry.Read(Advanced, "HideFileExt").Exists);
        Assert.True(registry.KeyExists(Advanced));
    }

    [Fact]
    public void Every_tweak_stays_inside_the_users_software_hive()
    {
        Assert.Equal(TweakCatalog.All.Count, TweakCatalog.All.Select(t => t.Id).Distinct().Count());
        Assert.All(TweakCatalog.All.SelectMany(t => t.Values), value => Assert.True(TweakCatalog.IsAllowedSubKey(value.SubKey)));
    }

    [Theory]
    [InlineData(@"SYSTEM\CurrentControlSet")]
    [InlineData(@"\Software\x")]
    [InlineData(@"Software\..\SYSTEM")]
    [InlineData(@"C:\Software")]
    [InlineData("")]
    public void Keys_outside_software_are_refused(string subKey)
    {
        Assert.False(TweakCatalog.IsAllowedSubKey(subKey));
        Assert.Throws<ArgumentException>(() => new WindowsUserRegistry().Read(subKey, "x"));
    }

    [Fact]
    public void The_real_registry_round_trip_removes_only_the_keys_it_created()
    {
        // A throwaway key under the current user's Software hive, removed at the end whatever happens.
        var root = @"Software\CtrlZ.Tests\" + Guid.NewGuid().ToString("n");
        var registry = new WindowsUserRegistry();
        try
        {
            registry.Write(root + @"\Keep", "marker", TweakValueKind.DWord, 1);
            var deep = root + @"\Made\By\Tweak";

            registry.Write(deep, "Value", TweakValueKind.DWord, 7);
            registry.Write(deep, string.Empty, TweakValueKind.String, string.Empty);
            Assert.Equal(7, registry.Read(deep, "Value").Value);
            Assert.Equal(string.Empty, registry.Read(deep, string.Empty).Value);

            registry.Delete(deep, "Value", root);
            Assert.True(registry.KeyExists(deep));
            registry.Delete(deep, string.Empty, root);

            Assert.False(registry.KeyExists(root + @"\Made"));
            Assert.True(registry.KeyExists(root + @"\Keep"));
            Assert.True(registry.KeyExists(root));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            using var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\CtrlZ.Tests");
            if (parent is { SubKeyCount: 0, ValueCount: 0 })
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(@"Software\CtrlZ.Tests", throwOnMissingSubKey: false);
            }
        }
    }

    [Fact]
    public void Profiles_keep_tweak_ids_and_drop_blanks_and_repeats()
    {
        using var temp = new TestDirectory();
        var paths = new CtrlZ.Core.Infrastructure.AppPaths(temp.PathOf("data"));
        paths.EnsureCreated();
        var store = new ProfileStore(paths, TestFactory.Log);
        var file = temp.PathOf("p.json");

        store.SaveAs(new AppProfile { Tweaks = ["dark-mode", "DARK-MODE", " ", "from-a-newer-version"] }, file);
        var loaded = store.Load(file);
        store.SaveAs(new AppProfile(), temp.PathOf("plain.json"));

        Assert.Equal(["dark-mode", "from-a-newer-version"], loaded.Tweaks);
        Assert.DoesNotContain("tweaks", File.ReadAllText(temp.PathOf("plain.json")));
        Assert.Equal(loaded.Tweaks, loaded.Clone().Tweaks);
        Assert.NotSame(loaded.Tweaks, loaded.Clone().Tweaks);
    }
}
