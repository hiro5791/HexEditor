using System.Globalization;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.Core.Sources;

namespace HexEditor.Core.FileSystems.Volumes;

/// <summary>論理ボリュームの方式。</summary>
public enum LogicalVolumeScheme
{
    Ldm,
    Lvm2,
}

/// <summary>論理ボリュームの種類 (FOR-07 の仕様 1・2。一覧の「種類」)。</summary>
public enum LogicalVolumeKind
{
    Simple,
    Spanned,
    Striped,
    Mirror,
    Raid5,
    Linear,
    Unsupported,
}

/// <summary>
/// 構成要素 1 つ (LDM のディスク、LVM2 の物理ボリューム)。<see cref="Id"/> は LDM のディスク GUID、LVM2 の PV UUID (ダッシュなし)。
/// <see cref="Reader"/> はディスク (LDM) または PV の範囲 (LVM2) の読み込み。
/// </summary>
public sealed record LogicalVolumeMember(LogicalVolumeScheme Scheme, string Id, string DisplayName, IVolumeReader Reader, string GroupId, long Sequence,
    LdmDatabase? Ldm, Lvm2PhysicalVolumeLabel? Lvm)
{
    /// <summary>表示用の識別子 (GUID は <c>D</c> 形式、PV UUID は 6-4-4-4-4-4-6)。</summary>
    public string DisplayId => Scheme == LogicalVolumeScheme.Ldm ? Id : Lvm2PhysicalVolumeLabel.Format(Id);
}

/// <summary>連続した区間 (ボリュームの位置 → 構成要素の位置。バイト)。</summary>
public sealed record LvPiece(long LogicalOffset, long Length, string MemberId, long MemberOffset);

/// <summary>ストライプの区間 (<see cref="StripeSize"/> バイトずつ、列の順に並べる)。</summary>
public sealed record LvStripeSet(long LogicalOffset, long Length, long StripeSize, IReadOnlyList<(string MemberId, long MemberOffset)> Columns);

/// <summary>FOR-26 (RAID の再構成) に渡す設定 (LDM のミラー・RAID-5)。</summary>
public sealed record LogicalVolumeRaidRequest(LogicalVolumeKind Kind, string VolumeName, long Size, long StripeSize,
    IReadOnlyList<(string MemberId, long MemberOffset, long Length)> Members, string Layout);

/// <summary>論理ボリューム 1 つ (FOR-07 の画面の一覧: 名前、種類、サイズ、構成、状態)。</summary>
public sealed record LogicalVolumeInfo
{
    public required LogicalVolumeScheme Scheme { get; init; }
    public required string Group { get; init; }
    public required string Name { get; init; }
    public required LogicalVolumeKind Kind { get; init; }

    /// <summary>対応していない種類のときの種類の名前 (LVM2 の thin など)。</summary>
    public string TypeText { get; init; } = string.Empty;

    public long Size { get; init; }

    /// <summary>構成要素の ID (並びの順)。</summary>
    public IReadOnlyList<string> Members { get; init; } = [];

    public IReadOnlyList<LvPiece> Pieces { get; init; } = [];
    public IReadOnlyList<LvStripeSet> Stripes { get; init; } = [];
    public LogicalVolumeRaidRequest? Raid { get; init; }

    /// <summary>構成の文 (訳さない。<c>2 disks, stripe 64 KiB</c> など)。</summary>
    public string Layout { get; init; } = string.Empty;

    /// <summary>仮想ドキュメントとして開けるか (ミラー・RAID-5 は FOR-26、LVM2 の thin などは開けない)。</summary>
    public bool CanOpenDirectly => Kind is LogicalVolumeKind.Simple or LogicalVolumeKind.Spanned or LogicalVolumeKind.Striped or LogicalVolumeKind.Linear;
}

/// <summary>メタデータの警告 (シーケンス番号の不一致など)。</summary>
public sealed record LogicalVolumeIssue(string Group, string Detail);

/// <summary>解析の結果。</summary>
public sealed record LogicalVolumeScan(IReadOnlyList<LogicalVolumeInfo> Volumes, IReadOnlyList<LogicalVolumeMember> Members, IReadOnlyList<LogicalVolumeIssue> Issues)
{
    /// <summary>ボリュームの構成要素のうち、開いているディスク・イメージの中に見つからないもの (表示用の ID)。</summary>
    public IReadOnlyList<string> MissingMembers(LogicalVolumeInfo volume) =>
        [.. volume.Members.Distinct().Where(id => !Members.Any(m => m.Id == id)).Select(id => volume.Scheme == LogicalVolumeScheme.Lvm2 ? Lvm2PhysicalVolumeLabel.Format(id) : id)];
}

/// <summary>
/// LDM と LVM2 の論理ボリュームの組み立て (FOR-07)。開いているディスク・イメージ・パーティションから構成要素 (LDM の PRIVHEAD、LVM2 の PV ラベル) を
/// 探し、同じディスクグループ・ボリュームグループのメタデータのうちシーケンス番号の最も大きいものを使う (違えば警告)。シンプル・スパン・ストライプ (LDM)、
/// linear・striped (LVM2) は <see cref="LogicalVolumeByteSource"/> として開ける。読むのはメタデータの数セクタと、要求された範囲だけ。
/// </summary>
public static class LogicalVolumeAnalyzer
{
    private const int Sector = 512;

    /// <summary>ディスク (またはパーティションのドキュメント) の構成要素を探す。</summary>
    public static IReadOnlyList<LogicalVolumeMember> Discover(IVolumeReader disk, string displayName, PartitionAnalysis? partitions = null)
    {
        var list = new List<LogicalVolumeMember>();

        // LDM: MBR の 42 は LBA 6、GPT の LDM メタデータパーティションは最後のセクタに PRIVHEAD。
        var candidates = new List<long> { 6 };
        foreach (PartitionRow row in partitions?.Partitions ?? [])
        {
            if (row.TypeGuid == PartitionTypes.LdmMetadata)
            {
                candidates.Insert(0, row.StartOffset / Sector + row.SectorCount * row.SectorSize / Sector - 1);
                candidates.Add(row.StartOffset / Sector);
            }
        }

        foreach (long lba in candidates.Distinct())
        {
            if (LdmDatabase.Read(disk, lba) is { } ldm)
            {
                list.Add(new LogicalVolumeMember(LogicalVolumeScheme.Ldm, ldm.DiskGuid.ToString("D"), displayName, disk, ldm.DiskGroupGuid.ToString("D"),
                    ldm.Sequence, ldm, null));
                break;
            }
        }

        // LVM2: ディスク全体、または LVM のパーティション。
        if (Lvm2PhysicalVolumeLabel.Read(disk) is { VolumeGroup: { } } whole)
        {
            list.Add(Member(whole, disk, displayName));
        }

        foreach (PartitionRow row in partitions?.Partitions ?? [])
        {
            if (row.IsContainer || row.StartOffset <= 0 || row.FileSystem != FileSystemKind.Lvm2 && row.TypeCode != "8E" && row.TypeGuid != PartitionTypes.LinuxLvm)
            {
                continue;
            }

            IVolumeReader part = disk.Slice(row.StartOffset, row.Size);
            if (Lvm2PhysicalVolumeLabel.Read(part) is { VolumeGroup: { } } pv)
            {
                list.Add(Member(pv, part, $"{displayName} ({row.Number})"));
            }
        }

        return list;
    }

    private static LogicalVolumeMember Member(Lvm2PhysicalVolumeLabel pv, IVolumeReader reader, string name) =>
        new(LogicalVolumeScheme.Lvm2, Lvm2PhysicalVolumeLabel.Normalize(pv.PvUuid), name, reader, pv.VolumeGroup!.Id, pv.VolumeGroup.Sequence, null, pv);

    /// <summary>構成要素から論理ボリュームを組み立てる。</summary>
    public static LogicalVolumeScan Analyze(IEnumerable<LogicalVolumeMember> members)
    {
        List<LogicalVolumeMember> all = [.. members];
        var volumes = new List<LogicalVolumeInfo>();
        var issues = new List<LogicalVolumeIssue>();
        foreach (IGrouping<(LogicalVolumeScheme, string), LogicalVolumeMember> group in all.GroupBy(m => (m.Scheme, m.GroupId)))
        {
            LogicalVolumeMember newest = group.OrderByDescending(m => m.Sequence).First();
            if (group.Select(m => m.Sequence).Distinct().Count() > 1)
            {
                issues.Add(new LogicalVolumeIssue(group.Key.Item2, string.Join(", ", group.Select(m => $"{m.DisplayName}: {m.Sequence}"))));
            }

            foreach (string w in group.SelectMany(m => (IEnumerable<string>?)m.Ldm?.Warnings ?? m.Lvm?.Warnings ?? []).Distinct())
            {
                issues.Add(new LogicalVolumeIssue(group.Key.Item2, w));
            }

            if (newest.Ldm is { } ldm)
            {
                volumes.AddRange(LdmVolumes(ldm));
            }
            else if (newest.Lvm?.VolumeGroup is { } vg)
            {
                volumes.AddRange(LvmVolumes(vg));
            }
        }

        // 同じ構成要素が複数のドキュメントで開かれている場合は最初のものを使う。
        List<LogicalVolumeMember> unique = [.. all.GroupBy(m => m.Id).Select(g => g.First())];
        return new LogicalVolumeScan(volumes, unique, issues);
    }

    private static IEnumerable<LogicalVolumeInfo> LdmVolumes(LdmDatabase db)
    {
        string group = db.DiskGroupName.Length > 0 ? db.DiskGroupName : db.DiskGroupGuid.ToString("D");
        foreach (LdmVolumeRecord vol in db.Volumes)
        {
            List<LdmComponentRecord> comps = [.. db.Components.Where(c => c.ParentId == vol.ObjectId)];
            long size = (long)Math.Min(vol.Sectors, long.MaxValue / Sector) * Sector;
            string Disk(ulong id) => db.Disks.FirstOrDefault(d => d.ObjectId == id)?.DiskGuid.ToString("D") ?? $"#{id}";
            long Offset(LdmPartitionRecord p) => (db.LogicalDiskStart + (long)Math.Min(p.Start, long.MaxValue / 4096)) * Sector;
            if (comps.Count == 0)
            {
                continue;
            }

            if (comps.Count > 1 || vol.VolumeType == "raid5" || comps[0].ComponentType == 3)
            {
                bool mirror = comps.Count > 1;
                var raidMembers = new List<(string, long, long)>();
                foreach (LdmComponentRecord c in comps)
                {
                    foreach (LdmPartitionRecord p in db.Partitions.Where(p => p.ParentId == c.ObjectId).OrderBy(p => p.Index ?? 0).ThenBy(p => p.VolumeOffset))
                    {
                        raidMembers.Add((Disk(p.DiskId), Offset(p), (long)p.Sectors * Sector));
                    }
                }

                long stripe = (long)comps[0].ChunkSectors * Sector;
                LogicalVolumeKind kind = mirror ? LogicalVolumeKind.Mirror : LogicalVolumeKind.Raid5;
                yield return new LogicalVolumeInfo
                {
                    Scheme = LogicalVolumeScheme.Ldm,
                    Group = group,
                    Name = vol.Name,
                    Kind = kind,
                    Size = size,
                    Members = [.. raidMembers.Select(m => m.Item1)],
                    Raid = new LogicalVolumeRaidRequest(kind, vol.Name, size, stripe, raidMembers, mirror ? "Mirror" : "LeftAsymmetric"),
                    Layout = mirror ? $"{comps.Count} plexes" : $"{raidMembers.Count} columns, stripe {stripe / 1024} KiB",
                };
                continue;
            }

            LdmComponentRecord comp = comps[0];
            List<LdmPartitionRecord> parts = [.. db.Partitions.Where(p => p.ParentId == comp.ObjectId)];
            if (comp.ComponentType == 1 && parts.Count > 0)
            {
                // ストライプ: 列の順 (索引があれば索引の順)。
                parts = [.. parts.OrderBy(p => p.Index ?? int.MaxValue).ThenBy(p => p.VolumeOffset)];
                long stripe = (long)Math.Max(1, comp.ChunkSectors) * Sector;
                yield return new LogicalVolumeInfo
                {
                    Scheme = LogicalVolumeScheme.Ldm,
                    Group = group,
                    Name = vol.Name,
                    Kind = LogicalVolumeKind.Striped,
                    Size = size,
                    Members = [.. parts.Select(p => Disk(p.DiskId))],
                    Stripes = [new LvStripeSet(0, size, stripe, [.. parts.Select(p => (Disk(p.DiskId), Offset(p)))])],
                    Layout = $"{parts.Count} disks, stripe {stripe / 1024} KiB",
                };
                continue;
            }

            parts = [.. parts.OrderBy(p => p.VolumeOffset)];
            var pieces = new List<LvPiece>();
            foreach (LdmPartitionRecord p in parts)
            {
                pieces.Add(new LvPiece((long)Math.Min(p.VolumeOffset, long.MaxValue / 4096) * Sector, (long)Math.Min(p.Sectors, long.MaxValue / 4096) * Sector,
                    Disk(p.DiskId), Offset(p)));
            }

            yield return new LogicalVolumeInfo
            {
                Scheme = LogicalVolumeScheme.Ldm,
                Group = group,
                Name = vol.Name,
                Kind = pieces.Count > 1 ? LogicalVolumeKind.Spanned : LogicalVolumeKind.Simple,
                Size = size,
                Members = [.. pieces.Select(p => p.MemberId)],
                Pieces = pieces,
                Layout = $"{pieces.Count} extents",
            };
        }
    }

    private static readonly string[] Unsupported = ["thin", "thin-pool", "snapshot", "cache", "cache-pool", "mirror", "raid", "vdo", "writecache", "integrity"];

    private static IEnumerable<LogicalVolumeInfo> LvmVolumes(LvmVolumeGroup vg)
    {
        long extent = vg.ExtentSize * Sector;
        foreach (LvmLogicalVolume lv in vg.LogicalVolumes)
        {
            long size = lv.Segments.Sum(s => s.ExtentCount) * extent;
            string? unsupported = lv.Segments.Select(s => s.Type).FirstOrDefault(t => t is not ("striped" or "linear"));
            if (unsupported is not null || lv.Segments.Count == 0)
            {
                yield return new LogicalVolumeInfo
                {
                    Scheme = LogicalVolumeScheme.Lvm2,
                    Group = vg.Name,
                    Name = lv.Name,
                    Kind = LogicalVolumeKind.Unsupported,
                    TypeText = unsupported ?? string.Empty,
                    Size = size,
                    Layout = string.Join(", ", lv.Segments.Select(s => s.Type)),
                };
                continue;
            }

            var pieces = new List<LvPiece>();
            var stripes = new List<LvStripeSet>();
            var members = new List<string>();
            foreach (LvmSegment seg in lv.Segments)
            {
                long logical = seg.StartExtent * extent;
                long length = seg.ExtentCount * extent;
                var columns = new List<(string, long)>();
                foreach ((string pvName, long pvExtent) in seg.Stripes)
                {
                    LvmPhysicalVolume? pv = vg.PhysicalVolumes.FirstOrDefault(p => p.Name == pvName);
                    string id = Lvm2PhysicalVolumeLabel.Normalize(pv?.Id ?? pvName);
                    columns.Add((id, (pv?.PeStart ?? 0) * Sector + pvExtent * extent));
                    members.Add(id);
                }

                if (columns.Count == 1)
                {
                    pieces.Add(new LvPiece(logical, length, columns[0].Item1, columns[0].Item2));
                }
                else if (columns.Count > 1)
                {
                    stripes.Add(new LvStripeSet(logical, length, Math.Max(1, seg.StripeSize) * Sector, columns));
                }
            }

            bool striped = stripes.Count > 0;
            yield return new LogicalVolumeInfo
            {
                Scheme = LogicalVolumeScheme.Lvm2,
                Group = vg.Name,
                Name = lv.Name,
                Kind = striped ? LogicalVolumeKind.Striped : LogicalVolumeKind.Linear,
                Size = size,
                Members = members,
                Pieces = pieces,
                Stripes = stripes,
                Layout = string.Create(CultureInfo.InvariantCulture, $"{lv.Segments.Count} segments, {members.Distinct().Count()} PVs"),
            };
        }
    }

    /// <summary>
    /// 仮想ドキュメントにする。構成要素が足りない・開けない種類なら <see cref="InvalidOperationException"/> (呼び出し側は先に
    /// <see cref="LogicalVolumeScan.MissingMembers"/> と <see cref="LogicalVolumeInfo.CanOpenDirectly"/> を確かめる)。
    /// </summary>
    public static LogicalVolumeByteSource Open(LogicalVolumeInfo volume, LogicalVolumeScan scan, IReadOnlyDictionary<string, IVolumeReader>? assigned = null)
    {
        if (!volume.CanOpenDirectly)
        {
            throw new InvalidOperationException($"{volume.Kind} volumes cannot be opened directly.");
        }

        var readers = new Dictionary<string, IVolumeReader>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in volume.Members.Distinct())
        {
            IVolumeReader? r = assigned?.GetValueOrDefault(id) ?? scan.Members.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))?.Reader;
            readers[id] = r ?? throw new InvalidOperationException($"Member {id} is missing.");
        }

        return new LogicalVolumeByteSource(volume, readers);
    }
}

/// <summary>
/// 論理ボリュームの読み取り専用のデータソース (FOR-07 の仕様 3)。要求された範囲だけを構成要素から読む。構成要素の範囲外・読めない部分は 0 で、
/// 読めなかった範囲を結果で返す。
/// </summary>
public sealed class LogicalVolumeByteSource : ByteSourceBase
{
    private readonly LogicalVolumeInfo _volume;
    private readonly IReadOnlyDictionary<string, IVolumeReader> _members;

    public LogicalVolumeByteSource(LogicalVolumeInfo volume, IReadOnlyDictionary<string, IVolumeReader> members)
    {
        _volume = volume;
        _members = members;
        Identity = $"lv:{volume.Scheme}:{volume.Group}:{volume.Name}:{string.Join(',', volume.Members.Distinct())}";
    }

    public LogicalVolumeInfo Volume => _volume;

    public override string DisplayName => _volume.Name;
    public override string Identity { get; }
    public override long Length => _volume.Size;
    public override int LogicalSectorSize => 512;
    public override SourceCapabilities Capabilities => SourceCapabilities.None;

    /// <summary>ボリュームの位置に対応する構成要素と位置 (ステータスバー。なければ null)。</summary>
    public (string MemberId, long Offset)? Map(long offset)
    {
        foreach (LvPiece p in _volume.Pieces)
        {
            if (offset >= p.LogicalOffset && offset < p.LogicalOffset + p.Length)
            {
                return (p.MemberId, p.MemberOffset + (offset - p.LogicalOffset));
            }
        }

        foreach (LvStripeSet s in _volume.Stripes)
        {
            if (offset >= s.LogicalOffset && offset < s.LogicalOffset + s.Length)
            {
                long within = offset - s.LogicalOffset;
                long chunk = within / s.StripeSize;
                int column = (int)(chunk % s.Columns.Count);
                long row = chunk / s.Columns.Count;
                (string id, long start) = s.Columns[column];
                return (id, start + row * s.StripeSize + within % s.StripeSize);
            }
        }

        return null;
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int total = ClampToLength(offset, buffer.Length);
        if (total <= 0)
        {
            return new ReadResult(0);
        }

        Span<byte> dest = buffer[..total];
        dest.Clear();
        List<UnreadableRange>? bad = null;
        int done = 0;
        while (done < total)
        {
            long at = offset + done;
            if (Map(at) is not { } target || !_members.TryGetValue(target.MemberId, out IVolumeReader? reader))
            {
                // 構成要素のない区間 (メタデータの矛盾): 次の区間の先頭まで 0。
                long next = _volume.Pieces.Select(p => p.LogicalOffset).Concat(_volume.Stripes.Select(s => s.LogicalOffset)).Where(x => x > at)
                    .DefaultIfEmpty(offset + total).Min();
                int gap = (int)Math.Max(1, Math.Min(total - done, next - at));
                (bad ??= []).Add(new UnreadableRange(at, gap, UnreadableReason.Unallocated));
                done += gap;
                continue;
            }

            int n = (int)Math.Min(total - done, RunLength(at));
            ReadResult r = reader.Read(target.Offset, dest.Slice(done, n));
            foreach (UnreadableRange u in r.Unreadable)
            {
                (bad ??= []).Add(u with { Offset = at + (u.Offset - target.Offset) });
            }

            done += Math.Max(1, n);
        }

        return new ReadResult(total, bad);
    }

    /// <summary>同じ構成要素の連続した区間の残りの長さ。</summary>
    private long RunLength(long offset)
    {
        foreach (LvPiece p in _volume.Pieces)
        {
            if (offset >= p.LogicalOffset && offset < p.LogicalOffset + p.Length)
            {
                return p.LogicalOffset + p.Length - offset;
            }
        }

        foreach (LvStripeSet s in _volume.Stripes)
        {
            if (offset >= s.LogicalOffset && offset < s.LogicalOffset + s.Length)
            {
                long within = offset - s.LogicalOffset;
                return Math.Min(s.StripeSize - within % s.StripeSize, s.LogicalOffset + s.Length - offset);
            }
        }

        return 1;
    }
}
