// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace CtrlZ.Core.Install;

public enum SignatureStatus
{
    /// <summary>Signed, the signature matches the file and the chain is trusted on this PC.</summary>
    Valid,

    /// <summary>No Authenticode signature. Common for small open-source installers; not an error.</summary>
    Unsigned,

    /// <summary>Signed and the file is intact, but the certificate is not trusted here (unknown root, expired, offline chain).</summary>
    Untrusted,

    /// <summary>The file was changed after signing, the certificate was revoked, or it is explicitly distrusted. Never run.</summary>
    Broken,

    /// <summary>The file type cannot carry a signature, or Windows could not check it.</summary>
    NotChecked
}

public sealed record SignatureCheck(SignatureStatus Status, string? Publisher, int Code)
{
    public bool BlocksInstall => Status == SignatureStatus.Broken;

    public string Describe() => Status switch
    {
        SignatureStatus.Valid => CoreText.Format("InstallerSignature_Valid", Publisher ?? CoreText.Get("InstallerSignature_UnnamedPublisher")),
        SignatureStatus.Unsigned => CoreText.Get("InstallerSignature_TheInstallerIsNotSigned"),
        SignatureStatus.Untrusted => CoreText.Format("InstallerSignature_Untrusted", Publisher ?? CoreText.Get("InstallerSignature_UnnamedPublisher"), Code),
        SignatureStatus.Broken => CoreText.Format("InstallerSignature_TheInstallerSSignatureIs", Code),
        _ => Code == 0
            ? CoreText.Get("InstallerSignature_TheInstallerSSignatureCould")
            : CoreText.Format("InstallerSignature_TheInstallerSSignatureCould2", Code)
    };
}

public interface IInstallerSignatureVerifier
{
    SignatureCheck Check(string path);
}

/// <summary>
/// Checks an installer's Authenticode signature with WinVerifyTrust before CTRL+Z runs it. Only a
/// broken signature (a file changed after signing, a revoked or distrusted certificate) stops the
/// install; unsigned or untrusted-but-intact files are reported in the install log. The check is
/// offline: no revocation lookup and only cached certificate downloads, so it adds no network call.
/// Because revocation is not fetched, a revoked certificate is caught only when Windows already
/// knows it (the Disallowed store). Without a published hash, an unsigned or re-signed file still
/// runs: the signature check catches tampering with a signed file, not a replaced one.
/// </summary>
public sealed class AuthenticodeVerifier : IInstallerSignatureVerifier
{
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int TrustECertSignature = unchecked((int)0x80096004);
    private const int CertERevoked = unchecked((int)0x800B010C);
    private const int TrustEExplicitDistrust = unchecked((int)0x800B0111);

    public static AuthenticodeVerifier Instance { get; } = new();

    /// <summary>Maps a WinVerifyTrust result to a status. Public so the mapping is testable.</summary>
    public static SignatureStatus Classify(int code) => code switch
    {
        0 => SignatureStatus.Valid,
        TrustENoSignature => SignatureStatus.Unsigned,
        TrustEBadDigest or TrustECertSignature or CertERevoked or TrustEExplicitDistrust => SignatureStatus.Broken,
        _ when UntrustedChainCodes.Contains(code) => SignatureStatus.Untrusted,
        // Subject or provider unknown, file errors, malformed signature blobs and anything unlisted.
        _ => SignatureStatus.NotChecked
    };

    // Chain and time problems only: the file's digest was checked and matched.
    private static readonly HashSet<int> UntrustedChainCodes =
    [
        unchecked((int)0x800B0109), // CERT_E_UNTRUSTEDROOT
        unchecked((int)0x800B0101), // CERT_E_EXPIRED
        unchecked((int)0x800B010A), // CERT_E_CHAINING
        unchecked((int)0x800B010D), // CERT_E_UNTRUSTEDTESTROOT
        unchecked((int)0x800B0112), // CERT_E_UNTRUSTEDCA
        unchecked((int)0x800B0110), // CERT_E_WRONG_USAGE
        unchecked((int)0x800B0102), // CERT_E_VALIDITYPERIODNESTING
        unchecked((int)0x80092013), // CRYPT_E_REVOCATION_OFFLINE
        unchecked((int)0x80092012), // CRYPT_E_NO_REVOCATION_CHECK
        unchecked((int)0x80096005)  // TRUST_E_TIME_STAMP
    ];

    public SignatureCheck Check(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return new SignatureCheck(SignatureStatus.NotChecked, null, 0);
        }

        int code;
        try
        {
            code = Verify(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return new SignatureCheck(SignatureStatus.NotChecked, null, 0);
        }

        var status = Classify(code);
        var publisher = status is SignatureStatus.Valid or SignatureStatus.Untrusted ? PublisherOf(path) : null;
        return new SignatureCheck(status, publisher, code);
    }

    private static string? PublisherOf(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // The signer of a signed file, not a certificate file being loaded.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false)?.Trim();
            var organization = certificate.SubjectName.EnumerateRelativeDistinguishedNames()
                .Where(part => part.GetSingleElementType().Value == "2.5.4.10")
                .Select(part => part.GetSingleElementValue()?.Trim())
                .FirstOrDefault(value => !string.IsNullOrEmpty(value));

            if (string.IsNullOrEmpty(name))
            {
                return organization;
            }

            return organization is null || organization.Equals(name, StringComparison.OrdinalIgnoreCase)
                ? name
                : $"{name} ({organization})";
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static int Verify(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = path
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var written = false;
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            written = true;
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPtr,
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdCacheOnlyUrlRetrieval | WtdRevocationCheckNone
            };

            var action = GenericVerifyV2;
            try
            {
                return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
            finally
            {
                // Always release the state WinVerifyTrust may have opened.
                data.dwStateAction = WtdStateActionClose;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
        }
        finally
        {
            if (written)
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr);
            }

            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);
}
