using System.Text;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>TD-FOR-FILESET の 1 ファイル (docs/test/cases/12-forensics.md の「ファイルの組 TD-FOR-FILESET」)。</summary>
public sealed record FileSetItem(string Name, byte[] Content, DateTime ModifiedUtc, bool NtfsOnly = false, string Directory = "")
{
    public string Path => Directory.Length == 0 ? "\\" + Name : $"\\{Directory}\\{Name}";
}

/// <summary>ファイルシステムのイメージに入れるファイルの組 (TD-FOR-FILESET)。内容は種を固定して作るので毎回同じ。</summary>
public static class FileSet
{
    public const string LongName = "長い名前のファイル_long_file_name.txt";

    /// <summary>readme.txt の更新日時 (FAT・exFAT ではタイムゾーンなしの 2026-01-15 10:30:00 として記録する)。</summary>
    public static readonly DateTime ReadmeModifiedUtc = new(2026, 1, 15, 1, 30, 0, DateTimeKind.Utc);

    /// <summary>FAT・exFAT の記録の時差 (JST。readme.txt の 01:30Z を 10:30 として記録する)。</summary>
    public static readonly TimeSpan FatZone = TimeSpan.FromHours(9);

    /// <summary>ほかのファイルの日時 (readme.txt から 1 分ずつ後。小数部を持つ)。</summary>
    public static DateTime TimeOf(int index) => ReadmeModifiedUtc.AddMinutes(index + 1).AddTicks(1234567);

    public static readonly byte[] SecretStream = Encoding.ASCII.GetBytes("TOP SECRET DATA\r\n");

    public static byte[] Readme => TestDataCatalog.TextAscii4K();

    public static byte[] Photo => TestDataCatalog.Png16();

    public static byte[] Data
    {
        get
        {
            byte[] b = new byte[1 << 20];
            TestDataCatalog.Random(TestDataCatalog.RandomSeed, 0, b);
            return b;
        }
    }

    public static byte[] LongNameText => TestDataCatalog.TextUtf8_4K();

    public static byte[] Frag
    {
        get
        {
            byte[] b = new byte[768 * 1024];
            TestDataCatalog.Sequence(0, b);
            return b;
        }
    }

    public static byte[] Small100 => [.. Enumerable.Repeat((byte)0x41, 100)];

    public static byte[] Contig
    {
        get
        {
            byte[] b = new byte[1 << 20];
            TestDataCatalog.Random(0xC0, 0, b);
            return b;
        }
    }

    public static byte[] Deleted => Repeat("DELETED-FILE-MARKER\r\n", 4096);

    public static byte[] FileTxt => Encoding.ASCII.GetBytes("MAIN STREAM DATA");

    public static byte[] Compressed
    {
        get
        {
            byte[] ascii = TestDataCatalog.TextAscii4K();
            byte[] b = new byte[1 << 20];
            for (int i = 0; i < b.Length; i += ascii.Length)
            {
                ascii.CopyTo(b, i);
            }

            return b;
        }
    }

    public static byte[] Stomped => Encoding.ASCII.GetBytes("STOMPED");

    /// <summary>stomped.txt の $STANDARD_INFORMATION の 4 つの日時。</summary>
    public static readonly DateTime StompedTime = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static byte[] Repeat(string text, int size)
    {
        byte[] unit = Encoding.ASCII.GetBytes(text);
        byte[] b = new byte[size];
        for (int i = 0; i < size; i++)
        {
            b[i] = unit[i % unit.Length];
        }

        return b;
    }

    /// <summary>dir1\fNNN.txt の内容 (10 バイト: <c>file-001</c> + CRLF)。</summary>
    public static byte[] Dir1File(int n) => Encoding.ASCII.GetBytes($"file-{n:D3}\r\n");

    /// <summary>すべてのファイル系で共通のファイル (NTFS のみのものを除く。順序は書き込みの順)。</summary>
    public static IReadOnlyList<FileSetItem> Common =>
    [
        new("readme.txt", Readme, ReadmeModifiedUtc),
        new("photo.png", Photo, TimeOf(1)),
        new("data.bin", Data, TimeOf(2)),
        new(LongName, LongNameText, TimeOf(3)),
        new("frag.bin", Frag, TimeOf(4)),
        new("small100.bin", Small100, TimeOf(5)),
        new("contig.bin", Contig, TimeOf(6)),
        new("deleted.txt", Deleted, TimeOf(7)),
    ];
}
