using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace HexEditor.Core.Search;

/// <summary>検索の種類 (FIND-04 の仕様 3。正規表現はフェーズ 2)。</summary>
public enum SearchKind
{
    Hex,
    Text,
    Integer,
    Float,
}

/// <summary>検索の条件 (種類とオプション)。履歴の項目に保存し、↑ / ↓ で呼び出したときに検索バーへ戻す (FIND-28 の仕様 1・3)。</summary>
public sealed record SearchConditions
{
    public SearchKind Kind { get; init; } = SearchKind.Hex;

    public TextEncodingId Encoding { get; init; } = TextEncodingId.Ascii;

    public bool CaseSensitive { get; init; }

    public bool UseEscapes { get; init; }

    public bool AlignToCharacters { get; init; }

    public bool WholeWord { get; init; }

    public int IntegerBits { get; init; } = 32;

    public IntegerSign Sign { get; init; } = IntegerSign.Either;

    public SearchEndian Endian { get; init; } = SearchEndian.Little;

    public FloatFormat FloatFormat { get; init; } = FloatFormat.Single;

    public ToleranceKind Tolerance { get; init; } = ToleranceKind.None;

    /// <summary>許容誤差の入力 (入力したとおりの文字列)。</summary>
    public string ToleranceText { get; init; } = string.Empty;

    internal JsonObject ToJson() => new()
    {
        ["kind"] = Kind.ToString(),
        ["encoding"] = Encoding.ToString(),
        ["caseSensitive"] = CaseSensitive,
        ["escapes"] = UseEscapes,
        ["align"] = AlignToCharacters,
        ["wholeWord"] = WholeWord,
        ["bits"] = IntegerBits,
        ["sign"] = Sign.ToString(),
        ["endian"] = Endian.ToString(),
        ["float"] = FloatFormat.ToString(),
        ["tolerance"] = Tolerance.ToString(),
        ["toleranceText"] = ToleranceText,
    };

    internal static SearchConditions FromJson(JsonObject o) => new()
    {
        Kind = Enum<SearchKind>(o["kind"], SearchKind.Hex),
        Encoding = Enum<TextEncodingId>(o["encoding"], TextEncodingId.Ascii),
        CaseSensitive = Bool(o["caseSensitive"]),
        UseEscapes = Bool(o["escapes"]),
        AlignToCharacters = Bool(o["align"]),
        WholeWord = Bool(o["wholeWord"]),
        IntegerBits = o["bits"] is JsonValue v && v.TryGetValue(out int bits) && NumericSearch.IntegerSizes.Contains(bits) ? bits : 32,
        Sign = Enum<IntegerSign>(o["sign"], IntegerSign.Either),
        Endian = Enum<SearchEndian>(o["endian"], SearchEndian.Little),
        FloatFormat = Enum<FloatFormat>(o["float"], FloatFormat.Single),
        Tolerance = Enum<ToleranceKind>(o["tolerance"], ToleranceKind.None),
        ToleranceText = o["toleranceText"] is JsonValue t && t.TryGetValue(out string? s) ? s : string.Empty,
    };

    private static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) && b;

    private static T Enum<T>(JsonNode? node, T fallback)
        where T : struct, Enum =>
        node is JsonValue v && v.TryGetValue(out string? s) && System.Enum.TryParse(s, out T value) ? value : fallback;
}

/// <summary>履歴の 1 項目: 検索語 (置換語) と、そのときの条件。</summary>
public sealed record SearchHistoryEntry(string Text, SearchConditions Conditions);

/// <summary>検索欄の履歴と置換欄の履歴 (FIND-28 の仕様 2)。</summary>
public enum HistoryList
{
    Find,
    Replace,
}

/// <summary>
/// 検索履歴 (FIND-28)。検索欄と置換欄で別に持ち、新しい順に並べる。同じ検索語と条件がすでにあれば先頭に移す。
/// 件数の上限は各 50 件 (0〜500。0 は保存しない)。4 KiB (UTF-8) を超える検索語は保存しない。スレッドセーフではない (UI のスレッドで使う)。
/// </summary>
public sealed class SearchHistory
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;

    /// <summary>保存する検索語の最大のバイト数 (UTF-8。FIND-28 の仕様 6)。</summary>
    public const int MaxEntryBytes = 4096;

    private readonly List<SearchHistoryEntry> _find = [];
    private readonly List<SearchHistoryEntry> _replace = [];
    private int _limit = DefaultLimit;

    /// <summary>件数の上限 (0〜500)。小さくすると古い項目を捨てる。</summary>
    public int Limit
    {
        get => _limit;
        set
        {
            _limit = Math.Clamp(value, 0, MaxLimit);
            Trim(_find);
            Trim(_replace);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>履歴が変わった (保存のきっかけ)。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<SearchHistoryEntry> Find => _find;

    public IReadOnlyList<SearchHistoryEntry> Replace => _replace;

    public IReadOnlyList<SearchHistoryEntry> Get(HistoryList list) => list == HistoryList.Find ? _find : _replace;

    /// <summary>項目を先頭に加える。同じ項目があれば先頭に移す。保存しない項目 (空、4 KiB 超、上限 0) なら false。</summary>
    public bool Add(HistoryList list, SearchHistoryEntry entry)
    {
        if (_limit == 0 || entry.Text.Length == 0 || Encoding.UTF8.GetByteCount(entry.Text) > MaxEntryBytes)
        {
            return false;
        }

        List<SearchHistoryEntry> items = list == HistoryList.Find ? _find : _replace;
        items.Remove(entry);
        items.Insert(0, entry);
        Trim(items);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>項目を消す (検索欄のドロップダウンの削除。FIND-28 の仕様 4)。</summary>
    public void RemoveAt(HistoryList list, int index)
    {
        List<SearchHistoryEntry> items = list == HistoryList.Find ? _find : _replace;
        if (index >= 0 && index < items.Count)
        {
            items.RemoveAt(index);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>すべて消す (「検索履歴を消去」)。</summary>
    public void Clear()
    {
        _find.Clear();
        _replace.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>設定に保存する形 (JSON)。</summary>
    public JsonObject ToJson() => new()
    {
        ["find"] = new JsonArray([.. _find.Select(ToJson)]),
        ["replace"] = new JsonArray([.. _replace.Select(ToJson)]),
    };

    /// <summary>設定から読む。読めない項目は捨てる。</summary>
    public void Load(JsonNode? node)
    {
        _find.Clear();
        _replace.Clear();
        if (node is JsonObject o)
        {
            Read(o["find"], _find);
            Read(o["replace"], _replace);
        }

        Trim(_find);
        Trim(_replace);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static JsonNode? ToJson(SearchHistoryEntry e)
    {
        JsonObject o = e.Conditions.ToJson();
        o["text"] = e.Text;
        return o;
    }

    private static void Read(JsonNode? node, List<SearchHistoryEntry> sink)
    {
        if (node is not JsonArray array)
        {
            return;
        }

        foreach (JsonNode? item in array)
        {
            if (item is JsonObject o && o["text"] is JsonValue v && v.TryGetValue(out string? text) && text.Length > 0
                && Encoding.UTF8.GetByteCount(text) <= MaxEntryBytes)
            {
                var entry = new SearchHistoryEntry(text, SearchConditions.FromJson(o));
                if (!sink.Contains(entry))
                {
                    sink.Add(entry);
                }
            }
        }
    }

    private void Trim(List<SearchHistoryEntry> items)
    {
        if (items.Count > _limit)
        {
            items.RemoveRange(_limit, items.Count - _limit);
        }
    }
}

/// <summary>
/// 検索欄の ↑ / ↓ で履歴をたどる位置 (FIND-28 の仕様 3)。↑ で古い方へ、↓ で新しい方へ進む。入力を変えたら <see cref="Reset"/>。
/// </summary>
public sealed class HistoryCursor
{
    /// <summary>今表示している項目の番号 (0 が最新)。−1 は履歴を表示していない (入力中)。</summary>
    public int Index { get; private set; } = -1;

    /// <summary>1 つ古い項目。なければ null (位置は変えない)。</summary>
    public SearchHistoryEntry? Older(IReadOnlyList<SearchHistoryEntry> items)
    {
        if (Index + 1 >= items.Count)
        {
            return null;
        }

        Index++;
        return items[Index];
    }

    /// <summary>1 つ新しい項目。最新より新しい方へは進まない (null)。</summary>
    public SearchHistoryEntry? Newer(IReadOnlyList<SearchHistoryEntry> items)
    {
        if (Index <= 0 || Index - 1 >= items.Count)
        {
            return null;
        }

        Index--;
        return items[Index];
    }

    public void Reset() => Index = -1;

    public override string ToString() => Index.ToString(CultureInfo.InvariantCulture);
}
