using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.Core.Panels;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;

namespace HexEditor.App;

/// <summary>
/// 検索のパネル: 「文字列の抽出」(FIND-32。下のパネル) と「複数ファイル検索」(FIND-30、FIND-31。左のパネル)。どちらも結果を持つため、
/// ウィンドウごとに 1 つの部品を使い、浮動パネルにはしない。複数語 (FIND-26) と一致しない箇所 (FIND-25) の検索のコマンドもここ。
/// </summary>
public sealed partial class MainWindow
{
    public const string StringsPanelId = "strings";

    public const string MultiFilePanelId = "multiFileSearch";

    /// <summary>文字列の抽出 (ウィンドウごとに 1 つ)。</summary>
    private readonly StringsPanel StringsExtraction = new();

    /// <summary>複数ファイル検索 (ウィンドウごとに 1 つ)。</summary>
    private readonly MultiFileSearchPanel MultiFileSearch = new();

    /// <summary>パネルの一覧に登録する (<see cref="RegisterSearchResultsPanel"/> から呼ぶ)。</summary>
    private static void RegisterSearchPanels()
    {
        if (PanelRegistry.Find(StringsPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(StringsPanelId, "Panel_Strings_Title", PanelDock.Bottom,
                ctx => ((MainWindow)ctx.Window).StringsExtraction)
            { CanFloat = false });
        }

        if (PanelRegistry.Find(MultiFilePanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(MultiFilePanelId, "Panel_MultiFile_Title", PanelDock.Left,
                ctx => ((MainWindow)ctx.Window).MultiFileSearch)
            { CanFloat = false });
        }
    }

    /// <summary>InitializeSearch から 1 回呼ぶ。</summary>
    private void InitializeSearchPanels()
    {
        SearchLabels.Signed = Loc.Get("Search_Label_Signed");
        SearchLabels.Unsigned = Loc.Get("Search_Label_Unsigned");
        SearchLabels.SignedAndUnsigned = Loc.Get("Search_Label_SignedAndUnsigned");

        // 文字列の抽出: 結果一覧は共通の部品 (00-overview 9 章)。
        AutomationProperties.SetAutomationId(StringsExtraction, "StringsPanel");
        StringsExtraction.EditorSource = () => Editor;
        SearchResultsPanel list = StringsExtraction.Results;
        list.WindowId = AppWindow.Id;
        list.Operations = Vm.Operations;
        list.Confirm = ConfirmAsync;
        list.HighlightsChanged += (_, _) => UpdateMatchHighlights();
        list.Shown += (_, _) => ShowPanel(StringsPanelId, focus: false);
        list.BookmarksRequested += (_, e) => AddSearchResultBookmarks(e.Editor, e.Items, e.Group);
        list.NoticeRequested += (_, n) => ShowNotice(n.Message, n.Severity);
        list.ActivateRequested += (_, editor) => ActivateEditor(editor);

        // 複数ファイル検索。
        AutomationProperties.SetAutomationId(MultiFileSearch, "MultiFilePanel");
        MultiFileSearch.WindowId = AppWindow.Id;
        MultiFileSearch.Operations = Vm.Operations;
        MultiFileSearch.Confirm = ConfirmAsync;
        MultiFileSearch.OpenDocuments = () => [.. Vm.Documents
            .Where(d => d.Editor.Document.Source is FileByteSource)
            .Select(d => new OpenFileDocument(((FileByteSource)d.Editor.Document.Source).Path, d.Editor))];
        MultiFileSearch.DefaultFolder = () => (Editor?.Document.Source as FileByteSource)?.Path is { } path ? Path.GetDirectoryName(path) : null;
        MultiFileSearch.FindBarQuery = () => FindBar.CaptureQuery();
        MultiFileSearch.OpenRequested += (_, e) => OpenMultiFileResult(e.Path, e.Offset, e.Length);
        MultiFileSearch.NoticeRequested += (_, n) => ShowNotice(n.Message, n.Severity);
    }

    /// <summary>複数ファイル検索の結果を開く: すでに開いていればそのタブに切り替え、一致を選択する (FIND-30 の仕様 6)。</summary>
    private void OpenMultiFileResult(string path, long offset, long length)
    {
        ViewModels.DocumentViewModel? doc = Vm.Documents.FirstOrDefault(d =>
            d.Editor.Document.Source is FileByteSource f && string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) ?? TryOpen(path, restorePosition: false);
        if (doc is null)
        {
            return;
        }

        Vm.Selected = doc;
        doc.Editor.SelectMatch(Math.Min(offset, doc.Editor.Document.Length), length);
        FocusEditor();
    }

    /// <summary>検索のパネルのコマンド (RegisterSearchCommands から呼ぶ)。</summary>
    private void RegisterSearchPanelCommands()
    {
        Commands.Register("search.multiFile", () => OpenMultiFile(replace: false));
        Commands.Register("search.multiFileReplace", () => OpenMultiFile(replace: true));
        Commands.Register("search.strings", () => ShowPanel(StringsPanelId), NeedsDocument);

        // 複数の語を検索 (FIND-26): 検索バーを開き、検索欄を複数行の一覧にする。
        Commands.Register("search.multiTerm", () =>
        {
            OpenFindBar(replace: false);
            FindBar.ShowMultiTerm();
        }, NeedsDocument);

        // 一致しない箇所を検索 (FIND-25): 検索バーを開き、種類「Hex」で「一致しない箇所を探す」をオンにする。
        Commands.Register("search.mismatch", () =>
        {
            OpenFindBar(replace: false);
            FindBar.ShowMismatch();
        }, NeedsDocument);
    }

    private void OpenMultiFile(bool replace)
    {
        ShowPanel(MultiFilePanelId);
        MultiFileSearch.PrepareToShow(replace);
    }

    /// <summary>
    /// フォルダを対象にして複数ファイル検索を開く (Explorer からフォルダをドロップしたときの InfoBar の「複数ファイル検索」ボタン。
    /// FIND-30 の「呼び出し」、UI-34 の仕様 4)。
    /// </summary>
    internal void OpenMultiFileFor(IReadOnlyList<string> folders)
    {
        MultiFileSearch.SetFolders(folders);
        OpenMultiFile(replace: false);
    }

    /// <summary>表示中の範囲の、文字列の抽出の結果 (検索バーと結果一覧の強調がないとき)。</summary>
    private Func<Core.Engine.DocumentSnapshot, long, long, IReadOnlyList<(long Offset, long Length)>>? StringsHighlights(HexView view) =>
        StringsExtraction.Results.IsOpen && StringsExtraction.Results.Shows(view.Editor) && IsPanelShown(StringsPanelId)
            ? StringsExtraction.Results.MatchesInView
            : null;
}
