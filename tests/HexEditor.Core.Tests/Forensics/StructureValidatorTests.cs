using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using HexEditor.Core.Forensics.Carving;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Forensics;

/// <summary>FOR-18 の仕様 3 の組み込みの構造の検証。ファイルの後ろに別のデータが続いていても、ファイルの終端までの長さを求める。</summary>
public sealed class StructureValidatorTests
{
    private static readonly byte[] Garbage = [.. Enumerable.Range(0, 300).Select(i => (byte)(i * 7 + 3))];

    /// <summary>ファイルを 100 バイト目に置き、後ろにごみを付けて検証する。</summary>
    private static long? Measure(string name, byte[] file, bool trailing = true)
    {
        byte[] data = [.. new byte[100], .. file, .. (trailing ? Garbage : [])];
        var window = new ByteWindow(new MemoryCarveData(data), data.Length, 1 << 24);
        return StructureValidators.Find(name)!.Measure(window, 100);
    }

    private static void ExpectExact(string name, byte[] file)
    {
        Assert.Equal(file.Length, Measure(name, file));
        Assert.Equal(file.Length, Measure(name, file, trailing: false));
    }

    [Fact]
    public void ImageFormats()
    {
        ExpectExact("png", TestDataCatalog.Png16());
        ExpectExact("jpeg", TestDataCatalog.ForJpeg());
        ExpectExact("jpeg", TestDataCatalog.Jpeg16());
        ExpectExact("gif", Gif());
        ExpectExact("bmp", Bmp());
        ExpectExact("tiff", Tiff(false));
        ExpectExact("tiff", Tiff(true));
    }

    [Fact]
    public void TruncatedOrInvalidImagesAreRejected()
    {
        byte[] png = TestDataCatalog.Png16();
        Assert.Null(Measure("png", png[..^3], trailing: false));
        Assert.Null(Measure("png", png[..^12], trailing: false));
        byte[] jpeg = TestDataCatalog.ForJpeg();
        Assert.Null(Measure("jpeg", jpeg[..^2], trailing: false));
        Assert.Null(Measure("jpeg", [0xFF, 0xD8, 0xFF, 0xD9], trailing: false));
        Assert.Null(Measure("gif", Gif()[..^1], trailing: false));
        byte[] bmp = Bmp();
        bmp[14] = 7;
        Assert.Null(Measure("bmp", bmp));
        Assert.Null(Measure("tiff", Tiff(false)[..30], trailing: false));
    }

    [Fact]
    public void DocumentFormats()
    {
        ExpectExact("pdf", TestDataCatalog.ForPdf());
        ExpectExact("pdf", TestDataCatalog.Pdf());
        ExpectExact("zip", TestDataCatalog.ZipSample());
        ExpectExact("zip", TestDataCatalog.Zip3());
        ExpectExact("ole", Ole());
        ExpectExact("rtf", Encoding.ASCII.GetBytes("{\\rtf1\\ansi Hello \\{ {\\b bold} world \\} }"));
        ExpectExact("html", Encoding.ASCII.GetBytes("<!DOCTYPE html><HTML><body>x</body></HTML>\r\n"));
        ExpectExact("html", Encoding.ASCII.GetBytes("<html><body>x</body></html>\n"));
        ExpectExact("xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>\n<!DOCTYPE r [<!ENTITY a \"b\">]>\n<root a=\"x>y\"><a/><b><!-- </root> --><![CDATA[</root>]]>t</b></root>"));
        Assert.Null(Measure("xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><root><a></root>"), trailing: false));
        Assert.Null(Measure("rtf", Encoding.ASCII.GetBytes("{\\rtf1 {unclosed"), trailing: false));
    }

    [Fact]
    public void ZipWithATrailingCommentAndAZipInsideAZipUseTheOutermostCentralDirectory()
    {
        byte[] zip = TestDataCatalog.ZipSample();
        // 末尾のコメントを付ける: EOCD のコメントの長さを書き換える。
        byte[] commented = [.. zip, .. "hello"u8];
        BinaryPrimitives.WriteUInt16LittleEndian(commented.AsSpan(zip.Length - 2), 5);
        ExpectExact("zip", commented);

        // 前に別のデータがあっても、EOCD の整合性 (中央ディレクトリの位置) が合わない EOCD は使わない。
        byte[] fake = [.. zip[..(zip.Length - 22)], .. "PK\x05\x06"u8, .. new byte[18], .. zip[(zip.Length - 22)..]];
        Assert.Equal(fake.Length, Measure("zip", fake, trailing: false));
    }

    [Fact]
    public void ArchiveFormats()
    {
        ExpectExact("rar", Rar4());
        ExpectExact("7z", SevenZip());
        ExpectExact("gzip", Gzip());
        ExpectExact("bzip2", Bzip2());

        // 7z の開始ヘッダーの CRC が合わなければ拒否する。
        byte[] bad = SevenZip();
        bad[12] ^= 1;
        Assert.Null(Measure("7z", bad));
        // 壊れた deflate は拒否する。
        byte[] gz = Gzip();
        gz[12] = 0xFF;
        gz[13] = 0xFF;
        Assert.Null(Measure("gzip", gz[..14], trailing: false));
    }

    [Fact]
    public void MediaFormats()
    {
        ExpectExact("mp3", Mp3());
        ExpectExact("mp4", Mp4());
        Assert.Null(Measure("mp3", Mp3()[..60], trailing: false));
    }

    [Fact]
    public void ExecutableAndDatabaseFormats()
    {
        // 小さな PE のサンプル: セクションの生データの終わり (0x400) まで。後ろの詰め物 (8 KiB まで) は含めない。
        Assert.Equal(0x400, Measure("pe", TestDataCatalog.PeX64()));
        ExpectExact("elf", Elf());
        ExpectExact("sqlite", Sqlite());
        ExpectExact("evtx", Evtx());
        ExpectExact("lnk", Lnk());
        byte[] sqlite = Sqlite();
        BinaryPrimitives.WriteUInt32BigEndian(sqlite.AsSpan(92), 99);
        Assert.Null(Measure("sqlite", sqlite));
    }

    // ---- 小さなサンプルの組み立て ----

    private static byte[] Gif() =>
    [
        .. "GIF89a"u8, 1, 0, 1, 0, 0x00, 0, 0,
        0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0x00, 2, 2, 0x4C, 0x01, 0,
        0x3B,
    ];

    private static byte[] Bmp()
    {
        byte[] bmp = new byte[70];
        "BM"u8.CopyTo(bmp);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), 70);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(18), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 24);
        return bmp;
    }

    private static byte[] Tiff(bool big)
    {
        byte[] t = new byte[66];
        void U16(int at, int v)
        {
            if (big)
            {
                BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(at), (ushort)v);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(at), (ushort)v);
            }
        }

        void U32(int at, int v)
        {
            if (big)
            {
                BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(at), (uint)v);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(at), (uint)v);
            }
        }

        (big ? "MM\0*"u8 : "II*\0"u8).CopyTo(t);
        U32(4, 8);
        U16(8, 3);
        int e = 10;
        foreach ((int tag, int type, int count, int value) in new[] { (256, 3, 1, 1), (273, 4, 1, 50), (279, 4, 1, 16) })
        {
            U16(e, tag);
            U16(e + 2, type);
            U32(e + 4, count);
            if (type == 3)
            {
                U16(e + 8, value);
            }
            else
            {
                U32(e + 8, value);
            }

            e += 12;
        }

        U32(46, 0);
        return t;
    }

    private static byte[] Ole()
    {
        byte[] f = new byte[512 * 3];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(f, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x18), 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x1A), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x1C), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x1E), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x2C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x30), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x44), 0xFFFFFFFE);
        for (int i = 0; i < 109; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x4C + 4 * i), i == 0 ? 0u : 0xFFFFFFFF);
        }

        for (int i = 0; i < 128; i++)
        {
            uint entry = i == 0 ? 0xFFFFFFFD : i == 1 ? 0xFFFFFFFE : 0xFFFFFFFF;
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(512 + 4 * i), entry);
        }

        return f;
    }

    private static byte[] Rar4() =>
    [
        .. "Rar!\x1A\x07\x00"u8,
        0x00, 0x00, 0x73, 0x00, 0x00, 13, 0, 0, 0, 0, 0, 0, 0,
        0x00, 0x00, 0x7B, 0x00, 0x00, 7, 0,
    ];

    private static byte[] SevenZip()
    {
        byte[] f = new byte[32 + 16 + 32];
        new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 }.CopyTo(f, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(20), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(8), StructureValidators.Crc32(f.AsSpan(12, 20)));
        return f;
    }

    private static byte[] Gzip()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("compressible text for the gzip test. ", 200))));
        }

        return ms.ToArray();
    }

    private static byte[] Bzip2() =>
    [
        .. "BZh9"u8, 0x31, 0x41, 0x59, 0x26, 0x53, 0x59, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x00, 0x11, 0x22, 0x33,
        0x17, 0x72, 0x45, 0x38, 0x50, 0x90, 0xDE, 0xAD, 0xBE, 0xEF,
    ];

    private static byte[] Mp3()
    {
        var f = new List<byte> { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, 0 };
        for (int i = 0; i < 10; i++)
        {
            byte[] frame = new byte[417];
            frame[0] = 0xFF;
            frame[1] = 0xFB;
            frame[2] = 0x90;
            frame[3] = 0x00;
            f.AddRange(frame);
        }

        return [.. f];
    }

    private static byte[] Mp4()
    {
        var f = new List<byte>();
        void Box(string type, int size)
        {
            byte[] b = new byte[size];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)size);
            Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
            f.AddRange(b);
        }

        Box("ftyp", 24);
        Box("moov", 16);
        Box("mdat", 24);
        return [.. f];
    }

    private static byte[] Elf()
    {
        byte[] f = new byte[200];
        new byte[] { 0x7F, 0x45, 0x4C, 0x46, 2, 1, 1 }.CopyTo(f, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(0x20), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x36), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x38), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x3A), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(64), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(64 + 8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(64 + 32), 200);
        return f;
    }

    private static byte[] Sqlite()
    {
        byte[] f = new byte[3 * 4096];
        "SQLite format 3\0"u8.CopyTo(f);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(16), 4096);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(24), 7);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(28), 3);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(92), 7);
        return f;
    }

    private static byte[] Evtx()
    {
        byte[] f = new byte[4096 + 65536];
        "ElfFile\0"u8.CopyTo(f);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(32), 128);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(38), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(40), 4096);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(42), 1);
        "ElfChnk\0"u8.CopyTo(f.AsSpan(4096));
        return f;
    }

    private static byte[] Lnk()
    {
        byte[] f = new byte[80];
        new byte[] { 0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46 }.CopyTo(f, 0);
        return f;
    }
}
