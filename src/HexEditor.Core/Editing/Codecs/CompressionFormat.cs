using System.Buffers.Binary;

namespace HexEditor.Core.Editing.Codecs;

/// <summary>圧縮の形式 (EDIT-41 の仕様 1)。</summary>
public enum CompressionFormat
{
    Deflate,
    Zlib,
    Gzip,
    Brotli,
    Zstd,
    Lz4Frame,
    Lz4Block,
    Lzma,
    Xz,
}

/// <summary>形式ごとの性質 (圧縮レベルの範囲と既定、自動判別の手がかりの有無)。</summary>
public sealed record CompressionFormatInfo(CompressionFormat Format, int MinLevel, int DefaultLevel, int MaxLevel, bool HasSignature)
{
    /// <summary>LZ4 ブロック形式の圧縮の上限 (1 つのブロックで表すため、メモリ上で圧縮する。EDIT-41 の「巨大ファイル」の注記)。</summary>
    public const long Lz4BlockCompressLimit = 256L * 1024 * 1024;
}

/// <summary>形式の一覧と自動判別 (EDIT-41 の仕様 1・3)。</summary>
public static class CompressionFormats
{
    /// <summary>一覧に出す順。</summary>
    public static IReadOnlyList<CompressionFormatInfo> All { get; } =
    [
        // 既定のレベルは各形式の標準 (gzip・zlib は 6、Brotli は 11、zstd は 3、LZ4 は 1、xz・lzma は 6)。
        new(CompressionFormat.Deflate, 0, 6, 9, false),
        new(CompressionFormat.Zlib, 0, 6, 9, true),
        new(CompressionFormat.Gzip, 0, 6, 9, true),
        new(CompressionFormat.Brotli, 0, 11, 11, false),
        new(CompressionFormat.Zstd, 1, 3, 22, true),
        new(CompressionFormat.Lz4Frame, 1, 1, 12, true),
        new(CompressionFormat.Lz4Block, 1, 1, 12, false),
        new(CompressionFormat.Lzma, 0, 6, 9, true),
        new(CompressionFormat.Xz, 0, 6, 9, true),
    ];

    public static CompressionFormatInfo Info(CompressionFormat format) => All.First(f => f.Format == format);

    /// <summary>XZ・LZMA に使う liblzma を読み込めるか (読み込めなければ一覧で選べないようにする)。</summary>
    public static bool LzmaAvailable() => LibLzma.IsAvailable(out _);

    /// <summary>
    /// 先頭のバイトから、手がかりの一致する形式を確からしい順に返す (一致がなければ空)。手がかりが一致しても展開に失敗した場合は
    /// 呼び出し側が次の候補を試し、すべて失敗したら「自動判別できませんでした」とする (仕様 3)。
    /// </summary>
    public static IReadOnlyList<CompressionFormat> Detect(ReadOnlySpan<byte> head)
    {
        var result = new List<CompressionFormat>();
        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B)
        {
            result.Add(CompressionFormat.Gzip);
        }

        if (head.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == 0xFD2FB528)
        {
            result.Add(CompressionFormat.Zstd);
        }

        if (head.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == Lz4Decoder.FrameMagic)
        {
            result.Add(CompressionFormat.Lz4Frame);
        }

        if (head.Length >= 6 && head[..6].SequenceEqual((ReadOnlySpan<byte>)[0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]))
        {
            result.Add(CompressionFormat.Xz);
        }

        if (IsZlibHeader(head))
        {
            result.Add(CompressionFormat.Zlib);
        }

        if (IsLzmaHeader(head))
        {
            result.Add(CompressionFormat.Lzma);
        }

        return result;
    }

    /// <summary>zlib のヘッダ (CMF の方式 8・窓 32 KiB 以下、CMF・FLG の 16 ビット値が 31 の倍数)。</summary>
    public static bool IsZlibHeader(ReadOnlySpan<byte> head) =>
        head.Length >= 2 && (head[0] & 0x0F) == 8 && (head[0] >> 4) <= 7 && ((head[0] << 8) | head[1]) % 31 == 0;

    /// <summary>
    /// LZMA (<c>.lzma</c>) のヘッダ: プロパティ (lc + lp × 9 + pb × 45 &lt; 225、多くは 0x5D)、辞書の大きさ (2^n または 2^n + 2^(n−1)、
    /// 4 KiB 以上)、展開後の大きさ (不明 = すべて FF、または 256 GiB 未満)。
    /// </summary>
    public static bool IsLzmaHeader(ReadOnlySpan<byte> head)
    {
        if (head.Length < 13 || head[0] >= 225)
        {
            return false;
        }

        uint dict = BinaryPrimitives.ReadUInt32LittleEndian(head[1..]);
        uint lowest = dict & (~dict + 1);
        bool dictOk = dict == uint.MaxValue || (dict >= 4096 && (dict == lowest || dict == lowest * 3));
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(head[5..]);
        return dictOk && (size == ulong.MaxValue || size < (1UL << 38));
    }
}
