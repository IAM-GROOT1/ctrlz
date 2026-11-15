using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CtrlZ.Core.Infrastructure;

namespace CtrlZ.Core.Licensing;

/// <summary>
/// Verifies CTRLZ1 tokens signed with ECDSA P-256/SHA-256. Verification happens locally;
/// the app never has the private signing key.
/// </summary>
public static class LicenseTokenCodec
{
    public const string Prefix = "CTRLZ1";

    public static LicenseTokenVerification Verify(string? token, string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new LicenseTokenVerification { Error = "No license token was provided." };
        }

        var parts = token.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != Prefix)
        {
            return new LicenseTokenVerification { Error = "This is not a CTRL+Z license token." };
        }

        try
        {
            var payloadBytes = Base64UrlDecode(parts[1]);
            var signature = Base64UrlDecode(parts[2]);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            if (!ecdsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256))
            {
                return new LicenseTokenVerification { Error = "The license signature is not valid." };
            }

            var payload = JsonSerializer.Deserialize<LicenseTokenPayload>(payloadBytes, JsonDefaults.Compact);
            if (payload is null || string.IsNullOrWhiteSpace(payload.LicenseId) ||
                string.IsNullOrWhiteSpace(payload.MachineId) || payload.SchemaVersion != 1)
            {
                return new LicenseTokenVerification { Error = "The license payload is incomplete." };
            }

            payload.Features ??= new List<string>();
            return new LicenseTokenVerification { Payload = payload, IsValid = true };
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
        {
            return new LicenseTokenVerification { Error = "The license token is malformed." };
        }
    }

    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        return Convert.FromBase64String(normalized);
    }

}
