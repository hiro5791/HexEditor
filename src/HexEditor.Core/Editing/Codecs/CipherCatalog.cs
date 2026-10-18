using System.Security.Cryptography;

namespace HexEditor.Core.Editing.Codecs;

/// <summary>暗号のアルゴリズム (EDIT-40 の仕様 1)。</summary>
public enum CipherAlgorithm
{
    Aes128,
    Aes192,
    Aes256,
    ChaCha20Poly1305,
    TripleDes,
    Des,
    Rc4,
}

/// <summary>暗号利用モード (EDIT-40 の仕様 1)。ChaCha20-Poly1305・RC4 は <see cref="None"/>。</summary>
public enum CipherMode
{
    None,
    Ecb,
    Cbc,
    Cfb8,
    Cfb128,
    Ctr,
    Gcm,
}

/// <summary>パディング (EDIT-40 の仕様 4。ECB・CBC のみ)。</summary>
public enum CipherPadding
{
    Pkcs7,
    Zeros,
    AnsiX923,
    Iso10126,
    None,
}

/// <summary>入力の誤り (EDIT-40 の「エラー」の入力欄のエラー)。</summary>
public enum CipherInputError
{
    None,
    KeyLength,
    IvLength,
    TagLength,
    NotBlockMultiple,
    TooLarge,
    Unsupported,
    MissingTag,
}

/// <summary>入力の確かめの結果 (<see cref="Expected"/>・<see cref="Actual"/> はバイト数)。</summary>
public readonly record struct CipherValidation(CipherInputError Error, int Expected = 0, int Actual = 0)
{
    public bool IsValid => Error == CipherInputError.None;
}

/// <summary>アルゴリズム・モードの性質 (EDIT-40 の仕様 1〜5)。</summary>
public static class CipherCatalog
{
    /// <summary>認証付き暗号 (GCM・ChaCha20-Poly1305) の対象の上限 (.NET の API が一括処理のため。EDIT-40 の「巨大ファイル」2)。</summary>
    public const long AeadLimit = 1024L * 1024 * 1024;

    /// <summary>認証タグの長さ。</summary>
    public const int TagLength = 16;

    /// <summary>PBKDF2 の反復回数の既定 (仕様 2)。</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>ソルトを空欄にしたときに作る乱数の長さ。</summary>
    public const int GeneratedSaltLength = 16;

    public static IReadOnlyList<CipherAlgorithm> Algorithms { get; } = Enum.GetValues<CipherAlgorithm>();

    /// <summary>選べるモード。</summary>
    public static IReadOnlyList<CipherMode> Modes(CipherAlgorithm algorithm) => algorithm switch
    {
        CipherAlgorithm.Aes128 or CipherAlgorithm.Aes192 or CipherAlgorithm.Aes256 =>
            [CipherMode.Ecb, CipherMode.Cbc, CipherMode.Cfb8, CipherMode.Cfb128, CipherMode.Ctr, CipherMode.Gcm],
        CipherAlgorithm.TripleDes or CipherAlgorithm.Des => [CipherMode.Ecb, CipherMode.Cbc],
        _ => [CipherMode.None],
    };

    /// <summary>鍵の長さ (バイト。3DES は 16 (2 鍵) と 24 (3 鍵))。</summary>
    public static IReadOnlyList<int> KeyLengths(CipherAlgorithm algorithm) => algorithm switch
    {
        CipherAlgorithm.Aes128 => [16],
        CipherAlgorithm.Aes192 => [24],
        CipherAlgorithm.Aes256 or CipherAlgorithm.ChaCha20Poly1305 => [32],
        CipherAlgorithm.TripleDes => [24, 16],
        CipherAlgorithm.Des => [8],
        _ => [16], // RC4 は 5〜256 バイト。パスフレーズから作るときは 16 バイト。
    };

    /// <summary>パスフレーズから作る鍵の長さ。</summary>
    public static int DerivedKeyLength(CipherAlgorithm algorithm) => KeyLengths(algorithm)[0];

    /// <summary>ブロック長 (ストリーム暗号は 1)。</summary>
    public static int BlockSize(CipherAlgorithm algorithm) => algorithm switch
    {
        CipherAlgorithm.TripleDes or CipherAlgorithm.Des => 8,
        CipherAlgorithm.ChaCha20Poly1305 or CipherAlgorithm.Rc4 => 1,
        _ => 16,
    };

    /// <summary>IV・ナンスの長さ (不要なら 0。仕様 3)。</summary>
    public static int IvLength(CipherAlgorithm algorithm, CipherMode mode) => (algorithm, mode) switch
    {
        (CipherAlgorithm.ChaCha20Poly1305, _) => 12,
        (_, CipherMode.Gcm) => 12,
        (_, CipherMode.Ecb or CipherMode.None) => 0,
        _ => BlockSize(algorithm),
    };

    public static bool IsAuthenticated(CipherAlgorithm algorithm, CipherMode mode) =>
        algorithm == CipherAlgorithm.ChaCha20Poly1305 || mode == CipherMode.Gcm;

    /// <summary>パディングを選べるモード (ECB・CBC)。</summary>
    public static bool UsesPadding(CipherMode mode) => mode is CipherMode.Ecb or CipherMode.Cbc;

    /// <summary>安全ではない方式 (「安全ではない方式です」と表示する。仕様 1)。</summary>
    public static bool IsInsecure(CipherAlgorithm algorithm) => algorithm is CipherAlgorithm.TripleDes or CipherAlgorithm.Des or CipherAlgorithm.Rc4;

    /// <summary>この OS で使えるか (ChaCha20-Poly1305 は OS の対応が必要。RC4 は CNG)。</summary>
    public static bool IsSupported(CipherAlgorithm algorithm) => algorithm switch
    {
        CipherAlgorithm.ChaCha20Poly1305 => System.Security.Cryptography.ChaCha20Poly1305.IsSupported,
        CipherAlgorithm.Rc4 => Rc4Cng.IsSupported,
        _ => true,
    };

    /// <summary>対象の長さ <paramref name="length"/> で使えるか (認証付き暗号は 1 GiB まで)。</summary>
    public static bool FitsLength(CipherAlgorithm algorithm, CipherMode mode, long length) =>
        !IsAuthenticated(algorithm, mode) || length <= AeadLimit;

    /// <summary>鍵・IV・タグの長さと、パディング「なし」のブロック長の倍数を確かめる。</summary>
    public static CipherValidation Validate(CipherParameters p, long inputLength, bool encrypt)
    {
        if (!IsSupported(p.Algorithm) || !Modes(p.Algorithm).Contains(p.Mode))
        {
            return new(CipherInputError.Unsupported);
        }

        int keyLength = p.Key.Length;
        if (p.Algorithm == CipherAlgorithm.Rc4 ? keyLength is < 5 or > 256 : !KeyLengths(p.Algorithm).Contains(keyLength))
        {
            return new(CipherInputError.KeyLength, KeyLengths(p.Algorithm)[0], keyLength);
        }

        int ivLength = IvLength(p.Algorithm, p.Mode);
        if (ivLength > 0 && p.Iv.Length != ivLength)
        {
            return new(CipherInputError.IvLength, ivLength, p.Iv.Length);
        }

        if (!FitsLength(p.Algorithm, p.Mode, inputLength))
        {
            return new(CipherInputError.TooLarge);
        }

        if (IsAuthenticated(p.Algorithm, p.Mode) && !encrypt)
        {
            if (p.TagFromSelectionEnd)
            {
                if (inputLength < TagLength)
                {
                    return new(CipherInputError.MissingTag, TagLength, (int)inputLength);
                }
            }
            else if (p.Tag is not { Length: TagLength })
            {
                return new(CipherInputError.TagLength, TagLength, p.Tag?.Length ?? 0);
            }
        }

        if (UsesPadding(p.Mode) && (p.Padding == CipherPadding.None || !encrypt) && inputLength % BlockSize(p.Algorithm) != 0)
        {
            return new(CipherInputError.NotBlockMultiple, BlockSize(p.Algorithm), (int)(inputLength % BlockSize(p.Algorithm)));
        }

        return new(CipherInputError.None);
    }

    /// <summary>
    /// パスフレーズから鍵を作る (PBKDF2-HMAC-SHA256。仕様 2)。<paramref name="salt"/> が空なら 16 バイトの乱数を作って返す。
    /// パスフレーズは UTF-8。作ったバイト列は呼び出し側が使い終えたら消す。
    /// </summary>
    public static byte[] DeriveKey(ReadOnlySpan<char> passphrase, ref byte[] salt, int iterations, int keyLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        if (salt.Length == 0)
        {
            salt = RandomNumberGenerator.GetBytes(GeneratedSaltLength);
        }

        byte[] password = new byte[System.Text.Encoding.UTF8.GetMaxByteCount(passphrase.Length)];
        try
        {
            int n = System.Text.Encoding.UTF8.GetBytes(passphrase, password);
            return Rfc2898DeriveBytes.Pbkdf2(password.AsSpan(0, n), salt, iterations, HashAlgorithmName.SHA256, keyLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    /// <summary>乱数の IV・ナンス (仕様 3 の「乱数で生成」)。</summary>
    public static byte[] RandomIv(CipherAlgorithm algorithm, CipherMode mode) => RandomNumberGenerator.GetBytes(IvLength(algorithm, mode));
}

/// <summary>
/// 暗号化・復号の設定 (EDIT-40)。鍵・IV・AAD・タグは <see cref="Dispose"/> で 0 にする (仕様 7。ダイアログを閉じたら消す)。
/// 設定・履歴・復旧用データ・ログに書かない。
/// </summary>
public sealed class CipherParameters : IDisposable
{
    public CipherAlgorithm Algorithm { get; init; }

    public CipherMode Mode { get; init; }

    public CipherPadding Padding { get; init; } = CipherPadding.Pkcs7;

    public byte[] Key { get; init; } = [];

    public byte[] Iv { get; init; } = [];

    /// <summary>追加認証データ (GCM・ChaCha20-Poly1305。省略可)。</summary>
    public byte[] Aad { get; init; } = [];

    /// <summary>暗号化: タグを結果の末尾に付ける (既定)。false なら別の欄に表示するだけ。</summary>
    public bool AppendTag { get; init; } = true;

    /// <summary>復号: タグは選択範囲の末尾 16 バイト (既定)。false なら <see cref="Tag"/>。</summary>
    public bool TagFromSelectionEnd { get; init; } = true;

    /// <summary>復号で Hex で入力したタグ。</summary>
    public byte[]? Tag { get; init; }

    /// <summary>復号でパディングを取り除かない (「パディングなしで復号する」)。</summary>
    public CipherParameters WithoutPadding() => new()
    {
        Algorithm = Algorithm,
        Mode = Mode,
        Padding = CipherPadding.None,
        Key = (byte[])Key.Clone(),
        Iv = (byte[])Iv.Clone(),
        Aad = (byte[])Aad.Clone(),
        AppendTag = AppendTag,
        TagFromSelectionEnd = TagFromSelectionEnd,
        Tag = Tag is null ? null : (byte[])Tag.Clone(),
    };

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Key);
        CryptographicOperations.ZeroMemory(Iv);
        CryptographicOperations.ZeroMemory(Aad);
        if (Tag is not null)
        {
            CryptographicOperations.ZeroMemory(Tag);
        }
    }
}
