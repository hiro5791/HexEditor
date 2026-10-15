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

    /// <summary>Microsoft の署名があるか (Roslyn のアセンブリの確認)。</summary>
    public static bool IsMicrosoftSigned(string path, out string? error) => IsSignedBy(path, "Microsoft Corporation", out error);

    private static int Verify(string path)
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
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = filePtr,
                dwStateAction = WtdStateActionIgnore,
                dwProvFlags = WtdCacheOnlyUrlRetrieval | WtdRevocationCheckNone,
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
