using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HexEditor.Core.Engine;
using HexEditor.Core.FileSystems.Partitions;

namespace HexEditor.Core.FileSystems;

/// <summary>ブラウザのツリーの仮想フォルダ (FOR-09 の仕様 4)。</summary>
public enum FsVirtualFolder
{
    Metadata,
    Deleted,
    Orphans,
    Unallocated,
    VolumeSlack,
}

/// <summary>
/// ドキュメント 1 つのファイルシステムの解析 (FOR-09 の仕様 2: ブラウザはドキュメントごとに 1 つ)。ファイルシステムはドキュメントの
/// <see cref="VolumeOffset"/> から始まる (パーティションのタブなら 0、ディスクのドキュメントならパーティションの開始位置)。クラスタの対応付け (FOR-15)、
/// 強調 (FOR-16)、削除項目 (FOR-17) はこれを通して使う。
/// </summary>
public sealed class FileSystemSession : IDisposable
{
    private static readonly ConditionalWeakTable<Document, FileSystemSession> Sessions = [];

    private ClusterOwnerMap? _owners;

    public FileSystemSession(Document document, IFileSystem fs, long volumeOffset, long volumeLength, PartitionAnalysis? partitions = null)
    {
        Document = document;
        FileSystem = fs;
        VolumeOffset = volumeOffset;
        VolumeLength = volumeLength;
        Partitions = partitions;
        Classifier = new FileSystemRegionClassifier(fs, volumeOffset, volumeLength, partitions, document.Length);
        Root = fs.Root;
        Virtual = new Dictionary<FsVirtualFolder, FsEntry>
        {
            [FsVirtualFolder.Metadata] = Folder("virtual:metadata", "$Metadata", FsVirtualFolder.Metadata),
            [FsVirtualFolder.Deleted] = Folder("virtual:deleted", "Deleted", FsVirtualFolder.Deleted),
            [FsVirtualFolder.Orphans] = Folder("virtual:orphans", "Orphans", FsVirtualFolder.Orphans),
            [FsVirtualFolder.Unallocated] = Folder("virtual:unallocated", "Unallocated", FsVirtualFolder.Unallocated),
            [FsVirtualFolder.VolumeSlack] = Folder("virtual:volumeSlack", "VolumeSlack", FsVirtualFolder.VolumeSlack),
        };
    }

    public Document Document { get; }
    public IFileSystem FileSystem { get; }
    public long VolumeOffset { get; }
    public long VolumeLength { get; }
    public PartitionAnalysis? Partitions { get; }
    public FileSystemRegionClassifier Classifier { get; }
    public FsEntry Root { get; }

    /// <summary>仮想フォルダ (ツリーのルートの下に並べる)。</summary>
    public IReadOnlyDictionary<FsVirtualFolder, FsEntry> Virtual { get; }

    /// <summary>逆引き表 (作るまで null)。</summary>
    public ClusterOwnerMap? Owners
    {
        get => _owners;
        set
        {
            if (!ReferenceEquals(_owners, value))
            {
                _owners?.Dispose();
                _owners = value;
            }
        }
    }

    /// <summary>ドキュメントに付けたファイルシステムの解析 (なければ null)。</summary>
    public static FileSystemSession? For(Document document) => Sessions.TryGetValue(document, out FileSystemSession? s) ? s : null;

    /// <summary>ドキュメントに付ける (null で外す)。入力式の <c>cluster</c> はこれのクラスタサイズになる (00 の 6.2)。</summary>
    public static void Attach(Document document, FileSystemSession? session)
    {
        if (Sessions.TryGetValue(document, out FileSystemSession? old) && !ReferenceEquals(old, session))
        {
            Sessions.Remove(document);
            old.Dispose();
        }

        if (session is not null)
        {
            Sessions.AddOrUpdate(document, session);
        }
    }

    /// <summary>入力式の <c>cluster</c> の値 (ファイルシステムを解析しているドキュメントのみ。00 の 6.2)。</summary>
    public static long? ClusterSizeFor(Document document) => For(document)?.FileSystem.ClusterSize;

    private static FsEntry Folder(string id, string name, FsVirtualFolder kind) => new()
    {
        Id = id,
        Name = name,
        Path = string.Empty,
        Flags = FsEntryFlags.Directory | FsEntryFlags.Virtual,
        Tag = kind,
    };

    /// <summary>仮想フォルダの種類 (仮想フォルダでなければ null)。</summary>
    public static FsVirtualFolder? VirtualKind(FsEntry entry) => entry.Flags.HasFlag(FsEntryFlags.Virtual) && entry.Tag is FsVirtualFolder k ? k : null;

    /// <summary>
    /// フォルダの中身 (1 件ずつ)。仮想フォルダは走査 (長時間処理) になるので、呼び出し側はバックグラウンドで読む。
    /// </summary>
    public IEnumerable<FsEntry> Children(FsEntry folder, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        switch (VirtualKind(folder))
        {
            case FsVirtualFolder.Metadata:
                return FileSystem.EnumerateMetadata(cancellationToken);
            case FsVirtualFolder.Deleted:
                return FileSystem.EnumerateDeleted(progress, cancellationToken);
            case FsVirtualFolder.Orphans:
                return FileSystem.EnumerateOrphans(progress, cancellationToken);
            case FsVirtualFolder.Unallocated:
                return UnallocatedEntries(cancellationToken);
            case FsVirtualFolder.VolumeSlack:
                return VolumeSlackEntries();
            default:
                return FileSystem.ListDirectory(folder, cancellationToken);
        }
    }

    private IEnumerable<FsEntry> UnallocatedEntries(CancellationToken token)
    {
        int n = 0;
        foreach (ExtractMapRow row in FileSystemExtraction.UnallocatedRuns(FileSystem, 0, token))
        {
            long first = FileSystem.ClusterAt(row.DiskOffset) ?? 0;
            long count = row.Length / FileSystem.ClusterSize;
            var extent = new FsExtent(0, row.Length, row.DiskOffset, first, count);
            yield return new FsEntry
            {
                Id = $"unalloc:{first}",
                Name = $"{first}–{first + count - 1}",
                Flags = FsEntryFlags.Metadata,
                Size = row.Length,
                AllocatedSize = row.Length,
                FileIdText = first.ToString(CultureInfo.InvariantCulture),
                FirstCluster = first,
                Tag = (IReadOnlyList<FsExtent>)[extent],
                FragmentCount = 1,
            };
            if (++n >= 1_000_000)
            {
                yield break;
            }
        }
    }

    private IEnumerable<FsEntry> VolumeSlackEntries()
    {
        long fsEnd = Math.Min(VolumeLength, FileSystem.FileSystemLength);
        if (VolumeLength > fsEnd)
        {
            var extent = new FsExtent(0, VolumeLength - fsEnd, fsEnd, -1, 0);
            yield return new FsEntry
            {
                Id = "volumeSlack",
                Name = "VolumeSlack",
                Flags = FsEntryFlags.Metadata,
                Size = VolumeLength - fsEnd,
                Tag = (IReadOnlyList<FsExtent>)[extent],
                FragmentCount = 1,
            };
        }
    }

    /// <summary>項目の断片 (ドキュメント上の位置。FOR-15 の仕様 4・FOR-09 の仕様 5)。</summary>
    public IReadOnlyList<(long Offset, long Length, FsExtent Extent)> DocumentFragments(FsEntry entry)
    {
        var list = new List<(long, long, FsExtent)>();
        foreach (FsExtent e in FileSystem.GetExtents(entry))
        {
            if (e.VolumeOffset is long v)
            {
                list.Add((VolumeOffset + v, e.Resident ? e.Length : e.DiskLength, e));
            }
        }

        return list;
    }

    /// <summary>クラスタの先頭のドキュメント上の位置 (範囲外なら null。「クラスタへ移動」)。</summary>
    public long? ClusterDocumentOffset(long cluster) => FileSystem.ClusterOffset(cluster) is long v ? VolumeOffset + v : null;

    /// <summary>ドキュメント上の位置のクラスタ番号とクラスタ内の位置 (クラスタの領域の外なら null)。</summary>
    public (long Cluster, long Within)? ClusterAtDocument(long offset)
    {
        if (offset < VolumeOffset || offset >= VolumeOffset + VolumeLength || FileSystem.ClusterAt(offset - VolumeOffset) is not long c
            || FileSystem.ClusterOffset(c) is not long start)
        {
            return null;
        }

        return (c, offset - VolumeOffset - start);
    }

    /// <summary>逆引き表を保存する名前 (ボリュームの位置ごと)。</summary>
    public string OwnerMapName(string key) => $"{key}.clustermap.{VolumeOffset:X}";

    public void Dispose()
    {
        _owners?.Dispose();
        _owners = null;
    }
}

/// <summary>断片のあいだの移動 (FOR-15 の仕様 5: 次の断片・前の断片)。</summary>
public static class FragmentNavigation
{
    /// <summary>カーソルより後ろの最初の断片の先頭 (なければ null)。</summary>
    public static long? Next(IReadOnlyList<(long Offset, long Length)> fragments, long cursor) =>
        fragments.Select(f => f.Offset).Where(o => o > cursor).Order().Cast<long?>().FirstOrDefault();

    /// <summary>カーソルを含む断片の前の断片の先頭 (カーソルが断片の途中ならその断片の先頭。なければ null)。</summary>
    public static long? Previous(IReadOnlyList<(long Offset, long Length)> fragments, long cursor) =>
        fragments.Select(f => f.Offset).Where(o => o < cursor).OrderDescending().Cast<long?>().FirstOrDefault();
}

/// <summary>ファイル一覧の絞り込み (FOR-09 の仕様 7: 名前の部分一致、ワイルドカード <c>*</c> <c>?</c>)。</summary>
public sealed class FsNameFilter
{
    private readonly Regex? _regex;
    private readonly string _text;

    public FsNameFilter(string? text)
    {
        _text = text?.Trim() ?? string.Empty;
        if (_text.IndexOfAny(['*', '?']) >= 0)
        {
            string pattern = "^" + Regex.Escape(_text).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
            _regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
    }

    public bool IsEmpty => _text.Length == 0;

    public bool Matches(string name)
    {
        if (_text.Length == 0)
        {
            return true;
        }

        if (_regex is not null)
        {
            try
            {
                return _regex.IsMatch(name);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return name.Contains(_text, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>一覧の書き出し (FOR-09 の仕様 6 の「一覧を CSV / JSON にエクスポート」)。</summary>
public static class FsListExport
{
    private static string Time(FsTimestamp t) => t.Utc?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture) ?? (t.Invalid ? "invalid" : string.Empty);

    public static string ToCsv(IEnumerable<FsEntry> entries)
    {
        var s = new StringBuilder("Name,Extension,Size,AllocatedSize,CreatedUtc,ModifiedUtc,AccessedUtc,ChangedUtc,Attributes,FileId,FirstCluster,Fragments,State,Recovery,Path\r\n");
        foreach (FsEntry e in entries)
        {
            s.Append(FileSystemExtraction.Csv(e.Name)).Append(',').Append(FileSystemExtraction.Csv(e.Extension)).Append(',')
                .Append(e.Size.ToString(CultureInfo.InvariantCulture)).Append(',').Append(e.AllocatedSize.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Time(e.Created)).Append(',').Append(Time(e.Modified)).Append(',').Append(Time(e.Accessed)).Append(',').Append(Time(e.Changed)).Append(',')
                .Append(e.Attributes).Append(',').Append(FileSystemExtraction.Csv(e.FileIdText)).Append(',')
                .Append(e.FirstCluster?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(e.FragmentCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',').Append(e.State).Append(',')
                .Append(e.Recovery == FsRecoveryStatus.None ? string.Empty : e.Recovery.ToString()).Append(',')
                .Append(FileSystemExtraction.Csv(e.Path.Length > 0 ? e.Path : e.OriginalPath)).Append("\r\n");
        }

        return s.ToString();
    }

    public static string ToJson(IEnumerable<FsEntry> entries) => JsonSerializer.Serialize(entries.Select(e => new
    {
        name = e.Name, extension = e.Extension, size = e.Size, allocatedSize = e.AllocatedSize, created = Time(e.Created), modified = Time(e.Modified),
        accessed = Time(e.Accessed), changed = Time(e.Changed), attributes = e.Attributes, fileId = e.FileIdText, firstCluster = e.FirstCluster,
        fragments = e.FragmentCount, state = e.State.ToString(), recovery = e.Recovery == FsRecoveryStatus.None ? null : e.Recovery.ToString(),
        path = e.Path.Length > 0 ? e.Path : e.OriginalPath,
    }), new JsonSerializerOptions { WriteIndented = true });
}
