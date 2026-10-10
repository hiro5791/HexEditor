using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.Core.Search;
using Microsoft.UI.Xaml.Media;
using HexEditor.Core.View;

namespace HexEditor.App;

/// <summary>
/// 検索の追加のつなぎ込み: すべて置換と結果一覧の変換・エクスポートのコマンド (FIND-21、FIND-23 の「呼び出し」)、一致の強調の設定
/// (FIND-04 の仕様 9)、範囲「選択範囲」の枠 (FIND-11 の仕様 3)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>InitializeSearch から 1 回呼ぶ。</summary>
    private void InitializeSearchExtras()
    {
        // 一致の強調の設定が変わったら描き直す (アプリ全体の通知なので、ウィンドウを閉じたら外す)。
        Action<IReadOnlyCollection<string>> settingsChanged = keys =>
        {
            if (keys.Contains(SearchSettings.HighlightKey))
            {
                DispatcherQueue.TryEnqueue(() => UpdateMatchHighlights());
            }
        };
        App.Settings.Changed += settingsChanged;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                App.Settings.Changed -= settingsChanged;
            }
        };

        FindBar.MatchesChanged += (_, _) => RefreshSearchScopeOutline();
    }

    /// <summary>検索のコマンド (RegisterSearchCommands から呼ぶ)。</summary>
    private void RegisterSearchExtraCommands()
    {
        // すべて置換: 置換欄に置換語があればすべて置換する。なければ置換の形で検索バーを開く (FIND-23 の「呼び出し」)。
        Commands.Register("search.replaceAll", async () =>
        {
            if (FindBar.IsOpen && FindBar.IsReplaceMode && FindBar.CanReplaceAll)
            {
                await FindBar.ReplaceAllAsync();
            }
            else
            {
                OpenFindBar(replace: true);
            }
        }, () => NeedsDocument(d => d.Document.IsReadOnly || d.Editor.ReadOnly ? Loc.Get("Command_ReadOnly") : null));

        Commands.Register("search.results.toBookmarks", () => SearchResults.ToBookmarksAsync(), NeedsSearchResults);
        Commands.Register("search.results.export", () => SearchResults.ExportChoosingFormatAsync(), NeedsSearchResults);
    }

    private CommandState NeedsSearchResults() =>
        SearchResults.HasResults ? CommandState.Available : CommandState.Unavailable(Loc.Get("Command_NoSearchResults"));

    /// <summary>表示中の一致を強調表示するか (設定。FIND-04 の仕様 9)。</summary>
    private static bool MatchHighlightEnabled => App.Settings.GetBool(SearchSettings.HighlightKey, true);

    // ---- 範囲「選択範囲」の枠 (FIND-11 の仕様 3) ----

    private readonly HashSet<HexView> _scopeOutlinedViews = [];

    /// <summary>Hex ビューに検索範囲の枠の提供元を付ける (ビューごとに 1 回)。</summary>
    private void AttachSearchScopeOutline(HexView view)
    {
        if (_scopeOutlinedViews.Add(view))
        {
            view.SetHighlightSource("searchScope", (start, end) => SearchScopeOutline(view, start, end));
        }
    }

    private void RefreshSearchScopeOutline()
    {
        foreach (HexView view in _views)
        {
            AttachSearchScopeOutline(view);
            view.RefreshHighlights();
        }
    }

    /// <summary>
    /// 範囲を「選択範囲」にしている間、その範囲を薄い枠で示す。ハイコントラストではシステムの強調色の点線の枠にする。
    /// </summary>
    private IEnumerable<HexHighlight> SearchScopeOutline(HexView view, long start, long end)
    {
        if (view.Editor is null || view.Editor != FindBar.Editor)
        {
            yield break;
        }

        bool hc = view.IsHighContrast;
        foreach (SearchRange r in FindBar.OutlinedScope.Where(r => r.Offset < end && r.End > start))
        {
            Brush border = hc ? AnnotationBrushes.Get("InspectorTargetBorderBrush", view, true) : AnnotationBrushes.Get("TextFillColorTertiaryBrush", view, false);
            yield return new HexHighlight(r.Offset, r.Length, CellLayer.Bookmark, null, border, hc ? [1, 2] : null, "searchScope");
        }
    }
}
