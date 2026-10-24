using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace HexEditor.TestData;

// cases/06-analysis.md の表のファイルマップ (ANA-33・ANA-34) のテストデータ。TD-ANA-MAP-ALT-4G は、テストが内容を計算する仮想の
// データソースで作る (ここでは作らない。内容は <see cref="TestDataCatalog.MapAlternating"/>)。
public static partial class TestDataCatalog
{
    /// <summary>TD-ANA-MAP-MIX の長さ (0x370000)。</summary>
    public const long MapMixLength = 0x370000;

    private static readonly Lazy<(byte[] Data, IReadOnlyList<MapPart> Parts)> MapMixData = new(BuildMapMix);

    /// <summary>TD-ANA-MAP-MIX の部分 1 つ (名前、開始オフセット、長さ)。</summary>
    public sealed record MapPart(string Name, long Offset, long Length)
    {
        public long End => Offset + Length;
    }

    private static IEnumerable<TestDataItem> FileMapItems() =>
    [
        new("TD-ANA-MAP-MIX", MapMixLength, "PE・テキスト・PNG・zlib・乱数・64 バイトのレコード・ZIP を決まった位置に置いたもの", path =>
        {
            WriteAll(path, MapMixData.Value.Data);

            // 各部分の開始オフセットと長さを期待値の JSON に書く (テストケースの定義の表)。
            File.WriteAllText(path + ".expected.json", JsonSerializer.Serialize(MapMixData.Value.Parts.Select(p => new { name = p.Name, offset = p.Offset, length = p.Length })));
        }),
        new("TD-ANA-MAP-XOR", MapMixLength, "TD-ANA-MAP-MIX のすべてのバイトを 5A と XOR したもの",
            path => WriteAll(path, [.. MapMixData.Value.Data.Select(b => (byte)(b ^ 0x5A))])),
        new("TD-ANA-MAP-PNG40", 160 * KiB, "すべて 00 のデータの 0x1000 × k (k = 0〜39) に TD-PNG を置いたもの", path => WriteAll(path, MapPng40())),
    ];

    /// <summary>TD-ANA-MAP-MIX の内容。</summary>
    public static byte[] MapMix() => MapMixData.Value.Data;

    /// <summary>TD-ANA-MAP-MIX の各部分 (定義の表の順。名前は pe・text・png・zlib・random・records・zip)。</summary>
    public static IReadOnlyList<MapPart> MapMixParts => MapMixData.Value.Parts;

    private static (byte[] Data, IReadOnlyList<MapPart> Parts) BuildMapMix()
    {
        byte[] data = new byte[MapMixLength];
        var parts = new List<MapPart>();
        void Put(string name, long offset, byte[] bytes, long? limit = null)
        {
            if (limit is long end && offset + bytes.Length > end)
            {
                throw new InvalidOperationException($"TD-ANA-MAP-MIX の {name} が次の部分に重なります ({bytes.Length} バイト)。");
            }

            bytes.CopyTo(data, offset);
            parts.Add(new MapPart(name, offset, bytes.Length));
        }

        // (1) 0x0: TD-PE-X64。
        Put("pe", 0, PeX64(), 0x10000);

        // (2) 0x10000: TD-TEXT-ASCII を 64 回繰り返した 256 KiB (0x50000 まで)。
        Put("text", 0x10000, RepeatBytes(Text(AsciiLines, Encoding.ASCII, 4 * KiB, bom: false), 64), 0x50000);

        // (3) 0x50000: TD-PNG。
        Put("png", 0x50000, Png16(), 0x60000);

        // (4) 0x60000: TD-ANA-CLASSES の (3) の英文の先頭 2 MiB を ZLibStream (Optimal、ヘッダ 78 DA) で圧縮したもの (1 MiB 未満)。
        byte[] english = EnglishText().AsSpan(0, (int)(2 * MiB)).ToArray();
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(english);
        }

        byte[] compressed = output.ToArray();
        compressed[1] = 0xDA;
        if (compressed.Length >= MiB)
        {
            throw new InvalidOperationException($"TD-ANA-MAP-MIX の zlib が 1 MiB 以上です ({compressed.Length} バイト)。");
        }

        Put("zlib", 0x60000, compressed, 0x160000);

        // (5) 0x160000: TD-ANA-CRYPT-10M の先頭 1 MiB (0x260000 まで)。
        byte[] random = new byte[MiB];
        AesCtr(CryptKey, 0, random);
        Put("random", 0x160000, random, 0x260000);

        // (6) 0x260000: TD-ANA-REC64 (0x360000 まで)。
        Put("records", 0x260000, Rec64(), 0x360000);

        // (7) 0x360000: TD-ZIP。
        Put("zip", 0x360000, ZipSample(), MapMixLength);
        return (data, parts);
    }

    /// <summary>TD-ANA-MAP-PNG40: すべて 00 の 160 KiB の、オフセット 0x1000 × k (k = 0〜39) に TD-PNG を置いたもの。</summary>
    public static byte[] MapPng40()
    {
        byte[] data = new byte[160 * KiB];
        byte[] png = Png16();
        for (int k = 0; k < 40; k++)
        {
            png.CopyTo(data, k * 0x1000);
        }

        return data;
    }

    /// <summary>
    /// TD-ANA-MAP-ALT-4G の内容 (4 GiB の仮想のデータ): 4 KiB のブロック b (0 始まり) が、b が偶数なら 00、奇数なら固定の種の乱数
    /// (種はブロックの番号から計算する)。
    /// </summary>
    public static void MapAlternating(long offset, Span<byte> destination)
    {
        int done = 0;
        while (done < destination.Length)
        {
            long at = offset + done;
            long block = at / (4 * KiB);
            int inBlock = (int)(at % (4 * KiB));
            int n = (int)Math.Min(destination.Length - done, (4 * KiB) - inBlock);
            if (block % 2 == 0)
            {
                destination.Slice(done, n).Clear();
            }
            else
            {
                Random(0xA17_0000UL + (ulong)block, inBlock, destination.Slice(done, n));
            }

            done += n;
        }
    }

    /// <summary>TD-ANA-MAP-ALT-4G の長さ。</summary>
    public const long MapAlternatingLength = 4 * GiB;
}
