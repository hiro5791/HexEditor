using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HexEditor.Core.FileSystems.Iso;

/// <summary>ISO 9660 と UDF を開く処理 (FOR-14)。</summary>
public sealed class OpticalFileSystemProvider : IFileSystemProvider
{
    public IReadOnlyCollection<FileSystemKind> Kinds { get; } = [FileSystemKind.Iso9660, FileSystemKind.Udf];

    public IFileSystem Open(IVolumeReader volume, FileSystemOpenOptions options) =>
        options.Kind == FileSystemKind.Udf ? new UdfFileSystem(volume, options) : new Iso9660FileSystem(volume, options);
}

/// <summary>表示する名前の種類 (FOR-14 の仕様 2)。</summary>
public enum IsoNameKind
{
    Joliet,
    RockRidge,
    Basic,
}

/// <summary>El Torito のブートカタログのエントリ (FOR-14 の仕様 2)。<see cref="ImageLength"/> はブートイメージのバイト数。</summary>
public sealed record IsoBootEntry(int Index, bool Bootable, byte Platform, byte Emulation, ushort LoadSegment, byte SystemType, ushort SectorCount,
    uint LoadRba, long ImageLength, long CatalogOffset)
{
    public string PlatformName => Platform switch { 0 => "x86", 1 => "PowerPC", 2 => "Mac", 0xEF => "EFI", _ => $"0x{Platform:X2}" };

    /// <summary>エミュレーションの種類の識別子 (訳は <c>Iso_Emulation_&lt;値&gt;</c>)。</summary>
    public string EmulationName => Emulation switch
    {
        0 => "NoEmulation", 1 => "Floppy12", 2 => "Floppy144", 3 => "Floppy288", 4 => "HardDisk", _ => "Unknown",
    };
}

/// <summary>セッション (複数セッションのディスク。FOR-14 の仕様 2)。</summary>
public sealed record IsoSession(int Index, long DescriptorSector, long VolumeSpaceSize, string VolumeId);

/// <summary>項目の実装用の情報。</summary>
internal sealed record IsoTag(IsoNameKind Kind, IReadOnlyList<FsExtent> Extents, long RecordOffset, int Mode, string? SymlinkTarget, bool IsRoot);

/// <summary>
/// ISO 9660 の解析 (FOR-14)。ボリューム記述子 (基本・補助 (Joliet)・El Torito のブートレコード) を列挙し、Joliet・Rock Ridge (SUSP の NM・PX・TF・SL)・
/// 基本の名前のどれでも一覧を作れる。複数セッションは最後のセッションを使い、選べる。ISO 9660 は書き換えない媒体として扱うので、削除された項目と
/// 空き領域は扱わない (FOR-14 の仕様 4)。
/// </summary>
public sealed class Iso9660FileSystem : FileSystemBase, IFsChoices
{
    public const int SectorBytes = 2048;
    public const int MaxDescriptors = 64;
    public const int MaxDepth = 64;
    public const int MaxDirectoryEntries = 1_000_000;
    public const int MaxSessions = 99;

    /// <summary>次のセッションを探す範囲 (セクタ数。CD のリードアウト・リードインの隙間を含む)。</summary>
    public const int SessionSearchSectors = 12_000;

    private readonly CachedVolumeReader _meta;
    private readonly FileSystemOpenOptions _options;
    private readonly List<IsoSession> _sessions = [];
    private readonly bool _hasUdf;
    private int _session;
    private byte[] _pvd = [];
    private byte[]? _joliet;
    private bool _rockRidge;
    private int _suspSkip;
    private int _blockSize = SectorBytes;
    private long _volumeSpace;
    private IsoNameKind _nameKind;
    private readonly FsEntry _root;
    private List<IsoBootEntry> _boot = [];
    private long _bootCatalog = -1;
    private readonly object _gate = new();

    public Iso9660FileSystem(IVolumeReader volume, FileSystemOpenOptions options) : base(volume)
    {
        _options = options;
        _meta = volume as CachedVolumeReader ?? new CachedVolumeReader(volume, 64 * 1024, 64);
        FindSessions();
        if (_sessions.Count == 0)
        {
            throw new FileSystemOpenException("No ISO 9660 primary volume descriptor was found.", FileSystemKind.Iso9660);
        }

        if (_sessions.Count > 1)
        {
            IssueList.Add(new FsIssue(FsIssueKind.IsoMultiSession, _sessions.Count.ToString(CultureInfo.InvariantCulture)));
        }

        _hasUdf = HasUdfRecognition();
        UseSession(_sessions.Count - 1);
        _root = new FsEntry
        {
            Id = "iso:root",
            Name = string.Empty,
            Path = "\\",
            Flags = FsEntryFlags.Directory,
            Tag = new IsoTag(IsoNameKind.Basic, [], -1, 0x41ED, null, IsRoot: true),
        };
    }

    public override FileSystemKind Kind => FileSystemKind.Iso9660;
    public override long ClusterSize => _blockSize;
    public override long ClusterCount => _volumeSpace;
    public override int SectorSize => SectorBytes;
    public override long FileSystemLength => _volumeSpace * _blockSize;
    protected override long ClusterAreaOffset => 0;
    public override FsEntry Root => _root;
    public override IAllocationMap? Allocation => null;

    public IReadOnlyList<IsoSession> Sessions => _sessions;
    public int SessionIndex => _session;
    public IsoNameKind NameKind => _nameKind;
    public bool HasJoliet => _joliet is not null;
    public bool HasRockRidge => _rockRidge;
    public IReadOnlyList<IsoBootEntry> BootEntries => _boot;

    /// <summary>使える名前の種類 (Joliet・Rock Ridge はあるときだけ)。</summary>
    public IReadOnlyList<IsoNameKind> NameKinds =>
        [.. new[] { IsoNameKind.Joliet, IsoNameKind.RockRidge, IsoNameKind.Basic }.Where(k => k switch
        {
            IsoNameKind.Joliet => HasJoliet,
            IsoNameKind.RockRidge => _rockRidge,
            _ => true,
        })];

    /// <summary>表示する名前の種類を切り替える。</summary>
    public void SelectNameKind(IsoNameKind kind)
    {
        if (NameKinds.Contains(kind))
        {
            _nameKind = kind;
        }
    }

    /// <summary>使うセッションを切り替える。</summary>
    public void SelectSession(int index)
    {
        if (index >= 0 && index < _sessions.Count)
        {
            UseSession(index);
        }
    }

    public IReadOnlyList<FsChoice> Choices
    {
        get
        {
            var list = new List<FsChoice>();
            IReadOnlyList<IsoNameKind> kinds = NameKinds;
            if (kinds.Count > 1)
            {
                list.Add(new FsChoice("NameKind", [.. kinds.Select(k => k.ToString())], kinds.ToList().IndexOf(_nameKind)));
            }

            if (_sessions.Count > 1)
            {
                list.Add(new FsChoice("Session", [.. _sessions.Select(s => (s.Index + 1).ToString(CultureInfo.InvariantCulture))], _session));
            }

            if (_hasUdf)
            {
                list.Add(new FsChoice("Format", ["Udf", "Iso9660"], 1));
            }

            return list;
        }
    }

    public FileSystemKind? Select(string key, int index)
    {
        switch (key)
        {
            case "NameKind" when index >= 0 && index < NameKinds.Count:
                SelectNameKind(NameKinds[index]);
                return null;
            case "Session":
                SelectSession(index);
                return null;
            case "Format" when index == 0 && _hasUdf:
                return FileSystemKind.Udf;
            default:
                return null;
        }
    }

    // ---- ボリューム記述子 ----

    private void FindSessions()
    {
        long start = 0;
        for (int s = 0; s < MaxSessions; s++)
        {
            long? pvd = s == 0 ? FindPvd(16, 16 + MaxDescriptors) : FindPvd(start, start + SessionSearchSectors);
            if (pvd is not long at)
            {
                break;
            }

            byte[] d = _meta.ReadBytes(at * SectorBytes, SectorBytes);
            long space = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(80));
            _sessions.Add(new IsoSession(s, at, space, AText(d.AsSpan(40, 32))));
            if (space <= at || space * SectorBytes + 17 * SectorBytes > Volume.Length)
            {
                break;
            }

            start = space;
        }
    }

    /// <summary>[from, to) のセクタから基本ボリューム記述子 (<c>01 CD001 01</c>) を探す。</summary>
    private long? FindPvd(long from, long to)
    {
        byte[] head = new byte[7];
        for (long sector = from; sector < to && (sector + 1) * SectorBytes <= Volume.Length; sector++)
        {
            _meta.Read(sector * SectorBytes, head);
            if (head[0] == 1 && head.AsSpan(1, 5).SequenceEqual("CD001"u8) && head[6] == 1)
            {
                return sector;
            }
        }

        return null;
    }

    private bool HasUdfRecognition()
    {
        byte[] head = new byte[6];
        for (int sector = 16; sector < 16 + 32; sector++)
        {
            _meta.Read((long)sector * SectorBytes, head);
            if (head.AsSpan(1, 5).SequenceEqual("NSR02"u8) || head.AsSpan(1, 5).SequenceEqual("NSR03"u8))
            {
                return true;
            }
        }

        return false;
    }

    private void UseSession(int index)
    {
        lock (_gate)
        {
            _session = index;
            IsoSession session = _sessions[index];
            _joliet = null;
            _boot = [];
            _bootCatalog = -1;
            for (long sector = session.DescriptorSector; sector < session.DescriptorSector + MaxDescriptors; sector++)
            {
                byte[] d = _meta.ReadBytes(sector * SectorBytes, SectorBytes, out bool complete);
                if (!complete || !d.AsSpan(1, 5).SequenceEqual("CD001"u8))
                {
                    break;
                }

                byte type = d[0];
                if (type == 255)
                {
                    break;
                }

                if (type == 1 && _pvd.Length == 0 || type == 1 && sector == session.DescriptorSector)
                {
                    _pvd = d;
                }
                else if (type == 2 && _joliet is null && IsJoliet(d))
                {
                    _joliet = d;
                }
                else if (type == 0 && Encoding.ASCII.GetString(d, 7, 23) == "EL TORITO SPECIFICATION")
                {
                    _bootCatalog = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x47));
                }
            }

            int blockSize = BinaryPrimitives.ReadUInt16LittleEndian(_pvd.AsSpan(128));
            _blockSize = blockSize is 512 or 1024 or 2048 ? blockSize : SectorBytes;
            _volumeSpace = BinaryPrimitives.ReadUInt32LittleEndian(_pvd.AsSpan(80));
            DetectRockRidge();
            _boot = _bootCatalog > 0 ? ReadBootCatalog(_bootCatalog) : [];
            _nameKind = HasJoliet ? IsoNameKind.Joliet : _rockRidge ? IsoNameKind.RockRidge : IsoNameKind.Basic;
        }
    }

    private static bool IsJoliet(byte[] d)
    {
        ReadOnlySpan<byte> esc = d.AsSpan(88, 3);
        return esc[0] == 0x25 && esc[1] == 0x2F && esc[2] is 0x40 or 0x43 or 0x45;
    }

    private void DetectRockRidge()
    {
        _rockRidge = false;
        _suspSkip = 0;
        if (RootRecord(IsoNameKind.Basic) is not { } root)
        {
            return;
        }

        byte[] first = _meta.ReadBytes(root.Lba * _blockSize, Math.Min(SectorBytes, (int)Math.Max(34, root.Length)));
        int len = first[0];
        if (len < 34 || len > first.Length)
        {
            return;
        }

        int nameLen = first[32];
        int su = 33 + nameLen + (nameLen % 2 == 0 ? 1 : 0);
        if (su + 7 <= len && first[su] == (byte)'S' && first[su + 1] == (byte)'P' && first[su + 4] == 0xBE && first[su + 5] == 0xEF)
        {
            _suspSkip = first[su + 6];
            _rockRidge = true;
        }
    }

    private readonly record struct DirRef(long Lba, long Length);

    private DirRef? RootRecord(IsoNameKind kind)
    {
        byte[] d = kind == IsoNameKind.Joliet && _joliet is not null ? _joliet : _pvd;
        ReadOnlySpan<byte> r = d.AsSpan(156, 34);
        long lba = BinaryPrimitives.ReadUInt32LittleEndian(r[2..]);
        long length = BinaryPrimitives.ReadUInt32LittleEndian(r[10..]);
        return lba <= 0 || lba * _blockSize >= Volume.Length ? null : new DirRef(lba, length);
    }

    // ---- El Torito ----

    private List<IsoBootEntry> ReadBootCatalog(long sector)
    {
        var list = new List<IsoBootEntry>();
        long at = sector * SectorBytes;
        byte[] cat = _meta.ReadBytes(at, SectorBytes, out bool complete);
        if (!complete || cat[0] != 1 || cat[30] != 0x55 || cat[31] != 0xAA)
        {
            return list;
        }

        Add(cat.AsSpan(32, 32), at + 32);
        int pos = 64;
        for (int guard = 0; guard < 64 && pos + 32 <= cat.Length; guard++)
        {
            byte header = cat[pos];
            if (header is not (0x90 or 0x91))
            {
                break;
            }

            int count = BinaryPrimitives.ReadUInt16LittleEndian(cat.AsSpan(pos + 2));
            pos += 32;
            for (int i = 0; i < count && pos + 32 <= cat.Length; i++, pos += 32)
            {
                Add(cat.AsSpan(pos, 32), at + pos, cat[pos - 32 * (i + 1) + 1]);
            }

            if (header == 0x91)
            {
                break;
            }
        }

        return list;

        void Add(ReadOnlySpan<byte> e, long offset, byte? platform = null)
        {
            if (e[0] is not (0x88 or 0x00) || e.IndexOfAnyExcept((byte)0) < 0)
            {
                return;
            }

            byte emulation = (byte)(e[1] & 0x0F);
            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
            uint rba = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            long length = emulation switch
            {
                1 => 1_228_800,
                2 => 1_474_560,
                3 => 2_949_120,
                4 => HardDiskImageLength(rba) ?? count * 512L,
                _ => count * 512L,
            };
            list.Add(new IsoBootEntry(list.Count + 1, e[0] == 0x88, platform ?? cat[1], emulation, BinaryPrimitives.ReadUInt16LittleEndian(e[2..]), e[4],
                count, rba, length, offset));
        }
    }

    /// <summary>ハードディスクのエミュレーション: イメージの MBR の最初のパーティションの終わりまで。</summary>
    private long? HardDiskImageLength(uint rba)
    {
        byte[] mbr = _meta.ReadBytes((long)rba * SectorBytes, 512);
        if (mbr[510] != 0x55 || mbr[511] != 0xAA)
        {
            return null;
        }

        uint start = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(0x1BE + 8));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(0x1BE + 12));
        return count == 0 ? null : ((long)start + count) * 512;
    }

    // ---- ディレクトリ ----

    private sealed record Record(long Offset, long Lba, long Length, byte Flags, byte[] RawName, byte[] SystemUse, DateTime? Recorded, int UnitSize);

    private IEnumerable<Record> ReadRecords(long lba, long length, CancellationToken token)
    {
        long start = lba * _blockSize;
        long end = start + Math.Min(length, (long)MaxDirectoryEntries * 34);
        int count = 0;
        byte[] sector = new byte[SectorBytes];
        for (long at = start; at < end; at += SectorBytes)
        {
            token.ThrowIfCancellationRequested();
            int n = (int)Math.Min(SectorBytes, end - at);
            if (!_meta.TryRead(at, sector.AsSpan(0, n)))
            {
                yield break;
            }

            int pos = 0;
            while (pos + 34 <= n)
            {
                int len = sector[pos];
                if (len == 0)
                {
                    break;
                }

                if (len < 34 || pos + len > n)
                {
                    break;
                }

                int nameLen = sector[pos + 32];
                if (33 + nameLen > len)
                {
                    break;
                }

                int su = 33 + nameLen + (nameLen % 2 == 0 ? 1 : 0);
                byte[] system = su < len ? sector.AsSpan(pos + su, len - su).ToArray() : [];
                yield return new Record(at + pos, BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(pos + 2)),
                    BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(pos + 10)), sector[pos + 25], sector.AsSpan(pos + 33, nameLen).ToArray(), system,
                    RecordingDate(sector.AsSpan(pos + 18, 7)), sector[pos + 26]);
                if (++count >= MaxDirectoryEntries)
                {
                    yield break;
                }

                pos += len;
            }
        }
    }

    /// <summary>ディレクトリレコードの日時 (1900 年からの年、月、日、時、分、秒、15 分単位の時差)。</summary>
    public static DateTime? RecordingDate(ReadOnlySpan<byte> d)
    {
        if (d.IndexOfAnyExcept((byte)0) < 0)
        {
            return null;
        }

        try
        {
            var local = new DateTime(1900 + d[0], d[1], d[2], d[3], d[4], d[5], DateTimeKind.Utc);
            return local.AddMinutes(-(sbyte)d[6] * 15);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>17 バイトの日時 (ボリューム記述子の <c>YYYYMMDDHHMMSScc</c> + 時差)。</summary>
    public static DateTime? DecDate(ReadOnlySpan<byte> d)
    {
        string s = Encoding.ASCII.GetString(d[..16]);
        if (!DateTime.TryParseExact(s[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTime t))
        {
            return null;
        }

        int cs = int.TryParse(s[14..16], NumberStyles.None, CultureInfo.InvariantCulture, out int c) ? c : 0;
        return t.AddMilliseconds(cs * 10).AddMinutes(-(sbyte)d[16] * 15);
    }

    private sealed class Susp
    {
        public string? Name;
        public int? Mode;
        public long? Links;
        public DateTime? Created, Modified, Accessed, Changed;
        public string? Symlink;
        public bool Relocated;
        public long? ChildLink;
    }

    /// <summary>SUSP の項目 (Rock Ridge の NM・PX・TF・SL・CL・RE) を読む。CE の続きは 16 回までたどる。</summary>
    private Susp ParseSusp(byte[] area)
    {
        var r = new Susp();
        var name = new StringBuilder();
        var link = new StringBuilder();
        bool nameSeen = false;
        int hops = 0;
        int total = 0;
        byte[] data = area;
        int pos = _suspSkip;
        while (true)
        {
            long? ceBlock = null, ceOffset = null, ceLength = null;
            while (pos + 4 <= data.Length && total < 64 * 1024)
            {
                int len = data[pos + 2];
                if (len < 4 || pos + len > data.Length)
                {
                    break;
                }

                total += len;
                ReadOnlySpan<byte> e = data.AsSpan(pos, len);
                string sig = Encoding.ASCII.GetString(e[..2]);
                switch (sig)
                {
                    case "NM" when len >= 5:
                        if ((e[4] & 0x06) == 0)
                        {
                            name.Append(Encoding.UTF8.GetString(e[5..]));
                            nameSeen = true;
                        }

                        break;
                    case "PX" when len >= 36:
                        r.Mode = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
                        r.Links = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
                        break;
                    case "TF" when len >= 5:
                        ParseTf(e, r);
                        break;
                    case "SL" when len >= 5:
                        ParseSl(e[5..], link);
                        break;
                    case "CL" when len >= 12:
                        r.ChildLink = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
                        break;
                    case "RE":
                        r.Relocated = true;
                        break;
                    case "CE" when len >= 28:
                        ceBlock = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
                        ceOffset = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
                        ceLength = BinaryPrimitives.ReadUInt32LittleEndian(e[20..]);
                        break;
                    case "ST":
                        pos = data.Length;
                        continue;
                }

                pos += len;
            }

            if (ceBlock is not long block || hops++ >= 16 || ceLength is not long cl || cl <= 0 || cl > 64 * 1024)
            {
                break;
            }

            data = _meta.ReadBytes(block * _blockSize + ceOffset!.Value, (int)cl);
            pos = 0;
        }

        if (nameSeen)
        {
            r.Name = name.ToString();
        }

        if (link.Length > 0)
        {
            r.Symlink = link.ToString();
        }

        return r;
    }

    private static void ParseTf(ReadOnlySpan<byte> e, Susp r)
    {
        byte flags = e[4];
        bool longForm = (flags & 0x80) != 0;
        int size = longForm ? 17 : 7;
        int pos = 5;
        for (int bit = 0; bit < 7; bit++)
        {
            if ((flags & (1 << bit)) == 0)
            {
                continue;
            }

            if (pos + size > e.Length)
            {
                break;
            }

            DateTime? t = longForm ? DecDate(e.Slice(pos, 17)) : RecordingDate(e.Slice(pos, 7));
            switch (bit)
            {
                case 0: r.Created = t; break;
                case 1: r.Modified = t; break;
                case 2: r.Accessed = t; break;
                case 3: r.Changed = t; break;
            }

            pos += size;
        }
    }

    private static void ParseSl(ReadOnlySpan<byte> components, StringBuilder link)
    {
        int pos = 0;
        while (pos + 2 <= components.Length)
        {
            byte flags = components[pos];
            int len = components[pos + 1];
            if (pos + 2 + len > components.Length)
            {
                break;
            }

            if (link.Length > 0 && link[^1] != '/')
            {
                link.Append('/');
            }

            if ((flags & 0x02) != 0)
            {
                link.Append('.');
            }
            else if ((flags & 0x04) != 0)
            {
                link.Append("..");
            }
            else if ((flags & 0x08) != 0)
            {
                link.Append('/');
            }
            else
            {
                link.Append(Encoding.UTF8.GetString(components.Slice(pos + 2, len)));
            }

            pos += 2 + len;
        }
    }

    private static string BasicName(byte[] raw) => Encoding.ASCII.GetString(raw);

    private static string JolietName(byte[] raw)
    {
        string s = Encoding.BigEndianUnicode.GetString(raw, 0, raw.Length & ~1);
        return StripVersion(s);
    }

    private static string StripVersion(string s)
    {
        int semi = s.LastIndexOf(';');
        if (semi > 0 && s[(semi + 1)..].All(char.IsAsciiDigit))
        {
            s = s[..semi];
        }

        return s.EndsWith('.') && s.Length > 1 ? s[..^1] : s;
    }

    public override IEnumerable<FsEntry> ListDirectory(FsEntry directory, CancellationToken cancellationToken = default)
    {
        if (!directory.IsDirectory || directory.Tag is not IsoTag tag)
        {
            yield break;
        }

        IsoNameKind kind = tag.IsRoot ? _nameKind : tag.Kind;
        DirRef? dir = tag.IsRoot ? RootRecord(kind) : tag.Extents.Count > 0 ? new DirRef(tag.Extents[0].StartCluster, tag.Extents[0].Length) : null;
        if (dir is not { } d)
        {
            yield break;
        }

        string basePath = tag.IsRoot ? "\\" : directory.Path;
        Record? pending = null;
        var pieces = new List<Record>();
        foreach (Record r in ReadRecords(d.Lba, d.Length, cancellationToken))
        {
            bool dot = r.RawName.Length == 1 && r.RawName[0] is 0 or 1;
            if (dot)
            {
                continue;
            }

            // 複数エクステントのファイル: 0x80 の付いたレコードは続きと 1 つにまとめる。
            if ((r.Flags & 0x80) != 0)
            {
                pieces.Add(r);
                pending = r;
                continue;
            }

            pieces.Add(r);
            pending = null;
            FsEntry? e = MakeEntry([.. pieces], kind, basePath, directory);
            pieces.Clear();
            if (e is not null)
            {
                yield return e;
            }
        }

        _ = pending;
    }

    private FsEntry? MakeEntry(Record[] pieces, IsoNameKind kind, string basePath, FsEntry parent)
    {
        Record r = pieces[^1];
        bool dir = (r.Flags & 0x02) != 0;
        if ((r.Flags & 0x04) != 0)
        {
            // 関連ファイル (Mac のリソースフォークなど) は一覧に出さない。
            return null;
        }

        Susp? susp = kind == IsoNameKind.RockRidge ? ParseSusp(r.SystemUse) : null;
        if (kind == IsoNameKind.RockRidge && susp is { Relocated: true })
        {
            return null;
        }

        string name = kind switch
        {
            IsoNameKind.Joliet => JolietName(r.RawName),
            IsoNameKind.RockRidge => susp?.Name ?? StripVersion(BasicName(r.RawName)),
            _ => BasicName(r.RawName),
        };
        long lba = r.Lba;
        long length = r.Length;
        if (kind == IsoNameKind.RockRidge && susp?.ChildLink is long child)
        {
            // 深い階層の移動 (CL): 子のディレクトリの実体は移動先にある。
            byte[] first = _meta.ReadBytes(child * _blockSize, 34);
            dir = true;
            lba = child;
            length = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(10));
        }

        var extents = new List<FsExtent>();
        long logical = 0;
        foreach (Record p in pieces.Length > 1 ? pieces : [r with { Lba = lba, Length = length }])
        {
            long blocks = (p.Length + _blockSize - 1) / _blockSize;
            if (p.Length > 0 && p.Lba * _blockSize < Volume.Length)
            {
                extents.Add(new FsExtent(logical, p.Length, p.Lba * _blockSize, p.Lba, blocks));
            }

            logical += p.Length;
        }

        bool rrAttributes = kind == IsoNameKind.RockRidge && susp?.Mode is not null;
        int mode = susp?.Mode ?? (dir ? 0x416D : 0x816D);
        DateTime? modified = kind == IsoNameKind.RockRidge && susp?.Modified is { } m ? m : r.Recorded;
        var entry = new FsEntry
        {
            Id = $"iso:{kind}:{r.Offset:X}",
            Name = name,
            Path = FsText.Combine(basePath, name),
            Flags = dir ? FsEntryFlags.Directory : 0,
            Size = dir ? 0 : logical,
            AllocatedSize = extents.Sum(e => e.ClusterCount) * _blockSize,
            Modified = new FsTimestamp(modified),
            Created = kind == IsoNameKind.RockRidge && susp?.Created is { } c ? new FsTimestamp(c) : default,
            Accessed = kind == IsoNameKind.RockRidge && susp?.Accessed is { } a ? new FsTimestamp(a) : default,
            Changed = kind == IsoNameKind.RockRidge && susp?.Changed is { } ch ? new FsTimestamp(ch) : default,
            Attributes = rrAttributes ? FsText.PosixMode(mode) : (r.Flags & 0x01) != 0 ? "H" : string.Empty,
            FileIdText = $"0x{r.Offset:X}",
            FirstCluster = extents.Count > 0 ? extents[0].StartCluster : null,
            MetadataOffset = r.Offset,
            MetadataLength = 34 + r.RawName.Length + r.SystemUse.Length,
            ParentId = parent.Id,
            FragmentCount = extents.Count,
            Tag = new IsoTag(kind, extents, r.Offset, mode, susp?.Symlink, IsRoot: false),
        };
        if (r.UnitSize != 0)
        {
            entry.Corruption = FsCorruptionKind.Unsupported;
            entry.CorruptionDetail = "interleaved";
            entry.Flags |= FsEntryFlags.Corrupt;
        }

        return entry;
    }

    public override IReadOnlyList<FsExtent> GetExtents(FsEntry entry) => entry.Tag switch
    {
        IsoTag { IsRoot: true } => RootRecord(_nameKind) is { } r ? [new FsExtent(0, r.Length, r.Lba * _blockSize, r.Lba, (r.Length + _blockSize - 1) / _blockSize)] : [],
        IsoTag tag => tag.Extents,
        IReadOnlyList<FsExtent> fixedExtents => fixedExtents,
        _ => [],
    };

    public override IVolumeReader OpenContent(FsEntry entry)
    {
        IReadOnlyList<FsExtent> extents = GetExtents(entry);
        long size = entry.IsDirectory || entry.Flags.HasFlag(FsEntryFlags.Metadata) ? extents.Sum(e => e.Length) : entry.Size;
        return new ExtentContentReader(Volume, extents, size);
    }

    public override IReadOnlyList<FsDetail> Describe(FsEntry entry)
    {
        var d = new List<FsDetail>();
        if (entry.Tag is IsoTag tag && !tag.IsRoot)
        {
            byte[] rec = _meta.ReadBytes(tag.RecordOffset, 34);
            d.Add(new FsDetail("RecordOffset", $"0x{tag.RecordOffset:X}"));
            d.Add(new FsDetail("ExtentLba", tag.Extents.FirstOrDefault()?.StartCluster.ToString(CultureInfo.InvariantCulture) ?? "-"));
            d.Add(new FsDetail("FileFlags", $"0x{rec[25]:X2}"));
            d.Add(new FsDetail("NameKind", tag.Kind.ToString()));
            if (_rockRidge)
            {
                d.Add(new FsDetail("PosixMode", $"{FsText.PosixMode(tag.Mode)} (0{Convert.ToString(tag.Mode & 0xFFF, 8)})", "RockRidge"));
            }

            if (tag.SymlinkTarget is { } link)
            {
                d.Add(new FsDetail("SymlinkTarget", link, "RockRidge"));
            }

            if (tag.Extents.Count > 1)
            {
                d.Add(new FsDetail("Extents", tag.Extents.Count.ToString(CultureInfo.InvariantCulture)));
            }
        }
        else if (entry.Tag is IReadOnlyList<FsExtent> && entry.Name.StartsWith("$BootImage", StringComparison.Ordinal)
            && int.TryParse(entry.Name.AsSpan(10), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= _boot.Count)
        {
            IsoBootEntry b = _boot[n - 1];
            d.Add(new FsDetail("BootPlatform", b.PlatformName, "ElTorito"));
            d.Add(new FsDetail("BootEmulation", b.EmulationName, "ElTorito"));
            d.Add(new FsDetail("BootStartSector", b.LoadRba.ToString(CultureInfo.InvariantCulture), "ElTorito"));
            d.Add(new FsDetail("BootSectorCount", b.SectorCount.ToString(CultureInfo.InvariantCulture), "ElTorito"));
            d.Add(new FsDetail("BootLoadSegment", $"0x{b.LoadSegment:X4}", "ElTorito"));
            d.Add(new FsDetail("BootIndicator", b.Bootable ? "0x88" : "0x00", "ElTorito"));
        }

        return d;
    }

    public override IEnumerable<FsEntry> EnumerateMetadata(CancellationToken cancellationToken = default)
    {
        yield return Fixed("$SystemArea", 0, 16L * SectorBytes);
        IsoSession s = _sessions[_session];
        yield return Fixed("$VolumeDescriptors", s.DescriptorSector * SectorBytes, DescriptorCount(s) * SectorBytes);
        long pathTable = BinaryPrimitives.ReadUInt32LittleEndian(_pvd.AsSpan(140));
        long pathTableSize = BinaryPrimitives.ReadUInt32LittleEndian(_pvd.AsSpan(132));
        if (pathTable > 0 && pathTableSize > 0)
        {
            yield return Fixed("$PathTable", pathTable * _blockSize, pathTableSize);
        }

        if (_bootCatalog > 0)
        {
            yield return Fixed("$BootCatalog", _bootCatalog * SectorBytes, SectorBytes);
            foreach (IsoBootEntry b in _boot)
            {
                yield return Fixed($"$BootImage{b.Index}", (long)b.LoadRba * SectorBytes, b.ImageLength);
            }
        }
    }

    private long DescriptorCount(IsoSession s)
    {
        long n = 0;
        byte[] head = new byte[6];
        for (long sector = s.DescriptorSector; sector < s.DescriptorSector + MaxDescriptors; sector++)
        {
            _meta.Read(sector * SectorBytes, head);
            n++;
            if (head[0] == 255 || !head.AsSpan(1, 5).SequenceEqual("CD001"u8))
            {
                break;
            }
        }

        return n;
    }

    private FsEntry Fixed(string name, long offset, long length)
    {
        long blocks = (length + _blockSize - 1) / _blockSize;
        var extent = new FsExtent(0, length, offset, offset / _blockSize, blocks);
        return new FsEntry
        {
            Id = "iso:meta:" + name,
            Name = name,
            Path = "\\" + name,
            Flags = FsEntryFlags.Metadata,
            Size = length,
            AllocatedSize = blocks * _blockSize,
            MetadataOffset = offset,
            MetadataLength = (int)Math.Min(length, 2048),
            FirstCluster = offset / _blockSize,
            FragmentCount = 1,
            Tag = (IReadOnlyList<FsExtent>)[extent],
        };
    }

    /// <summary>ブートイメージ (El Torito のエントリの内容。FOR-14 の仕様 2 の書き出し)。</summary>
    public IVolumeReader OpenBootImage(IsoBootEntry entry) =>
        new ExtentContentReader(Volume, [new FsExtent(0, entry.ImageLength, (long)entry.LoadRba * SectorBytes, entry.LoadRba, (entry.ImageLength + SectorBytes - 1) / SectorBytes)],
            entry.ImageLength);

    public override IEnumerable<FsEntry> EnumerateDeleted(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(1);
        return [];
    }

    public override IEnumerable<FsEntry> EnumerateAll(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        foreach (FsEntry m in EnumerateMetadata(cancellationToken))
        {
            yield return m;
        }

        var queue = new Queue<(FsEntry, int)>([(_root, 0)]);
        var seen = new HashSet<long>();
        long done = 0;
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (FsEntry dir, int depth) = queue.Dequeue();
            foreach (FsEntry e in ListDirectory(dir, cancellationToken))
            {
                yield return e;
                if (e.IsDirectory && depth + 1 < MaxDepth && e.FirstCluster is long lba && seen.Add(lba))
                {
                    queue.Enqueue((e, depth + 1));
                }
            }

            progress?.Report(Math.Min(0.99, ++done / (double)(done + queue.Count + 1)));
        }

        progress?.Report(1);
    }

    public override IReadOnlyList<FsRegion> MetadataRegions
    {
        get
        {
            IsoSession s = _sessions[_session];
            var list = new List<FsRegion>
            {
                new(0, 16L * SectorBytes, FsRegionKind.Metadata),
                new(s.DescriptorSector * SectorBytes, DescriptorCount(s) * SectorBytes, FsRegionKind.Metadata),
            };
            if (_bootCatalog > 0)
            {
                list.Add(new FsRegion(_bootCatalog * SectorBytes, SectorBytes, FsRegionKind.Metadata));
            }

            return list;
        }
    }

    public override FileSystemSummary Summary
    {
        get
        {
            byte[] p = _pvd;
            var d = new List<FsDetail>
            {
                new("SystemId", AText(p.AsSpan(8, 32)), "PrimaryVolumeDescriptor"),
                new("VolumeId", AText(p.AsSpan(40, 32)), "PrimaryVolumeDescriptor"),
                new("VolumeSpaceSize", _volumeSpace.ToString(CultureInfo.InvariantCulture), "PrimaryVolumeDescriptor"),
                new("LogicalBlockSize", _blockSize.ToString(CultureInfo.InvariantCulture), "PrimaryVolumeDescriptor"),
                new("VolumeSetId", AText(p.AsSpan(190, 128)), "PrimaryVolumeDescriptor"),
                new("PublisherId", AText(p.AsSpan(318, 128)), "PrimaryVolumeDescriptor"),
                new("PreparerId", AText(p.AsSpan(446, 128)), "PrimaryVolumeDescriptor"),
                new("ApplicationId", AText(p.AsSpan(574, 128)), "PrimaryVolumeDescriptor"),
                new("CreationDate", new FsTimestamp(DecDate(p.AsSpan(813, 17))).ToString(), "PrimaryVolumeDescriptor"),
                new("ModificationDate", new FsTimestamp(DecDate(p.AsSpan(830, 17))).ToString(), "PrimaryVolumeDescriptor"),
                new("Joliet", HasJoliet ? "yes" : "no"),
                new("RockRidge", _rockRidge ? "yes" : "no"),
                new("NameKind", _nameKind.ToString()),
                new("Session", $"{_session + 1} / {_sessions.Count}"),
                new("UdfBridge", _hasUdf ? "yes" : "no"),
            };
            if (_joliet is { } j)
            {
                d.Add(new FsDetail("JolietVolumeId", Encoding.BigEndianUnicode.GetString(j, 40, 32).TrimEnd(' ', '\0'), "Joliet"));
            }

            foreach (IsoBootEntry b in _boot)
            {
                string g = "ElTorito";
                d.Add(new FsDetail($"BootEntry{b.Index}",
                    $"{b.PlatformName}, {b.EmulationName}, start {b.LoadRba}, sectors {b.SectorCount}, {(b.Bootable ? "bootable" : "not bootable")}", g));
            }

            return new FileSystemSummary
            {
                Kind = FileSystemKind.Iso9660,
                TypeName = "ISO 9660" + (HasJoliet || _rockRidge ? $" ({string.Join(", ", new[] { HasJoliet ? "Joliet" : null, _rockRidge ? "Rock Ridge" : null }.OfType<string>())})" : string.Empty),
                Label = HasJoliet && _nameKind == IsoNameKind.Joliet ? Encoding.BigEndianUnicode.GetString(_joliet!, 40, 32).TrimEnd(' ', '\0') : AText(p.AsSpan(40, 32)),
                ClusterSize = _blockSize,
                TotalClusters = _volumeSpace,
                Created = new FsTimestamp(DecDate(p.AsSpan(813, 17))),
                Details = d,
            };
        }
    }

    private static string AText(ReadOnlySpan<byte> b) => Encoding.ASCII.GetString(b).TrimEnd(' ', '\0');
}
