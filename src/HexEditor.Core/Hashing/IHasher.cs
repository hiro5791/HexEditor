namespace HexEditor.Core.Hashing;

/// <summary>
/// 1 つのアルゴリズムの計算の途中の状態 (ANA-18、ANA-19)。データを先頭から順に <see cref="Append"/> で渡し、最後に
/// <see cref="Finish"/> で値を受け取る。1 つのインスタンスは 1 回の計算だけに使う。
/// </summary>
public interface IHasher
{
    /// <summary>続きのデータを渡す。</summary>
    void Append(ReadOnlySpan<byte> data);

    /// <summary>
    /// 値を返す。64 bit 以下のチェックサム・CRC は数値としての表記 (ビッグエンディアン) のバイト列、それ以外はアルゴリズムの
    /// 定義どおりのバイト列。
    /// </summary>
    byte[] Finish();
}

/// <summary>アルゴリズムの一覧のグループ (ANA-19 の「画面」)。</summary>
public enum HashGroup
{
    Checksum,
    Crc,
    NonCryptographic,
    Cryptographic,
}

/// <summary>加算チェックサムの「結果の補数」(ANA-19 の仕様 1)。</summary>
public enum HashComplement
{
    None,

    /// <summary>1 の補数 (全ビット反転)。</summary>
    Ones,

    /// <summary>2 の補数 (符号反転。合計に足すと 0 になる値)。</summary>
    Twos,
}

/// <summary>ed2k の、データ長がちょうど 9,728,000 バイトの倍数のときの扱い (ANA-19 の仕様 4)。</summary>
public enum Ed2kMode
{
    /// <summary>新方式 (blue。既定)。</summary>
    Blue,

    /// <summary>旧方式 (red)。</summary>
    Red,
}

/// <summary>
/// アルゴリズムのパラメータ (ANA-18 の仕様 8、ANA-19 の表の「パラメータ」)。持たないパラメータは既定値のまま。
/// レコードの等値比較で「同じ設定か」を判定するため、鍵は配列ではなく Hex 文字列で持つ。
/// </summary>
public sealed record HashParameters
{
    public static readonly HashParameters Default = new();

    /// <summary>シード (xxHash、MurmurHash など)。</summary>
    public ulong Seed { get; init; }

    /// <summary>結果の補数 (加算チェックサム)。</summary>
    public HashComplement Complement { get; init; }

    /// <summary>ワードのエンディアン (ワード単位の加算・XOR・Fletcher・インターネットチェックサム)。null はアルゴリズムの既定。</summary>
    public bool? BigEndian { get; init; }

    /// <summary>ワード単位の加算の要素を符号付きとみなす。</summary>
    public bool Signed { get; init; }

    /// <summary>鍵 (SipHash、BLAKE2、BLAKE3 の鍵付きモード) の Hex 文字列 (区切りなしの大文字)。null は鍵なし (SipHash は全 0)。</summary>
    public string? KeyHex { get; init; }

    /// <summary>出力のビット数 (SHAKE、BLAKE2、BLAKE3、Tiger の切り詰め、SipHash の 128 bit 版)。0 はアルゴリズムの既定。</summary>
    public int OutputBits { get; init; }

    /// <summary>ed2k の扱い。</summary>
    public Ed2kMode Ed2k { get; init; }

    /// <summary>鍵のバイト列 (なければ空)。</summary>
    public byte[] Key => KeyHex is { Length: > 0 } hex ? Convert.FromHexString(hex) : [];
}
