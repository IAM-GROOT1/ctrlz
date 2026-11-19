// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.Json;
using System.Text.Json.Nodes;

namespace CtrlZ.Core.Services;

public static class BrowserLocalStateSanitizer
{
    /// <summary>
    /// Removes DPAPI-bound secrets from Local State. Profile names, flags, and extension
    /// metadata remain, so the file restores cleanly under a different Windows account.
    /// </summary>
    public static byte[] SanitizeFile(string path)
    {
        var text = File.ReadAllText(path);
        var sanitized = Sanitize(text);
        return JsonSerializer.SerializeToUtf8Bytes(sanitized, new JsonSerializerOptions { WriteIndented = false });
    }

    public static JsonObject Sanitize(string json)
    {
        // Local State is usually UTF-8 without a BOM, but some tools write one. JsonNode
        // rejects U+FEFF, so normalize it away before parsing.
        if (json.Length > 0 && json[0] == '\uFEFF')
        {
            json = json[1..];
        }

        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new InvalidDataException("Local State is not a JSON object.");
        root.Remove("os_crypt");
        root.Remove("protection");
        return root;
    }

    public static JsonObject MergeForRestore(
        string? archivedLocalState,
        string? currentLocalState,
        IReadOnlyDictionary<string, string> profileDirectoryMap,
        bool mergeProfileEntries)
    {
        var archived = ParseObject(archivedLocalState);
        var current = ParseObject(currentLocalState);
        if (archived is not null)
        {
            foreach (var property in archived.ToList())
            {
                if (property.Key is "os_crypt" or "protection" or "profile")
                {
                    continue;
                }

                current[property.Key] = property.Value?.DeepClone();
            }
        }

        if (mergeProfileEntries)
        {
            var currentProfile = current["profile"] as JsonObject ?? new JsonObject();
            var infoCache = currentProfile["info_cache"] as JsonObject ?? new JsonObject();
            var archivedInfoCache = (archived?["profile"] as JsonObject)?["info_cache"] as JsonObject;
            foreach (var pair in profileDirectoryMap)
            {
                var sourceEntry = archivedInfoCache?[pair.Key] as JsonObject;
                if (sourceEntry is null)
                {
                    continue;
                }

                infoCache[pair.Value] = sourceEntry.DeepClone();
            }

            currentProfile["info_cache"] = infoCache;
            current["profile"] = currentProfile;
        }

        return current;
    }

    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
