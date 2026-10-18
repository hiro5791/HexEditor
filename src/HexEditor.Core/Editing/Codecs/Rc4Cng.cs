using System.Runtime.InteropServices;

namespace HexEditor.Core.Editing.Codecs;

/// <summary>
/// RC4 (互換のため。EDIT-40 の仕様 1「CNG の RC4 を使う」)。Windows の CNG (bcrypt.dll) の RC4 を呼ぶ。ストリーム暗号のため、同じ鍵のハンドルで
/// 続けて呼ぶと鍵ストリームが続く。鍵のハンドルは <see cref="Dispose"/> で破棄する。
/// </summary>
internal sealed unsafe partial class Rc4Cng : IDisposable
{
    private nint _algorithm;
    private nint _key;

    public Rc4Cng(ReadOnlySpan<byte> key)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("RC4 は Windows の CNG でのみ使えます。");
        }

        Check(BCryptOpenAlgorithmProvider(out _algorithm, "RC4", null, 0));
        fixed (byte* k = key)
        {
            int status = BCryptGenerateSymmetricKey(_algorithm, out _key, null, 0, k, key.Length, 0);
            if (status != 0)
            {
                BCryptCloseAlgorithmProvider(_algorithm, 0);
                _algorithm = 0;
                Check(status);
            }
        }
    }

    /// <summary>この OS で RC4 を使えるか。</summary>
    public static bool IsSupported
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                using var probe = new Rc4Cng([1, 2, 3, 4, 5]);
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or System.Security.Cryptography.CryptographicException)
            {
                return false;
            }
        }
    }

    /// <summary>鍵ストリームと XOR する (暗号化・復号は同じ)。</summary>
    public void Transform(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.IsEmpty)
        {
            return;
        }

        fixed (byte* i = input)
        fixed (byte* o = output)
        {
            Check(BCryptEncrypt(_key, i, input.Length, null, null, 0, o, output.Length, out int written, 0));
            if (written != input.Length)
            {
                throw new System.Security.Cryptography.CryptographicException("RC4 の結果の長さが違います。");
            }
        }
    }

    public void Dispose()
    {
        if (_key != 0)
        {
            BCryptDestroyKey(_key);
            _key = 0;
        }

        if (_algorithm != 0)
        {
            BCryptCloseAlgorithmProvider(_algorithm, 0);
            _algorithm = 0;
        }
    }

    private static void Check(int status)
    {
        if (status != 0)
        {
            throw new System.Security.Cryptography.CryptographicException($"CNG の呼び出しに失敗しました (0x{status:X8})。");
        }
    }

    [LibraryImport("bcrypt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int BCryptOpenAlgorithmProvider(out nint algorithm, string algId, string? implementation, uint flags);

    [LibraryImport("bcrypt.dll")]
    private static partial int BCryptCloseAlgorithmProvider(nint algorithm, uint flags);

    [LibraryImport("bcrypt.dll")]
    private static partial int BCryptGenerateSymmetricKey(nint algorithm, out nint key, byte* keyObject, int keyObjectLength, byte* secret,
        int secretLength, uint flags);

    [LibraryImport("bcrypt.dll")]
    private static partial int BCryptDestroyKey(nint key);

    [LibraryImport("bcrypt.dll")]
    private static partial int BCryptEncrypt(nint key, byte* input, int inputLength, void* paddingInfo, byte* iv, int ivLength, byte* output,
        int outputLength, out int result, uint flags);
}
