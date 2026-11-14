using System.Text;

namespace CtrlZ.Core.Services;

public enum InstallerType
{
    Msi,
    InnoSetup,
    Nsis,
    InstallShield,
    Squirrel,
    WixBurn,
    Msix,
    PortableArchive,
    PortableExecutable,
    Unknown
}

public sealed record SilentFlagPreset(
    InstallerType Type,
    string DisplayName,
    string Arguments,
    string Notes,
    bool IsRecommended = true);

/// <summary>
/// A small, curated database of silent switches. It is intentionally conservative:
/// custom switches always win, and detection is reported to the user instead of being
/// silently guessed.
/// </summary>
public static class SilentInstallerCatalog
{
    public static readonly IReadOnlyList<SilentFlagPreset> Presets =
    [
        new(InstallerType.InnoSetup, "Inno Setup", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
            "The most common silent set for Inno Setup installers."),
        new(InstallerType.Nsis, "NSIS", "/S",
            "NSIS installers generally use /S. Some apps additionally support /D=<path>."),
        new(InstallerType.Msi, "Windows Installer (MSI)", "/qn /norestart",
            "Passed to msiexec. Use /qb for a basic visible progress bar."),
        new(InstallerType.InstallShield, "InstallShield", "/s /v\"/qn\"",
            "InstallShield setup.exe silences the bootstrapper and its MSI."),
        new(InstallerType.Squirrel, "Squirrel", "--silent",
            "Squirrel/Electron apps commonly accept --silent."),
        new(InstallerType.WixBurn, "WiX Burn bundle", "/quiet /norestart",
            "WiX Burn bootstrappers use /quiet. /norestart prevents an unexpected reboot."),
        new(InstallerType.Msix, "MSIX / AppX", string.Empty,
            "Installed with Add-AppxPackage; no command-line silent flags are needed."),
        new(InstallerType.PortableArchive, "Portable archive", string.Empty,
            "Extracted to the CTRL+Z apps folder; no silent switches are used."),
        new(InstallerType.PortableExecutable, "Portable executable", string.Empty,
            "Copied to the CTRL+Z apps folder; no installer is run.")
    ];

    public static SilentFlagPreset GetPreset(InstallerType type) =>
        Presets.FirstOrDefault(p => p.Type == type)
        ?? new SilentFlagPreset(InstallerType.Unknown, "Unknown installer",
            "/S", "Installer type was not recognized. /S works for many NSIS/Inno builds but should be tested.", false);

    public static InstallerType Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return InstallerType.Unknown;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".msi")
        {
            return InstallerType.Msi;
        }

        if (extension is ".msix" or ".appx" or ".msixbundle" or ".appxbundle")
        {
            return InstallerType.Msix;
        }

        if (extension is ".zip" or ".7z" or ".rar" or ".tar" or ".gz")
        {
            return InstallerType.PortableArchive;
        }

        if (extension != ".exe")
        {
            return InstallerType.Unknown;
        }

        var name = Path.GetFileName(path);
        if (name.Contains("inno", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerType.InnoSetup;
        }

        if (name.Contains("nsis", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerType.Nsis;
        }

        var signatures = ReadSignatures(path);
        if (signatures.Contains("Inno Setup", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerType.InnoSetup;
        }

        if (signatures.Contains("Nullsoft", StringComparison.OrdinalIgnoreCase) ||
            signatures.Contains("NSIS", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerType.Nsis;
        }

        if (signatures.Contains("InstallShield", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerType.InstallShield;
        }

        if (signatures.Contains("Squirrel", StringComparison.OrdinalIgnoreCase) ||
            signatures.Contains("Update.exe", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerType.Squirrel;
        }

        if (signatures.Contains("WixBundle", StringComparison.OrdinalIgnoreCase) ||
            signatures.Contains("Burn", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerType.WixBurn;
        }

        return InstallerType.Unknown;
    }

    public static string RecommendedArguments(string path) => GetPreset(Detect(path)).Arguments;

    /// <summary>
    /// Returns a user-facing validation message, or null when the switches are safe to
    /// pass as a single argument string.
    /// </summary>
    public static string? ValidateCustomArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        if (arguments.Length > 1000)
        {
            return "Silent switches are limited to 1000 characters.";
        }

        if (arguments.Any(c => c is '\r' or '\n' or '\0'))
        {
            return "Silent switches must be a single line.";
        }

        return null;
    }

    public static string BuildDisplayCommand(string filePath, string arguments)
    {
        var extension = Path.GetExtension(filePath);
        var command = extension.Equals(".msi", StringComparison.OrdinalIgnoreCase)
            ? $"msiexec.exe /i \"{filePath}\" {arguments}".Trim()
            : $"\"{filePath}\" {arguments}".Trim();
        return command;
    }

    private static string ReadSignatures(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(stream.Length, 12 * 1024 * 1024);
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            if (read == 0)
            {
                return string.Empty;
            }

            var ascii = Encoding.ASCII.GetString(buffer, 0, read);
            var unicodeProbe = Encoding.Unicode.GetString(buffer, 0, read - (read % 2));
            return ascii + "\n" + unicodeProbe;
        }
        catch
        {
            return string.Empty;
        }
    }
}
