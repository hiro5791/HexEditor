using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace HexEditor.Core.FileSystems;

/// <summary>クラスタの持ち主の種類 (FOR-15 の仕様 1・3)。</summary>
public enum ClusterOwnerKind : byte
{
    File = 0,
    Metadata = 1,

    /// <summary>削除されたファイルの以前のクラスタ (割り当て解除済み。仕様 3)。</summary>
    Deleted = 2,
}

/// <summary>持ち主 1 つ (パスとファイル ID)。</summary>
public sealed record ClusterOwner(string Path, string Id, ClusterOwnerKind Kind);

/// <summary>
/// クラスタの範囲 → 持ち主の逆引き表 (FOR-15 の仕様 3)。作るのはボリューム全体の割り当ての走査 (長時間処理) で、表は 2 つのファイルに書く
/// (クラスタの範囲を開始の順に並べた固定長のレコードと、持ち主の文字列)。問い合わせはファイルを二分探索するので、ファイル数が 1,000 万でも
/// メモリ使用量は一定。ファイルはドキュメントの付随データの場所に置き、データソースの大きさとファイルシステムのシリアル番号が同じなら再利用する。
/// </summary>
public sealed class ClusterOwnerMap : IDisposable
{
    /// <summary>レコード: 開始 (8)、数 (8)、持ち主の位置 (8)、それまでの最大の終わり (8)。</summary>
    private const int RecordSize = 32;

    /// <summary>並べ替えのためにメモリに持つ範囲の数の上限 (超えたら一時ファイルに書いて後でまとめる。00 の 11.2)。</summary>
    public const int MemoryRuns = 1 << 20;

    public const int FormatVersion = 1;

    private readonly FileStream _runs;
    private readonly FileStream _owners;
    private readonly object _gate = new();
    private readonly long _count;

    private ClusterOwnerMap(string runsPath, string ownersPath, long skipped)
    {
        _runs = new FileStream(runsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.RandomAccess);
        _owners = new FileStream(ownersPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.RandomAccess);
        _count = _runs.Length / RecordSize;
        RunsPath = runsPath;
        OwnersPath = ownersPath;
        SkippedCorrupt = skipped;
    }

    public string RunsPath { get; }

    public string OwnersPath { get; }

    /// <summary>範囲の数。</summary>
    public long RunCount => _count;

    /// <summary>作成中に飛ばした破損したメタデータの項目の数 (仕様の「エラー」)。</summary>
    public long SkippedCorrupt { get; }

    /// <summary>作成した回数 (テスト用。TC-FOR-15-04 の「作成の回数」)。</summary>
    public static int BuildCount => _buildCount;

    private static int _buildCount;

    /// <summary>
    /// 表を作る (長時間処理)。<paramref name="folder"/> に <paramref name="name"/>.runs と .owners を作る。<paramref name="progress"/> は 0〜1。
    /// 割り当てられている項目と、削除された項目の以前のクラスタを含める。
    /// </summary>
    public static ClusterOwnerMap Build(IFileSystem fs, string folder, string name, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _buildCount);
        Directory.CreateDirectory(folder);
        string runsPath = Path.Combine(folder, name + ".runs");
        string ownersPath = Path.Combine(folder, name + ".owners");
        string tempRuns = runsPath + ".tmp";
        string tempOwners = ownersPath + ".tmp";
        var chunks = new List<string>();
        long skipped = 0;
        try
        {
            var buffer = new List<(long Start, long Count, long Owner)>();
            using (var owners = new FileStream(tempOwners, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                void Flush()
                {
                    if (buffer.Count == 0)
                    {
                        return;
                    }

                    buffer.Sort((a, b) => a.Start.CompareTo(b.Start));
                    string chunk = Path.Combine(folder, $"{name}.chunk{chunks.Count}.tmp");
                    using (var f = new FileStream(chunk, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        Span<byte> r = stackalloc byte[24];
                        foreach ((long s, long c, long o) in buffer)
                        {
                            BinaryPrimitives.WriteInt64LittleEndian(r, s);
                            BinaryPrimitives.WriteInt64LittleEndian(r[8..], c);
                            BinaryPrimitives.WriteInt64LittleEndian(r[16..], o);
                            f.Write(r);
                        }
                    }

                    chunks.Add(chunk);
                    buffer.Clear();
                }

                void AddOwner(FsEntry e, ClusterOwnerKind kind)
                {
                    IReadOnlyList<FsExtent> extents;
                    try
                    {
                        extents = fs.GetExtents(e);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
                    {
                        skipped++;
                        return;
                    }

                    if (e.Flags.HasFlag(FsEntryFlags.Corrupt))
                    {
                        skipped++;
                    }

                    var ranges = extents.Where(x => !x.Resident && !x.IsSparse && x.StartCluster >= 0 && x.ClusterCount > 0).ToList();
                    if (ranges.Count == 0)
                    {
                        return;
                    }

                    long ownerOffset = owners.Position;
                    string path = e.Path.Length > 0 ? e.Path : e.OriginalPath;
                    byte[] text = Encoding.UTF8.GetBytes(path + "\0" + e.Id);
                    Span<byte> head = stackalloc byte[5];
                    BinaryPrimitives.WriteInt32LittleEndian(head, text.Length);
                    head[4] = (byte)kind;
                    owners.Write(head);
                    owners.Write(text);
                    foreach (FsExtent x in ranges)
                    {
                        buffer.Add((x.StartCluster, x.ClusterCount, ownerOffset));
                        if (buffer.Count >= MemoryRuns)
                        {
                            Flush();
                        }
                    }
                }

                foreach (FsEntry e in fs.EnumerateAll(progress is null ? null : new Progress<double>(p => progress.Report(p * 0.6)), cancellationToken))
                {
                    AddOwner(e, e.Flags.HasFlag(FsEntryFlags.Metadata) ? ClusterOwnerKind.Metadata : ClusterOwnerKind.File);
                }

                foreach (FsEntry e in fs.EnumerateDeleted(progress is null ? null : new Progress<double>(p => progress.Report(0.6 + p * 0.3)), cancellationToken))
                {
                    if (e.Recovery != FsRecoveryStatus.Resident)
                    {
                        AddOwner(e, ClusterOwnerKind.Deleted);
                    }
                }

                Flush();
            }

            Merge(chunks, tempRuns, cancellationToken);
            progress?.Report(1);
            File.Move(tempRuns, runsPath, overwrite: true);
            File.Move(tempOwners, ownersPath, overwrite: true);
            return new ClusterOwnerMap(runsPath, ownersPath, skipped);
        }
        finally
        {
            foreach (string c in chunks)
            {
                TryDelete(c);
            }

            TryDelete(tempRuns);
            TryDelete(tempOwners);
        }
    }

    /// <summary>並べた一時ファイルを 1 つにまとめ、それまでの最大の終わりを書き込む。</summary>
    private static void Merge(List<string> chunks, string output, CancellationToken token)
    {
        var readers = chunks.Select(c => new BinaryReader(new FileStream(c, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16))).ToList();
        try
        {
            var heads = new (long Start, long Count, long Owner)?[readers.Count];
            for (int i = 0; i < readers.Count; i++)
            {
                heads[i] = Next(readers[i]);
            }

            using var w = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            Span<byte> r = stackalloc byte[RecordSize];
            long maxEnd = 0;
            long n = 0;
            while (true)
            {
                int best = -1;
                for (int i = 0; i < heads.Length; i++)
                {
                    if (heads[i] is { } h && (best < 0 || h.Start < heads[best]!.Value.Start))
                    {
                        best = i;
                    }
                }

                if (best < 0)
                {
                    break;
                }

                (long s, long c, long o) = heads[best]!.Value;
                heads[best] = Next(readers[best]);
                maxEnd = Math.Max(maxEnd, s + c);
                BinaryPrimitives.WriteInt64LittleEndian(r, s);
                BinaryPrimitives.WriteInt64LittleEndian(r[8..], c);
                BinaryPrimitives.WriteInt64LittleEndian(r[16..], o);
                BinaryPrimitives.WriteInt64LittleEndian(r[24..], maxEnd);
                w.Write(r);
                if ((++n & 0xFFFF) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }
            }
        }
        finally
        {
            foreach (BinaryReader reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    private static (long, long, long)? Next(BinaryReader r)
    {
        if (r.BaseStream.Position + 24 > r.BaseStream.Length)
        {
            return null;
        }

        return (r.ReadInt64(), r.ReadInt64(), r.ReadInt64());
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>保存した表を開く。ファイルがなければ null。</summary>
    public static ClusterOwnerMap? TryOpen(string folder, string name, long skipped = 0)
    {
        string runs = Path.Combine(folder, name + ".runs");
        string owners = Path.Combine(folder, name + ".owners");
        if (!File.Exists(runs) || !File.Exists(owners))
        {
            return null;
        }

        try
        {
            return new ClusterOwnerMap(runs, owners, skipped);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 保存した表があり、識別 (<see cref="Identity"/>) が同じなら開き、なければ作って識別を保存する (仕様 3: 2 回目以降・再起動後は作り直さない)。
    /// </summary>
    public static ClusterOwnerMap LoadOrBuild(string folder, string name, JsonObject identity, IFileSystem fs, out bool built,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        string identityPath = Path.Combine(folder, name + ".json");
        try
        {
            if (File.Exists(identityPath) && JsonNode.Parse(File.ReadAllText(identityPath)) is JsonObject saved && SameIdentity(saved, identity)
                && TryOpen(folder, name, saved["skipped"]?.GetValue<long>() ?? 0) is { } existing)
            {
                built = false;
                return existing;
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
        }

        ClusterOwnerMap map = Build(fs, folder, name, progress, cancellationToken);
        var stored = (JsonObject)identity.DeepClone();
        stored["skipped"] = map.SkippedCorrupt;
        File.WriteAllText(identityPath, stored.ToJsonString());
        built = true;
        return map;
    }

    /// <summary>再利用の判定に使う識別 (データソースの大きさとファイルシステムのシリアル番号。仕様 3)。</summary>
    public static JsonObject Identity(long sourceLength, long volumeOffset, IFileSystem fs) => new()
    {
        ["version"] = FormatVersion,
        ["sourceLength"] = sourceLength,
        ["volumeOffset"] = volumeOffset,
        ["kind"] = fs.Kind.ToString(),
        ["serial"] = fs.Summary.Serial,
    };

    public static bool SameIdentity(JsonObject? a, JsonObject b) =>
        a is not null && b.All(p => a[p.Key]?.ToJsonString() == p.Value?.ToJsonString());

    private (long Start, long Count, long Owner, long MaxEnd) Record(long index)
    {
        Span<byte> r = stackalloc byte[RecordSize];
        lock (_gate)
        {
            _runs.Position = index * RecordSize;
            _runs.ReadExactly(r);
        }

        return (BinaryPrimitives.ReadInt64LittleEndian(r), BinaryPrimitives.ReadInt64LittleEndian(r[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(r[16..]), BinaryPrimitives.ReadInt64LittleEndian(r[24..]));
    }

    private ClusterOwner Owner(long offset)
    {
        lock (_gate)
        {
            _owners.Position = offset;
            Span<byte> head = stackalloc byte[5];
            _owners.ReadExactly(head);
            int length = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(head), 0, 1 << 20);
            byte[] text = new byte[length];
            _owners.ReadExactly(text);
            string[] parts = Encoding.UTF8.GetString(text).Split('\0', 2);
            return new ClusterOwner(parts[0], parts.Length > 1 ? parts[1] : string.Empty, (ClusterOwnerKind)head[4]);
        }
    }

    /// <summary>クラスタ範囲 [first, first + count) を使っている持ち主 (重複を除き、最大 <paramref name="limit"/> 件)。</summary>
    public IReadOnlyList<ClusterOwner> OwnersOf(long first, long count = 1, int limit = 1000)
    {
        long last = first + Math.Max(1, count) - 1;

        // 開始が last 以下の最後のレコード。
        long lo = 0, hi = _count - 1, at = -1;
        while (lo <= hi)
        {
            long mid = (lo + hi) >>> 1;
            if (Record(mid).Start <= last)
            {
                at = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var seen = new HashSet<long>();
        var result = new List<ClusterOwner>();
        for (long i = at; i >= 0 && result.Count < limit; i--)
        {
            var rec = Record(i);
            if (rec.MaxEnd <= first)
            {
                break;
            }

            if (rec.Start + rec.Count > first && seen.Add(rec.Owner))
            {
                result.Add(Owner(rec.Owner));
            }
        }

        // 割り当て済み (ファイル・メタデータ) を先に、削除されたものを後に。
        return [.. result.OrderBy(o => o.Kind == ClusterOwnerKind.Deleted ? 1 : 0)];
    }

    public void Dispose()
    {
        _runs.Dispose();
        _owners.Dispose();
    }
}
