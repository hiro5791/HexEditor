using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using HexEditor.Core.Statistics;

namespace HexEditor.Core.FileTypes;

/// <summary>判定の候補 1 つ (ANA-17 の仕様 3)。<see cref="Matches"/> は一致した条件の位置 (判定の基準からの相対位置)。</summary>
public sealed record FileTypeCandidate(string Name, string Mime, IReadOnlyList<string> Extensions, int Confidence,
    IReadOnlyList<(long Offset, int Length)> Matches, string Source, string? Id = null);

/// <summary>テキストとしての判定 (どの形式にも当たらないとき。ANA-17 の「エラー」)。</summary>
public enum TextKind
{
    Ascii,
    Utf8,
    Utf8Bom,
    Utf16LE,
    Utf16BE,
}

/// <summary>判定の結果。</summary>
public sealed record FileTypeReport(IReadOnlyList<FileTypeCandidate> Candidates, bool ExtensionMismatch, TextKind? Text)
{
    public FileTypeCandidate? Best => Candidates.Count > 0 ? Candidates[0] : null;
}

/// <summary>プラグインの判定 (08 の AUTO-31 の拡張ポイント)。結果は内蔵の項目と同じように候補の一覧に加える。</summary>
public interface IFileTypeDetector
{
    string Name { get; }

    IEnumerable<FileTypeCandidate> Detect(IMagicData data);
}

/// <summary>埋め込まれた形式の探索の結果 1 件 (ANA-17 の仕様 6)。オフセットはドキュメント上の位置。</summary>
public sealed record EmbeddedFormat(long Offset, string Name, string Mime, int Confidence);

/// <summary>スナップショットを判定のデータとして読む (<paramref name="start"/> を基準の位置にする)。</summary>
public sealed class SnapshotMagicData(DocumentSnapshot snapshot, long start, long limit = long.MaxValue) : IMagicData
{
    public long Length => Math.Max(0, snapshot.Length - start);

    public int Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= Length || offset >= limit)
        {
            return 0;
        }

        int n = (int)Math.Min(destination.Length, Math.Min(Length, limit) - offset);
        ReadResult r = snapshot.Read(start + offset, destination[..n]);
        return r.IsComplete ? r.BytesReturned : r.Unreadable[0].Offset > start + offset ? (int)(r.Unreadable[0].Offset - start - offset) : 0;
    }
}

/// <summary>先頭と末尾だけを読んだデータ (開いたときの自動判定。最大 128 KB しか読まない。ANA-17 の仕様 4)。</summary>
public sealed class HeadTailMagicData : IMagicData
{
    public const int Window = 64 * 1024;

    private readonly byte[] _head;
    private readonly byte[] _tail;
    private readonly long _tailStart;

    public HeadTailMagicData(DocumentSnapshot snapshot, long start = 0)
    {
        Length = Math.Max(0, snapshot.Length - start);
        _head = ReadSome(snapshot, start, (int)Math.Min(Window, Length));
        _tailStart = Math.Max(_head.Length, Length - Window);
        _tail = ReadSome(snapshot, start + _tailStart, (int)Math.Max(0, Length - _tailStart));
        BytesRead = _head.Length + _tail.Length;
    }

    public long Length { get; }

    /// <summary>判定のために読んだバイト数 (TC-ANA-17-03)。</summary>
    public long BytesRead { get; }

    public int Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= Length)
        {
            return 0;
        }

        if (offset < _head.Length)
        {
            int n = (int)Math.Min(destination.Length, _head.Length - offset);
            _head.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }

        if (offset >= _tailStart && offset - _tailStart < _tail.Length)
        {
            // 末尾に読めない部分があると、読んだ末尾は短い。
            int n = (int)Math.Min(destination.Length, _tail.Length - (offset - _tailStart));
            _tail.AsSpan((int)(offset - _tailStart), n).CopyTo(destination);
            return n;
        }

        return 0;
    }

    private static byte[] ReadSome(DocumentSnapshot snapshot, long offset, int length)
    {
        byte[] buffer = new byte[Math.Max(0, length)];
        if (length > 0)
        {
            ReadResult r = snapshot.Read(offset, buffer);
            if (!r.IsComplete)
            {
                // 読めない部分より前だけを使う。
                long first = r.Unreadable[0].Offset - offset;
                return buffer.AsSpan(0, (int)Math.Clamp(first, 0, length)).ToArray();
            }
        }

        return buffer;
    }
}

/// <summary>
/// ファイル形式の判定 (ANA-17)。データベースのすべての項目の条件を調べ、一致したものを確度の高い順に並べる。確度は一致した固定の
/// バイトの数から求める (多いほど高い)。同じ確度なら優先度の高い順。
/// </summary>
public sealed class FileTypeDetector(FileTypeDatabase database, IEnumerable<IFileTypeDetector>? plugins = null)
{
    private readonly IReadOnlyList<IFileTypeDetector> _plugins = plugins?.ToList() ?? [];
    private AhoCorasick? _automaton;
    private List<FileFormat>? _anchored;

    public FileTypeDatabase Database => database;

    /// <summary>判定する。<paramref name="extension"/> はドキュメントの拡張子 (「拡張子と内容が一致しません」の判定。ANA-17 の仕様 8)。</summary>
    public FileTypeReport Detect(IMagicData data, string? extension = null)
    {
        var candidates = new List<(FileTypeCandidate Candidate, int Priority)>();
        foreach (FileFormat format in database.Formats)
        {
            var matches = new List<(long, int)>();
            int score = 0;
            if (format.Match.Evaluate(data, matches, ref score))
            {
                candidates.Add((new FileTypeCandidate(format.Name, format.Mime, format.Extensions, ConfidenceOf(score, format.Priority), matches,
                    format.Source, format.Id), format.Priority));
            }
        }

        foreach (IFileTypeDetector plugin in _plugins)
        {
            try
            {
                candidates.AddRange(plugin.Detect(data).Select(c => (c with { Source = plugin.Name }, 50)));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // プラグインの失敗は判定全体を止めない。
            }
        }

        List<FileTypeCandidate> ordered = [.. candidates.OrderByDescending(c => c.Candidate.Confidence).ThenByDescending(c => c.Priority)
            .ThenBy(c => c.Candidate.Name, StringComparer.Ordinal).Select(c => c.Candidate)];
        TextKind? text = ordered.Count == 0 ? DetectText(data) : null;
        return new FileTypeReport(ordered, ExtensionMismatch(ordered, extension), text);
    }

    /// <summary>確度 (%): 一致した固定のバイトが多いほど高い (2 バイトで 50%、4 バイトで 75%、8 バイトで 94%)。優先度で少し補正する。</summary>
    internal static int ConfidenceOf(int score, int priority)
    {
        double c = 100 * (1 - Math.Pow(2, -score / 2.0));
        c += (priority - 50) / 10.0;
        return (int)Math.Clamp(Math.Round(c), 1, 99);
    }

    /// <summary>
    /// 拡張子と判定結果が食い違うか: 拡張子がデータベースのどれかの形式のもので、最も確度の高い候補の拡張子に含まれない場合。
    /// </summary>
    public bool ExtensionMismatch(IReadOnlyList<FileTypeCandidate> candidates, string? extension)
    {
        string ext = (extension ?? string.Empty).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0 || candidates.Count == 0 || GenericExtensions.Contains(ext))
        {
            return false;
        }

        FileTypeCandidate best = candidates[0];
        if (best.Extensions.Contains(ext))
        {
            return false;
        }

        // 同じ確度の候補の拡張子なら食い違いとしない (形式が近いもの)。
        if (candidates.Any(c => c.Confidence == best.Confidence && c.Extensions.Contains(ext)))
        {
            return false;
        }

        return database.Formats.Any(f => f.Extensions.Contains(ext));
    }

    /// <summary>中身の形式を表さない拡張子 (食い違いとしない)。</summary>
    private static readonly HashSet<string> GenericExtensions = ["bin", "dat", "data", "raw", "img", "tmp", "bak", "out", "dump", "dmp", "old", "orig"];

    /// <summary>テキストとして妥当か (BOM、または先頭 4 KB が UTF-8 / ASCII のテキスト)。</summary>
    public static TextKind? DetectText(IMagicData data)
    {
        Span<byte> head = stackalloc byte[4096];
        int n = data.Read(0, head);
        if (n == 0)
        {
            return null;
        }

        head = head[..n];
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return TextKind.Utf8Bom;
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return TextKind.Utf16LE;
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return TextKind.Utf16BE;
        }

        int control = 0;
        bool high = false;
        foreach (byte b in head)
        {
            if (b < 0x20 && !StatMath.IsWhitespace(b) && b != 0x0C)
            {
                control++;
            }

            high |= b >= 0x80;
        }

        if (control > 0)
        {
            return null;
        }

        if (!high)
        {
            return TextKind.Ascii;
        }

        // 末尾で切れた文字は許す。
        var decoder = new UTF8Encoding(false, true);
        for (int cut = 0; cut < 4 && cut < head.Length; cut++)
        {
            try
            {
                decoder.GetCharCount(head[..(head.Length - cut)]);
                return TextKind.Utf8;
            }
            catch (DecoderFallbackException)
            {
            }
        }

        return null;
    }

    // ---- 埋め込まれた形式を探す (ANA-17 の仕様 6) ----

    /// <summary>
    /// 対象範囲の全オフセットでシグネチャを探す。先頭の固定のバイト列が 4 バイト以上ある項目だけを使い、多数のパターンを
    /// Aho-Corasick 法で同時に探す。見つかった位置で、その項目の条件をすべて確かめる。
    /// </summary>
    public IReadOnlyList<EmbeddedFormat> FindEmbedded(DocumentSnapshot snapshot, IReadOnlyList<HashRange> ranges, LongRunningOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        CancellationToken token = operation?.CancellationToken ?? cancellationToken;
        BuildAutomaton();
        var logical = new LogicalRanges(HashEngine.Normalize(ranges, snapshot.Length));
        var results = new List<EmbeddedFormat>();
        var scanner = new RangeScanner(snapshot, logical, RangeScanner.DefaultChunkSize, token);
        operation?.SetTotal(logical.Length);
        int state = 0;
        var seen = new HashSet<(long, string)>();

        // 範囲の先頭は、すべての項目で判定する (先頭の固定のバイト列が短い形式 (PE の MZ など) も見つける)。
        foreach (HashRange range in logical.Ranges)
        {
            if (Detect(new SnapshotMagicData(snapshot, range.Offset, HeadTailMagicData.Window)).Best is { } best)
            {
                results.Add(new EmbeddedFormat(range.Offset, best.Name, best.Mime, best.Confidence));
            }
        }

        foreach (ScanChunk chunk in scanner.Read())
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> span = chunk.Span;
            for (int i = 0; i < span.Length; i++)
            {
                state = _automaton!.Next(state, span[i]);
                if (!_automaton.HasOutput(state))
                {
                    continue;
                }

                foreach (int pattern in _automaton.Outputs(state))
                {
                    FileFormat format = _anchored![pattern];
                    long start = logical.ToDocument(chunk.Logical + i - format.Anchor!.Length + 1);
                    var data = new SnapshotMagicData(snapshot, start, HeadTailMagicData.Window);
                    var matches = new List<(long, int)>();
                    int score = 0;
                    if (format.Match.Evaluate(data, matches, ref score) && seen.Add((start, format.Id)))
                    {
                        results.Add(new EmbeddedFormat(start, format.Name, format.Mime, ConfidenceOf(score, format.Priority)));
                    }
                }
            }

            operation?.Report(scanner.BytesRead);
        }

        // 同じ位置の候補は確度の高いものだけを残す。
        return [.. results.GroupBy(r => r.Offset).Select(g => g.OrderByDescending(r => r.Confidence).First()).OrderBy(r => r.Offset)];
    }

    private void BuildAutomaton()
    {
        if (_automaton is not null)
        {
            return;
        }

        _anchored = [.. database.Formats.Where(f => f.Anchor is not null)];
        _automaton = new AhoCorasick(_anchored.Select(f => f.Anchor!).ToList());
    }

    /// <summary>多数のバイト列を同時に探す (状態遷移を 256 通りの表で持つ)。</summary>
    private sealed class AhoCorasick
    {
        private readonly List<int[]> _next = [];
        private readonly List<List<int>> _outputs = [];

        public AhoCorasick(IReadOnlyList<byte[]> patterns)
        {
            NewState();
            var goTo = new List<Dictionary<byte, int>> { new() };
            for (int p = 0; p < patterns.Count; p++)
            {
                int s = 0;
                foreach (byte b in patterns[p])
                {
                    if (!goTo[s].TryGetValue(b, out int t))
                    {
                        t = NewState();
                        goTo.Add([]);
                        goTo[s][b] = t;
                    }

                    s = t;
                }

                _outputs[s].Add(p);
            }

            // 幅優先で失敗の遷移を作り、表を埋める。
            int[] fail = new int[_next.Count];
            var queue = new Queue<int>();
            for (int b = 0; b < 256; b++)
            {
                if (goTo[0].TryGetValue((byte)b, out int t))
                {
                    _next[0][b] = t;
                    fail[t] = 0;
                    queue.Enqueue(t);
                }
            }

            while (queue.Count > 0)
            {
                int s = queue.Dequeue();
                _outputs[s].AddRange(_outputs[fail[s]]);
                for (int b = 0; b < 256; b++)
                {
                    if (goTo[s].TryGetValue((byte)b, out int t))
                    {
                        fail[t] = _next[fail[s]][b];
                        _next[s][b] = t;
                        queue.Enqueue(t);
                    }
                    else
                    {
                        _next[s][b] = _next[fail[s]][b];
                    }
                }
            }
        }

        public int Next(int state, byte b) => _next[state][b];

        public bool HasOutput(int state) => _outputs[state].Count > 0;

        public IReadOnlyList<int> Outputs(int state) => _outputs[state];

        private int NewState()
        {
            _next.Add(new int[256]);
            _outputs.Add([]);
            return _next.Count - 1;
        }
    }
}
