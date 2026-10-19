using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HexEditor.Core.FileSystems.Ntfs;

/// <summary>NTFS を開く処理 (FOR-10)。</summary>
public sealed class NtfsFileSystemProvider : IFileSystemProvider
{
    public IReadOnlyCollection<FileSystemKind> Kinds { get; } = [FileSystemKind.Ntfs];

    public IFileSystem Open(IVolumeReader volume, FileSystemOpenOptions options) => new NtfsFileSystem(volume, options);
}

/// <summary>NTFS の項目の実装用の情報 (MFT 番号、シーケンス番号、ストリーム名)。</summary>
internal sealed record NtfsTag(long Record, ushort Sequence, string Stream, NtfsFileName? IndexKey = null, long SlackOffset = -1);

/// <summary>ベースのレコードと拡張レコードの属性をまとめた 1 つのファイル。</summary>
internal sealed class NtfsFile
{
    public required NtfsRecord Base { get; init; }
    public List<NtfsAttribute> Attributes { get; } = [];
    public NtfsStandardInformation? StandardInformation { get; set; }
    public List<NtfsFileName> FileNames { get; } = [];
    public bool AttributeListBroken { get; set; }

    /// <summary>表示に使う名前 (Win32 → Win32&amp;DOS → POSIX → DOS の順)。</summary>
    public NtfsFileName? PreferredName =>
        FileNames.FirstOrDefault(f => f.Namespace == NtfsFileName.Win32) ?? FileNames.FirstOrDefault(f => f.Namespace == NtfsFileName.Win32AndDos)
        ?? FileNames.FirstOrDefault(f => f.Namespace == NtfsFileName.Posix) ?? FileNames.FirstOrDefault();

    /// <summary>名前ごとのデータストリーム (名前なしは空文字列)。複数の属性に分かれていれば VCN の順。</summary>
    public IEnumerable<IGrouping<string, NtfsAttribute>> DataStreams =>
        Attributes.Where(a => a.Type == NtfsAttributeType.Data).OrderBy(a => a.StartVcn).GroupBy(a => a.Name);

    public List<NtfsAttribute> Stream(NtfsAttributeType type, string name) =>
        [.. Attributes.Where(a => a.Type == type && a.Name == name).OrderBy(a => a.StartVcn)];
}

/// <summary>
/// NTFS の解析 (FOR-10)。ブートセクタ、$MFT (レコード 0 のランリスト)、MFT レコードと属性 (フィックスアップ、$ATTRIBUTE_LIST)、ディレクトリの
/// インデックス (INDX とインデックススラック)、代替データストリーム、LZNT1 の圧縮、$Bitmap による割り当てを扱う。MFT レコードは必要なものだけ読み、
/// 全体の走査 (削除・孤立・逆引き) は 1 レコードずつ順に読んで結果だけを返す。
/// </summary>
public sealed class NtfsFileSystem : FileSystemBase
{
    public const long RootRecord = 5;
    public const int MaxPathDepth = 256;
    public const int MaxExtensionRecords = 1024;
    private const int RecordCacheSize = 4096;

    private static readonly string[] SystemNames =
        ["$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", ".", "$Bitmap", "$Boot", "$BadClus", "$Secure", "$UpCase", "$Extend"];

    private readonly byte[] _boot;
    private readonly FileSystemOpenOptions _options;
    private readonly IVolumeReader _cached;
    private readonly Dictionary<long, LinkedListNode<NtfsFile>> _files = [];
    private readonly LinkedList<NtfsFile> _lru = new();
    private readonly Dictionary<long, string> _dirPaths = [];
    private readonly Dictionary<long, List<FsEntry>> _slack = [];
    private readonly object _gate = new();
    private readonly FsEntry _root;
    private NtfsStreamReader _mft = null!;
    private IAllocationMap? _allocation;
    private bool _allocationTried;

    public NtfsFileSystem(IVolumeReader volume, FileSystemOpenOptions options) : base(volume)
    {
        _options = options;
        _cached = new CachedVolumeReader(volume, 64 * 1024, 128);
        _boot = volume.ReadBytes(0, 512);
        if (!_boot.AsSpan(3, 8).SequenceEqual("NTFS    "u8))
        {
            throw new FileSystemOpenException("Not an NTFS volume.", FileSystemKind.Ntfs);
        }

        BytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(_boot.AsSpan(11));
        int spc = FileSystemDetector.NtfsSectorsPerCluster(_boot[13]);
        TotalSectors = BinaryPrimitives.ReadInt64LittleEndian(_boot.AsSpan(0x28));
        MftCluster = BinaryPrimitives.ReadInt64LittleEndian(_boot.AsSpan(0x30));
        MftMirrorCluster = BinaryPrimitives.ReadInt64LittleEndian(_boot.AsSpan(0x38));
        if (BytesPerSector is < 256 or > 4096 || (BytesPerSector & (BytesPerSector - 1)) != 0 || spc <= 0 || TotalSectors <= 0)
        {
            throw new FileSystemOpenException("The boot sector is inconsistent (sector or cluster size).", FileSystemKind.Ntfs);
        }

        _clusterSize = (long)BytesPerSector * spc;
        RecordSize = SizeFromBoot((sbyte)_boot[0x40]);
        IndexRecordSize = SizeFromBoot((sbyte)_boot[0x44]);
        if (RecordSize is < 256 or > 65536 || IndexRecordSize is < 256 or > 65536 || MftCluster <= 0 || MftCluster >= ClusterCount)
        {
            throw new FileSystemOpenException("The boot sector is inconsistent (MFT location or record size).", FileSystemKind.Ntfs);
        }

        Serial = BinaryPrimitives.ReadUInt64LittleEndian(_boot.AsSpan(0x48));

        // 末尾の予備のブートセクタ (仕様 1)。
        byte[] backup = volume.ReadBytes(TotalSectors * BytesPerSector, 512, out bool backupRead);
        if (!backupRead || !backup.AsSpan().SequenceEqual(_boot))
        {
            IssueList.Add(new FsIssue(FsIssueKind.BootSectorBackupMismatch, $"sector {TotalSectors}"));
        }

        // $MFT 自体の配置 (レコード 0 のデータ属性のランリスト。仕様 2)。
        byte[] raw0 = volume.ReadBytes(MftCluster * _clusterSize, RecordSize);
        NtfsRecord record0 = NtfsFormat.ParseRecord(0, MftCluster * _clusterSize, raw0, BytesPerSector);
        NtfsAttribute? data0 = record0.Of(NtfsAttributeType.Data).FirstOrDefault(a => a.Name.Length == 0 && a.NonResident);
        if (!record0.HasSignature || data0 is null)
        {
            throw new FileSystemOpenException("The $MFT record could not be read.", FileSystemKind.Ntfs);
        }

        _mft = new NtfsStreamReader(_cached, data0.Runs, _clusterSize, data0.RealSize, data0.InitializedSize, 0);
        if (record0.Of(NtfsAttributeType.AttributeList).Any())
        {
            // $MFT が拡張レコードを持つ: レコード 0 を読み直し、全部のランを集める。
            NtfsFile mftFile = Load(0, useCache: false)!;
            List<NtfsAttribute> parts = mftFile.Stream(NtfsAttributeType.Data, string.Empty);
            if (parts.Count > 0)
            {
                _mft = new NtfsStreamReader(_cached, [.. parts.SelectMany(p => p.Runs)], _clusterSize, parts[0].RealSize, parts[0].InitializedSize, 0);
            }
        }

        CompareMirror();

        _root = new FsEntry
        {
            Id = "mft:5",
            Name = string.Empty,
            Path = "\\",
            Flags = FsEntryFlags.Directory,
            FileIdText = "5",
            Tag = new NtfsTag(RootRecord, Load(RootRecord)?.Base.Sequence ?? 5, string.Empty),
        };
        lock (_gate)
        {
            _dirPaths[RootRecord] = "\\";
        }
    }

    private readonly long _clusterSize;

    public int BytesPerSector { get; }
    public long TotalSectors { get; }
    public long MftCluster { get; }
    public long MftMirrorCluster { get; }
    public int RecordSize { get; }
    public int IndexRecordSize { get; }
    public ulong Serial { get; }

    /// <summary>
    /// MFT レコードの数 ($MFT のデータのサイズ ÷ レコードサイズ)。壊れた $MFT のサイズがランの範囲・ボリュームを超えるときは、ランのある範囲までに限る
    /// (全レコードの走査がランのない範囲を延々と読まないため)。
    /// </summary>
    public long RecordCount => _mft.BoundedLength(Volume.Length) / RecordSize;

    public override FileSystemKind Kind => FileSystemKind.Ntfs;
    public override long ClusterSize => _clusterSize;
    public override long ClusterCount => TotalSectors * BytesPerSector / _clusterSize;
    public override int SectorSize => BytesPerSector;

    /// <summary>ファイルシステムの範囲 (末尾の予備のブートセクタを含む)。</summary>
    public override long FileSystemLength => (TotalSectors + 1) * BytesPerSector;

    protected override long ClusterAreaOffset => 0;
    public override FsEntry Root => _root;

    private int SizeFromBoot(sbyte value) => value < 0 ? (-value < 31 ? 1 << -value : 0) : (int)Math.Min(int.MaxValue, value * _clusterSize);

    private void CompareMirror()
    {
        if (MftMirrorCluster <= 0 || MftMirrorCluster >= ClusterCount)
        {
            IssueList.Add(new FsIssue(FsIssueKind.MftMirrorMismatch, "location"));
            return;
        }

        for (int i = 0; i < 4; i++)
        {
            byte[] main = ReadRecordRaw(i, out _);
            byte[] mirror = Volume.ReadBytes(MftMirrorCluster * _clusterSize + (long)i * RecordSize, RecordSize);
            if (!main.AsSpan().SequenceEqual(mirror))
            {
                IssueList.Add(new FsIssue(FsIssueKind.MftMirrorMismatch, $"record {i}"));
                return;
            }
        }
    }

    // ---- MFT レコード ----

    private byte[] ReadRecordRaw(long number, out long volumeOffset)
    {
        volumeOffset = _mft.VolumeOffsetOf(number * RecordSize) ?? -1;
        byte[] raw = new byte[RecordSize];
        if (number >= 0 && (number + 1) * RecordSize <= _mft.Length)
        {
            _mft.Read(number * RecordSize, raw);
        }

        return raw;
    }

    /// <summary>MFT レコード 1 つを読む (「MFT レコードを表示」。範囲外なら null)。</summary>
    public NtfsRecord? ReadRecord(long number)
    {
        if (number < 0 || number >= RecordCount)
        {
            return null;
        }

        byte[] raw = ReadRecordRaw(number, out long offset);
        return NtfsFormat.ParseRecord(number, offset, raw, BytesPerSector);
    }

    /// <summary>MFT レコードのボリューム上の位置 (「MFT レコードへ移動」)。</summary>
    public long? RecordOffset(long number) => number >= 0 && number < RecordCount ? _mft.VolumeOffsetOf(number * RecordSize) : null;

    /// <summary>ベースのレコードと拡張レコードを読んでまとめる。署名のないレコードなら null。</summary>
    internal NtfsFile? Load(long number, bool useCache = true)
    {
        if (useCache)
        {
            lock (_gate)
            {
                if (_files.TryGetValue(number, out LinkedListNode<NtfsFile>? node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return node.Value;
                }
            }
        }

        NtfsRecord? record = ReadRecord(number);
        if (record is null || !record.HasSignature)
        {
            return null;
        }

        var file = new NtfsFile { Base = record };
        file.Attributes.AddRange(record.Attributes);
        if (record.Of(NtfsAttributeType.AttributeList).FirstOrDefault() is { } list)
        {
            LoadExtensions(file, list);
        }

        foreach (NtfsAttribute a in file.Attributes)
        {
            if (a.Type == NtfsAttributeType.StandardInformation && !a.NonResident)
            {
                file.StandardInformation ??= NtfsStandardInformation.Parse(a.Value);
            }
            else if (a.Type == NtfsAttributeType.FileName && !a.NonResident && NtfsFileName.Parse(a.Value) is { } fn)
            {
                file.FileNames.Add(fn);
            }
        }

        if (useCache)
        {
            lock (_gate)
            {
                if (!_files.ContainsKey(number))
                {
                    _files[number] = _lru.AddFirst(file);
                    while (_files.Count > RecordCacheSize && _lru.Last is { } last)
                    {
                        _lru.RemoveLast();
                        _files.Remove(last.Value.Base.Number);
                    }
                }
            }
        }

        return file;
    }

    private void LoadExtensions(NtfsFile file, NtfsAttribute list)
    {
        byte[] value = list.NonResident
            ? ReadAll(new NtfsStreamReader(_cached, list.Runs, _clusterSize, list.RealSize, list.InitializedSize, 0), 1 << 20)
            : list.Value;
        var loaded = new HashSet<long> { file.Base.Number };
        int at = 0;
        while (at + 26 <= value.Length)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(at + 4));
            if (length < 26 || at + length > value.Length)
            {
                file.AttributeListBroken = true;
                break;
            }

            long reference = (long)(BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(at + 16)) & 0xFFFFFFFFFFFF);
            if (loaded.Add(reference) && loaded.Count <= MaxExtensionRecords && ReadRecord(reference) is { HasSignature: true } ext
                && ext.BaseRecord == file.Base.Number)
            {
                file.Attributes.AddRange(ext.Attributes);
            }

            at += length;
        }
    }

    private static byte[] ReadAll(IVolumeReader reader, int limit)
    {
        byte[] data = new byte[(int)Math.Min(reader.Length, limit)];
        reader.Read(0, data);
        return data;
    }

    // ---- 割り当て ($Bitmap) ----

    public override IAllocationMap? Allocation
    {
        get
        {
            if (!_allocationTried)
            {
                _allocationTried = true;
                if (Load(6) is { } bitmap && bitmap.Stream(NtfsAttributeType.Data, string.Empty) is { Count: > 0 } parts)
                {
                    IVolumeReader content = parts[0].NonResident
                        ? new NtfsStreamReader(_cached, [.. parts.SelectMany(p => p.Runs)], _clusterSize, parts[0].RealSize, parts[0].InitializedSize, 0)
                        : new ArrayVolumeReader(parts[0].Value);
                    if (content.Length * 8 >= ClusterCount)
                    {
                        _allocation = new BitmapAllocationMap(content, ClusterCount, 0);
                    }
                }

                if (_allocation is null)
                {
                    IssueList.Add(new FsIssue(FsIssueKind.AllocationUnreadable, "$Bitmap"));
                }
            }

            return _allocation;
        }
    }

    public override IReadOnlyList<FsRegion> MetadataRegions
    {
        get
        {
            var list = new List<FsRegion> { new(0, Math.Max(8192, _clusterSize), FsRegionKind.Metadata) };
            for (long r = 0; r < 16; r++)
            {
                if (r == RootRecord || Load(r) is not { } file)
                {
                    continue;
                }

                foreach (NtfsAttribute a in file.Attributes.Where(a => a.NonResident))
                {
                    if (r == 8 && a.Name == "$Bad")
                    {
                        continue;
                    }

                    foreach (NtfsRun run in a.Runs)
                    {
                        if (run.Lcn is long lcn)
                        {
                            list.Add(new FsRegion(lcn * _clusterSize, run.Length * _clusterSize, FsRegionKind.Metadata));
                        }
                    }
                }
            }

            list.Add(new FsRegion(TotalSectors * BytesPerSector, BytesPerSector, FsRegionKind.Metadata));
            return list;
        }
    }

    // ---- 概要 ----

    public override FileSystemSummary Summary
    {
        get
        {
            string label = string.Empty;
            string version = string.Empty;
            FsTimestamp created = default;
            if (Load(3) is { } volumeFile)
            {
                if (volumeFile.Attributes.FirstOrDefault(a => a.Type == NtfsAttributeType.VolumeName) is { } name)
                {
                    label = Encoding.Unicode.GetString(name.Value);
                }

                if (volumeFile.Attributes.FirstOrDefault(a => a.Type == NtfsAttributeType.VolumeInformation) is { Value.Length: >= 12 } info)
                {
                    version = $"{info.Value[8]}.{info.Value[9]}";
                }

                created = FsTimestamp.FromFileTime(volumeFile.StandardInformation?.Created ?? 0);
            }

            var d = new List<FsDetail>
            {
                new("OemName", "NTFS", "BPB"),
                new("BytesPerSector", BytesPerSector.ToString(CultureInfo.InvariantCulture), "BPB"),
                new("SectorsPerCluster", (_clusterSize / BytesPerSector).ToString(CultureInfo.InvariantCulture), "BPB"),
                new("TotalSectors", TotalSectors.ToString(CultureInfo.InvariantCulture), "BPB"),
                new("MftCluster", MftCluster.ToString(CultureInfo.InvariantCulture), "BPB"),
                new("MftMirrorCluster", MftMirrorCluster.ToString(CultureInfo.InvariantCulture), "BPB"),
                new("FileRecordSize", RecordSize.ToString(CultureInfo.InvariantCulture), "BPB"),
                new("IndexRecordSize", IndexRecordSize.ToString(CultureInfo.InvariantCulture), "BPB"),
                new("VolumeSerialNumber", $"0x{Serial:X16}", "BPB"),
                new("NtfsVersion", version),
                new("MftRecords", RecordCount.ToString(CultureInfo.InvariantCulture)),
                new("MftSize", _mft.Length.ToString(CultureInfo.InvariantCulture)),
                new("MftFragments", _mft.Runs.Count.ToString(CultureInfo.InvariantCulture)),
            };
            return new FileSystemSummary
            {
                Kind = FileSystemKind.Ntfs,
                TypeName = "NTFS",
                Label = label,
                Serial = $"{Serial:X16}",
                ClusterSize = _clusterSize,
                TotalClusters = ClusterCount,
                Created = created,
                Details = d,
            };
        }
    }

    // ---- 項目 ----

    /// <summary>$STANDARD_INFORMATION と $FILE_NAME の日時の不一致 (仕様 4。前、または $SI だけ小数部が 0)。</summary>
    internal static bool TimesMismatch(NtfsStandardInformation? si, NtfsFileName? fn)
    {
        if (si is null || fn is null)
        {
            return false;
        }

        (long S, long F)[] pairs = [(si.Created, fn.Created), (si.Modified, fn.Modified), (si.MftModified, fn.MftModified), (si.Accessed, fn.Accessed)];
        foreach ((long s, long f) in pairs)
        {
            if (s == 0 || f == 0)
            {
                continue;
            }

            if (s < f || s % 10_000_000 == 0 && f % 10_000_000 != 0)
            {
                return true;
            }
        }

        return false;
    }

    private FsEntry MakeEntry(NtfsFile file, NtfsFileName? name, string parentPath, string? stream, FsEntryState state)
    {
        name ??= file.PreferredName;
        string baseName = name?.Name ?? $"$MFT{file.Base.Number}";
        bool dir = file.Base.IsDirectory && stream is null;
        NtfsStandardInformation? si = file.StandardInformation;
        List<NtfsAttribute> data = file.Stream(NtfsAttributeType.Data, stream ?? string.Empty);
        long size = data.Count == 0 ? 0 : data[0].DataSize;
        long allocated = data.Count == 0 ? 0 : data[0].NonResident ? data[0].AllocatedSize : 0;
        uint attributes = si?.Attributes ?? name?.Attributes ?? 0;
        bool compressed = data.Any(a => a.IsCompressed);
        bool encrypted = data.Any(a => a.IsEncrypted) || (attributes & 0x4000) != 0;
        string displayName = stream is null ? baseName : $"{baseName}:{stream}";
        string path = (parentPath.EndsWith('\\') ? parentPath : parentPath + "\\") + displayName;
        var flags = (dir ? FsEntryFlags.Directory : 0) | (stream is not null ? FsEntryFlags.Stream : 0) | (compressed ? FsEntryFlags.Compressed : 0)
            | (encrypted ? FsEntryFlags.Encrypted : 0) | (data.Any(a => a.IsSparse) ? FsEntryFlags.Sparse : 0)
            | (data.Count > 0 && !data[0].NonResident ? FsEntryFlags.Resident : 0)
            | (TimesMismatch(si, name) ? FsEntryFlags.TimeMismatch : 0) | (file.Base.Number < 16 ? FsEntryFlags.Metadata : 0);
        var entry = new FsEntry
        {
            Id = stream is null ? $"mft:{file.Base.Number}" : $"mft:{file.Base.Number}:{stream}",
            Name = displayName,
            Path = state == FsEntryState.Deleted ? string.Empty : path,
            OriginalPath = state == FsEntryState.Deleted ? path : string.Empty,
            Flags = flags,
            Size = dir ? 0 : size,
            AllocatedSize = allocated,
            Created = FsTimestamp.FromFileTime(si?.Created ?? name?.Created ?? 0),
            Modified = FsTimestamp.FromFileTime(si?.Modified ?? name?.Modified ?? 0),
            Accessed = FsTimestamp.FromFileTime(si?.Accessed ?? name?.Accessed ?? 0),
            Changed = FsTimestamp.FromFileTime(si?.MftModified ?? name?.MftModified ?? 0),
            Attributes = NtfsFormat.AttributeText(attributes | (dir ? 0x10000000u : 0)),
            FileIdText = file.Base.Number.ToString(CultureInfo.InvariantCulture),
            State = state,
            DeletedSource = state == FsEntryState.Deleted ? FsDeletedSource.MftRecord : FsDeletedSource.None,
            MetadataOffset = file.Base.VolumeOffset >= 0 ? file.Base.VolumeOffset : null,
            MetadataLength = RecordSize,
            StreamName = stream,
            ParentId = name is null ? null : $"mft:{name.ParentRecord}",
            Tag = new NtfsTag(file.Base.Number, file.Base.Sequence, stream ?? string.Empty),
            FirstCluster = data.Count > 0 && data[0].NonResident ? data[0].Runs.FirstOrDefault(r => r.Lcn is not null).Lcn : null,
        };
        if (!file.Base.FixupOk)
        {
            entry.Flags |= FsEntryFlags.Corrupt;
            entry.Corruption = FsCorruptionKind.FixupMismatch;
            entry.CorruptionDetail = $"record {file.Base.Number}";
        }
        else if (!file.Base.AttributesValid || file.AttributeListBroken)
        {
            entry.Flags |= FsEntryFlags.Corrupt;
            entry.Corruption = FsCorruptionKind.BadAttribute;
            entry.CorruptionDetail = $"record {file.Base.Number}";
        }
        else if (data.Any(a => !a.RunListValid))
        {
            entry.Flags |= FsEntryFlags.Corrupt;
            entry.Corruption = FsCorruptionKind.BadRunList;
            entry.CorruptionDetail = $"record {file.Base.Number}";
        }

        IReadOnlyList<FsExtent> extents = GetExtents(entry);
        entry.FragmentCount = extents.Count(e => !e.Resident);
        if (state == FsEntryState.Deleted)
        {
            entry.Recovery = RecoveryOf(extents);
        }

        return entry;
    }

    /// <summary>レコードが壊れて読めない項目 (インデックスの $FILE_NAME だけで作る。仕様 3 の「破損」)。</summary>
    private FsEntry BrokenEntry(long record, NtfsFileName key, string parentPath, NtfsRecord? raw)
    {
        bool dir = (key.Attributes & 0x10000000) != 0;
        var entry = new FsEntry
        {
            Id = $"mft:{record}",
            Name = key.Name,
            Path = (parentPath.EndsWith('\\') ? parentPath : parentPath + "\\") + key.Name,
            Flags = (dir ? FsEntryFlags.Directory : 0) | FsEntryFlags.Corrupt,
            Size = dir ? 0 : key.RealSize,
            AllocatedSize = key.AllocatedSize,
            Created = FsTimestamp.FromFileTime(key.Created),
            Modified = FsTimestamp.FromFileTime(key.Modified),
            Accessed = FsTimestamp.FromFileTime(key.Accessed),
            Changed = FsTimestamp.FromFileTime(key.MftModified),
            Attributes = NtfsFormat.AttributeText(key.Attributes),
            FileIdText = record.ToString(CultureInfo.InvariantCulture),
            MetadataOffset = RecordOffset(record),
            MetadataLength = RecordSize,
            ParentId = $"mft:{key.ParentRecord}",
            Tag = new NtfsTag(record, 0, string.Empty, key),
            Corruption = raw is { HasSignature: true } ? FsCorruptionKind.FixupMismatch : FsCorruptionKind.BadSignature,
            CorruptionDetail = $"record {record}",
            FragmentCount = 0,
        };
        return entry;
    }

    public override FsEntry? FindById(string id)
    {
        if (!id.StartsWith("mft:", StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = id[4..].Split(':', 2);
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long number) || Load(number) is not { } file)
        {
            return null;
        }

        if (number == RootRecord && parts.Length == 1)
        {
            return _root;
        }

        NtfsFileName? name = file.PreferredName;
        string parent = name is null ? "\\" : PathOfDirectory(name.ParentRecord) ?? "\\?";
        return MakeEntry(file, name, parent, parts.Length > 1 ? parts[1] : null, file.Base.InUse ? FsEntryState.Normal : FsEntryState.Deleted);
    }

    /// <summary>ディレクトリのパス (親をたどる。見つからなければ null)。</summary>
    private string? PathOfDirectory(long record, int depth = 0)
    {
        lock (_gate)
        {
            if (_dirPaths.TryGetValue(record, out string? known))
            {
                return known;
            }
        }

        if (depth > MaxPathDepth || Load(record) is not { } file || !file.Base.InUse || !file.Base.IsDirectory
            || file.PreferredName is not { } name)
        {
            return null;
        }

        if (name.ParentRecord == record)
        {
            return null;
        }

        string? parent = PathOfDirectory(name.ParentRecord, depth + 1);
        if (parent is null)
        {
            return null;
        }

        string path = (parent.EndsWith('\\') ? parent : parent + "\\") + name.Name;
        lock (_gate)
        {
            if (_dirPaths.Count > 1_000_000)
            {
                _dirPaths.Clear();
                _dirPaths[RootRecord] = "\\";
            }

            _dirPaths[record] = path;
        }

        return path;
    }

    // ---- ディレクトリのインデックス (仕様 6) ----

    private sealed record IndexEntry(long Record, ushort Sequence, NtfsFileName Key, long VolumeOffset);

    /// <summary>INDX レコードのフィックスアップを適用して返す (署名が違えば null)。</summary>
    private byte[]? ReadIndexBlock(NtfsStreamReader allocation, long index, out long volumeOffset)
    {
        volumeOffset = allocation.VolumeOffsetOf(index * IndexRecordSize) ?? -1;
        byte[] block = new byte[IndexRecordSize];
        allocation.Read(index * IndexRecordSize, block);
        if (!block.AsSpan(0, 4).SequenceEqual("INDX"u8))
        {
            return null;
        }

        NtfsFormat.ApplyFixup(block, BytesPerSector);
        return block;
    }

    private static IEnumerable<IndexEntry> ParseIndexEntries(byte[] buffer, int start, int end, long volumeBase)
    {
        int at = start;
        for (int n = 0; n < 65536 && at + 16 <= end; n++)
        {
            ulong reference = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(at));
            int length = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at + 8));
            int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at + 10));
            int flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at + 12));
            if (length < 16 || at + length > end)
            {
                yield break;
            }

            if ((flags & 2) != 0)
            {
                yield break;
            }

            if (keyLength >= 66 && 16 + keyLength <= length && NtfsFileName.Parse(buffer.AsSpan(at + 16, keyLength)) is { } key)
            {
                yield return new IndexEntry((long)(reference & 0xFFFFFFFFFFFF), (ushort)(reference >> 48), key, volumeBase >= 0 ? volumeBase + at : -1);
            }

            at += length;
        }
    }

    /// <summary>インデックススラックに残ったエントリを探す (仕様 6)。親の参照がこのディレクトリのものだけを候補にする。</summary>
    private static IEnumerable<IndexEntry> ScanSlack(byte[] buffer, int start, int end, long directory, long volumeBase)
    {
        for (int at = (start + 7) & ~7; at + 16 + 66 <= end; at += 8)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at + 8));
            int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at + 10));
            if (keyLength is < 66 or > 66 + 510 || length < 16 + keyLength || at + 16 + keyLength > end)
            {
                continue;
            }

            if (NtfsFileName.Parse(buffer.AsSpan(at + 16, keyLength)) is not { } key || key.ParentRecord != directory || key.Name.Length == 0
                || key.Name.Any(c => c < 0x20) || !Plausible(key.Created) || !Plausible(key.Modified))
            {
                continue;
            }

            ulong reference = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(at));
            yield return new IndexEntry((long)(reference & 0xFFFFFFFFFFFF), (ushort)(reference >> 48), key, volumeBase >= 0 ? volumeBase + at : -1);
            at += ((length + 7) & ~7) - 8;
        }
    }

    /// <summary>1980〜2200 年の FILETIME か。</summary>
    private static bool Plausible(long fileTime) => fileTime is > 0x01A8E79FE1D58000 and < 0x0410000000000000;

    /// <summary>ディレクトリのインデックスの全エントリと、スラックのエントリ。</summary>
    private (List<IndexEntry> Live, List<IndexEntry> Slack) ReadIndex(NtfsFile dir, CancellationToken token)
    {
        var live = new List<IndexEntry>();
        var slack = new List<IndexEntry>();
        if (dir.Stream(NtfsAttributeType.IndexRoot, "$I30").FirstOrDefault() is { NonResident: false } root && root.Value.Length >= 32)
        {
            byte[] v = root.Value;
            int headerAt = 16;
            int entriesOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(headerAt));
            int used = (int)BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(headerAt + 4));
            live.AddRange(ParseIndexEntries(v, headerAt + entriesOffset, Math.Min(v.Length, headerAt + used),
                dir.Base.VolumeOffset >= 0 ? dir.Base.VolumeOffset + root.ValueOffset : -1));
        }

        List<NtfsAttribute> allocation = dir.Stream(NtfsAttributeType.IndexAllocation, "$I30");
        if (allocation.Count > 0 && allocation[0].NonResident)
        {
            var reader = new NtfsStreamReader(_cached, [.. allocation.SelectMany(a => a.Runs)], _clusterSize, allocation[0].RealSize,
                allocation[0].InitializedSize, 0);
            byte[] bitmap = dir.Stream(NtfsAttributeType.Bitmap, "$I30").FirstOrDefault() is { } bm
                ? bm.NonResident ? ReadAll(new NtfsStreamReader(_cached, bm.Runs, _clusterSize, bm.RealSize, bm.InitializedSize, 0), 1 << 24) : bm.Value
                : [];
            // 壊れた実サイズでランのない範囲を延々と走査しないよう、ランのある範囲までに限る。
            long blocks = reader.BoundedLength(Volume.Length) / IndexRecordSize;
            for (long b = 0; b < blocks; b++)
            {
                if ((b & 0x3F) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                bool inUse = bitmap.Length == 0 || b / 8 < bitmap.Length && (bitmap[b / 8] & (1 << (int)(b % 8))) != 0;
                byte[]? block = ReadIndexBlock(reader, b, out long blockOffset);
                if (block is null)
                {
                    continue;
                }

                int entriesOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0x18));
                int used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0x1C)), (uint)IndexRecordSize);
                int allocated = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0x20)), (uint)IndexRecordSize);
                int start = 0x18 + entriesOffset;
                int usedEnd = Math.Min(IndexRecordSize, 0x18 + used);
                int allocEnd = Math.Min(IndexRecordSize, 0x18 + allocated);
                if (inUse)
                {
                    live.AddRange(ParseIndexEntries(block, start, usedEnd, blockOffset));
                    slack.AddRange(ScanSlack(block, usedEnd, allocEnd, dir.Base.Number, blockOffset));
                }
                else
                {
                    slack.AddRange(ScanSlack(block, start, allocEnd, dir.Base.Number, blockOffset));
                }
            }
        }

        // スラックに残っている現在の項目の古い写しは除く。
        var names = live.Select(e => (e.Record, e.Key.Name)).ToHashSet();
        slack = [.. slack.Where(s => !names.Contains((s.Record, s.Key.Name))).DistinctBy(s => (s.Record, s.Key.Name, s.Key.Namespace))];
        return (live, slack);
    }

    public override IEnumerable<FsEntry> ListDirectory(FsEntry directory, CancellationToken cancellationToken = default)
    {
        if (directory.Tag is not NtfsTag tag || !directory.IsDirectory || tag.Stream.Length > 0 || Load(tag.Record) is not { } dir)
        {
            yield break;
        }

        string parentPath = directory.Path.Length > 0 ? directory.Path : directory.OriginalPath;
        lock (_gate)
        {
            if (directory.State == FsEntryState.Normal)
            {
                _dirPaths[tag.Record] = parentPath;
            }
        }

        (List<IndexEntry> live, List<IndexEntry> slack) = ReadIndex(dir, cancellationToken);
        lock (_gate)
        {
            _slack[tag.Record] = [.. slack.Select(s => SlackEntry(s, parentPath))];
        }

        var seen = new HashSet<(long, string)>();
        var longNamed = live.Where(o => o.Key.Namespace != NtfsFileName.Dos).Select(o => o.Record).ToHashSet();
        int index = 0;
        foreach (IndexEntry e in live)
        {
            if ((++index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (e.Key.Namespace == NtfsFileName.Dos && longNamed.Contains(e.Record))
            {
                continue;
            }

            // システムファイル (レコード 0〜15) はルートの一覧に出さず「$メタデータ」に出す (仕様 10)。
            if (e.Record == tag.Record || e.Record < 16 || !seen.Add((e.Record, e.Key.Name)))
            {
                continue;
            }

            NtfsFile? file = Load(e.Record);
            if (file is null || !file.Base.FixupOk || !file.Base.InUse)
            {
                yield return BrokenEntry(e.Record, e.Key, parentPath, file?.Base);
                continue;
            }

            FsEntry entry = MakeEntry(file, e.Key, parentPath, null, FsEntryState.Normal);
            yield return entry;
            if (_options.ShowStreams)
            {
                foreach (IGrouping<string, NtfsAttribute> stream in file.DataStreams.Where(g => g.Key.Length > 0))
                {
                    yield return MakeEntry(file, e.Key, parentPath, stream.Key, FsEntryState.Normal);
                }
            }
        }
    }

    private FsEntry SlackEntry(IndexEntry e, string parentPath)
    {
        string path = (parentPath.EndsWith('\\') ? parentPath : parentPath + "\\") + e.Key.Name;
        bool dir = (e.Key.Attributes & 0x10000000) != 0;
        var entry = new FsEntry
        {
            Id = $"indx:{e.VolumeOffset:X}",
            Name = e.Key.Name,
            OriginalPath = path,
            Flags = dir ? FsEntryFlags.Directory : 0,
            Size = e.Key.RealSize,
            AllocatedSize = e.Key.AllocatedSize,
            Created = FsTimestamp.FromFileTime(e.Key.Created),
            Modified = FsTimestamp.FromFileTime(e.Key.Modified),
            Accessed = FsTimestamp.FromFileTime(e.Key.Accessed),
            Changed = FsTimestamp.FromFileTime(e.Key.MftModified),
            Attributes = NtfsFormat.AttributeText(e.Key.Attributes),
            FileIdText = e.Record.ToString(CultureInfo.InvariantCulture),
            State = FsEntryState.Deleted,
            DeletedSource = FsDeletedSource.IndexSlack,
            MetadataOffset = e.VolumeOffset >= 0 ? e.VolumeOffset : null,
            MetadataLength = 16 + 66 + e.Key.Name.Length * 2,
            ParentId = $"mft:{e.Key.ParentRecord}",
            Tag = new NtfsTag(e.Record, e.Sequence, string.Empty, e.Key, e.VolumeOffset),
        };

        // MFT レコードがまだ同じファイルのもの (シーケンス番号が同じで、使われていない) なら、そこからデータを読める。
        NtfsFile? file = Load(e.Record);
        if (file is not null && !file.Base.InUse && file.Base.Sequence == e.Sequence)
        {
            IReadOnlyList<FsExtent> extents = GetExtents(entry);
            entry.FragmentCount = extents.Count(x => !x.Resident);
            entry.Recovery = RecoveryOf(extents);
        }
        else if (file is not null && file.Base.InUse && file.Base.Sequence != e.Sequence)
        {
            entry.Recovery = FsRecoveryStatus.Overwritten;
        }
        else
        {
            entry.Recovery = FsRecoveryStatus.Unknown;
            entry.Flags |= FsEntryFlags.LocationUnknown;
        }

        return entry;
    }

    // ---- データ ----

    public override IReadOnlyList<FsExtent> GetExtents(FsEntry entry)
    {
        if (entry.Tag is IReadOnlyList<FsExtent> fixedExtents)
        {
            return fixedExtents;
        }

        if (entry.Tag is not NtfsTag tag || Load(tag.Record) is not { } file || !file.Base.FixupOk)
        {
            return [];
        }

        if (tag.SlackOffset >= 0 && (file.Base.InUse || file.Base.Sequence != tag.Sequence))
        {
            return [];
        }

        if (entry.IsDirectory && tag.Stream.Length == 0)
        {
            List<NtfsAttribute> index = file.Stream(NtfsAttributeType.IndexAllocation, "$I30");
            return index.Count == 0 ? [] : RunsToExtents(index, index[0].AllocatedSize);
        }

        List<NtfsAttribute> data = file.Stream(NtfsAttributeType.Data, tag.Stream);
        if (data.Count == 0)
        {
            return [];
        }

        if (!data[0].NonResident)
        {
            long offset = file.Base.VolumeOffset >= 0 ? file.Base.VolumeOffset + data[0].ValueOffset : -1;
            return [new FsExtent(0, data[0].Value.Length, offset >= 0 ? offset : null, -1, 0) { Resident = true }];
        }

        return RunsToExtents(data, data[0].RealSize);
    }

    private List<FsExtent> RunsToExtents(List<NtfsAttribute> parts, long size)
    {
        bool compressed = parts.Any(p => p.IsCompressed);
        var list = new List<FsExtent>();
        foreach (NtfsRun run in parts.SelectMany(p => p.Runs).OrderBy(r => r.Vcn))
        {
            long logical = run.Vcn * _clusterSize;
            if (run.Lcn is not long lcn || logical >= size && size >= 0)
            {
                continue;
            }

            long length = Math.Min(run.Length * _clusterSize, size - logical);
            list.Add(new FsExtent(logical, length, lcn * _clusterSize, lcn, run.Length) { Compressed = compressed, StoredLength = run.Length * _clusterSize });
        }

        return list;
    }

    public override IVolumeReader OpenContent(FsEntry entry)
    {
        if (entry.Tag is IReadOnlyList<FsExtent> fixedExtents)
        {
            return new ExtentContentReader(Volume, fixedExtents, entry.Size);
        }

        if (entry.Tag is not NtfsTag tag || Load(tag.Record) is not { } file)
        {
            return new ArrayVolumeReader([]);
        }

        if (tag.SlackOffset >= 0 && (file.Base.InUse || file.Base.Sequence != tag.Sequence))
        {
            return new ArrayVolumeReader([]);
        }

        List<NtfsAttribute> data = entry.IsDirectory && tag.Stream.Length == 0
            ? file.Stream(NtfsAttributeType.IndexAllocation, "$I30")
            : file.Stream(NtfsAttributeType.Data, tag.Stream);
        if (data.Count == 0)
        {
            return new ArrayVolumeReader([]);
        }

        if (!data[0].NonResident)
        {
            return new ArrayVolumeReader(data[0].Value);
        }

        NtfsAttribute first = data[0];
        return new NtfsStreamReader(_cached, [.. data.SelectMany(p => p.Runs)], _clusterSize, first.RealSize, first.InitializedSize,
            first.IsCompressed ? first.CompressionUnit : 0);
    }

    /// <summary>ファイルの論理オフセットのボリューム上の位置 (圧縮・常駐・スパースなら null)。</summary>
    public long? VolumeOffsetOf(FsEntry entry, long logical) => OpenContent(entry) is NtfsStreamReader r ? r.VolumeOffsetOf(logical) : null;

    // ---- 詳細 ----

    public override IReadOnlyList<FsDetail> Describe(FsEntry entry)
    {
        var d = new List<FsDetail>();
        if (entry.Tag is not NtfsTag tag)
        {
            return d;
        }

        d.Add(new FsDetail("MftRecord", tag.Record.ToString(CultureInfo.InvariantCulture)));
        if (tag.IndexKey is { } key && entry.DeletedSource == FsDeletedSource.IndexSlack)
        {
            d.Add(new FsDetail("IndexSlackOffset", $"0x{tag.SlackOffset:X}"));
            AddFileName(d, key);
        }

        NtfsFile? file = Load(tag.Record);
        if (file is null)
        {
            return d;
        }

        NtfsRecord r = file.Base;
        d.Add(new FsDetail("RecordOffset", $"0x{r.VolumeOffset:X}", "Header"));
        d.Add(new FsDetail("Signature", r.HasSignature ? "FILE" : "-", "Header"));
        d.Add(new FsDetail("Fixup", r.FixupOk ? "OK" : "NG", "Header"));
        d.Add(new FsDetail("SequenceNumber", r.Sequence.ToString(CultureInfo.InvariantCulture), "Header"));
        d.Add(new FsDetail("HardLinkCount", r.HardLinks.ToString(CultureInfo.InvariantCulture), "Header"));
        d.Add(new FsDetail("RecordFlags", $"0x{r.Flags:X4}" + (r.InUse ? " InUse" : string.Empty) + (r.IsDirectory ? " Directory" : string.Empty), "Header"));
        d.Add(new FsDetail("BytesInUse", r.BytesInUse.ToString(CultureInfo.InvariantCulture), "Header"));
        d.Add(new FsDetail("BaseRecord", r.BaseRecord.ToString(CultureInfo.InvariantCulture), "Header"));
        d.Add(new FsDetail("NextAttributeId", r.NextAttributeId.ToString(CultureInfo.InvariantCulture), "Header"));
        d.Add(new FsDetail("LogSequenceNumber", r.LogSequenceNumber.ToString(CultureInfo.InvariantCulture), "Header"));
        if (file.StandardInformation is { } si)
        {
            d.Add(new FsDetail("SiCreated", FsTimestamp.FromFileTime(si.Created).ToString(), "$STANDARD_INFORMATION"));
            d.Add(new FsDetail("SiModified", FsTimestamp.FromFileTime(si.Modified).ToString(), "$STANDARD_INFORMATION"));
            d.Add(new FsDetail("SiMftModified", FsTimestamp.FromFileTime(si.MftModified).ToString(), "$STANDARD_INFORMATION"));
            d.Add(new FsDetail("SiAccessed", FsTimestamp.FromFileTime(si.Accessed).ToString(), "$STANDARD_INFORMATION"));
            d.Add(new FsDetail("SiAttributes", $"0x{si.Attributes:X8}", "$STANDARD_INFORMATION"));
            d.Add(new FsDetail("OwnerId", si.OwnerId.ToString(CultureInfo.InvariantCulture), "$STANDARD_INFORMATION"));
            d.Add(new FsDetail("SecurityId", si.SecurityId.ToString(CultureInfo.InvariantCulture), "$STANDARD_INFORMATION"));
            d.Add(new FsDetail("Usn", si.Usn.ToString(CultureInfo.InvariantCulture), "$STANDARD_INFORMATION"));
        }

        foreach (NtfsFileName fn in file.FileNames)
        {
            AddFileName(d, fn);
        }

        if (TimesMismatch(file.StandardInformation, file.PreferredName))
        {
            d.Add(new FsDetail("TimeMismatch", "1"));
        }

        foreach (NtfsAttribute a in file.Attributes)
        {
            string text = a.NonResident
                ? $"non-resident, VCN {a.StartVcn}–{a.LastVcn}, allocated {a.AllocatedSize}, size {a.RealSize}, initialized {a.InitializedSize}"
                  + (a.IsCompressed ? $", compression unit {a.CompressionUnit}" : string.Empty)
                  + $", runs: {string.Join(" ", a.Runs.Take(64).Select(x => x.Lcn is long l ? $"{x.Length}@{l}" : $"{x.Length}@sparse"))}"
                  + (a.Runs.Count > 64 ? " …" : string.Empty)
                : $"resident, {a.Value.Length} bytes";
            d.Add(new FsDetail(AttributeName(a.Type) + (a.Name.Length > 0 ? $" \"{a.Name}\"" : string.Empty), text, "Attributes"));
        }

        if (entry.Flags.HasFlag(FsEntryFlags.Encrypted))
        {
            d.Add(new FsDetail("Efs", "1"));
        }

        // 小さいストリーム (Zone.Identifier など) は内容をテキストで示す (仕様 5)。
        if (entry.StreamName is not null && entry.Size is > 0 and <= 4096)
        {
            byte[] content = new byte[entry.Size];
            OpenContent(entry).Read(0, content);
            d.Add(new FsDetail("StreamContent", Encoding.UTF8.GetString(content)));
        }

        return d;
    }

    private static void AddFileName(List<FsDetail> d, NtfsFileName fn)
    {
        string ns = fn.Namespace switch { 0 => "POSIX", 1 => "Win32", 2 => "DOS", _ => "Win32&DOS" };
        string group = $"$FILE_NAME ({ns})";
        d.Add(new FsDetail("FnName", fn.Name, group));
        d.Add(new FsDetail("FnParent", $"{fn.ParentRecord} (seq {fn.ParentSequence})", group));
        d.Add(new FsDetail("FnCreated", FsTimestamp.FromFileTime(fn.Created).ToString(), group));
        d.Add(new FsDetail("FnModified", FsTimestamp.FromFileTime(fn.Modified).ToString(), group));
        d.Add(new FsDetail("FnMftModified", FsTimestamp.FromFileTime(fn.MftModified).ToString(), group));
        d.Add(new FsDetail("FnAccessed", FsTimestamp.FromFileTime(fn.Accessed).ToString(), group));
        d.Add(new FsDetail("FnRealSize", fn.RealSize.ToString(CultureInfo.InvariantCulture), group));
    }

    public static string AttributeName(NtfsAttributeType type) => type switch
    {
        NtfsAttributeType.StandardInformation => "$STANDARD_INFORMATION",
        NtfsAttributeType.AttributeList => "$ATTRIBUTE_LIST",
        NtfsAttributeType.FileName => "$FILE_NAME",
        NtfsAttributeType.ObjectId => "$OBJECT_ID",
        NtfsAttributeType.SecurityDescriptor => "$SECURITY_DESCRIPTOR",
        NtfsAttributeType.VolumeName => "$VOLUME_NAME",
        NtfsAttributeType.VolumeInformation => "$VOLUME_INFORMATION",
        NtfsAttributeType.Data => "$DATA",
        NtfsAttributeType.IndexRoot => "$INDEX_ROOT",
        NtfsAttributeType.IndexAllocation => "$INDEX_ALLOCATION",
        NtfsAttributeType.Bitmap => "$BITMAP",
        NtfsAttributeType.ReparsePoint => "$REPARSE_POINT",
        NtfsAttributeType.EaInformation => "$EA_INFORMATION",
        NtfsAttributeType.Ea => "$EA",
        NtfsAttributeType.LoggedUtilityStream => "$LOGGED_UTILITY_STREAM",
        _ => $"0x{(uint)type:X}",
    };

    // ---- 走査 ----

    public override IEnumerable<FsEntry> EnumerateMetadata(CancellationToken cancellationToken = default)
    {
        for (long r = 0; r < SystemNames.Length; r++)
        {
            if (r == RootRecord || Load(r) is not { } file)
            {
                continue;
            }

            NtfsFileName name = file.PreferredName ?? new NtfsFileName(RootRecord, 5, 0, 0, 0, 0, 0, 0, 0, 1, SystemNames[r]);
            yield return MakeEntry(file, name, "\\", null, FsEntryState.Normal);
            foreach (IGrouping<string, NtfsAttribute> stream in file.DataStreams.Where(g => g.Key.Length > 0))
            {
                yield return MakeEntry(file, name, "\\", stream.Key, FsEntryState.Normal);
            }
        }

        // $Extend 以下。
        if (FindById("mft:11") is { IsDirectory: true } extend)
        {
            foreach (FsEntry e in ListDirectory(extend, cancellationToken))
            {
                e.Flags |= FsEntryFlags.Metadata;
                yield return e;
            }
        }
    }

    /// <summary>MFT のすべてのレコードを順に読む (長時間処理)。</summary>
    private IEnumerable<NtfsFile> ScanRecords(IProgress<double>? progress, CancellationToken token)
    {
        long count = RecordCount;
        for (long n = 0; n < count; n++)
        {
            if ((n & 0xFF) == 0)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report((double)n / Math.Max(1, count));
            }

            NtfsFile? file = Load(n, useCache: false);
            if (file is not null)
            {
                yield return file;
            }
        }

        progress?.Report(1);
    }

    public override IEnumerable<FsEntry> EnumerateDeleted(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var dirs = new List<long>();
        foreach (NtfsFile file in ScanRecords(progress is null ? null : new Progress<double>(p => progress.Report(p * 0.7)), cancellationToken))
        {
            if (file.Base.BaseRecord != 0)
            {
                continue;
            }

            if (file.Base.InUse)
            {
                if (file.Base.IsDirectory && file.Stream(NtfsAttributeType.IndexAllocation, "$I30").Count > 0)
                {
                    dirs.Add(file.Base.Number);
                }

                continue;
            }

            if (file.PreferredName is not { } name)
            {
                continue;
            }

            string parent = PathOfDirectory(name.ParentRecord) ?? DeletedParentPath(name.ParentRecord);
            yield return MakeEntry(file, name, parent, null, FsEntryState.Deleted);
        }

        // インデックススラックのエントリ (すべてのディレクトリの INDX)。
        for (int i = 0; i < dirs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.7 + 0.3 * i / Math.Max(1, dirs.Count));
            long number = dirs[i];
            List<FsEntry>? known;
            lock (_gate)
            {
                _slack.TryGetValue(number, out known);
            }

            if (known is null && Load(number) is { } dir)
            {
                string path = PathOfDirectory(number) ?? "\\?";
                (_, List<IndexEntry> slack) = ReadIndex(dir, cancellationToken);
                known = [.. slack.Select(s => SlackEntry(s, path))];
            }

            foreach (FsEntry e in known ?? [])
            {
                yield return e;
            }
        }

        progress?.Report(1);
    }

    /// <summary>削除された親の分かる範囲のパス (FOR-17 の仕様 2)。</summary>
    private string DeletedParentPath(long record)
    {
        var parts = new List<string>();
        long current = record;
        for (int depth = 0; depth < MaxPathDepth; depth++)
        {
            if (PathOfDirectory(current) is { } known)
            {
                parts.Reverse();
                return known.TrimEnd('\\') + "\\" + string.Join("\\", parts);
            }

            if (Load(current) is not { } file || file.PreferredName is not { } name || name.ParentRecord == current)
            {
                break;
            }

            parts.Add(name.Name);
            current = name.ParentRecord;
        }

        parts.Reverse();
        return "\\?\\" + string.Join("\\", parts);
    }

    public override IEnumerable<FsEntry> EnumerateOrphans(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        foreach (NtfsFile file in ScanRecords(progress, cancellationToken))
        {
            if (!file.Base.InUse || file.Base.BaseRecord != 0 || file.Base.Number < 16 || file.PreferredName is not { } name)
            {
                continue;
            }

            NtfsFile? parent = Load(name.ParentRecord);
            bool orphan = parent is null || !parent.Base.InUse || !parent.Base.IsDirectory || parent.Base.Sequence != name.ParentSequence
                || PathOfDirectory(name.ParentRecord) is null;
            if (orphan)
            {
                FsEntry e = MakeEntry(file, name, "\\?", null, FsEntryState.Orphan);
                e.State = FsEntryState.Orphan;
                yield return e;
            }
        }
    }

    public override IEnumerable<FsEntry> EnumerateAll(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        foreach (NtfsFile file in ScanRecords(progress, cancellationToken))
        {
            if (!file.Base.InUse || file.Base.BaseRecord != 0)
            {
                continue;
            }

            NtfsFileName? name = file.PreferredName;
            string parent = file.Base.Number == RootRecord ? string.Empty
                : name is null ? "\\" : PathOfDirectory(name.ParentRecord) ?? "\\?";
            if (file.Base.Number == RootRecord)
            {
                yield return MakeEntry(file, new NtfsFileName(RootRecord, 5, 0, 0, 0, 0, 0, 0, 0x10000000, 1, string.Empty), string.Empty, null,
                    FsEntryState.Normal) is var root ? WithPath(root, "\\") : null!;
                continue;
            }

            yield return MakeEntry(file, name, parent, null, FsEntryState.Normal);
            foreach (IGrouping<string, NtfsAttribute> stream in file.DataStreams.Where(g => g.Key.Length > 0))
            {
                yield return MakeEntry(file, name, parent, stream.Key, FsEntryState.Normal);
            }
        }
    }

    public override IEnumerable<FsEntry> GetStreams(FsEntry entry)
    {
        if (entry.Tag is not NtfsTag { Stream.Length: 0 } tag || entry.StreamName is not null || Load(tag.Record) is not { } file)
        {
            yield break;
        }

        NtfsFileName? name = file.PreferredName;
        string full = entry.Path.Length > 0 ? entry.Path : entry.OriginalPath;
        int slash = full.LastIndexOf('\\');
        string parentPath = slash > 0 ? full[..slash] : "\\";
        foreach (IGrouping<string, NtfsAttribute> stream in file.DataStreams.Where(g => g.Key.Length > 0))
        {
            yield return MakeEntry(file, name, parentPath, stream.Key, entry.State);
        }
    }

    /// <summary>$MFT 自体のデータラン ($LogFile の対象の MFT 番号を求めるのに使う)。</summary>
    public IReadOnlyList<NtfsRun> MftRuns => _mft.Runs;

    private static FsEntry WithPath(FsEntry entry, string path)
    {
        entry.Path = path;
        return entry;
    }
}
