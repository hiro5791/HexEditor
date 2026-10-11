using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.Core.View;

namespace HexEditor.App.Controls;

/// <summary>
/// 検索の設定 (チャンクの大きさ。FIND-01 の仕様 2) と、検索中の読み込みエラーの扱い (FIND-01 の「エラー」)。設定「読み込みエラー」が
/// 「確認する」なら、読めない範囲に出会ったときに InfoBar で「飛ばして続ける」か「中止する」かを尋ねる (1 回の検索で 1 回だけ)。
/// 件数の数え上げとインクリメンタルサーチは利用者が明示的に始めた検索ではないため尋ねずに飛ばす (「中止する」の設定なら中止する)。
/// </summary>
public sealed partial class FindBar
{
    /// <summary>
    /// 読めない範囲を飛ばすか中止するかを利用者に尋ねる (MainWindow が InfoBar で尋ねる。検索のスレッドから呼ばれる)。
    /// null なら尋ねずに飛ばす。
    /// </summary>
    public Func<UnreadableRange, EditorState, CancellationToken, Task<UnreadableAction>>? AskUnreadable { get; set; }

    /// <summary>
    /// 正規表現の照合がチャンクの時間の上限に達したとき、そのチャンクを飛ばして続けるか中止するかを尋ねる (FIND-18 の「エラー」。
    /// MainWindow が InfoBar で尋ねる。検索のスレッドから呼ばれる)。null なら尋ねずに飛ばす。
    /// </summary>
    public Func<SearchRange, EditorState, CancellationToken, Task<UnreadableAction>>? AskTimeout { get; set; }

    /// <summary>設定のチャンクの大きさ (バイト)。</summary>
    internal static int ChunkSizeSetting =>
        SearchSettings.ChunkSizeBytes(App.Settings?.GetInt(SearchSettings.ChunkSizeKey, SearchSettings.DefaultChunkSizeKiB) ?? SearchSettings.DefaultChunkSizeKiB);

    /// <summary>設定の読み込みエラーの扱い。</summary>
    internal static ReadErrorPolicy ReadErrorSetting => SearchSettings.ReadErrorPolicyOf(App.Settings?.GetString(SearchSettings.ReadErrorsKey, "ask"));

    /// <summary>
    /// 検索の設定を作る。<paramref name="interactive"/> なら読み込みエラーを利用者に尋ねる (設定が「確認する」のとき)。
    /// </summary>
    private SearchOptions NewOptions(EditorState editor, SearchScope scope, CancellationToken token, bool interactive = true)
    {
        ReadErrorPolicy policy = ReadErrorSetting;
        if (!interactive && policy == ReadErrorPolicy.Ask)
        {
            policy = ReadErrorPolicy.Skip;
        }

        Func<UnreadableRange, UnreadableAction>? ask = AskUnreadable is { } handler
            ? range => handler(range, editor, token).GetAwaiter().GetResult()
            : null;
        var decider = new ReadErrorDecider(policy, ask);

        // 時間の上限は 1 回の検索で 1 回だけ尋ね、その答えを残りのチャンクにも使う (件数の数え上げなどは尋ねずに飛ばす)。
        UnreadableAction? timeoutAnswer = interactive ? null : UnreadableAction.Skip;
        var timeoutLock = new object();
        Func<SearchRange, UnreadableAction> onTimeout = range =>
        {
            lock (timeoutLock)
            {
                timeoutAnswer ??= AskTimeout is { } handler ? handler(range, editor, token).GetAwaiter().GetResult() : UnreadableAction.Skip;
                return timeoutAnswer.Value;
            }
        };
        return new SearchOptions { Scope = scope, ChunkSize = ChunkSizeSetting, OnUnreadable = decider.Decide, OnTimeout = onTimeout };
    }

    /// <summary>読めない範囲のために中止した (「中止する」を選んだ、または設定が「中止する」)。</summary>
    private void ShowAborted(SearchAbortedException ex)
    {
        Status.Text = Services.Loc.Format("Find_AbortedUnreadable", StatusFormat.Hex(ex.Range.Offset));
        MarkQuery(QueryState.NotFound);
        Announce(Status.Text);
    }
}
