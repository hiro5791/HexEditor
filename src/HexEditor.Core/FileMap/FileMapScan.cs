using HexEditor.Core.Engine;
using HexEditor.Core.FileTypes;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Sources;
using HexEditor.Core.Templates;

namespace HexEditor.Core.FileMap;

/// <summary>ゼロ・定数のバイトの連続 1 つ (ドキュメント上の位置)。</summary>
internal readonly record struct ConstantRun(long Start, long Length, byte Value)
{
    public long End => Start + Length;
}

/// <summary>
/// ゼロ・定数のバイトの 512 バイト以上の連続を、走査と同じ読み込みでバイト単位に求める (ANA-33 の仕様 2 の 2 の (d))。
/// 256 バイトごとの窓がすべて同じ値かを確かめ (乱数のデータでは最初の 2 バイトで終わる)、同じ値の窓から前後に広げる。読み込みの境目を
/// またぐ連続は、前の読み込みの末尾の同じ値のバイト数と、終わっていない連続でつなぐ。上限を超えた分は件数だけを数える。
/// </summary>
internal sealed class RunTracker
{
    public const int Window = 256;

    /// <summary>持つ連続の上限。</summary>
    public const int MaxRuns = 1 << 20;

    private readonly List<ConstantRun> _runs = [];
    private long _prevEnd = -1;
    private byte _tailValue;
    private long _tailCount;
    private long _openStart = -1;
    private byte _openValue;

    public IReadOnlyList<ConstantRun> Runs => _runs;

    public long Omitted { get; private set; }

    /// <summary>読めない範囲があった (連続を切る)。</summary>
    public void Break()
    {
        if (_openStart >= 0)
        {
            Close(_openStart, _prevEnd - _openStart, _openValue);
            _openStart = -1;
        }

        _prevEnd = -1;
        _tailCount = 0;
    }

    /// <summary>[<paramref name="offset"/>, +span.Length) を読んだ。</summary>
    public void Feed(long offset, ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
        {
            return;
        }

        bool continues = _prevEnd == offset;
        if (!continues)
        {
            Break();
        }

        int i = 0;
        if (_openStart >= 0)
        {
            int k = span.IndexOfAnyExcept(_openValue);
            if (k < 0)
            {
                _prevEnd = offset + span.Length;
                _tailValue = _openValue;
                _tailCount += span.Length;
                return;
            }

            Close(_openStart, offset + k - _openStart, _openValue);
            _openStart = -1;
            i = k;
        }

        int w = i;
        while (w + Window <= span.Length)
        {
            ReadOnlySpan<byte> window = span.Slice(w, Window);
            byte v = window[0];
            if (window.IndexOfAnyExcept(v) >= 0)
            {
                w += Window;
                continue;
            }

            // 同じ値の窓: 前に広げる (前の連続の終わり i より前には広げない)。
            int back = span[i..w].LastIndexOfAnyExcept(v);
            int b = back < 0 ? i : i + back + 1;
            long runStart = offset + b;
            if (b == 0 && continues && _tailValue == v)
            {
                runStart -= _tailCount;
            }

            // 後ろに広げる。
            int forward = span[w..].IndexOfAnyExcept(v);
            if (forward < 0)
            {
                _openStart = runStart;
                _openValue = v;
                _prevEnd = offset + span.Length;
                _tailValue = v;
                _tailCount = offset + span.Length - runStart;
                return;
            }

            int end = w + forward;
            Close(runStart, offset + end - runStart, v);
            i = end;
            w = end;
        }

        // 末尾の同じ値のバイト数 (次の読み込みにまたがる連続のため)。
        byte last = span[^1];
        int other = span.LastIndexOfAnyExcept(last);
        long count = span.Length - 1 - other;
        _tailCount = other < 0 && continues && _tailValue == last ? _tailCount + count : count;
        _tailValue = last;
        _prevEnd = offset + span.Length;
    }

    /// <summary>走査の終わり (終わっていない連続を閉じる)。</summary>
    public void Finish() => Break();

    private void Close(long start, long length, byte value)
    {
        if (length < FileMapBuilder.MinConstantRun)
        {
            return;
        }

        if (_runs.Count >= MaxRuns)
        {
            Omitted++;
            return;
        }

        _runs.Add(new ConstantRun(start, length, value));
    }

    /// <summary><paramref name="offset"/> 以降で終わる最初の連続の添字 (End &gt; offset)。</summary>
    public int FirstEndingAfter(long offset)
    {
        int lo = 0;
        int hi = _runs.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_runs[mid].End <= offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}

/// <summary>埋め込まれた形式の候補 1 つ (走査で見つけた位置。ANA-33 の仕様 2 の 2 の (b))。</summary>
internal sealed class EmbeddedCandidate(long offset)
{
    public long Offset { get; } = offset;

    /// <summary>シグネチャデータベースの項目 (一致したもの。なければ null)。</summary>
    public FileFormat? Format { get; set; }

    public int FormatConfidence { get; set; }

    /// <summary>ヘッダが一致したカービングの定義 (なければ null)。</summary>
    public CarvingSignature? Carving { get; set; }
}

/// <summary>
/// 埋め込まれた形式のシグネチャを探す (ANA-33 の仕様 2 の 2 の (b))。ANA-17 の 6 と同じ条件 (先頭の固定のバイト列が 4 バイト以上の項目) の
/// シグネチャデータベースの項目と、カービングの組み込みの定義 (FOR-18 の 3) のヘッダ (同じく固定の先頭が 4 バイト以上のもの) を、1 つの
/// Aho-Corasick の表で同時に探す。見つかった位置で、項目の条件 (データベース) とヘッダの全体 (カービング) を確かめる。対象の先頭は先頭の
/// 形式 (構造の解析の (a)) なので候補にしない。
/// </summary>
internal sealed class EmbeddedScanner
{
    private readonly DocumentSnapshot _snapshot;
    private readonly long _targetStart;
    private readonly CancellationToken _token;
    private readonly FileTypeDetector.AhoCorasick? _automaton;
    private readonly List<(FileFormat? Format, CarvingSignature? Carving, HexMaskPattern? Header, int AnchorLength)> _patterns = [];
    private readonly Dictionary<long, EmbeddedCandidate> _candidates = [];
    private int _state;
    private long _prevEnd = -1;

    public EmbeddedScanner(DocumentSnapshot snapshot, FileTypeDatabase database, IReadOnlyList<CarvingSignature> carving, long targetStart, CancellationToken token)
    {
        _snapshot = snapshot;
        _targetStart = targetStart;
        _token = token;
        var anchors = new List<byte[]>();
        foreach (FileFormat format in new FileTypeDetector(database).AnchoredFormats)
        {
            anchors.Add(format.Anchor!);
            _patterns.Add((format, null, null, format.Anchor!.Length));
        }

        foreach (CarvingSignature sig in carving.Where(s => s.Enabled))
        {
            foreach (HexMaskPattern header in sig.ParsedHeaders())
            {
                int prefix = 0;
                while (prefix < header.Length && header.Mask[prefix] == 0xFF)
                {
                    prefix++;
                }

                if (prefix >= FileTypeDatabase.MinAnchor)
                {
                    anchors.Add(header.Value[..prefix]);
                    _patterns.Add((null, sig, header, prefix));
                }
            }
        }

        _automaton = anchors.Count > 0 ? new FileTypeDetector.AhoCorasick(anchors) : null;
    }

    /// <summary>上限を超えて持たなかった候補の数。</summary>
    public long Omitted { get; private set; }

    public IReadOnlyCollection<EmbeddedCandidate> Candidates => _candidates.Values;

    public void Break()
    {
        _state = 0;
        _prevEnd = -1;
    }

    public void Feed(long offset, ReadOnlySpan<byte> span)
    {
        if (_automaton is null)
        {
            return;
        }

        if (_prevEnd != offset)
        {
            _state = 0;
        }

        _state = _automaton.Feed(_state, span, (i, state) =>
        {
            foreach (int p in _automaton.Outputs(state))
            {
                (FileFormat? format, CarvingSignature? sig, HexMaskPattern? header, int anchor) = _patterns[p];
                long headerAt = offset + i - anchor + 1;
                long fileStart = headerAt - (sig?.HeaderOffset ?? 0);
                if (fileStart <= _targetStart)
                {
                    continue;
                }

                Verify(fileStart, headerAt, format, sig, header);
            }
        });
        _prevEnd = offset + span.Length;
    }

    private void Verify(long fileStart, long headerAt, FileFormat? format, CarvingSignature? sig, HexMaskPattern? header)
    {
        _token.ThrowIfCancellationRequested();
        int confidence = 0;
        if (format is not null)
        {
            var data = new SnapshotMagicData(_snapshot, fileStart, HeadTailMagicData.Window);
            var matches = new List<(long, int)>();
            int score = 0;
            if (!format.Match.Evaluate(data, matches, ref score))
            {
                return;
            }

            confidence = FileTypeDetector.ConfidenceOf(score, format.Priority);
        }
        else if (header is not null)
        {
            Span<byte> bytes = stackalloc byte[Math.Min(header.Length, 256)];
            if (header.Length > bytes.Length || _snapshot.Read(headerAt, bytes) is not { IsComplete: true, BytesReturned: var n } || n < header.Length
                || !header.MatchesAt(bytes))
            {
                return;
            }
        }

        if (!_candidates.TryGetValue(fileStart, out EmbeddedCandidate? c))
        {
            if (_candidates.Count >= FileMapBuilder.MaxCandidates)
            {
                Omitted++;
                return;
            }

            c = new EmbeddedCandidate(fileStart);
            _candidates[fileStart] = c;
        }

        if (format is not null && (c.Format is null || confidence > c.FormatConfidence))
        {
            c.Format = format;
            c.FormatConfidence = confidence;
        }

        c.Carving ??= sig;
    }
}

/// <summary>表示どおりのデータ (変換ビューを含む) をカービングの検証で読む (<see cref="SnapshotCarveData"/> は生のデータを読む)。</summary>
internal sealed class ViewCarveData(DocumentSnapshot snapshot) : ICarveData
{
    public long Length => snapshot.Length;

    public ReadResult Read(long offset, Span<byte> destination) => snapshot.Read(offset, destination);
}

/// <summary>ドキュメントの一部 [offset, offset + length) を、先頭を 0 とするテンプレートのデータにする (埋め込まれたファイルの評価)。</summary>
internal sealed class SubViewTemplateData(DocumentSnapshot snapshot, long offset, long length) : TemplateDataSource
{
    public override long Length => length;

    public override int Read(long at, Span<byte> destination, out long unreadableAt)
    {
        unreadableAt = -1;
        if (at < 0 || at >= length)
        {
            return 0;
        }

        int n = (int)Math.Min(destination.Length, length - at);
        ReadResult r = snapshot.ReadThroughCache(offset + at, destination[..n]);
        if (!r.IsComplete)
        {
            unreadableAt = r.Unreadable.Count > 0 ? r.Unreadable.Min(u => u.Offset) - offset : at + r.BytesReturned;
        }

        return r.BytesReturned;
    }
}
