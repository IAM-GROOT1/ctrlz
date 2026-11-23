// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Globalization;
using System.Runtime.CompilerServices;

namespace CtrlZ.App.Tests;

/// <summary>
/// Tests compare against the English text, so they run in English whatever the Windows display
/// language of the machine running them.
/// </summary>
internal static class EnglishTestCulture
{
    [ModuleInitializer]
    internal static void Use()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    }
}
