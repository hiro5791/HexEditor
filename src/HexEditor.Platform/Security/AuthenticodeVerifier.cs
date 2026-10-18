using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace HexEditor.Platform.Security;

/// <summary>
/// Authenticode の署名の確認 (10 の PKG-17 の仕様 7、08 の AUTO-03 の仕様 3)。WinVerifyTrust で署名を検証し、署名者が Microsoft か調べる。
/// 失効の確認はキャッシュだけを使い、ネットワークに接続しない (08 の AUTO-35 の仕様 5。オフラインでも同じ結果)。ファイルを読むだけ。
/// </summary>
public static class AuthenticodeVerifier
{
    /// <summary>有効な署名があり、署名者の名前が <paramref name="publisherPrefix"/> で始まれば true。</summary>
    public static bool IsSignedBy(string path, string publisherPrefix, out string? error)
    {
        if (!File.Exists(path))
        {
            error = "file not found";
            return false;
        }

        int result = Verify(path);
        if (result != 0)
        {
            error = $"WinVerifyTrust 0x{result:X8}";
            return false;
        }

        try
        {
#pragma warning disable SYSLIB0057 // 署名者の証明書を取り出すだけ (検証は WinVerifyTrust で済んでいる)。
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            string name = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (!name.StartsWith(publisherPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "publisher: " + name;
                return false;
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            error = ex.Message;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// 有効な署名の署名者の名前 (署名がない・検証できなければ null)。外部からの自動操作の接続の確認ダイアログに出す (08 の AUTO-45 の仕様 4)。
    /// </summary>
    public static string? SignerOf(string path)
    {
        if (!IsSignedBy(path, string.Empty, out _))
        {
            return null;
        }

        try
        {
#pragma warning disable SYSLIB0057 // 署名者の証明書を取り出すだけ (検証は WinVerifyTrust で済んでいる)。
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            return null;
        }
    }

    /// <summary>Microsoft の署名があるか (Roslyn のアセンブリの確認)。</summary>
    /// <remarks>
    /// 署名者は "Microsoft Corporation" か ".NET" (.NET 8 以降のランタイムと一部の NuGet パッケージ) で、発行者が Microsoft の CA のもの。
    /// </remarks>
    public static bool IsMicrosoftSigned(string path, out string? error)
    {
        if (!IsSignedBy(path, string.Empty, out error))
        {
            return false;
        }

        try
        {
#pragma warning disable SYSLIB0057 // 署名者の証明書を取り出すだけ (検証は WinVerifyTrust で済んでいる)。
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            string name = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            string issuer = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
            if (name is not ("Microsoft Corporation" or ".NET") || !issuer.StartsWith("Microsoft ", StringComparison.Ordinal))
            {
                error = $"publisher: {name} (issuer: {issuer})";
                return false;
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            error = ex.Message;
            return false;
        }

        error = null;
        return true;
    }

    // ======================== プラグインの署名 (08 の AUTO-35) ========================

    /// <summary>署名の確認の結果 (AUTO-35 の仕様 1 の 3 つの状態)。</summary>
    public enum SignatureStatus
    {
        Unsigned,
        Valid,
        Invalid,
    }

    /// <summary>
    /// 確認の結果。<see cref="Code"/> は WinVerifyTrust の結果 (0 は成功)。<see cref="Thumbprint"/> は署名者の証明書の SHA-256 (大文字の Hex)。
    /// <see cref="RevocationUnchecked"/> は失効を確認できなかった (オフライン。署名ありとして扱う。AUTO-35 の仕様 5)。
    /// </summary>
    public sealed record SignatureResult(SignatureStatus Status, int Code, string? Publisher = null, string? Thumbprint = null, bool RevocationUnchecked = false);

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int CryptERevocationOffline = unchecked((int)0x80092013);
    private const int CryptENoRevocationCheck = unchecked((int)0x80092012);
    private const int CertERevocationFailure = unchecked((int)0x800B010E);

    /// <summary>
    /// プラグインの DLL の署名を確かめる (AUTO-35)。WinVerifyTrust で署名・証明書のチェーン・期限・失効を確かめる。
    /// <list type="bullet">
    /// <item>署名がない (WinVerifyTrust が署名なしと答え、PE に証明書の表もない) ものは「署名なし」。</item>
    /// <item>失効を確認できない (ネットワークに接続できない、<paramref name="offline"/>) 場合は、失効の確認を除いて有効なら「有効 (失効は未確認)」。</item>
    /// <item>それ以外の失敗 (改ざん、期限切れ、失効、信頼されていないルート、確認の途中の例外) はすべて「無効」。</item>
    /// </list>
    /// <paramref name="offline"/> なら、失効の確認はキャッシュだけを使い、接続しない (<c>network.offline</c>)。
    /// </summary>
    public static SignatureResult VerifyPluginFile(string path, bool offline)
    {
        if (!File.Exists(path))
        {
            return new SignatureResult(SignatureStatus.Invalid, -1);
        }

        int code;
        try
        {
            code = Verify(path, revocation: true, cacheOnly: offline);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new SignatureResult(SignatureStatus.Invalid, ex.HResult);
        }

        bool revocationUnchecked = false;
        if (code is CryptERevocationOffline or CryptENoRevocationCheck or CertERevocationFailure)
        {
            // 失効だけを確認できなかった: 失効の確認を除いて確かめ直す。
            code = Verify(path, revocation: false, cacheOnly: true);
            revocationUnchecked = code == 0;
        }

        if (code is TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown)
        {
            // 証明書の表があるのに「署名なし」と答えた場合は、壊れた署名として扱う (署名を外して「署名なし」に見せる改ざんを除く)。
            return HasCertificateTable(path) ? new SignatureResult(SignatureStatus.Invalid, code) : new SignatureResult(SignatureStatus.Unsigned, code);
        }

        if (code != 0)
        {
            return new SignatureResult(SignatureStatus.Invalid, code);
        }

        try
        {
#pragma warning disable SYSLIB0057 // 署名者の証明書を取り出すだけ (検証は WinVerifyTrust で済んでいる)。
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            string thumbprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.RawData));
            return new SignatureResult(SignatureStatus.Valid, 0, cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false), thumbprint, revocationUnchecked);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            return new SignatureResult(SignatureStatus.Invalid, ex.HResult);
        }
    }

    /// <summary>PE のセキュリティディレクトリ (証明書の表) が空でないか。PE でなければ false。</summary>
    internal static bool HasCertificateTable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] head = new byte[4096];
            int n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (n < 0x40 || head[0] != (byte)'M' || head[1] != (byte)'Z')
            {
                return false;
            }

            int pe = BitConverter.ToInt32(head, 0x3C);
            if (pe < 0 || pe + 24 + 2 > n || head[pe] != (byte)'P' || head[pe + 1] != (byte)'E')
            {
                return false;
            }

            int optional = pe + 24;
            int directories = BitConverter.ToUInt16(head, optional) switch
            {
                0x10B => optional + 96,
                0x20B => optional + 112,
                _ => -1,
            };
            if (directories < 0 || directories + 5 * 8 > n)
            {
                return false;
            }

            int security = directories + 4 * 8;
            return BitConverter.ToInt32(head, security) != 0 || BitConverter.ToInt32(head, security + 4) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdRevocationCheckChainExcludeRoot = 0x00000080;

    private static int Verify(string path) => Verify(path, revocation: false, cacheOnly: true);

    private static int Verify(string path, bool revocation, bool cacheOnly)
    {
        var file = new WintrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            pcwszFilePath = path,
        };
        IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = revocation ? WtdRevokeWholeChain : WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = filePtr,
                dwStateAction = WtdStateActionIgnore,
                dwProvFlags = (cacheOnly ? WtdCacheOnlyUrlRetrieval : 0) | (revocation ? WtdRevocationCheckChainExcludeRoot : WtdRevocationCheckNone),
            };
            Guid action = WintrustActionGenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
        }
    }

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionIgnore = 0;
    private const uint WtdRevocationCheckNone = 0x00000010;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
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
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WintrustData pWVTData);
}
