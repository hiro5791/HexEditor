using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;

namespace HexEditor.TestData;

/// <summary>
/// 暗号化・圧縮 (EDIT-40・EDIT-41)、派生ドキュメント (EDIT-46)、YARA (FIND-33) のテストデータ (cases/03-editing.md・cases/04-search.md の表)。
/// <para>
/// TD-EDIT-EXT-* は他のツールで作ったデータの代わりに、ツールと同じ形式・設定を、アプリの展開とは別の実装 (.NET の zlib・Brotli、ZstdSharp、
/// K4os.Compression.LZ4、liblzma) で作る (この PC にツールを入れずに作るため)。ツールそのものの出力での確かめは CI の結合テストで行う。
/// </para>
/// </summary>
public static partial class TestDataCatalog
{
    /// <summary>TD-FIND-YARA-128M の文字列の位置 (64 MiB − 16) と内容。</summary>
    public const long YaraBoundaryOffset = 64 * MiB - 16;

    public static readonly byte[] YaraBoundaryString = Encoding.ASCII.GetBytes("YARA-BOUNDARY-TEST-STRING-000001");

    private static IEnumerable<TestDataItem> DataCodecItems() =>
    [
        new("TD-EDIT-ZIP-1M", 0, "エントリ random.bin (TD-RANDOM-16M の先頭 1 MiB) 1 つの ZIP", path => WriteAll(path, Zip1M()))
        {
            ComputeLength = () => Zip1M().Length,
        },
        new("TD-EDIT-ZBOMB", 0, "5 GiB の 00 を zlib (レベル 9) で圧縮したデータ", WriteZbomb) { ComputeLength = ZbombLength },
        new("TD-EDIT-EXT-GZ", 0, "TD-TEXT-ASCII を gzip -9 -n と同じ形式で圧縮", path => WriteAll(path, ExtGzip())) { ComputeLength = () => ExtGzip().Length },
        new("TD-EDIT-EXT-ZLIB", 0, "TD-TEXT-ASCII を zlib.compress(data, 9) と同じ形式で圧縮", path => WriteAll(path, ExtZlib())) { ComputeLength = () => ExtZlib().Length },
        new("TD-EDIT-EXT-ZST", 0, "TD-TEXT-ASCII を zstd -19 と同じ形式で圧縮", path => WriteAll(path, ExtZstd())) { ComputeLength = () => ExtZstd().Length },
        new("TD-EDIT-EXT-LZ4", 0, "TD-TEXT-ASCII を lz4 -9 (フレーム) と同じ形式で圧縮", path => WriteAll(path, ExtLz4())) { ComputeLength = () => ExtLz4().Length },
        new("TD-EDIT-EXT-XZ", 0, "TD-TEXT-ASCII を xz (プリセット 9) で圧縮", path => WriteAll(path, ExtXz())) { ComputeLength = () => ExtXz().Length },
        new("TD-EDIT-EXT-LZMA", 0, "TD-TEXT-ASCII を .lzma (ヘッダに展開後の大きさ) で圧縮", path => WriteAll(path, ExtLzma())) { ComputeLength = () => ExtLzma().Length },
        new("TD-EDIT-EXT-BR", 0, "TD-TEXT-ASCII を brotli -q 11 と同じ形式で圧縮", path => WriteAll(path, ExtBrotli())) { ComputeLength = () => ExtBrotli().Length },
        new("TD-EDIT-EXT-DEFLATE", 0, "TD-TEXT-ASCII を raw deflate (レベル 9) で圧縮", path => WriteAll(path, ExtDeflate())) { ComputeLength = () => ExtDeflate().Length },
        new("TD-FIND-YARA-128M", 128 * MiB, "00 の中の 0x3FFFFF0 に YARA-BOUNDARY-TEST-STRING-000001 (スパース)",
            path => WriteSparse(path, 128 * MiB, YaraBoundaryOffset, YaraBoundaryString.Length, (o, s) => YaraBoundaryString.AsSpan((int)(o - YaraBoundaryOffset), s.Length).CopyTo(s))),
    ];

    /// <summary>TD-TEXT-ASCII の内容。</summary>
    public static byte[] TextAscii() => Text(AsciiLines, Encoding.ASCII, 4 * KiB, bom: false);

    // ---- ZIP ----

    private static byte[]? s_zip;

    /// <summary>TD-EDIT-ZIP-1M: ローカルファイルヘッダはオフセット 0、圧縮データは 0x28 から (ヘッダ 30 + 名前 10)。</summary>
    public static byte[] Zip1M()
    {
        if (s_zip is not null)
        {
            return s_zip;
        }

        byte[] content = new byte[MiB];
        Random(RandomSeed, 0, content);
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.CreateEntry("random.bin", CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using Stream s = entry.Open();
            s.Write(content);
        }

        return s_zip = ms.ToArray();
    }

    // ---- 圧縮爆弾 ----

    private static long? s_zbombLength;

    private static void WriteZbomb(string path)
    {
        using FileStream file = File.Create(path);
        using (var z = new ZLibStream(file, new ZLibCompressionOptions { CompressionLevel = 9 }, leaveOpen: true))
        {
            byte[] zeros = new byte[(int)MiB];
            for (long done = 0; done < 5 * GiB; done += zeros.Length)
            {
                z.Write(zeros);
            }
        }

        s_zbombLength = file.Length;
    }

    /// <summary>圧縮爆弾の長さ (作らないと分からないので、キャッシュのファイルがあればその長さ)。</summary>
    private static long ZbombLength()
    {
        if (s_zbombLength is long n)
        {
            return n;
        }

        string cached = Path.Combine(CacheDirectory, "TD-EDIT-ZBOMB.bin");
        return File.Exists(cached) && new FileInfo(cached).Length > 1024 ? new FileInfo(cached).Length : -1;
    }

    // ---- 他のツールの代わりのデータ ----

    private static byte[] ExtGzip()
    {
        // gzip -9 -n: 名前・時刻なし、XFL = 2 (最大の圧縮)、OS = 3 (Unix)。
        byte[] data = TextAscii();
        var ms = new MemoryStream();
        ms.Write([0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 2, 3]);
        using (var d = new DeflateStream(ms, new ZLibCompressionOptions { CompressionLevel = 9 }, leaveOpen: true))
        {
            d.Write(data);
        }

        Span<byte> trailer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, System.IO.Hashing.Crc32.HashToUInt32(data));
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], (uint)data.Length);
        ms.Write(trailer);
        return ms.ToArray();
    }

    private static byte[] ExtZlib()
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, new ZLibCompressionOptions { CompressionLevel = 9 }, leaveOpen: true))
        {
            z.Write(TextAscii());
        }

        return ms.ToArray();
    }

    private static byte[] ExtDeflate()
    {
        var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, new ZLibCompressionOptions { CompressionLevel = 9 }, leaveOpen: true))
        {
            z.Write(TextAscii());
        }

        return ms.ToArray();
    }

    private static byte[] ExtBrotli()
    {
        var ms = new MemoryStream();
        using (var b = new BrotliStream(ms, new BrotliCompressionOptions { Quality = 11 }, leaveOpen: true))
        {
            b.Write(TextAscii());
        }

        return ms.ToArray();
    }

    private static byte[] ExtZstd()
    {
        // zstd -19: 内容の大きさとチェックサムを持つフレーム。
        using var compressor = new ZstdSharp.Compressor(19);
        compressor.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        return compressor.Wrap(TextAscii()).ToArray();
    }

    private static byte[] ExtLz4()
    {
        var ms = new MemoryStream();
        var settings = new LZ4EncoderSettings { CompressionLevel = LZ4Level.L09_HC, BlockSize = 4 * 1024 * 1024, ChainBlocks = true, ContentChecksum = true };
        using (Stream s = LZ4Stream.Encode(ms, settings, leaveOpen: true))
        {
            s.Write(TextAscii());
        }

        return ms.ToArray();
    }

    private static byte[] ExtXz() => Lzma.Encode(TextAscii(), xz: true, 9);

    /// <summary>LZMA SDK の lzma e と同じく、ヘッダに展開後の大きさを書く。</summary>
    private static byte[] ExtLzma()
    {
        byte[] data = TextAscii();
        byte[] packed = Lzma.Encode(data, xz: false, 6);
        BinaryPrimitives.WriteInt64LittleEndian(packed.AsSpan(5), data.Length);
        return packed;
    }

    /// <summary>liblzma の最小の呼び出し (テストデータの生成だけに使う)。</summary>
    private static unsafe class Lzma
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Stream
        {
            public byte* NextIn;
            public nuint AvailIn;
            public ulong TotalIn;
            public byte* NextOut;
            public nuint AvailOut;
            public ulong TotalOut;
            public fixed long Rest[14];
        }

        [DllImport("liblzma", EntryPoint = "lzma_easy_encoder")]
        private static extern int EasyEncoder(Stream* s, uint preset, int check);

        [DllImport("liblzma", EntryPoint = "lzma_alone_encoder")]
        private static extern int AloneEncoder(Stream* s, void* options);

        [DllImport("liblzma", EntryPoint = "lzma_lzma_preset")]
        private static extern byte LzmaPreset(void* options, uint preset);

        [DllImport("liblzma", EntryPoint = "lzma_code")]
        private static extern int Code(Stream* s, int action);

        [DllImport("liblzma", EntryPoint = "lzma_end")]
        private static extern void End(Stream* s);

        public static byte[] Encode(byte[] data, bool xz, int preset)
        {
            Stream s = default;
            byte* options = stackalloc byte[512];
            new Span<byte>(options, 512).Clear();
            int ret = xz ? EasyEncoder(&s, (uint)preset, 4) : LzmaPreset(options, (uint)preset) == 0 ? AloneEncoder(&s, options) : -1;
            if (ret != 0)
            {
                throw new InvalidOperationException("liblzma: " + ret);
            }

            byte[] output = new byte[data.Length + 65536];
            try
            {
                fixed (byte* i = data)
                fixed (byte* o = output)
                {
                    s.NextIn = i;
                    s.AvailIn = (nuint)data.Length;
                    s.NextOut = o;
                    s.AvailOut = (nuint)output.Length;
                    ret = Code(&s, 3);
                    if (ret != 1)
                    {
                        throw new InvalidOperationException("liblzma: " + ret);
                    }

                    return output[..(output.Length - (int)s.AvailOut)];
                }
            }
            finally
            {
                End(&s);
            }
        }
    }
}

/// <summary>TD-FIND-YARA-RULES (4 つのルールのファイル)。</summary>
public static class YaraRuleFiles
{
    public const string Mz = "rule mz { strings: $a = \"MZ\" condition: $a at 0 }";

    /// <summary>3 行目が構文エラー (at の後に式がない)。</summary>
    public const string Bad = "rule bad {\nstrings: $a = \"MZ\"\ncondition: $a at\n}\n";

    public const string Boundary = "rule b { strings: $s = \"YARA-BOUNDARY-TEST-STRING-000001\" condition: $s }";

    public const string Added = "rule added { strings: $s = \"ADDED-BY-EDIT\" condition: $s }";

    /// <summary>TD-INSP-YARA-PK (ZIP のローカルファイルヘッダ <c>50 4B 03 04</c>)。</summary>
    public const string ZipLocalHeader = "rule zip_local_header { strings: $pk = { 50 4B 03 04 } condition: $pk }";

    /// <summary><paramref name="directory"/> に TD-INSP-YARA-PK (<c>pk.yar</c>) を作り、ファイルのパスを返す。</summary>
    public static string WriteZipLocalHeader(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "pk.yar");
        File.WriteAllText(path, ZipLocalHeader + "\n");
        return path;
    }

    /// <summary><paramref name="directory"/> に mz.yar・bad.yar・boundary.yar・added.yar を作り、フォルダを返す。</summary>
    public static string Write(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "mz.yar"), Mz + "\n");
        File.WriteAllText(Path.Combine(directory, "bad.yar"), Bad);
        File.WriteAllText(Path.Combine(directory, "boundary.yar"), Boundary + "\n");
        File.WriteAllText(Path.Combine(directory, "added.yar"), Added + "\n");
        return directory;
    }
}
