using HexEditor.Core.Sources;

namespace HexEditor.Core.Search;

/// <summary>検索の設定のキーと値の解釈 (FIND-01、FIND-04、FIND-20)。</summary>
public static class SearchSettings
{
    /// <summary>チャンクの大きさ (KiB。FIND-01 の仕様 2: 256 KiB〜64 MiB、既定 4 MiB)。</summary>
    public const string ChunkSizeKey = "search.chunkSize";

    public const int DefaultChunkSizeKiB = SearchEngine.DefaultChunkSize / 1024;

    public const int MinChunkSizeKiB = 256;

    public const int MaxChunkSizeKiB = 64 * 1024;

    /// <summary>読み込みエラーの既定の動作 (FIND-01 の「エラー」: <c>ask</c> 確認する / <c>skip</c> 飛ばす / <c>abort</c> 中止する)。</summary>
    public const string ReadErrorsKey = "search.readErrors";

    /// <summary>表示中の一致を強調表示する (FIND-04 の仕様 9)。</summary>
    public const string HighlightKey = "search.highlightMatches";

    /// <summary>すべて検索のたびに結果一覧の新しいタブを作る (FIND-20 の仕様 3。オフなら固定していないタブを置き換える)。</summary>
    public const string NewTabKey = "search.results.newTab";

    /// <summary>設定の KiB をバイトのチャンクの大きさにする (範囲外は範囲に収める)。</summary>
    public static int ChunkSizeBytes(int kib) => Math.Clamp(kib, MinChunkSizeKiB, MaxChunkSizeKiB) * 1024;

    /// <summary>読み込みエラーの設定値を解釈する。知らない値は「確認する」。</summary>
    public static ReadErrorPolicy ReadErrorPolicyOf(string? value) => value switch
    {
        "skip" => ReadErrorPolicy.Skip,
        "abort" => ReadErrorPolicy.Abort,
        _ => ReadErrorPolicy.Ask,
    };
}

/// <summary>検索中の読み込みエラーの扱い (FIND-01 の「エラー」)。</summary>
public enum ReadErrorPolicy
{
    /// <summary>InfoBar で飛ばすか中止するかを尋ねる。</summary>
    Ask,

    /// <summary>尋ねずに飛ばして続ける。</summary>
    Skip,

    /// <summary>尋ねずに検索を中止する。</summary>
    Abort,
}

/// <summary>
/// 1 回の検索の読み込みエラーの判断 (FIND-01 の「エラー」)。同じ範囲は 1 回の検索で 1 回だけ尋ね (チャンクの重なり部分を読み直しても
/// 尋ね直さない)、「飛ばす」を選んだらその検索の残りの読めない範囲も尋ねずに飛ばす。「中止する」を選んだら以後も中止を返す。
/// <paramref name="ask"/> は検索のスレッドから呼ばれ、利用者の答えを待って返す (UI の InfoBar)。
/// </summary>
public sealed class ReadErrorDecider(ReadErrorPolicy policy, Func<UnreadableRange, UnreadableAction>? ask)
{
    private readonly object _lock = new();
    private readonly List<(long Start, long End)> _asked = [];
    private UnreadableAction? _decided = policy switch
    {
        ReadErrorPolicy.Skip => UnreadableAction.Skip,
        ReadErrorPolicy.Abort => UnreadableAction.Abort,
        _ => null,
    };

    /// <summary>尋ねた回数 (テスト用)。</summary>
    public int AskCount { get; private set; }

    /// <summary><see cref="SearchOptions.OnUnreadable"/> に渡す判断。</summary>
    public UnreadableAction Decide(UnreadableRange range)
    {
        lock (_lock)
        {
            if (_decided is { } decided)
            {
                return decided;
            }

            long end = range.Offset + Math.Max(1, range.Length);
            if (_asked.Any(a => a.Start <= range.Offset && end <= a.End))
            {
                return UnreadableAction.Skip;
            }

            _asked.Add((range.Offset, end));
            AskCount++;
            UnreadableAction answer = ask?.Invoke(range) ?? UnreadableAction.Skip;
            _decided = answer;
            return answer;
        }
    }
}
