using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>
/// 検索のテストデータ (docs/test/cases/04-search.md の表) をメモリ上に作る。どれも小さいため、ファイルにせず
/// <see cref="MemoryByteSource"/> にする。あわせて、テストの「基準の実装」(1 バイトずつ照合する単純な検索) を持つ。
/// </summary>
internal static class SearchTestData
{
    /// <summary>TD-FIND-CHUNK-16M の 16 バイトのパターン P。</summary>
    public static readonly byte[] ChunkPattern = [0x5A, 0xA5, 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54];

    /// <summary>TD-FIND-CHUNK-16M で P を置く位置。</summary>
    public static readonly long[] ChunkPositions = [0x3FFFF8, 0x7FFFF0, 0x800000, 0xBFFFFF, 0xFFFFF0];

    private static readonly Lazy<byte[]> ChunkData = new(() =>
    {
        byte[] data = new byte[16 * 1024 * 1024];
        foreach (long p in ChunkPositions)
        {
            ChunkPattern.CopyTo(data, p);
        }

        return data;
    });

    private static readonly Lazy<byte[]> Random16M = new(() =>
    {
        byte[] data = new byte[16 * 1024 * 1024];
        TestDataCatalog.Expected("TD-RANDOM-16M", 0, data);
        return data;
    });

    /// <summary>TD-FIND-CHUNK-16M: すべて 00 の中に P を 5 か所。</summary>
    public static byte[] Chunk16M => ChunkData.Value;

    /// <summary>TD-RANDOM-16M (固定の種の乱数)。</summary>
    public static byte[] RandomData16M => Random16M.Value;

    /// <summary>TD-FIND-HITS-1000: すべて 00 の中に、k × 1,024 (k = 0〜999) から `12 34 56 78`。</summary>
    public static byte[] Hits1000()
    {
        byte[] data = new byte[1024 * 1024];
        for (int k = 0; k < 1000; k++)
        {
            data[k * 1024] = 0x12;
            data[k * 1024 + 1] = 0x34;
            data[k * 1024 + 2] = 0x56;
            data[k * 1024 + 3] = 0x78;
        }

        return data;
    }

    /// <summary>TD-FIND-HITS-1500K: `AB CD 00 00` を 1,500,000 回繰り返したもの。</summary>
    public static byte[] Hits1500K()
    {
        byte[] data = new byte[6_000_000];
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i] = 0xAB;
            data[i + 1] = 0xCD;
        }

        return data;
    }

    /// <summary>TD-FIND-AAAA: 0x00〜0x03 は `41 41 41 41`、0x20〜0x21 は `41 41`、それ以外は 00 (64 バイト)。</summary>
    public static byte[] Aaaa()
    {
        byte[] data = new byte[64];
        data[0] = data[1] = data[2] = data[3] = 0x41;
        data[0x20] = data[0x21] = 0x41;
        return data;
    }

    /// <summary>TD-EDIT-SAMPLE (03-editing.md で定義。32 バイト)。</summary>
    public static byte[] EditSample()
    {
        byte[] data = new byte[32];
        new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x0A, 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0xFF, 0x48, 0x69 }.CopyTo(data, 0);
        return data;
    }

    public static Document Doc(byte[] data) => new(new MemoryByteSource(data), Options());

    /// <summary>すべて検索の一致の開始位置の列。</summary>
    public static long[] Offsets(SearchResults results) => results.Matches.Select(m => m.Offset).ToArray();

    /// <summary>
    /// 基準の実装: データ全体を 1 つの配列として、先頭から 1 バイトずつ照合する。範囲を指定すると、範囲の中に収まる一致だけを返す。
    /// </summary>
    public static List<SearchMatch> Naive(byte[] data, SearchPattern pattern, bool overlapping = true, IEnumerable<SearchRange>? ranges = null)
    {
        var result = new List<SearchMatch>();
        foreach (SearchRange r in ranges ?? [new SearchRange(0, data.Length)])
        {
            long next = r.Offset;
            for (long i = r.Offset; i < Math.Min(r.End, data.Length); i++)
            {
                if (i < next || i % pattern.Alignment != 0 || (pattern.IsLiteral && data[i] != pattern.Bytes[0]))
                {
                    continue;
                }

                int len = pattern.MatchLength(data.AsSpan((int)i, (int)(Math.Min(r.End, data.Length) - i)));
                if (len >= 0)
                {
                    result.Add(new SearchMatch(i, len));
                    next = overlapping ? i + 1 : i + len;
                }
            }
        }

        return result;
    }
}
