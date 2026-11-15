using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace CtrlZ.Core.Licensing;

/// <summary>
/// Produces a stable, non-reversible machine binding. Raw hardware identifiers are never
/// sent to the license server.
/// </summary>
public static class MachineIdProvider
{
    public static string GetMachineId()
    {
        var source = ReadMachineGuid();
        if (string.IsNullOrWhiteSpace(source))
        {
            source = $"{Environment.MachineName}|{Environment.OSVersion.VersionString}";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }
}
