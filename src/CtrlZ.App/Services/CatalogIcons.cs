// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CtrlZ.App.Services;

/// <summary>
/// Full-colour icons for the apps in the built-in catalog, bundled with CTRL+Z (see
/// <c>build/app-icons</c>). Used when an app is not installed on this PC, before the one-colour
/// brand logo. Nothing is downloaded.
/// </summary>
public static class CatalogIcons
{
    private sealed record Index(
        IReadOnlyDictionary<string, string> ByPackage,
        IReadOnlyDictionary<string, string> ByName,
        int Count);

    private static readonly Lazy<Index> Data = new(Load);
    private static readonly Dictionary<string, ImageSource?> Images = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>How many icons are bundled (for tests).</summary>
    public static int Count => Data.Value.Count;

    /// <summary>The icon for an app, by its winget id first and then by its name, or null.</summary>
    public static ImageSource? Find(string? packageId, string? name)
    {
        var index = Data.Value;
        if (!string.IsNullOrWhiteSpace(packageId) &&
            index.ByPackage.TryGetValue(packageId.Trim().ToLowerInvariant(), out var byId))
        {
            return Decode(byId);
        }

        foreach (var key in BrandIcons.NameKeys(name))
        {
            if (index.ByName.TryGetValue(key, out var byName))
            {
                return Decode(byName);
            }
        }

        return null;
    }

    private static ImageSource? Decode(string base64)
    {
        lock (Gate)
        {
            if (Images.TryGetValue(base64, out var cached))
            {
                return cached;
            }

            ImageSource? image = null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(Convert.FromBase64String(base64));
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException or InvalidOperationException)
            {
                // A broken icon only costs that icon; the logo or initials still show.
            }

            Images[base64] = image;
            return image;
        }
    }

    private static Index Load()
    {
        var byPackage = new Dictionary<string, string>(StringComparer.Ordinal);
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        var count = 0;
        try
        {
            using var stream = typeof(CatalogIcons).Assembly.GetManifestResourceStream("CtrlZ.App.Resources.app-icons.json");
            if (stream is null)
            {
                return new Index(byPackage, byName, 0);
            }

            using var document = JsonDocument.Parse(stream);
            foreach (var item in document.RootElement.GetProperty("icons").EnumerateArray())
            {
                var data = item.GetProperty("d").GetString();
                if (string.IsNullOrEmpty(data))
                {
                    continue;
                }

                count++;
                foreach (var id in item.GetProperty("w").EnumerateArray())
                {
                    byPackage.TryAdd(id.GetString() ?? string.Empty, data);
                }

                foreach (var name in item.GetProperty("n").EnumerateArray())
                {
                    byName.TryAdd(name.GetString() ?? string.Empty, data);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or KeyNotFoundException)
        {
            // A broken bundle only costs the icons; the logos and initials still show.
        }

        return new Index(byPackage, byName, count);
    }
}
