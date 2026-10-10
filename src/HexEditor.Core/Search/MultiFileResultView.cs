namespace HexEditor.Core.Search;

/// <summary>
/// 複数ファイル検索の結果一覧の行の並び (FIND-30 の仕様 5: ファイルの行の下に一致の行) と、置換モードのチェック (FIND-31 の仕様 2)。
/// 行ごとのオブジェクトは作らず、行の番号から (ファイル, 一致の番号) を求める (100 万件以上でも、メモリは見えている行の分だけ)。
/// チェックはファイルごとの既定 (オン / オフ) と、それと違う一致の番号の集合で持つ。<see cref="Refresh"/> を呼ぶまで行の並びは変わらない
/// (UI のスレッドから呼ぶ)。
/// </summary>
public sealed class MultiFileResultView
{
    private readonly MultiFileSearchResults _results;
    private readonly List<FileSearchResult> _files = [];
    private readonly List<int> _counts = [];
    private readonly List<long> _starts = [];
    private readonly List<FileChecks> _checks = [];

    public MultiFileResultView(MultiFileSearchResults results) => _results = results;

    public MultiFileSearchResults Results => _results;

    /// <summary>行の数 (ファイルの行と一致の行)。</summary>
    public long RowCount { get; private set; }

    /// <summary>一覧にあるファイルの数。</summary>
    public int FileCount => _files.Count;

    public FileSearchResult File(int fileIndex) => _files[fileIndex];

    /// <summary>一覧にある、ファイルの一致の件数 (<see cref="Refresh"/> の時点)。</summary>
    public int MatchCount(int fileIndex) => _counts[fileIndex];

    /// <summary>結果に加わったファイル・一致を行に入れる。行の並びが変わったら true。</summary>
    public bool Refresh()
    {
        int old = _files.Count;
        int n = _results.FileCount;
        for (int i = old; i < n; i++)
        {
            _files.Add(_results.FileAt(i));
            _counts.Add(0);
            _starts.Add(0);
            _checks.Add(new FileChecks());
        }

        // 一致の件数が増えたファイル (「続ける」) があれば、そこから後ろの行の開始を数え直す。
        int from = old;
        for (int i = 0; i < old; i++)
        {
            if (_files[i].MatchCount != _counts[i])
            {
                from = i;
                break;
            }
        }

        if (from >= _files.Count)
        {
            return false;
        }

        long row = from == 0 ? 0 : _starts[from - 1] + 1 + _counts[from - 1];
        for (int i = from; i < _files.Count; i++)
        {
            int count = _files[i].MatchCount;
            _counts[i] = count;
            _starts[i] = row;
            row += 1 + count;
        }

        RowCount = row;
        return true;
    }

    /// <summary>行 <paramref name="row"/> のファイルの番号と一致の番号 (ファイルの行なら −1)。</summary>
    public (int File, int Match) RowAt(long row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, RowCount);
        int lo = 0;
        int hi = _files.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (_starts[mid] <= row)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return (lo, (int)(row - _starts[lo]) - 1);
    }

    /// <summary>ファイルの行 (<paramref name="match"/> が −1)・一致の行の番号。</summary>
    public long RowOf(int fileIndex, int match) => _starts[fileIndex] + 1 + match;

    /// <summary>チェック (ファイルの行は一致の行のまとめ。一部だけなら null)。</summary>
    public bool? IsChecked(int fileIndex, int match)
    {
        FileChecks c = _checks[fileIndex];
        if (match >= 0)
        {
            return c.Base != (c.Flipped?.Contains(match) ?? false);
        }

        int count = _counts[fileIndex];
        int on = CheckedCount(fileIndex);
        return on == count ? true : on == 0 ? false : null;
    }

    /// <summary>チェックした一致の件数。</summary>
    public int CheckedCount(int fileIndex)
    {
        FileChecks c = _checks[fileIndex];
        int flipped = c.Flipped?.Count(m => m < _counts[fileIndex]) ?? 0;
        return c.Base ? _counts[fileIndex] - flipped : flipped;
    }

    /// <summary>チェックを切り替える (ファイルの行なら、その一致をまとめて)。</summary>
    public void SetChecked(int fileIndex, int match, bool value)
    {
        FileChecks c = _checks[fileIndex];
        if (match < 0)
        {
            c.Base = value;
            c.Flipped = null;
            return;
        }

        if (value == c.Base)
        {
            c.Flipped?.Remove(match);
        }
        else
        {
            (c.Flipped ??= []).Add(match);
        }
    }

    /// <summary>チェックした一致があるか。</summary>
    public bool AnyChecked
    {
        get
        {
            for (int i = 0; i < _files.Count; i++)
            {
                if (CheckedCount(i) > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>ファイルの、チェックした一致 (開始の昇順)。</summary>
    public IReadOnlyList<SearchMatch> CheckedMatches(int fileIndex)
    {
        FileChecks c = _checks[fileIndex];
        IReadOnlyList<SearchMatch> all = _files[fileIndex].GetMatches(0, _counts[fileIndex]);
        if (c.Flipped is not { Count: > 0 } flipped)
        {
            return c.Base ? all : [];
        }

        var result = new List<SearchMatch>(c.Base ? all.Count - flipped.Count : flipped.Count);
        for (int i = 0; i < all.Count; i++)
        {
            if (c.Base != flipped.Contains(i))
            {
                result.Add(all[i]);
            }
        }

        return result;
    }

    /// <summary>ファイルごとのチェック: 既定と、既定と違う一致の番号。</summary>
    private sealed class FileChecks
    {
        public bool Base = true;
        public HashSet<int>? Flipped;
    }
}
