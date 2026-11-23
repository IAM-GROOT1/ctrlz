// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Windows.Markup;

namespace CtrlZ.App.Localization;

/// <summary>
/// <c>{l:Tr Key}</c> in XAML: the text for <c>Key</c> from <see cref="Loc"/>. The language is
/// chosen once at startup, so the value is read when the element loads and never changes.
/// It also works as a binding's format: <c>{Binding Name, StringFormat={l:Tr Key}}</c>.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
