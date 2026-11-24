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
    public void Arabic_lays_windows_out_right_to_left_with_its_own_plural_forms()
    {
        var before = CultureInfo.CurrentUICulture;
        var beforeDefault = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            Assert.Equal("reg.exe /s a.reg", CoreText.LeftToRight("reg.exe /s a.reg"));
            Assert.Equal("Backup is 60 days old", CoreText.Plural("ReadinessScanner_BackupIsOld", 60));
            Assert.Equal(System.Windows.FlowDirection.LeftToRight, AppLanguage.FlowDirection);
            AppLanguage.Apply("ar");
            Assert.Equal(System.Windows.FlowDirection.RightToLeft, AppLanguage.FlowDirection);
            Assert.Equal("اللغة", Loc.Get("Settings_Language"));
            Assert.Equal("1 تطبيق", Loc.Plural("MyApps_AppCount", 1));
            Assert.Equal("2 تطبيقان", Loc.Plural("MyApps_AppCount", 2));
            Assert.Equal("3 تطبيقات", Loc.Plural("MyApps_AppCount", 3));
            Assert.Equal("11 تطبيقًا", Loc.Plural("MyApps_AppCount", 11));
            Assert.Equal("100 تطبيق", Loc.Plural("MyApps_AppCount", 100));
            // No Zero form is written, so 0 uses Other rather than falling back to English.
            Assert.Equal("0 تطبيق", Loc.Plural("MyApps_AppCount", 0));
            Assert.Equal("العمر: 5 أيام", CoreText.Plural("ReadinessScanner_AgeDays", 5));
            Assert.Equal("منذ 2 يومين", CoreText.Plural("ReadinessScanner_DaysAgo", 2));
            // Risk labels are looked up per scan, so they follow the language chosen at startup.
            Assert.Equal(["يُضعف إعدادات الأمان"], CtrlZ.Core.Install.PostInstallCommandRisk.Scan(
                new CtrlZ.Core.Models.PostInstallCommand { Executable = "netsh.exe", Arguments = ["advfirewall", "set", "allprofiles", "state", "off"], Enabled = true }));
            // Commands inside a right-to-left sentence keep their own order.
            Assert.Equal("⁦reg.exe /s a.reg⁩", CoreText.LeftToRight("reg.exe /s a.reg"));
        }
        finally
        {
            CoreText.Culture = null;
            CultureInfo.CurrentUICulture = before;
            CultureInfo.DefaultThreadCurrentUICulture = beforeDefault;
        }
    }

    [Fact]
    public void An_untranslated_right_to_left_Windows_language_keeps_English_left_to_right()
    {
        try
        {
            CoreText.Culture = CultureInfo.GetCultureInfo("he-IL");
            Assert.Equal(System.Windows.FlowDirection.LeftToRight, AppLanguage.FlowDirection);
            CoreText.Culture = CultureInfo.GetCultureInfo("ar-EG");
            Assert.Equal(System.Windows.FlowDirection.RightToLeft, AppLanguage.FlowDirection);
        }
        finally
        {
            CoreText.Culture = null;
        }
    }

    [Fact]
    public void Spanish_reads_its_own_text_and_stays_left_to_right()
    {
        var before = CultureInfo.CurrentUICulture;
        var beforeDefault = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            AppLanguage.Apply("es");
            Assert.Equal(System.Windows.FlowDirection.LeftToRight, AppLanguage.FlowDirection);
            Assert.Equal("Idioma", Loc.Get("Settings_Language"));
            Assert.Equal("1 aplicación", Loc.Plural("MyApps_AppCount", 1));
            Assert.Equal("3 aplicaciones", Loc.Plural("MyApps_AppCount", 3));
            Assert.Equal("hace 4 días", CoreText.Plural("ReadinessScanner_DaysAgo", 4));
            Assert.Equal("Mostrar las extensiones de archivo", CtrlZ.Core.Tweaks.TweakCatalog.All[0].Title);
        }
        finally
        {
            CoreText.Culture = null;
            CultureInfo.CurrentUICulture = before;
            CultureInfo.DefaultThreadCurrentUICulture = beforeDefault;
        }
    }

    [Fact]
    public void Russian_reads_its_own_text_with_three_plural_forms()
    {
        var before = CultureInfo.CurrentUICulture;
        var beforeDefault = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            AppLanguage.Apply("ru");
            Assert.Equal(System.Windows.FlowDirection.LeftToRight, AppLanguage.FlowDirection);
            Assert.Equal("Язык", Loc.Get("Settings_Language"));
            Assert.Equal("1 приложение", Loc.Plural("MyApps_AppCount", 1));
            Assert.Equal("3 приложения", Loc.Plural("MyApps_AppCount", 3));
            Assert.Equal("5 приложений", Loc.Plural("MyApps_AppCount", 5));
            Assert.Equal("21 приложение", Loc.Plural("MyApps_AppCount", 21));
            Assert.Equal("11 дней назад", CoreText.Plural("ReadinessScanner_DaysAgo", 11));
            Assert.Equal("Показывать расширения файлов", CtrlZ.Core.Tweaks.TweakCatalog.All[0].Title);
        }
        finally
        {
            CoreText.Culture = null;
            CultureInfo.CurrentUICulture = before;
            CultureInfo.DefaultThreadCurrentUICulture = beforeDefault;
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
