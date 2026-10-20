using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.TestData;

/// <summary>
/// メモリダンプ (cases/01-engine-and-sources.md の TD-ENG-FULLDUMP・TD-ENG-DUMP-64G) と暗号化ボリューム (cases/12-forensics.md の
/// TD-FOR-BITLOCKER-RAW・TD-FOR-LUKS2) のテストデータ。どれもアプリの読み込み部品とは別に、形式の定義からここで組み立てる
/// (読み込み部品の間違いを生成側で打ち消さないため)。BitLocker・cryptsetup の機能は使わない。
/// </summary>
public static partial class TestDataCatalog
{
    /// <summary>TD-ENG-FULLDUMP の物理メモリの範囲 (開始ページ、ページ数)。</summary>
    /// <remarks>静的な初期化の順に依存しないよう、プロパティで返す (目録の初期化から使う)。</remarks>
    public static (long BasePage, long Pages)[] FullDumpRuns => [(0x1, 0x9F), (0x100, 0x700), (0x1000, 0x100)];

    /// <summary>TD-ENG-FULLDUMP の取得日時。</summary>
    public static DateTime FullDumpTime => new(2026, 10, 7, 3, 15, 42, DateTimeKind.Utc);

    /// <summary>TD-ENG-DUMP-64G の物理メモリのページ数 (64 GiB)。</summary>
    public const long Dump64GPages = 0x1000000;

    /// <summary>TD-FOR-LUKS2 の UUID とキー。</summary>
    public const string Luks2Uuid = "12345678-1234-1234-1234-123456789abc";

    /// <summary>TD-FOR-BITLOCKER-RAW のパーティションの開始 (バイト) と、ボリュームの GUID、キー保護機能の GUID。</summary>
    public const long BitLockerPartitionOffset = 1 * MiB;

    public static Guid BitLockerVolumeGuid => new("6a1b9f32-6c1d-4f0b-9a73-3d1e5b7c2a10");

    public static Guid BitLockerPasswordProtector => new("0f4c8d2e-1a3b-4c5d-8e9f-a0b1c2d3e4f5");

    public static Guid BitLockerRecoveryProtector => new("5e6f7a8b-9c0d-4e1f-a2b3-c4d5e6f7a8b9");

    private static IEnumerable<TestDataItem> DevicesProtectItems() =>
    [
        new("TD-ENG-FULLDUMP", 0x2000 + FullDumpRuns.Sum(r => r.Pages) * 4096,
            "64 bit の完全メモリダンプの形式 (PAGEDU64、DumpType 1、範囲 3 つ)。各ページの先頭に PFN: とページ番号の 16 桁の Hex",
            WriteFullDump),
        new("TD-ENG-DUMP-64G", 64 * GiB + 8 * KiB, "完全メモリダンプの形式で範囲 1 つ (64 GiB)。各 1 GiB の先頭のページだけに目印 (スパース)",
            WriteDump64G),
        new("TD-FOR-BITLOCKER-RAW", 64 * MiB,
            "GPT のパーティション 1 つに BitLocker の形式のボリュームヘッダと FVE メタデータ 3 つ (AES-XTS 128、パスワードと回復パスワード)。中身は 00 (スパース)",
            WriteBitLockerRaw),
        new("TD-FOR-LUKS2", 32 * MiB, "LUKS2 のヘッダ (UUID 12345678-1234-1234-1234-123456789abc、キースロット 1 つ、aes-xts-plain64)。中身は 00 (スパース)",
            WriteLuks2),
    ];

    /// <summary>ページの目印: ASCII の PFN: とページ番号の 16 桁の Hex (大文字)。</summary>
    public static byte[] PfnMarker(long page) => Encoding.ASCII.GetBytes("PFN:" + page.ToString("X16", System.Globalization.CultureInfo.InvariantCulture));

    private static byte[] CrashDumpHeader64(IReadOnlyList<(long BasePage, long Pages)> runs)
    {
        byte[] h = new byte[0x2000];
        Encoding.ASCII.GetBytes("PAGEDU64").CopyTo(h, 0);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), 15);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(12), 26100);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x30), 0x8664);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x88), (uint)runs.Count);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(0x90), runs.Sum(r => r.Pages));
        for (int i = 0; i < runs.Count; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(0x98 + i * 16), runs[i].BasePage);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(0xA0 + i * 16), runs[i].Pages);
        }

        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(0xF98), 1);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(0xFA8), FullDumpTime.ToFileTimeUtc());
        return h;
    }

    private static void WriteFullDump(string path)
    {
        using FileStream stream = File.Create(path);
        stream.Write(CrashDumpHeader64(FullDumpRuns));
        byte[] page = new byte[4096];
        foreach ((long basePage, long pages) in FullDumpRuns)
        {
            for (long p = basePage; p < basePage + pages; p++)
            {
                Array.Clear(page);
                PfnMarker(p).CopyTo(page, 0);
                stream.Write(page);
            }
        }
    }

    private static void WriteDump64G(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        MakeSparse(handle);
        RandomAccess.SetLength(handle, 64 * GiB + 8 * KiB);
        RandomAccess.Write(handle, CrashDumpHeader64([(0, Dump64GPages)]), 0);
        for (long gib = 0; gib < 64; gib++)
        {
            long page = gib * (GiB / 4096);
            RandomAccess.Write(handle, PfnMarker(page), 0x2000 + page * 4096);
        }
    }

    // ---- TD-FOR-BITLOCKER-RAW ----

    private static void WriteBitLockerRaw(string path)
    {
        const int sector = 512;
        long diskSize = 64 * MiB;
        long sectors = diskSize / sector;
        long firstLba = BitLockerPartitionOffset / sector;
        long lastLba = sectors - 34;
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        MakeSparse(handle);
        RandomAccess.SetLength(handle, diskSize);

        // 保護 MBR と GPT (主・副)。パーティションは Microsoft の基本データ。
        byte[] mbr = new byte[sector];
        mbr[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 12), (uint)Math.Min(sectors - 1, uint.MaxValue));
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        RandomAccess.Write(handle, mbr, 0);
        byte[] entries = new byte[128 * 128];
        new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7").TryWriteBytes(entries);
        new Guid("3c8f1d7e-2b4a-4e6f-9d1c-7a5b3e9f0c21").TryWriteBytes(entries.AsSpan(16));
        BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(32), firstLba);
        BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(40), lastLba);
        Encoding.Unicode.GetBytes("Basic data partition").CopyTo(entries, 56);
        uint entriesCrc = System.IO.Hashing.Crc32.HashToUInt32(entries);
        RandomAccess.Write(handle, BitLockerGptHeader(1, sectors - 1, 2, sectors, entriesCrc), sector);
        RandomAccess.Write(handle, entries, 2 * sector);
        RandomAccess.Write(handle, entries, (sectors - 33) * sector);
        RandomAccess.Write(handle, BitLockerGptHeader(sectors - 1, 1, sectors - 33, sectors, entriesCrc), (sectors - 1) * sector);

        // BitLocker のボリュームヘッダ (Windows 7 以降の形) と 3 つの FVE メタデータブロック。
        long volumeSize = (lastLba - firstLba + 1) * sector;
        long[] metadata = [0x0210_0000L, 0x0230_0000L, 0x0250_0000L];
        byte[] header = new byte[sector];
        header[0] = 0xEB;
        header[1] = 0x58;
        header[2] = 0x90;
        Encoding.ASCII.GetBytes("-FVE-FS-").CopyTo(header, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x0B), sector);
        header[0x0D] = 8;
        new Guid("4967d63b-2e29-4ad8-8399-f6a339e3d001").TryWriteBytes(header.AsSpan(0xA0));
        for (int i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0xB0 + i * 8), metadata[i]);
        }

        header[510] = 0x55;
        header[511] = 0xAA;
        RandomAccess.Write(handle, header, BitLockerPartitionOffset);
        byte[] block = BitLockerMetadataBlock(volumeSize, metadata);
        foreach (long at in metadata)
        {
            RandomAccess.Write(handle, block, BitLockerPartitionOffset + at);
        }
    }

    private static byte[] BitLockerGptHeader(long current, long backup, long entriesLba, long sectors, uint entriesCrc)
    {
        byte[] h = new byte[512];
        Encoding.ASCII.GetBytes("EFI PART").CopyTo(h, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 92);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), current);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), backup);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), 34);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(48), sectors - 34);
        new Guid("9b2e4a71-0c3d-4f5e-8a6b-1c2d3e4f5a6b").TryWriteBytes(h.AsSpan(56));
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), entriesLba);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), entriesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), System.IO.Hashing.Crc32.HashToUInt32(h.AsSpan(0, 92)));
        return h;
    }

    /// <summary>FVE メタデータブロック (ブロックの見出し 0x40 + メタデータの見出し 0x30 + エントリ)。</summary>
    private static byte[] BitLockerMetadataBlock(long volumeSize, long[] offsets)
    {
        DateTime created = new(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);
        var entries = new List<byte>();
        void Entry(ushort type, ushort valueType, byte[] value)
        {
            byte[] e = new byte[8 + value.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(e, (ushort)e.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2), type);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(4), valueType);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(6), 1);
            value.CopyTo(e, 8);
            entries.AddRange(e);
        }

        // VMK: GUID、更新日時、不明 2 バイト、保護の種類 (0x2000 パスワード、0x0800 回復パスワード)、入れ子のエントリ (空)。
        foreach ((Guid id, ushort type) in new[] { (BitLockerPasswordProtector, (ushort)0x2000), (BitLockerRecoveryProtector, (ushort)0x0800) })
        {
            byte[] vmk = new byte[28 + 8];
            id.TryWriteBytes(vmk);
            BinaryPrimitives.WriteInt64LittleEndian(vmk.AsSpan(16), created.ToFileTimeUtc());
            BinaryPrimitives.WriteUInt16LittleEndian(vmk.AsSpan(26), type);
            Entry(0x0002, 0x0008, vmk);
        }

        Entry(0x0007, 0x0002, Encoding.Unicode.GetBytes("HEXTEST-PC E: 2026/10/01\0"));
        int metaSize = 0x30 + entries.Count;
        byte[] block = new byte[0x40 + metaSize];
        Encoding.ASCII.GetBytes("-FVE-FS-").CopyTo(block, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0x0A), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0x0C), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0x0E), 4);
        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(0x10), volumeSize);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0x1C), 16);
        for (int i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(0x20 + i * 8), offsets[i]);
        }

        Span<byte> meta = block.AsSpan(0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(meta, (uint)metaSize);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[8..], 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[12..], (uint)metaSize);
        BitLockerVolumeGuid.TryWriteBytes(meta[0x10..]);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[0x20..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(meta[0x24..], 0x8004);
        BinaryPrimitives.WriteInt64LittleEndian(meta[0x28..], created.ToFileTimeUtc());
        entries.ToArray().CopyTo(meta[0x30..]);
        return block;
    }

    // ---- TD-FOR-LUKS2 ----

    private static void WriteLuks2(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        MakeSparse(handle);
        RandomAccess.SetLength(handle, 32 * MiB);
        const int headerSize = 16384;
        string json = "{\"keyslots\":{\"0\":{\"type\":\"luks2\",\"key_size\":64,\"area\":{\"type\":\"raw\",\"offset\":\"32768\",\"size\":\"258048\","
            + "\"encryption\":\"aes-xts-plain64\",\"key_size\":64},\"kdf\":{\"type\":\"pbkdf2\",\"hash\":\"sha256\",\"iterations\":1000,"
            + "\"salt\":\"c2FsdHNhbHRzYWx0c2FsdHNhbHRzYWx0c2FsdHNhbHQ=\"},\"af\":{\"type\":\"luks1\",\"stripes\":4000,\"hash\":\"sha256\"},\"priority\":1}},"
            + "\"tokens\":{},\"segments\":{\"0\":{\"type\":\"crypt\",\"offset\":\"16777216\",\"size\":\"dynamic\",\"iv_tweak\":\"0\","
            + "\"encryption\":\"aes-xts-plain64\",\"sector_size\":512}},\"digests\":{\"0\":{\"type\":\"pbkdf2\",\"keyslots\":[\"0\"],\"segments\":[\"0\"],"
            + "\"hash\":\"sha256\",\"iterations\":1000,\"salt\":\"c2FsdA==\",\"digest\":\"ZGlnZXN0\"}},\"config\":{\"json_size\":\"12288\",\"keyslots_size\":\"16744448\"}}";
        for (int copy = 0; copy < 2; copy++)
        {
            byte[] h = new byte[headerSize];
            byte[] magic = copy == 0 ? [(byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xBA, 0xBE] : [(byte)'S', (byte)'K', (byte)'U', (byte)'L', 0xBA, 0xBE];
            magic.CopyTo(h, 0);
            BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(6), 2);
            BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(8), headerSize);
            BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(16), 3);
            Encoding.ASCII.GetBytes("sha256").CopyTo(h, 72);
            Encoding.ASCII.GetBytes(Luks2Uuid).CopyTo(h, 168);
            BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(256), (ulong)(copy * headerSize));
            Encoding.ASCII.GetBytes(json).CopyTo(h, 4096);
            RandomAccess.Write(handle, h, copy * headerSize);
        }
    }
}
