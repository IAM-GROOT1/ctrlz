// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Globalization;
using System.IO;
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
            Assert.Equal("‎reg.exe /s a.reg‎", CoreText.LeftToRight("reg.exe /s a.reg"));
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
    public void An_update_failure_is_shown_in_the_users_language_while_the_log_keeps_english()
    {
        var before = CultureInfo.CurrentUICulture;
        var beforeDefault = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            AppLanguage.Apply("de");
            var error = CtrlZ.Core.Updates.UpdateApplyResult.Failed(
                "The installer reported an error (code 5). Your previous version is still installed.",
                "UpdateApply_InstallerError", "5");
            Assert.Equal(
                "Das Installationsprogramm hat einen Fehler gemeldet (Code 5). Ihre bisherige Version ist weiterhin installiert.",
                error.DisplayMessage);
            Assert.StartsWith("The installer", error.Message);

            // The specific reason is kept after the translated sentence.
            Assert.Equal("Das Update konnte nicht angewendet werden: hash mismatch",
                CtrlZ.Core.Updates.UpdateApplyResult.Failed(
                    "The update could not be applied: hash mismatch", "UpdateApply_CouldNotApply", "hash mismatch").DisplayMessage);

            // A key the helper never writes (the result file can be edited) shows the English text.
            Assert.Equal("English", CtrlZ.Core.Updates.UpdateApplyResult.Failed("English", "Settings_Language").DisplayMessage);
            Assert.Equal("English", CtrlZ.Core.Updates.UpdateApplyResult.Failed("English").DisplayMessage);

            // Too few values for the placeholders falls back to the English text instead of throwing.
            Assert.Equal("English", CtrlZ.Core.Updates.UpdateApplyResult.Failed("English", "UpdateApply_InstallerError").DisplayMessage);

            Assert.Equal("Das Update wurde abgebrochen. Es wurde nichts geändert.",
                CtrlZ.Core.Updates.UpdateFailureMessages.ForCancellationDisplay(userRequested: true));
            Assert.Equal("The update was cancelled. Nothing was changed.",
                CtrlZ.Core.Updates.UpdateFailureMessages.ForCancellation(userRequested: true));
            Assert.Equal("GitHub ist nicht erreichbar. Prüfen Sie Ihre Verbindung und versuchen Sie es erneut.",
                new CtrlZ.Core.Updates.UpdateCheckException(CtrlZ.Core.Updates.UpdateFailureKind.Offline, "x").FriendlyMessage);
        }
        finally
        {
            CoreText.Culture = null;
            CultureInfo.CurrentUICulture = before;
            CultureInfo.DefaultThreadCurrentUICulture = beforeDefault;
        }
    }

    [Fact]
    public void A_typed_name_keeps_its_own_direction_in_right_to_left_text()
    {
        var before = CultureInfo.CurrentUICulture;
        var beforeDefault = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            AppLanguage.Apply("ar");
            // Right-to-left marks around the name: WPF ignores the Unicode isolate marks.
            Assert.Equal("‏My apps‏", CoreText.Isolate("My apps"));
            Assert.StartsWith("‏My apps‏ · 3", Loc.Plural("Main_ProfileLabel", 3, CoreText.Isolate("My apps")));
            Assert.DoesNotContain(CoreText.LeftToRight("a") + CoreText.Isolate("b"), c => c is >= '⁦' and <= '⁩');
        }
        finally
        {
            CoreText.Culture = null;
            CultureInfo.CurrentUICulture = before;
            CultureInfo.DefaultThreadCurrentUICulture = beforeDefault;
        }

        Assert.Equal("My apps", CoreText.Isolate("My apps"));
    }

    [Fact]
    public void Every_message_key_the_update_helper_writes_can_be_shown()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "CtrlZ.Core", "Updates", "UpdateApplier.cs")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory!.FullName, "src", "CtrlZ.Core", "Updates", "UpdateApplier.cs"));
        var keys = System.Text.RegularExpressions.Regex.Matches(source, @"""(UpdateApply_[A-Za-z]+)""")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.Equal(10, keys.Count);
        Assert.All(keys, key => Assert.True(CtrlZ.Core.Updates.UpdateApplyResult.IsKnownMessageKey(key), key));
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
