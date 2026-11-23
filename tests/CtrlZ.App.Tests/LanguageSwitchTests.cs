// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Globalization;
using CtrlZ.App.Localization;
using CtrlZ.Core.Localization;
using Xunit;

namespace CtrlZ.App.Tests;

/// <summary>Switching the language changes process-wide state, so nothing else runs alongside.</summary>
[CollectionDefinition(nameof(LanguageSwitchTests), DisableParallelization = true)]
public sealed class LanguageSwitchCollection;

[Collection(nameof(LanguageSwitchTests))]
public class LanguageSwitchTests
{
    [Fact]
    public void A_supported_language_applies_and_reads_its_own_text()
    {
        var before = CultureInfo.CurrentUICulture;
        var beforeDefault = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            Assert.Equal(AppLanguage.Supported.Count + 1, AppLanguage.Options().Count);
            Assert.Equal("de", AppLanguage.Normalize("DE"));
            Assert.Null(AppLanguage.Normalize("xx"));
            Assert.Null(AppLanguage.Normalize(" "));

            AppLanguage.Apply("de");
            Assert.Equal("de", CultureInfo.CurrentUICulture.Name);
            Assert.Equal("Sprache", Loc.Get("Settings_Language"));
            Assert.Equal("1 App", Loc.Plural("MyApps_AppCount", 1));
            Assert.Equal("3 Apps", Loc.Plural("MyApps_AppCount", 3));
            Assert.Equal("Alter: 2 Tage", CoreText.Plural("ReadinessScanner_AgeDays", 2));
            Assert.Equal("Dateierweiterungen anzeigen", CtrlZ.Core.Tweaks.TweakCatalog.All[0].Title);
        }
        finally
        {
            CoreText.Culture = null;
            CultureInfo.CurrentUICulture = before;
            CultureInfo.DefaultThreadCurrentUICulture = beforeDefault;
        }
    }

    [Fact]
    public async Task The_language_stays_after_an_async_method_that_set_it_returns()
    {
        // .NET restores CurrentUICulture when an async method returns; the app's startup is one.
        var before = CultureInfo.CurrentUICulture;
        var beforeDefault = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            await ApplyInsideAsync();
            Assert.Equal("Sprache", Loc.Get("Settings_Language"));
            Assert.Equal("Abbrechen", Loc.Get("DialogService_Cancel"));
        }
        finally
        {
            CoreText.Culture = null;
            CultureInfo.CurrentUICulture = before;
            CultureInfo.DefaultThreadCurrentUICulture = beforeDefault;
        }

        static async Task ApplyInsideAsync()
        {
            await Task.Yield();
            AppLanguage.Apply("de");
        }
    }

    [Fact]
    public void An_unknown_language_leaves_the_current_one_alone()
    {
        var before = CultureInfo.CurrentUICulture;
        AppLanguage.Apply("xx");
        AppLanguage.Apply(null);
        Assert.Equal(before, CultureInfo.CurrentUICulture);
        Assert.Null(CoreText.Culture);
    }
}
