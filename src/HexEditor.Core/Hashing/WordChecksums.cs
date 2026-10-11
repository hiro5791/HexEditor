using System.Buffers.Binary;

namespace HexEditor.Core.Hashing;

/// <summary>
/// 16 / 32 / 64 bit のワードを要素とするチェックサムの共通部分 (ANA-19 の仕様 1・3)。データの区切りがワードの途中でもよいように、
/// 端数のバイトを持ち越す。最後に残った端数のバイトは 0 で埋めて 1 ワードにする。
/// </summary>
public abstract class WordChecksumHasher : IHasher
{
    private readonly byte[] _partial = new byte[8];
    private int _partialCount;

    protected WordChecksumHasher(int wordBytes, bool bigEndian)
    {
        if (wordBytes is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(wordBytes));
        }

        WordBytes = wordBytes;
        BigEndian = bigEndian;
    }

    /// <summary>1 ワードのバイト数。</summary>
    protected int WordBytes { get; }

    /// <summary>ワードをビッグエンディアンで読む。</summary>
    protected bool BigEndian { get; }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_partialCount > 0)
        {
            int n = Math.Min(WordBytes - _partialCount, data.Length);
            data[..n].CopyTo(_partial.AsSpan(_partialCount));
            _partialCount += n;
            data = data[n..];
            if (_partialCount < WordBytes)
            {
                return;
            }

            AppendWords(_partial.AsSpan(0, WordBytes));
            _partialCount = 0;
        }

        int whole = data.Length - (data.Length % WordBytes);
        if (whole > 0)
        {
            AppendWords(data[..whole]);
        }

        data[whole..].CopyTo(_partial);
        _partialCount = data.Length - whole;
    }

    public byte[] Finish()
    {
        if (_partialCount > 0)
        {
            // 端数のバイトは 0 で埋める (ANA-19 の仕様 1)。
            _partial.AsSpan(_partialCount, WordBytes - _partialCount).Clear();
            AppendWords(_partial.AsSpan(0, WordBytes));
            _partialCount = 0;
        }

        return Result();
    }

    /// <summary>ワード 1 つを読む (<paramref name="s"/> は 1 ワード以上)。</summary>
    protected ulong ReadWord(ReadOnlySpan<byte> s) => WordBytes switch
    {
        1 => s[0],
        2 => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s),
        4 => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s),
        _ => BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s),
    };

    /// <summary>ワードの並び (長さはワードのバイト数の倍数) を処理する。</summary>
    protected abstract void AppendWords(ReadOnlySpan<byte> words);

    /// <summary>値 (数値としての表記のバイト列)。</summary>
    protected abstract byte[] Result();

    protected static ulong MaskFor(int bits) => bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;

    /// <summary>結果の補数を付ける (ANA-19 の仕様 1)。</summary>
    protected static ulong ApplyComplement(ulong value, HashComplement complement) => complement switch
    {
        HashComplement.Ones => ~value,
        HashComplement.Twos => unchecked(0 - value),
        _ => value,
    };
}

/// <summary>
/// 加算 16 / 32 / 64 bit (ワード単位) のチェックサム (ANA-19 の仕様 1)。要素を足し、幅で切り捨てる。符号ありでも、
/// 幅で切り捨てた和のビット列は符号なしと同じになる (符号は表示だけに影響する。<see cref="HashValueFormatter.FormatSignedDecimal"/>)。
/// </summary>
public sealed class WordSumHasher(int bits, bool bigEndian, HashComplement complement) : WordChecksumHasher(bits / 8, bigEndian)
{
    private ulong _sum;

    protected override void AppendWords(ReadOnlySpan<byte> words)
    {
        ulong sum = _sum;
        for (int i = 0; i < words.Length; i += WordBytes)
        {
            sum = unchecked(sum + ReadWord(words[i..]));
        }

        _sum = sum;
    }

    protected override byte[] Result() => HashBytes.FromNumber(ApplyComplement(_sum, complement) & MaskFor(bits), bits / 8);
}

/// <summary>XOR 16 / 32 / 64 bit (ワード単位) のチェックサム (ANA-19 の仕様 1)。</summary>
public sealed class WordXorHasher(int bits, bool bigEndian) : WordChecksumHasher(bits / 8, bigEndian)
{
    private ulong _value;

    protected override void AppendWords(ReadOnlySpan<byte> words)
    {
        ulong value = _value;
        for (int i = 0; i < words.Length; i += WordBytes)
        {
            value ^= ReadWord(words[i..]);
        }

        _value = value;
    }

    protected override byte[] Result() => HashBytes.FromNumber(_value & MaskFor(bits), bits / 8);
}

/// <summary>
/// インターネットチェックサム (RFC 1071。16 bit ワードの 1 の補数和の 1 の補数)。既定のワードのエンディアンは BE
/// (ネットワークのバイト順)。LE で読むと、値は BE の値のバイト順を入れ替えたものになる (RFC 1071 の 2 章 (B))。
/// </summary>
public sealed class InternetChecksumHasher(bool bigEndian) : WordChecksumHasher(2, bigEndian)
{
    /// <summary>16 bit ワードの和。2^48 ワード (512 TB) まで桁あふれしない。</summary>
    private ulong _sum;

    protected override void AppendWords(ReadOnlySpan<byte> words)
    {
        ulong sum = _sum;
        for (int i = 0; i < words.Length; i += 2)
        {
            sum += ReadWord(words[i..]);
        }

        _sum = sum;
    }

    protected override byte[] Result()
    {
        ulong sum = _sum;
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return HashBytes.FromNumber(~sum & 0xFFFF, 2);
    }
}

/// <summary>
/// Fletcher-16 / 32 / 64 (ANA-19 の仕様 3)。それぞれ 8 / 16 / 32 bit の要素を 2^8−1 / 2^16−1 / 2^32−1 を法として 2 つの和に足し、
/// 値は (和 2 &lt;&lt; 幅/2) | 和 1。32 / 64 は要素のエンディアンを選ぶ (既定 LE)。端数のバイトは 0 で埋める。
/// </summary>
public sealed class FletcherHasher : WordChecksumHasher
{
    private readonly int _bits;
    private readonly ulong _modulus;

    /// <summary>剰余を取らずに足せる要素の数 (和 2 が 2^64 を超えない数)。</summary>
    private readonly int _batch;

    private ulong _sum1;
    private ulong _sum2;

    public FletcherHasher(int bits, bool bigEndian)
        : base(bits / 16, bigEndian)
    {
        if (bits is not (16 or 32 or 64))
        {
            throw new ArgumentOutOfRangeException(nameof(bits));
        }

        _bits = bits;
        _modulus = (1UL << (bits / 2)) - 1;
        _batch = bits == 64 ? 1 << 15 : 1 << 20;
    }

    protected override void AppendWords(ReadOnlySpan<byte> words)
    {
        ulong s1 = _sum1, s2 = _sum2;
        int count = words.Length / WordBytes;
        int at = 0;
        while (count > 0)
        {
            int n = Math.Min(_batch, count);
            for (int i = 0; i < n; i++, at += WordBytes)
            {
                s1 += ReadWord(words[at..]);
                s2 += s1;
            }

            s1 %= _modulus;
            s2 %= _modulus;
            count -= n;
        }

        _sum1 = s1;
        _sum2 = s2;
    }

    protected override byte[] Result() => HashBytes.FromNumber((_sum2 << (_bits / 2)) | _sum1, _bits / 8);
}
