using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HexEditor.Core.FileSystems.Ext;

/// <summary>ext の機能フラグ (スーパーブロックの compat / incompat / ro_compat。FOR-12 の仕様 1)。</summary>
public static class ExtFeatures
{
    public const uint CompatHasJournal = 0x4;
    public const uint CompatDirIndex = 0x20;
    public const uint CompatSparseSuper2 = 0x200;

    public const uint IncompatFiletype = 0x2;
    public const uint IncompatRecover = 0x4;
    public const uint IncompatJournalDev = 0x8;
    public const uint IncompatMetaBg = 0x10;
    public const uint IncompatExtents = 0x40;
    public const uint Incompat64Bit = 0x80;
    public const uint IncompatFlexBg = 0x200;
    public const uint IncompatCsumSeed = 0x2000;
    public const uint IncompatInlineData = 0x8000;
    public const uint IncompatEncrypt = 0x10000;

    public const uint RoSparseSuper = 0x1;
    public const uint RoHugeFile = 0x8;
    public const uint RoGdtCsum = 0x10;
    public const uint RoDirNlink = 0x20;
    public const uint RoExtraIsize = 0x40;
    public const uint RoBigalloc = 0x200;
    public const uint RoMetadataCsum = 0x400;

    private static readonly (uint Bit, string Name)[] CompatNames =
    [
        (0x1, "dir_prealloc"), (0x2, "imagic_inodes"), (0x4, "has_journal"), (0x8, "ext_attr"), (0x10, "resize_inode"), (0x20, "dir_index"),
        (0x40, "lazy_bg"), (0x80, "exclude_inode"), (0x100, "exclude_bitmap"), (0x200, "sparse_super2"), (0x400, "fast_commit"),
        (0x800, "stable_inodes"), (0x1000, "orphan_file"),
    ];

    private static readonly (uint Bit, string Name)[] IncompatNames =
    [
        (0x1, "compression"), (0x2, "filetype"), (0x4, "needs_recovery"), (0x8, "journal_dev"), (0x10, "meta_bg"), (0x40, "extent"),
        (0x80, "64bit"), (0x100, "mmp"), (0x200, "flex_bg"), (0x400, "ea_inode"), (0x1000, "dirdata"), (0x2000, "metadata_csum_seed"),
        (0x4000, "large_dir"), (0x8000, "inline_data"), (0x10000, "encrypt"), (0x20000, "casefold"),
    ];

    private static readonly (uint Bit, string Name)[] RoCompatNames =
    [
        (0x1, "sparse_super"), (0x2, "large_file"), (0x4, "btree_dir"), (0x8, "huge_file"), (0x10, "uninit_bg"), (0x20, "dir_nlink"),
        (0x40, "extra_isize"), (0x80, "snapshot"), (0x100, "quota"), (0x200, "bigalloc"), (0x400, "metadata_csum"), (0x800, "replica"),
        (0x1000, "read-only"), (0x2000, "project"), (0x4000, "shared_blocks"), (0x8000, "verity"), (0x10000, "orphan_present"),
    ];

    /// <summary>フラグの名前 (e2fsprogs の名前。知らないビットは <c>0x…</c>)。</summary>
    public static string Names(uint value, int kind)
    {
        (uint Bit, string Name)[] table = kind switch { 0 => CompatNames, 1 => IncompatNames, _ => RoCompatNames };
        var names = new List<string>();
        uint known = 0;
        foreach ((uint bit, string name) in table)
        {
            known |= bit;
            if ((value & bit) != 0)
            {
                names.Add(name);
            }
        }

        if ((value & ~known) != 0)
        {
            names.Add($"0x{value & ~known:X}");
        }

        return string.Join(' ', names);
    }
}

/// <summary>ext のスーパーブロック (先頭から 1024 バイト。FOR-12 の仕様 1)。</summary>
public sealed record ExtSuperblock
{
    public const int Offset = 1024;
    public const int Size = 1024;

    public required byte[] Raw { get; init; }
    public uint InodesCount { get; init; }
    public long BlocksCount { get; init; }
    public long FreeBlocks { get; init; }
    public uint FreeInodes { get; init; }
    public uint FirstDataBlock { get; init; }
    public int BlockSize { get; init; }
    public uint BlocksPerGroup { get; init; }
    public uint InodesPerGroup { get; init; }
    public uint MountTime { get; init; }
    public uint WriteTime { get; init; }
    public ushort State { get; init; }
    public uint CreatorOs { get; init; }
    public uint RevLevel { get; init; }
    public uint FirstInode { get; init; }
    public int InodeSize { get; init; }
    public uint Compat { get; init; }
    public uint Incompat { get; init; }
    public uint RoCompat { get; init; }
    public Guid Uuid { get; init; }
    public string VolumeName { get; init; } = string.Empty;
    public string LastMounted { get; init; } = string.Empty;
    public uint JournalInode { get; init; }
    public int DescriptorSize { get; init; }
    public uint FirstMetaBg { get; init; }
    public uint MkfsTime { get; init; }
    public int LogGroupsPerFlex { get; init; }
    public uint ChecksumSeed { get; init; }
    public uint Checksum { get; init; }
    public ushort ReservedGdtBlocks { get; init; }
    public ushort MinExtraIsize { get; init; }

    public bool Has64Bit => (Incompat & ExtFeatures.Incompat64Bit) != 0;
    public bool HasExtents => (Incompat & ExtFeatures.IncompatExtents) != 0;
    public bool HasFlexBg => (Incompat & ExtFeatures.IncompatFlexBg) != 0;
    public bool HasMetaBg => (Incompat & ExtFeatures.IncompatMetaBg) != 0;
    public bool HasJournal => (Compat & ExtFeatures.CompatHasJournal) != 0;
    public bool HasMetadataCsum => (RoCompat & ExtFeatures.RoMetadataCsum) != 0;
    public bool HasSparseSuper => (RoCompat & ExtFeatures.RoSparseSuper) != 0;
    public bool HasFiletype => (Incompat & ExtFeatures.IncompatFiletype) != 0;

    public long GroupCount => BlocksPerGroup == 0 ? 0 : (BlocksCount - FirstDataBlock + BlocksPerGroup - 1) / BlocksPerGroup;

    /// <summary>ext2 / ext3 / ext4 のどれか (機能から推定する)。</summary>
    public string Variant
    {
        get
        {
            const uint Ext4Incompat = ExtFeatures.IncompatExtents | ExtFeatures.Incompat64Bit | ExtFeatures.IncompatFlexBg | ExtFeatures.IncompatInlineData
                | ExtFeatures.IncompatCsumSeed | ExtFeatures.IncompatEncrypt;
            const uint Ext4Ro = ExtFeatures.RoHugeFile | ExtFeatures.RoGdtCsum | ExtFeatures.RoDirNlink | ExtFeatures.RoExtraIsize
                | ExtFeatures.RoMetadataCsum | ExtFeatures.RoBigalloc;
            return (Incompat & Ext4Incompat) != 0 || (RoCompat & Ext4Ro) != 0 ? "ext4" : HasJournal ? "ext3" : "ext2";
        }
    }

    /// <summary>メタデータのチェックサムの種 (metadata_csum_seed ならスーパーブロックの値、そうでなければ UUID の CRC32C)。</summary>
    public uint CsumSeed => (Incompat & ExtFeatures.IncompatCsumSeed) != 0 ? ChecksumSeed : FsChecksums.Crc32cRaw(0xFFFFFFFF, Raw.AsSpan(104, 16));

    /// <summary>スーパーブロックのチェックサムが正しいか (metadata_csum がなければ null)。</summary>
    public bool? ChecksumValid => HasMetadataCsum ? FsChecksums.Crc32cRaw(0xFFFFFFFF, Raw.AsSpan(0, 1020)) == Checksum : null;

    /// <summary>読む。マジックか値が成り立たなければ null。</summary>
    public static ExtSuperblock? Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < Size || BinaryPrimitives.ReadUInt16LittleEndian(b[56..]) != 0xEF53)
        {
            return null;
        }

        uint logBlock = U32(b, 24);
        uint inodes = U32(b, 0);
        uint bpg = U32(b, 32);
        uint ipg = U32(b, 40);
        uint rev = U32(b, 76);
        uint incompat = U32(b, 96);
        bool is64 = (incompat & ExtFeatures.Incompat64Bit) != 0;
        long blocks = U32(b, 4) | (is64 ? (long)U32(b, 336) << 32 : 0);
        int inodeSize = rev == 0 ? 128 : BinaryPrimitives.ReadUInt16LittleEndian(b[88..]);
        int descSize = is64 ? BinaryPrimitives.ReadUInt16LittleEndian(b[254..]) : 32;
        if (descSize < 32)
        {
            descSize = 32;
        }

        int blockSize = logBlock <= 6 ? 1024 << (int)logBlock : 0;
        if (blockSize == 0 || inodes == 0 || blocks <= 0 || bpg == 0 || ipg == 0 || bpg > 8L * blockSize || ipg > 8L * blockSize
            || inodeSize < 128 || inodeSize > blockSize || (inodeSize & (inodeSize - 1)) != 0 || descSize > 1024 || (descSize & (descSize - 1)) != 0)
        {
            return null;
        }

        return new ExtSuperblock
        {
            Raw = b[..Size].ToArray(),
            InodesCount = inodes,
            BlocksCount = blocks,
            FreeBlocks = U32(b, 12) | (is64 ? (long)U32(b, 344) << 32 : 0),
            FreeInodes = U32(b, 16),
            FirstDataBlock = U32(b, 20),
            BlockSize = blockSize,
            BlocksPerGroup = bpg,
            InodesPerGroup = ipg,
            MountTime = U32(b, 44),
            WriteTime = U32(b, 48),
            State = BinaryPrimitives.ReadUInt16LittleEndian(b[58..]),
            CreatorOs = U32(b, 72),
            RevLevel = rev,
            FirstInode = rev == 0 ? 11 : U32(b, 84),
            InodeSize = inodeSize,
            Compat = U32(b, 92),
            Incompat = incompat,
            RoCompat = U32(b, 100),
            Uuid = new Guid(b.Slice(104, 16), bigEndian: true),
            VolumeName = Text(b.Slice(120, 16)),
            LastMounted = Text(b.Slice(136, 64)),
            ReservedGdtBlocks = BinaryPrimitives.ReadUInt16LittleEndian(b[206..]),
            JournalInode = U32(b, 224),
            DescriptorSize = descSize,
            FirstMetaBg = U32(b, 260),
            MkfsTime = U32(b, 264),
            MinExtraIsize = BinaryPrimitives.ReadUInt16LittleEndian(b[348..]),
            LogGroupsPerFlex = b[372],
            ChecksumSeed = U32(b, 0x270),
            Checksum = U32(b, 0x3FC),
        };
    }

    internal static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);

    internal static string Text(ReadOnlySpan<byte> b)
    {
        int end = b.IndexOf((byte)0);
        return ExtNames.Decode(end >= 0 ? b[..end] : b);
    }
}

/// <summary>グループ記述子 (32 バイト版と 64 bit 機能の 64 バイト版。FOR-12 の仕様 2)。</summary>
public readonly record struct ExtGroupDescriptor(long BlockBitmap, long InodeBitmap, long InodeTable, long FreeBlocks, long FreeInodes, ushort Flags,
    long ItableUnused, ushort Checksum)
{
    public const ushort InodeUninit = 0x1;
    public const ushort BlockUninit = 0x2;

    public static ExtGroupDescriptor Parse(ReadOnlySpan<byte> d, bool is64)
    {
        bool hi = is64 && d.Length >= 64;
        return new ExtGroupDescriptor(
            ExtSuperblock.U32(d, 0) | (hi ? (long)ExtSuperblock.U32(d, 32) << 32 : 0),
            ExtSuperblock.U32(d, 4) | (hi ? (long)ExtSuperblock.U32(d, 36) << 32 : 0),
            ExtSuperblock.U32(d, 8) | (hi ? (long)ExtSuperblock.U32(d, 40) << 32 : 0),
            BinaryPrimitives.ReadUInt16LittleEndian(d[12..]) | (hi ? (long)BinaryPrimitives.ReadUInt16LittleEndian(d[44..]) << 16 : 0),
            BinaryPrimitives.ReadUInt16LittleEndian(d[14..]) | (hi ? (long)BinaryPrimitives.ReadUInt16LittleEndian(d[46..]) << 16 : 0),
            BinaryPrimitives.ReadUInt16LittleEndian(d[18..]),
            BinaryPrimitives.ReadUInt16LittleEndian(d[28..]) | (hi ? (long)BinaryPrimitives.ReadUInt16LittleEndian(d[50..]) << 16 : 0),
            BinaryPrimitives.ReadUInt16LittleEndian(d[30..]));
    }

    /// <summary>metadata_csum のときのチェックサム (下位 16 ビット)。</summary>
    public static ushort ComputeChecksum(uint csumSeed, uint group, ReadOnlySpan<byte> descriptor)
    {
        uint c = FsChecksums.Crc32cRaw(csumSeed, group);
        c = FsChecksums.Crc32cRaw(c, descriptor[..30]);
        c = FsChecksums.Crc32cRaw(c, [0, 0]);
        if (descriptor.Length > 32)
        {
            c = FsChecksums.Crc32cRaw(c, descriptor[32..]);
        }

        return (ushort)(c & 0xFFFF);
    }
}

/// <summary>inode (FOR-12 の仕様 3)。<see cref="Raw"/> は inode の全体 (inode サイズ分)。</summary>
public sealed record ExtInode
{
    public const uint FlagCompressed = 0x4;
    public const uint FlagEncrypted = 0x800;
    public const uint FlagIndex = 0x1000;
    public const uint FlagHugeFile = 0x40000;
    public const uint FlagExtents = 0x80000;
    public const uint FlagEaInode = 0x200000;
    public const uint FlagInlineData = 0x10000000;

    public required long Number { get; init; }
    public required byte[] Raw { get; init; }
    public long Offset { get; init; }
    public ushort Mode { get; init; }
    public uint Uid { get; init; }
    public uint Gid { get; init; }
    public long Size { get; init; }
    public ushort Links { get; init; }
    public long BlocksRaw { get; init; }
    public uint Flags { get; init; }
    public uint Generation { get; init; }
    public long FileAcl { get; init; }
    public int ExtraIsize { get; init; }
    public FsTimestamp Accessed { get; init; }
    public FsTimestamp Changed { get; init; }
    public FsTimestamp Modified { get; init; }
    public FsTimestamp Deleted { get; init; }
    public FsTimestamp Created { get; init; }
    public uint DtimeRaw { get; init; }

    public int FileType => Mode >> 12;
    public bool IsDirectory => FileType == 0x4;
    public bool IsRegular => FileType == 0x8;
    public bool IsSymlink => FileType == 0xA;
    public bool UsesExtents => (Flags & FlagExtents) != 0;
    public bool HasInlineData => (Flags & FlagInlineData) != 0;
    public bool IsEncrypted => (Flags & FlagEncrypted) != 0;

    /// <summary>削除された inode か (dtime が 0 でなく、リンク数が 0。FOR-12 の仕様 5)。</summary>
    public bool IsDeleted => DtimeRaw != 0 && Links == 0;

    /// <summary>使われていない (全部 0) inode か。</summary>
    public bool IsEmpty => Mode == 0 && Links == 0 && DtimeRaw == 0;

    /// <summary>i_block (60 バイト)。</summary>
    public ReadOnlySpan<byte> Block => Raw.AsSpan(0x28, 60);

    public static ExtInode Parse(long number, long offset, byte[] raw)
    {
        ReadOnlySpan<byte> b = raw;
        int extra = raw.Length > 128 ? BinaryPrimitives.ReadUInt16LittleEndian(b[0x80..]) : 0;
        if (128 + extra > raw.Length)
        {
            extra = 0;
        }

        bool Fits(int fieldEnd) => 128 + extra >= fieldEnd;
        uint uid = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]) | (uint)BinaryPrimitives.ReadUInt16LittleEndian(b[0x78..]) << 16;
        uint gid = BinaryPrimitives.ReadUInt16LittleEndian(b[0x18..]) | (uint)BinaryPrimitives.ReadUInt16LittleEndian(b[0x7A..]) << 16;
        uint dtime = ExtSuperblock.U32(b, 0x14);
        return new ExtInode
        {
            Number = number,
            Raw = raw,
            Offset = offset,
            Mode = BinaryPrimitives.ReadUInt16LittleEndian(b),
            Uid = uid,
            Gid = gid,
            Size = ExtSuperblock.U32(b, 4) | (long)ExtSuperblock.U32(b, 0x6C) << 32,
            Links = BinaryPrimitives.ReadUInt16LittleEndian(b[0x1A..]),
            BlocksRaw = ExtSuperblock.U32(b, 0x1C) | (long)BinaryPrimitives.ReadUInt16LittleEndian(b[0x74..]) << 32,
            Flags = ExtSuperblock.U32(b, 0x20),
            Generation = ExtSuperblock.U32(b, 0x64),
            FileAcl = ExtSuperblock.U32(b, 0x68) | (long)BinaryPrimitives.ReadUInt16LittleEndian(b[0x76..]) << 32,
            ExtraIsize = extra,
            Accessed = Time(ExtSuperblock.U32(b, 0x08), Fits(0x90) ? ExtSuperblock.U32(b, 0x8C) : null),
            Changed = Time(ExtSuperblock.U32(b, 0x0C), Fits(0x88) ? ExtSuperblock.U32(b, 0x84) : null),
            Modified = Time(ExtSuperblock.U32(b, 0x10), Fits(0x8C) ? ExtSuperblock.U32(b, 0x88) : null),
            Deleted = Time(dtime, null),
            Created = Fits(0x94) ? Time(ExtSuperblock.U32(b, 0x90), Fits(0x98) ? ExtSuperblock.U32(b, 0x94) : null) : default,
            DtimeRaw = dtime,
        };
    }

    /// <summary>
    /// 日時 (1970 年からの秒。拡張領域があれば下位 2 ビットが秒の 33・34 ビット目、上位 30 ビットがナノ秒)。0 は日時なし。
    /// </summary>
    public static FsTimestamp Time(uint seconds, uint? extra)
    {
        if (seconds == 0 && (extra ?? 0) == 0)
        {
            return default;
        }

        long s = (int)seconds;
        long ns = 0;
        if (extra is uint x)
        {
            s += (long)(x & 3) << 32;
            ns = x >> 2;
            if (ns > 999_999_999)
            {
                return new FsTimestamp(null, Invalid: true, Raw: s);
            }
        }

        DateTime utc = DateTime.UnixEpoch.AddSeconds(s).AddTicks(ns / 100);
        return new FsTimestamp(utc, Raw: s);
    }

    /// <summary>inode のチェックサム (metadata_csum。下位 16 ビットは 0x7C、上位は 0x82)。</summary>
    public static uint ComputeChecksum(uint csumSeed, long number, uint generation, ReadOnlySpan<byte> raw)
    {
        uint seed = FsChecksums.Crc32cRaw(FsChecksums.Crc32cRaw(csumSeed, (uint)number), generation);
        uint c = FsChecksums.Crc32cRaw(seed, raw[..0x7C]);
        c = FsChecksums.Crc32cRaw(c, [0, 0]);
        c = FsChecksums.Crc32cRaw(c, raw[0x7E..128]);
        if (raw.Length > 128)
        {
            int extra = BinaryPrimitives.ReadUInt16LittleEndian(raw[0x80..]);
            c = FsChecksums.Crc32cRaw(c, raw[128..0x82]);
            int offset = 0x82;
            if (128 + extra >= 0x84)
            {
                c = FsChecksums.Crc32cRaw(c, [0, 0]);
                offset += 2;
            }

            c = FsChecksums.Crc32cRaw(c, raw[offset..]);
        }

        return c;
    }

    /// <summary>inode のチェックサムが正しいか (記録された幅だけを比べる)。</summary>
    public bool ChecksumMatches(uint csumSeed)
    {
        uint c = ComputeChecksum(csumSeed, Number, Generation, Raw);
        uint stored = BinaryPrimitives.ReadUInt16LittleEndian(Raw.AsSpan(0x7C));
        if (Raw.Length > 128 && 128 + ExtraIsize >= 0x84)
        {
            stored |= (uint)BinaryPrimitives.ReadUInt16LittleEndian(Raw.AsSpan(0x82)) << 16;
            return c == stored;
        }

        return (c & 0xFFFF) == stored;
    }

    /// <summary>モードの文字列 (<c>-rw-r--r--</c>)。</summary>
    public static string ModeText(int mode) => FsText.PosixMode(mode);
}

/// <summary>ext のファイル名の読み方 (UTF-8 として読めないバイトは <c>\xNN</c> にする。fscrypt の暗号化された名前も同じ)。</summary>
public static class ExtNames
{
    private static readonly UTF8Encoding Strict = new(false, true);

    public static string Decode(ReadOnlySpan<byte> name)
    {
        try
        {
            string s = Strict.GetString(name);
            if (!s.Any(char.IsControl))
            {
                return s;
            }
        }
        catch (DecoderFallbackException)
        {
        }

        var b = new StringBuilder(name.Length * 2);
        foreach (byte c in name)
        {
            if (c is >= 0x20 and < 0x7F && c != (byte)'\\')
            {
                b.Append((char)c);
            }
            else
            {
                b.Append(CultureInfo.InvariantCulture, $"\\x{c:X2}");
            }
        }

        return b.ToString();
    }
}
