using System.Text.RegularExpressions;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>
/// winget has no stable JSON output for "list" on every version, so the table is parsed
/// by locating header columns rather than splitting on whitespace. App names contain
/// spaces; package ids and versions generally do not.
/// </summary>
public static partial class WingetTableParser
{
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiRegex();

    public static IReadOnlyList<WingetPackage> ParseList(string output)
    {
        return ParseTable(output);
    }

    private static IReadOnlyList<WingetPackage> ParseTable(string output)
    {
        var lines = CleanLines(output);
        var headerIndex = FindHeader(lines, requireSource: false);
        if (headerIndex < 0)
        {
            return Array.Empty<WingetPackage>();
        }

        var header = lines[headerIndex];
        var nameIndex = header.IndexOf("Name", StringComparison.Ordinal);
        var idIndex = header.IndexOf("Id", StringComparison.Ordinal);
        var versionIndex = header.IndexOf("Version", StringComparison.Ordinal);
        var availableIndex = header.IndexOf("Available", StringComparison.Ordinal);
        var sourceIndex = header.IndexOf("Source", StringComparison.Ordinal);
        if (nameIndex < 0 || idIndex <= nameIndex || versionIndex <= idIndex)
        {
            return Array.Empty<WingetPackage>();
        }

        var result = new List<WingetPackage>();
        for (var i = headerIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (IsTableNoise(line) || line.Length <= idIndex)
            {
                continue;
            }

            string? Slice(int start, int end)
            {
                if (start < 0 || start >= line.Length)
                {
                    return null;
                }

                var safeEnd = end > start ? Math.Min(end, line.Length) : line.Length;
                return line[start..safeEnd].Trim();
            }

            var name = Slice(nameIndex, idIndex);
            var id = Slice(idIndex, versionIndex > idIndex ? versionIndex : line.Length);
            var version = Slice(versionIndex, availableIndex > versionIndex ? availableIndex : sourceIndex > versionIndex ? sourceIndex : line.Length);
            string? available = null;
            if (availableIndex > versionIndex)
            {
                available = Slice(availableIndex, sourceIndex > availableIndex ? sourceIndex : line.Length);
            }

            string? source = null;
            if (sourceIndex > 0)
            {
                source = Slice(sourceIndex, line.Length);
            }

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            // A malformed/short row can produce a version that is actually the source.
            if (source is null && version is "winget" or "msstore")
            {
                source = version;
                version = null;
            }

            if (source is not ("winget" or "msstore" or "Microsoft Store") && !LooksLikePackageId(id))
            {
                continue;
            }

            if (source is not null && source is not ("winget" or "msstore" or "Microsoft Store"))
            {
                continue;
            }

            // Names are sometimes truncated by winget's console renderer.
            if (name.EndsWith('…') || name.EndsWith("...", StringComparison.Ordinal))
            {
                name = name.TrimEnd('…').TrimEnd('.');
            }

            source ??= id.StartsWith('9') && id.Length >= 12 ? "msstore" : "winget";
            result.Add(new WingetPackage(name, id, NullIfEmpty(version), NullIfEmpty(available), source));
        }

        return result;
    }

    public static IReadOnlyList<WingetPackage> ParseSearch(string output)
    {
        return ParseTable(output);
    }

    private static int FindHeader(IReadOnlyList<string> lines, bool requireSource)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Contains("Name", StringComparison.Ordinal) &&
                line.Contains("Id", StringComparison.Ordinal) &&
                line.Contains("Version", StringComparison.Ordinal) &&
                (!requireSource || line.Contains("Source", StringComparison.Ordinal)))
            {
                // Avoid matching a package name that happens to contain these words.
                if (line.TrimStart().StartsWith("Name", StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static List<string> CleanLines(string output)
    {
        return output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => AnsiRegex().Replace(line, string.Empty))
            .Select(line => new string(line.Where(c => c == '\t' || !char.IsControl(c)).ToArray()))
            .ToList();
    }

    private static bool IsTableNoise(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.All(c => c is '-' or '=' or ' ' or '_'))
        {
            return true;
        }

        return trimmed.StartsWith("No installed package", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("No package found", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("The following", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Name ", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("Id", StringComparison.Ordinal)
               || trimmed.Contains("trying to update source", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("Downloading", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePackageId(string id) =>
        id.Length is >= 3 and <= 200 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '\\' or '+');

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
