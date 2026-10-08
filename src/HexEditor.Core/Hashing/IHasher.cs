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

/// <summary>アルゴリズムのパラメータ (ANA-18 の仕様 8)。持たないパラメータは既定値のまま。</summary>
public sealed record HashParameters
{
    public static readonly HashParameters Default = new();

    /// <summary>シード (xxHash など)。</summary>
    public ulong Seed { get; init; }

    /// <summary>結果の補数 (加算チェックサム)。</summary>
    public HashComplement Complement { get; init; }
}
