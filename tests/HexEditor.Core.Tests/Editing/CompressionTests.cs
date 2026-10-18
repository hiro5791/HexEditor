using System.IO.Compression;
using HexEditor.Core.Editing.Codecs;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Editing;

/// <summary>EDIT-41 圧縮と展開 (各形式の往復、自動判別、圧縮データの終わり、壊れたデータ、チェックサム、展開の大きさの確認)。</summary>
public sealed class CompressionTests
{
    private static Document Open(byte[] data) => new(new MemoryByteSource(data), Options());

    private static byte[] Compress(byte[] data, CompressionFormat format, int level)
    {
        using Document doc = Open(data);
        using EditContent content = CompressionEngine.Compress(doc.Current, 0, data.Length, new CompressionOptions(format, level), ContentSink.For(doc));
        return ReadContent(content);
    }

    internal static byte[] ReadContent(EditContent content)
    {
        using var doc = new Document(MemoryByteSource.CreateEmpty("c"), Options());
        doc.InsertContent(0, content);
        return ReadAll(doc.Current);
    }

    private static DecompressionResult Decompress(Document doc, CompressionFormat? format, long offset = 0, long? length = null,
        long? limit = null, Func<long, bool>? ask = null)
    {
        long len = length ?? doc.Length - offset;
        CodecOutput NewOutput() => new(ContentSink.For(doc)) { Limit = limit, LimitReached = ask };
        return format is { } f
            ? CompressionEngine.Decompress(doc.Current, offset, len, f, NewOutput())
            : CompressionEngine.DecompressAuto(doc.Current, offset, len, NewOutput) ?? throw new Xunit.Sdk.XunitException("自動判別できませんでした");
    }

    private static byte[] Inputs(string id) => File.ReadAllBytes(TestDataCatalog.Get(id));

    public static TheoryData<CompressionFormat> Formats() => [.. Enum.GetValues<CompressionFormat>()];

    [Theory]
    [MemberData(nameof(Formats))]
    [Trait(TC, "TC-EDIT-41-01")]
    public void Every_format_round_trips_at_min_default_and_max_level(CompressionFormat format)
    {
        CompressionFormatInfo info = CompressionFormats.Info(format);
        foreach (string id in new[] { "TD-EMPTY", "TD-BYTES-256", "TD-TEXT-ASCII", "TD-RANDOM-16M" })
        {
            byte[] data = Inputs(id);
            foreach (int level in new[] { info.MinLevel, info.DefaultLevel, info.MaxLevel }.Distinct())
            {
                // 最大レベルの 16 MiB の乱数は時間がかかる形式 (Brotli・zstd・xz・lzma) では標準のレベルまでにする。
                if (id == "TD-RANDOM-16M" && level == info.MaxLevel && level != info.DefaultLevel
                    && format is CompressionFormat.Brotli or CompressionFormat.Zstd or CompressionFormat.Xz or CompressionFormat.Lzma)
                {
                    continue;
                }

                byte[] packed = Compress(data, format, level);
                using Document doc = Open(packed);

                // 手順 2: 形式を指定して展開する。
                DecompressionResult result = Decompress(doc, format);
                Assert.False(result.ChecksumMismatch);
                Assert.Equal(packed.Length, result.ConsumedInput);
                Assert.Equal(data, ReadContent(result.Content!));

                // 手順 3: 手がかりのある形式は自動判別で展開でき、判別の結果が圧縮に使った形式と一致する。
                if (info.HasSignature)
                {
                    DecompressionResult auto = Decompress(doc, null);
                    Assert.Equal(format, auto.Format);
                    Assert.Equal(data, ReadContent(auto.Content!));
                }
            }
        }
    }

    [Theory]
    [InlineData("TD-EDIT-EXT-GZ", CompressionFormat.Gzip, true)]
    [InlineData("TD-EDIT-EXT-ZLIB", CompressionFormat.Zlib, true)]
    [InlineData("TD-EDIT-EXT-ZST", CompressionFormat.Zstd, true)]
    [InlineData("TD-EDIT-EXT-LZ4", CompressionFormat.Lz4Frame, true)]
    [InlineData("TD-EDIT-EXT-XZ", CompressionFormat.Xz, true)]
    [InlineData("TD-EDIT-EXT-LZMA", CompressionFormat.Lzma, true)]
    [InlineData("TD-EDIT-EXT-BR", CompressionFormat.Brotli, false)]
    [InlineData("TD-EDIT-EXT-DEFLATE", CompressionFormat.Deflate, false)]
    [Trait(TC, "TC-EDIT-41-02")]
    public void Data_made_by_other_tools_is_detected_or_asks_for_the_format(string id, CompressionFormat format, bool detectable)
    {
        byte[] expected = Inputs("TD-TEXT-ASCII");
        using Document doc = Open(Inputs(id));
        DecompressionResult? auto = CompressionEngine.DecompressAuto(doc.Current, 0, doc.Length, () => new CodecOutput(ContentSink.For(doc)));
        if (detectable)
        {
            Assert.NotNull(auto);
            Assert.Equal(format, auto!.Format);
            Assert.Equal(expected, ReadContent(auto.Content!));
            return;
        }

        // 手がかりのない形式は自動判別できず (形式の選択を求める)、形式を指定すると展開できる。
        Assert.Null(auto);
        Assert.Equal(expected, ReadContent(Decompress(doc, format).Content!));
    }

    [Fact]
    public void Probe_of_the_zip_entry_finds_the_deflate_range()
    {
        // TC-EDIT-41-03 の Core の部分: 0x28 から展開を試み、圧縮データの長さ (ヘッダの圧縮後サイズ) と展開後の大きさを返す。
        byte[] zip = Inputs("TD-EDIT-ZIP-1M");
        using Document doc = Open(zip);
        long compressedSize = BitConverter.ToUInt32(zip, 0x12);
        DecompressionResult r = CompressionEngine.Decompress(doc.Current, 0x28, doc.Length - 0x28, CompressionFormat.Deflate, new CodecOutput(null));
        Assert.Equal(compressedSize, r.ConsumedInput);
        Assert.Equal(1024 * 1024, r.OutputLength);
        Assert.Null(r.Content);
    }

    [Fact]
    public void Detection_without_a_signature_returns_null()
    {
        byte[] packed = Compress(Inputs("TD-TEXT-ASCII"), CompressionFormat.Deflate, 9);
        using Document doc = Open(packed);
        Assert.Null(CompressionEngine.DecompressAuto(doc.Current, 0, doc.Length, () => new CodecOutput(ContentSink.For(doc))));
    }

    [Theory]
    [InlineData(CompressionFormat.Deflate)]
    [InlineData(CompressionFormat.Zlib)]
    [InlineData(CompressionFormat.Gzip)]
    [InlineData(CompressionFormat.Brotli)]
    [InlineData(CompressionFormat.Zstd)]
    [InlineData(CompressionFormat.Lz4Frame)]
    [InlineData(CompressionFormat.Lzma)]
    [InlineData(CompressionFormat.Xz)]
    public void Decompression_stops_at_the_end_of_the_compressed_data(CompressionFormat format)
    {
        // 仕様 4: 後ろに余ったデータは変えず、圧縮データの長さを返す。
        byte[] data = Inputs("TD-TEXT-ASCII");
        byte[] packed = Compress(data, format, CompressionFormats.Info(format).DefaultLevel);
        byte[] withTail = [.. packed, .. Enumerable.Range(0, 300).Select(i => (byte)(i * 7))];
        using Document doc = Open(withTail);
        DecompressionResult result = Decompress(doc, format);
        Assert.Equal(packed.Length, result.ConsumedInput);
        Assert.Equal(data, ReadContent(result.Content!));
    }

    [Fact]
    public void Corrupt_data_reports_the_offset_and_keeps_what_was_decompressed()
    {
        // 途中で終わっている圧縮データ: 誤りの位置と、それまでに展開できた分 (元のデータの先頭と一致する) を返す。
        byte[] data = Inputs("TD-RANDOM-16M")[..(4 * 1024 * 1024)];
        byte[] packed = Compress(data, CompressionFormat.Deflate, 1);
        using Document doc = Open(packed[..(packed.Length / 2)]);
        var e = Assert.ThrowsAny<CompressedDataException>(() => Decompress(doc, CompressionFormat.Deflate));
        Assert.Equal(CompressedDataError.Truncated, e.Error);
        Assert.InRange(e.InputOffset, packed.Length / 2 - 16, packed.Length / 2);
        Assert.NotNull(e.Partial);
        byte[] got = ReadContent(e.Partial!);
        Assert.True(got.Length > 1024 * 1024);
        Assert.Equal(data.AsSpan(0, got.Length).ToArray(), got);

        // 不正なブロックの種類 (BTYPE = 11)。
        using Document bad = Open([0x07, 0x00]);
        Assert.Equal(CompressedDataError.Corrupt, Assert.ThrowsAny<CompressedDataException>(() => Decompress(bad, CompressionFormat.Deflate)).Error);
    }

    [Theory]
    [InlineData(CompressionFormat.Gzip)]
    [InlineData(CompressionFormat.Zlib)]
    [InlineData(CompressionFormat.Zstd)]
    public void Checksum_mismatch_is_reported_with_the_result(CompressionFormat format)
    {
        byte[] data = Inputs("TD-TEXT-ASCII");
        byte[] packed = Compress(data, format, CompressionFormats.Info(format).DefaultLevel);

        // チェックサムは末尾 (gzip は CRC-32 と ISIZE の 8 バイト、zlib・zstd は末尾 4 バイト)。
        packed[format == CompressionFormat.Gzip ? ^8 : ^1] ^= 0x01;
        using Document doc = Open(packed);
        DecompressionResult result = Decompress(doc, format);
        Assert.True(result.ChecksumMismatch);
        Assert.Equal(data, ReadContent(result.Content!));
    }

    [Fact]
    public void Gzip_header_can_contain_the_file_name_and_time()
    {
        byte[] data = Inputs("TD-TEXT-ASCII");
        using Document doc = Open(data);
        var options = new CompressionOptions(CompressionFormat.Gzip, 6) { GzipFileName = "text.txt", GzipModified = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000) };
        using EditContent content = CompressionEngine.Compress(doc.Current, 0, data.Length, options, ContentSink.For(doc));
        byte[] packed = ReadContent(content);
        Assert.Equal(0x08, packed[3]);
        Assert.Equal(1_700_000_000u, BitConverter.ToUInt32(packed, 4));
        Assert.Equal("text.txt\0"u8.ToArray(), packed[10..19]);

        // .NET の GZipStream でも読める。
        using var gz = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
        var ms = new MemoryStream();
        gz.CopyTo(ms);
        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public void Output_limit_asks_once_and_abort_disposes_the_partial_output()
    {
        // 仕様 7 (小さな上限で): 1 MiB の 00 を zlib で圧縮し、上限 64 KiB で展開する。
        byte[] packed = Compress(new byte[1024 * 1024], CompressionFormat.Zlib, 9);
        using Document doc = Open(packed);
        int asked = 0;
        long askedAt = 0;
        Assert.Throws<OutputLimitAbortedException>(() => Decompress(doc, CompressionFormat.Zlib, limit: 64 * 1024, ask: n =>
        {
            asked++;
            askedAt = n;
            return false;
        }));
        Assert.Equal(1, asked);
        Assert.True(askedAt > 64 * 1024);

        // 「続ける」なら最後まで展開する。
        asked = 0;
        DecompressionResult result = Decompress(doc, CompressionFormat.Zlib, limit: 64 * 1024, ask: _ => ++asked > 0);
        Assert.Equal(1, asked);
        Assert.Equal(1024 * 1024, result.OutputLength);
    }

    [Fact]
    public void Lz4_block_compression_is_limited()
    {
        Assert.Equal(256L * 1024 * 1024, CompressionFormatInfo.Lz4BlockCompressLimit);
    }

    [Fact]
    public void Lzma_with_a_known_size_header_is_decoded()
    {
        // LZMA SDK の lzma e は展開後の大きさをヘッダに書く (liblzma は不明 (FF…) と終わりの印)。大きさを書き込んだものも読める。
        byte[] data = Inputs("TD-TEXT-ASCII");
        byte[] packed = Compress(data, CompressionFormat.Lzma, 6);
        BitConverter.TryWriteBytes(packed.AsSpan(5, 8), (long)data.Length);
        using Document doc = Open(packed);
        DecompressionResult result = Decompress(doc, null);
        Assert.Equal(CompressionFormat.Lzma, result.Format);
        Assert.Equal(data, ReadContent(result.Content!));
    }
}
