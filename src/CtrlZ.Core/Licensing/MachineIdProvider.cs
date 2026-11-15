using System.Management;
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
    private static readonly Lazy<string> Cached = new(Compute, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string GetMachineId() => Cached.Value;

    private static string Compute()
    {
        // SMBIOS UUID survives a Windows reinstall, unlike MachineGuid. Use it first so a
        // restored Pro license still matches after a clean install on the same PC.
        var source = ReadSmbiosUuid();
        if (string.IsNullOrWhiteSpace(source))
        {
            source = ReadMachineGuid();
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            source = $"{Environment.MachineName}|{Environment.OSVersion.VersionString}";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ReadSmbiosUuid()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT UUID FROM Win32_ComputerSystemProduct");
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    var uuid = item["UUID"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(uuid) &&
                        !uuid.Equals("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", StringComparison.OrdinalIgnoreCase) &&
                        !uuid.Equals("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase))
                    {
                        return "smbios:" + uuid;
                    }
                }
            }
        }
        catch
        {
            // WMI is unavailable on some systems; fall back to MachineGuid.
        }

        return null;
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
