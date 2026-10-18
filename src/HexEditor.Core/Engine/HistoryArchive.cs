using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using HexEditor.Core.Patches;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Engine;

/// <summary>履歴ファイルの破損 (EDIT-21 の「エラー」: 「履歴ファイルを読み込めません: 理由」)。</summary>
public sealed class HistoryArchiveException(string reason) : IOException(reason)
{
    public string Reason { get; } = reason;
}

/// <summary>読み込む前に見る履歴ファイルの情報 (識別情報の確認。EDIT-21 の仕様 9)。</summary>
public sealed record HistoryArchiveInfo(SourceIdentity Identity, int NodeCount, long DataLength);

/// <summary>
/// 分岐する履歴の保存と読み込み (EDIT-21 の仕様 8・9)。<c>.hexhist</c> は zip 形式で、節の構造を表す JSON (<c>history.json</c>) と、
/// 各節の編集内容が参照するデータ (<c>data.bin</c>) を入れる。元ファイルの識別情報 (パス、長さ、更新日時、先頭・中央・末尾の各 1 MiB の
/// SHA-256、任意で全体の SHA-256) を含める。
/// <para>
/// 各節は親からの差分 (編集の範囲と、その範囲の新しいピース) で持つ。根は全体のピース。今の元ファイルのデータは位置の参照で、それ以外
/// (追加データ・別のドキュメントの範囲・保存より前の版の元データ) は data.bin に書き出す。ファイルの大きさは追加データの量に比例し、
/// 元ファイルの大きさには比例しない (保存をまたいだ履歴を除く)。
/// </para>
/// </summary>
public static class HistoryArchive
{
    public const string Extension = ".hexhist";

    private const string JsonEntry = "history.json";
    private const string DataEntry = "data.bin";
    private const string FormatName = "hexhist";
    private const int Version = 1;

    /// <summary>書き出す推定サイズ (保存前に表示する。EDIT-21 の「巨大ファイル」)。</summary>
    public static long EstimateSize(Document document)
    {
        long size = 1024;
        foreach (HistoryNode node in document.History.AllNodes())
        {
            size += 256;
            foreach ((_, Piece p, bool reference) in Pieces(document, node))
            {
                if (!reference && p.Kind is not (PieceKind.Pattern or PieceKind.Random))
                {
                    size += p.Length;
                }
                else if (p.Kind == PieceKind.Pattern)
                {
                    size += p.PatternLength;
                }
            }
        }

        return size;
    }

    /// <summary>節が持つピース (根は全体、それ以外は親からの差分の範囲)。reference は今の元ファイルを位置で指すか。</summary>
    private static IEnumerable<(DocumentSnapshot Snapshot, Piece Piece, bool Reference)> Pieces(Document document, HistoryNode node)
    {
        DocumentSnapshot snapshot = node.Entry.Snapshot;
        bool latest = ReferenceEquals(snapshot.Storage, document.LatestStorage);
        IEnumerable<(long, Piece)> pieces = node.Parent is null || node.Entry.Range is not { } r
            ? snapshot.Tree.EnumerateAll()
            : snapshot.Tree.Enumerate(r.Offset, r.AfterLength);
        foreach ((_, Piece p) in pieces)
        {
            yield return (snapshot, p, latest && p.Kind == PieceKind.Original);
        }
    }

    /// <summary>
    /// 履歴を書き出す。<paramref name="fullHash"/> なら元ファイルの全体の SHA-256 も入れる (巨大ファイルでは時間がかかるため既定オフ)。
    /// <paramref name="progress"/> には書いたデータのバイト数を知らせる。
    /// </summary>
    public static void Save(Document document, Stream output, bool fullHash = false, Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (document.LinkedParent is not null)
        {
            throw new InvalidOperationException("連動ビューの履歴は親のドキュメントで保存します。");
        }

        EditHistory history = document.History;
        SourceIdentity identity = IdentityOf(document, fullHash, cancellationToken);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        // data.bin を先に書き、各ピースの data.bin 上の位置を決める。
        var nodePieces = new Dictionary<HistoryNode, List<JsonPiece>>();
        long dataLength = 0;
        using (Stream data = zip.CreateEntry(DataEntry, CompressionLevel.Fastest).Open())
        {
            byte[] buffer = new byte[1 << 20];
            foreach (HistoryNode node in history.AllNodes())
            {
                var list = new List<JsonPiece>();
                foreach ((DocumentSnapshot snapshot, Piece p, bool reference) in Pieces(document, node))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reference)
                    {
                        list.Add(new JsonPiece('o', p.Offset, p.Length, 0, 0, 0));
                        continue;
                    }

                    if (p.Kind == PieceKind.Random)
                    {
                        list.Add(new JsonPiece('r', p.Offset, p.Length, 0, 0, p.Seed));
                        continue;
                    }

                    if (p.Kind == PieceKind.Pattern)
                    {
                        byte[] pattern = new byte[p.PatternLength];
                        snapshot.Storage.AddBuffer.Read(p.Offset, pattern);
                        data.Write(pattern);
                        list.Add(new JsonPiece('p', dataLength, p.Length, p.PatternLength, p.Phase, 0));
                        dataLength += pattern.Length;
                        continue;
                    }

                    // 追加データ・別のドキュメントの範囲・保存より前の版の元データ: 内容を書き出す。
                    var single = new DocumentSnapshot(snapshot.Storage, PieceTree.FromPiece(p));
                    for (long done = 0; done < p.Length;)
                    {
                        int n = (int)Math.Min(buffer.Length, p.Length - done);
                        ReadResult r = single.Read(done, buffer.AsSpan(0, n));
                        if (!r.IsComplete)
                        {
                            throw new IOException($"履歴のデータを読み込めません (0x{r.Unreadable[0].Offset:X})。");
                        }

                        data.Write(buffer, 0, n);
                        done += n;
                    }

                    list.Add(new JsonPiece('d', dataLength, p.Length, 0, 0, 0));
                    dataLength += p.Length;
                    progress?.Invoke(dataLength);
                }

                nodePieces[node] = list;
            }
        }

        using Stream jsonStream = zip.CreateEntry(JsonEntry, CompressionLevel.Optimal).Open();
        using var json = new Utf8JsonWriter(jsonStream);
        json.WriteStartObject();
        json.WriteString("format", FormatName);
        json.WriteNumber("version", Version);
        json.WritePropertyName("identity");
        identity.WriteTo(json);
        json.WriteNumber("current", history.CurrentNode.Id);

        // 保存した時点は、今の元ファイルの内容 (最新の元データの版) のときだけ記録する。
        if (history.SavedNode is { } saved && ReferenceEquals(saved.Entry.Snapshot.Storage, document.LatestStorage))
        {
            json.WriteNumber("saved", saved.Id);
        }

        json.WriteNumber("dataLength", dataLength);
        json.WriteStartArray("nodes");
        foreach (HistoryNode node in history.AllNodes())
        {
            HistoryEntry e = node.Entry;
            json.WriteStartObject();
            json.WriteNumber("id", node.Id);
            if (node.Parent is { } parent)
            {
                json.WriteNumber("parent", parent.Id);
            }

            json.WriteString("description", e.Description);
            json.WriteString("time", e.Time.ToString("O", CultureInfo.InvariantCulture));
            if (node.Parent is not null && e.Range is { } range)
            {
                json.WriteStartArray("range");
                json.WriteNumberValue(range.Offset);
                json.WriteNumberValue(range.BeforeLength);
                json.WriteNumberValue(range.AfterLength);
                json.WriteEndArray();
            }

            if (node.Name is not null)
            {
                json.WriteString("name", node.Name);
            }

            if (node.IsPinned)
            {
                json.WriteBoolean("pinned", true);
            }

            if (node.LastChild is { } last)
            {
                json.WriteNumber("lastChild", last.Id);
            }

            json.WriteStartArray("pieces");
            foreach (JsonPiece p in nodePieces[node])
            {
                json.WriteStartArray();
                json.WriteStringValue(p.Kind.ToString());
                json.WriteNumberValue(p.Offset);
                json.WriteNumberValue(p.Length);
                if (p.Kind == 'p')
                {
                    json.WriteNumberValue(p.PatternLength);
                    json.WriteNumberValue(p.Phase);
                }
                else if (p.Kind == 'r')
                {
                    json.WriteStringValue(p.Seed.ToString(CultureInfo.InvariantCulture));
                }

                json.WriteEndArray();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>ドキュメントの今の元ファイルの識別情報。</summary>
    public static SourceIdentity IdentityOf(Document document, bool full = false, CancellationToken cancellationToken = default)
    {
        IByteSource source = document.Source;
        SourceIdentity identity = SourceIdentity.Compute((offset, destination) =>
        {
            int n = (int)Math.Min(destination.Length, Math.Max(0, source.Length - offset));
            source.Read(offset, destination[..n]);
            return n;
        }, source.Length, full, cancellationToken: cancellationToken);
        string? path = (source as FileByteSource)?.Path;
        return identity with
        {
            Path = path,
            LastWriteTime = path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null,
        };
    }

    /// <summary>履歴ファイルの情報を読む (読み込む前の識別情報の確認)。</summary>
    public static HistoryArchiveInfo ReadInfo(Stream input)
    {
        using var zip = OpenZip(input);
        using JsonDocument doc = ReadJson(zip);
        JsonElement root = doc.RootElement;
        try
        {
            return new HistoryArchiveInfo(SourceIdentity.ReadFrom(root.GetProperty("identity")), root.GetProperty("nodes").GetArrayLength(),
                root.GetProperty("dataLength").GetInt64());
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new HistoryArchiveException("identity");
        }
    }

    /// <summary>
    /// 今の元ファイルが履歴ファイルの元ファイルと同じか (長さと部分的な SHA-256。全体の SHA-256 があればそれも。EDIT-21 の仕様 9)。
    /// </summary>
    public static bool Matches(Document document, HistoryArchiveInfo info) =>
        IdentityOf(document, info.Identity.FullSha256 is not null).Matches(info.Identity);

    /// <summary>
    /// 履歴を読み込み、ドキュメントの履歴を置き換える。読み込んだ後は、保存時に現在だった節に移る (EDIT-21 の仕様 9)。識別情報の確認は
    /// 呼び出し側 (<see cref="Matches"/>) で行う。一致しない場合も読み込めるが、保存した時点は記録しない (すべて「変更あり」)。
    /// </summary>
    public static void Load(Document document, Stream input, Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using PreparedHistory prepared = Prepare(document, input, progress, cancellationToken);
        prepared.ApplyTo(document);
    }

    /// <summary>
    /// 読み込みの準備 (data.bin の取り出しと JSON の読み込み。追加データの量に比例するので、UI はバックグラウンドで行う)。ドキュメントは変えない。
    /// 履歴の置き換えは <see cref="PreparedHistory.ApplyTo"/> (UI スレッド)。
    /// </summary>
    public static PreparedHistory Prepare(Document document, Stream input, Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (document.LinkedParent is not null)
        {
            throw new InvalidOperationException("連動ビューには履歴を読み込めません。");
        }

        using var zip = OpenZip(input);
        JsonDocument doc = ReadJson(zip);
        try
        {
            JsonElement root = doc.RootElement;
            bool matches;
            long dataLength;
            try
            {
                matches = IdentityOf(document).Matches(SourceIdentity.ReadFrom(root.GetProperty("identity")) with { FullSha256 = null });
                dataLength = root.GetProperty("dataLength").GetInt64();
            }
            catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                throw new HistoryArchiveException("identity");
            }

            // data.bin を一時ファイルに取り出す (追加データの量に比例する)。ドキュメントを閉じるときに消す。
            ZipArchiveEntry dataEntry = GetEntry(zip, DataEntry) ?? throw new HistoryArchiveException(DataEntry);
            if (dataLength < 0 || dataEntry.Length != dataLength)
            {
                throw new HistoryArchiveException(DataEntry);
            }

            string tempPath = Path.Combine(document.Options.TempDirectory, document.Id.ToString("N"), "history-" + Guid.NewGuid().ToString("N") + ".bin");
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            SafeFileHandle handle = File.OpenHandle(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);
            try
            {
                try
                {
                    using Stream data = dataEntry.Open();
                    byte[] buffer = new byte[1 << 20];
                    long done = 0;
                    int n;
                    while ((n = data.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // 宣言した長さを超えて展開しない (zip の中の長さが偽りでも、一時ファイルを際限なく大きくしない)。
                        if (n > dataLength - done)
                        {
                            throw new HistoryArchiveException(DataEntry);
                        }

                        RandomAccess.Write(handle, buffer.AsSpan(0, n), done);
                        done += n;
                        progress?.Invoke(done);
                    }

                    if (done != dataLength)
                    {
                        throw new HistoryArchiveException(DataEntry);
                    }
                }
                catch (InvalidDataException ex)
                {
                    throw new HistoryArchiveException(ex.Message);
                }

                return new PreparedHistory(doc, new TempFileSource(handle, dataLength, []), matches);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    /// <summary>読み込みの準備ができた履歴 (<see cref="Prepare"/>)。<see cref="ApplyTo"/> しなければ、Dispose で一時ファイルを消す。</summary>
    public sealed class PreparedHistory : IDisposable
    {
        private readonly JsonDocument _json;
        private TempFileSource? _data;

        internal PreparedHistory(JsonDocument json, TempFileSource data, bool matches)
        {
            _json = json;
            _data = data;
            IdentityMatches = matches;
        }

        /// <summary>今の元ファイルが履歴の元ファイルと一致したか (一致しなければ保存した時点を記録しない)。</summary>
        public bool IdentityMatches { get; }

        /// <summary>ドキュメントの履歴を置き換える (UI スレッド)。壊れていれば <see cref="HistoryArchiveException"/> で、ドキュメントは変えない。</summary>
        public void ApplyTo(Document document)
        {
            TempFileSource data = _data ?? throw new InvalidOperationException("すでに読み込みました。");
            try
            {
                Build(document, _json.RootElement, data, IdentityMatches);
                _data = null;
            }
            catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException
                or IndexOutOfRangeException)
            {
                throw new HistoryArchiveException(ex.Message);
            }
        }

        public void Dispose()
        {
            _json.Dispose();
            _data?.Dispose();
            _data = null;
        }
    }

    private static void Build(Document document, JsonElement root, TempFileSource data, bool matches)
    {
        // 外部参照の番号は次に加える番号 (木を作り終えてから加える。途中で壊れていればドキュメントを変えない)。
        int dataIndex = document.NextExternalIndex;
        var nodes = new Dictionary<int, HistoryNode>();
        var lastChild = new Dictionary<int, int>();
        HistoryNode? rootNode = null;
        long sourceLength = document.Source.Length;
        foreach (JsonElement e in root.GetProperty("nodes").EnumerateArray())
        {
            int id = e.GetProperty("id").GetInt32();
            HistoryNode? parent = e.TryGetProperty("parent", out JsonElement p) ? nodes.GetValueOrDefault(p.GetInt32()) ?? throw new FormatException($"parent {id}") : null;
            if (parent is null && rootNode is not null)
            {
                throw new FormatException("root");
            }

            var pieces = new List<Piece>();
            foreach (JsonElement pe in e.GetProperty("pieces").EnumerateArray())
            {
                string kind = pe[0].GetString()!;
                long offset = pe[1].GetInt64(), length = pe[2].GetInt64();
                if (length <= 0)
                {
                    throw new FormatException("piece length");
                }

                // 範囲は引き算で比べる (位置と長さの和が long をあふれても範囲内に見えないように)。
                pieces.Add(kind switch
                {
                    "o" when offset >= 0 && offset <= sourceLength && length <= sourceLength - offset => Piece.Original(offset, length),
                    "o" => throw new FormatException("元ファイルの範囲外を指しています。"),
                    "d" when offset >= 0 && offset <= data.Length && length <= data.Length - offset => Piece.External(dataIndex, offset, length),
                    "p" => document.LoadPatternPiece(data, offset, pe[3].GetInt32(), length, pe[4].GetInt64()),
                    "r" when offset >= 0 && length <= long.MaxValue - offset => Piece.Random(ulong.Parse(pe[3].GetString()!, CultureInfo.InvariantCulture), offset, length),
                    _ => throw new FormatException("piece"),
                });
            }

            PieceTree content = PieceTree.Build(pieces);
            PieceTree tree;
            EditRange? range = null;
            if (parent is null)
            {
                tree = content;
            }
            else if (e.TryGetProperty("range", out JsonElement r))
            {
                range = new EditRange(r[0].GetInt64(), r[1].GetInt64(), r[2].GetInt64());
                if (content.Length != range.Value.AfterLength)
                {
                    throw new FormatException($"range {id}");
                }

                tree = parent.Entry.Snapshot.Tree.Replace(range.Value.Offset, range.Value.BeforeLength, content);
            }
            else
            {
                tree = content;
            }

            var entry = new HistoryEntry(new DocumentSnapshot(document.LatestStorage, tree), e.GetProperty("description").GetString() ?? string.Empty, null)
            {
                Range = range,
                Time = DateTimeOffset.TryParse(e.GetProperty("time").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset t) ? t : default,
            };
            string? name = e.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
            bool pinned = e.TryGetProperty("pinned", out JsonElement pin) && pin.GetBoolean();
            HistoryNode node = EditHistory.CreateLoadedNode(id, entry, parent, name is { Length: > 0 and <= EditHistory.MaxNameLength } ? name : null, pinned);
            nodes.Add(id, node);
            rootNode ??= node;
            if (e.TryGetProperty("lastChild", out JsonElement lc))
            {
                lastChild[id] = lc.GetInt32();
            }
        }

        if (rootNode is null)
        {
            throw new FormatException("nodes");
        }

        foreach ((int id, int child) in lastChild)
        {
            if (nodes.TryGetValue(child, out HistoryNode? c) && ReferenceEquals(c.Parent, nodes[id]))
            {
                nodes[id].LastChild = c;
            }
        }

        HistoryNode current = nodes.GetValueOrDefault(root.GetProperty("current").GetInt32()) ?? throw new FormatException("current");
        HistoryNode? saved = matches && root.TryGetProperty("saved", out JsonElement s) ? nodes.GetValueOrDefault(s.GetInt32()) : null;
        if (document.AddOwnedExternal(data) != dataIndex)
        {
            throw new InvalidOperationException("外部参照の番号が変わりました。");
        }

        document.ReplaceHistory(rootNode, current, saved);
    }

    /// <summary>history.json の大きさの上限 (節の数に比例する。圧縮爆弾で際限なくメモリを使わない)。</summary>
    private const long MaxJsonBytes = 1L << 30;

    private static ZipArchive OpenZip(Stream input)
    {
        try
        {
            return new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new HistoryArchiveException(ex.Message);
        }
    }

    /// <summary>項目を探す (中央ディレクトリが壊れていれば、ここで初めて読むので誤りになる)。</summary>
    private static ZipArchiveEntry? GetEntry(ZipArchive zip, string name)
    {
        try
        {
            return zip.GetEntry(name);
        }
        catch (InvalidDataException ex)
        {
            throw new HistoryArchiveException(ex.Message);
        }
    }

    private static JsonDocument ReadJson(ZipArchive zip)
    {
        ZipArchiveEntry entry = GetEntry(zip, JsonEntry) ?? throw new HistoryArchiveException(JsonEntry);
        if (entry.Length > MaxJsonBytes)
        {
            throw new HistoryArchiveException(JsonEntry);
        }

        JsonDocument doc;
        try
        {
            using Stream s = new LimitedReadStream(entry.Open(), MaxJsonBytes);
            doc = JsonDocument.Parse(s);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new HistoryArchiveException(ex.Message);
        }

        bool ok;
        try
        {
            ok = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("format", out JsonElement f)
                && f.ValueKind == JsonValueKind.String && f.GetString() == FormatName
                && doc.RootElement.TryGetProperty("version", out JsonElement v) && v.TryGetInt32(out int version) && version <= Version;
        }
        catch (InvalidOperationException)
        {
            ok = false;
        }

        if (!ok)
        {
            doc.Dispose();
            throw new HistoryArchiveException("format");
        }

        return doc;
    }

    private readonly record struct JsonPiece(char Kind, long Offset, long Length, int PatternLength, long Phase, ulong Seed);
}
