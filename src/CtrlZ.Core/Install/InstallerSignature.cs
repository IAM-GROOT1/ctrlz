// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

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
        SignatureStatus.Valid => $"Signature valid: signed by {Publisher ?? "an unnamed publisher"}.",
        SignatureStatus.Unsigned => "The installer is not signed. Its SHA-256 is the only check.",
        SignatureStatus.Untrusted => $"Signed by {Publisher ?? "an unnamed publisher"}, but the certificate is not trusted on this PC (0x{Code:X8}). The file itself is intact.",
        SignatureStatus.Broken => $"The installer's signature is broken (0x{Code:X8}): the file was changed after it was signed, or its certificate was revoked. CTRL+Z will not run it.",
        _ => "The installer's signature could not be checked."
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
/// </summary>
public sealed class AuthenticodeVerifier : IInstallerSignatureVerifier
{
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int TrustECertSignature = unchecked((int)0x80096004);
    private const int CertERevoked = unchecked((int)0x800B010C);
    private const int TrustEExplicitDistrust = unchecked((int)0x800B0111);
    private const int CryptEFileError = unchecked((int)0x80092003);

    public static AuthenticodeVerifier Instance { get; } = new();

    /// <summary>Maps a WinVerifyTrust result to a status. Public so the mapping is testable.</summary>
    public static SignatureStatus Classify(int code) => code switch
    {
        0 => SignatureStatus.Valid,
        TrustENoSignature => SignatureStatus.Unsigned,
        TrustEBadDigest or TrustECertSignature or CertERevoked or TrustEExplicitDistrust => SignatureStatus.Broken,
        TrustESubjectFormUnknown or TrustEProviderUnknown or CryptEFileError => SignatureStatus.NotChecked,
        _ => SignatureStatus.Untrusted
    };

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
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
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
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.dwStateAction = WtdStateActionClose;
            _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr);
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
