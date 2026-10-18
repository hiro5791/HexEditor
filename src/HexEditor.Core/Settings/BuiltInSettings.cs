using System.Text.Json.Nodes;
using HexEditor.Core.Commands;
using HexEditor.Core.Files;
using HexEditor.Core.Saving;
using HexEditor.Core.View;

namespace HexEditor.Core.Settings;

/// <summary>
/// 組み込みの設定項目 (UI-22)。各機能の仕様書の「設定の一覧」の項目を、機能を作るときにここに足す。
/// </summary>
/// <remarks>
/// 足すときは、ここに 1 行足し、<c>Strings/en</c> と <c>Strings/ja</c> に名前 (<c>Set_&lt;キーの . を _ に&gt;</c>)、説明
/// (<c>SetDesc_…</c>)、選択肢の表示名 (<c>SetOpt_…_&lt;値&gt;</c>) を書き、<c>settings.schema.json</c> を作り直す
/// (<c>HEXEDITOR_UPDATE_SCHEMA=1 dotnet test --filter SettingsSchema</c>)。
/// </remarks>
public static class BuiltInSettings
{
    /// <summary>表示言語の選択肢 (UI-43、00-overview.md 5.1)。</summary>
    public static readonly string[] Languages =
    [
        "system", "en", "zh-Hans", "zh-Hant", "ja", "ko", "id", "vi", "th", "de", "fr", "es", "pt",
        "it", "ru", "uk", "pl", "cs", "hu", "ro", "el", "ar", "tr", "fa",
    ];

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        // ---- 全般 ----
        new("ui.toolbar.visible", SettingCategories.General, SettingKind.Bool, false) { Order = 10 },
        new(ToolbarItems.ItemsKey, SettingCategories.General, SettingKind.List, ToolbarItems.DefaultJson()) { Order = 11, ShowInPage = false },
        new("ui.statusBar.visible", SettingCategories.General, SettingKind.Bool, true) { Order = 20 },
        new("ui.statusBar.items", SettingCategories.General, SettingKind.String, string.Empty) { Order = 21, ShowInPage = false },
        new("ui.fullScreen.showStatusBar", SettingCategories.General, SettingKind.Bool, true) { Order = 25 },
        new(StartupPlanner.RestoreOnStartupKey, SettingCategories.General, SettingKind.Choice, "always") { Options = ["always", "ask", "never"], Order = 30 },

        // 外部から開いたファイルの開き方 (UI-15 の仕様 2・4)。
        new("window.openExternalIn", SettingCategories.General, SettingKind.Choice, "lastActiveWindow") { Options = ["lastActiveWindow", "newWindow"], Order = 40 },
        new("window.launchWithoutFileOpensNewWindow", SettingCategories.General, SettingKind.Bool, false) { Order = 41 },

        // ---- 外観 ----
        new("ui.theme", SettingCategories.Appearance, SettingKind.Choice, "system") { Options = ["system", "light", "dark"], Order = 10 },
        // アクセントカラー (UI-27 の仕様 1)。system か #RRGGBB。色のリソースは最初のウィンドウを作る前に上書きするため、反映は再起動後。
        new("ui.accentColor", SettingCategories.Appearance, SettingKind.Color, "system") { Options = ["system"], Order = 15, RequiresRestart = true },
        new("ui.backdrop", SettingCategories.Appearance, SettingKind.Choice, "mica") { Options = ["mica", "micaAlt", "none"], Order = 20 },

        // Hex 表示のフォントと配色 (UI-28、UI-29)。フォントと配色は一覧から選ぶ専用の区画 (App の ViewSections) で変える。
        new("view.font.family", SettingCategories.Appearance, SettingKind.String, string.Empty) { Order = 30, ShowInPage = false },
        new("view.font.size", SettingCategories.Appearance, SettingKind.Number, 10.0) { Min = 6, Max = 72, Step = 0.5, Order = 31 },
        new("view.font.lineHeight", SettingCategories.Appearance, SettingKind.Number, 1.2) { Min = 1.0, Max = 2.0, Step = 0.1, Order = 32 },
        new("view.font.fallback", SettingCategories.Appearance, SettingKind.String, string.Empty) { Order = 33 },
        new("view.font.followTextScaling", SettingCategories.Appearance, SettingKind.Bool, true) { Order = 34 },
        new("view.colorScheme", SettingCategories.Appearance, SettingKind.String, "default") { Order = 35, ShowInPage = false },

        // ---- 表示 ----
        new("view.cursor.nibbleArrowKeys", SettingCategories.View, SettingKind.Bool, false) { Order = 10 },
        new("view.statusBar.showNibble", SettingCategories.View, SettingKind.Bool, false) { Order = 20 },
        new("view.scroll.fixedOffsetColumn", SettingCategories.View, SettingKind.Bool, true) { Order = 31 },
        new("view.scroll.cursorMargin", SettingCategories.View, SettingKind.Int, 0) { Min = 0, Max = 10, Order = 30 },
        new("view.jump.position", SettingCategories.View, SettingKind.Choice, "third") { Options = ["top", "third", "center"], Order = 40 },
        new("view.tooltips", SettingCategories.View, SettingKind.Bool, true) { Order = 50 },
        new("view.text.nonPrintable", SettingCategories.View, SettingKind.Choice, "dot") { Options = ["dot", "space", "controlPictures"], Order = 55 },
        new("view.scrollBar.cursorMark", SettingCategories.View, SettingKind.Bool, true) { Order = 57 },
        new("view.scrollBar.searchMarks", SettingCategories.View, SettingKind.Bool, true) { Order = 58 },
        new("view.scrollBar.selectionMark", SettingCategories.View, SettingKind.Bool, false) { Order = 59 },
        new("view.scrollBar.bookmarkMarks", SettingCategories.View, SettingKind.Bool, false) { Order = 59 },
        new("view.scrollBar.diffMarks", SettingCategories.View, SettingKind.Bool, false) { Order = 59 },
        new("view.text.invalidSymbol", SettingCategories.View, SettingKind.Choice, "dot") { Options = ["dot", "replacement"], Order = 56 },
        new("view.modified.keepAfterSave", SettingCategories.View, SettingKind.Bool, false) { Order = 60 },
        new("view.modified.showDeletions", SettingCategories.View, SettingKind.Bool, false) { Order = 61 },

        // バイトテーマ (VIEW-17)。表示メニューで選ぶ (none / category / gradient / custom:ファイル名)。
        new("view.byteTheme", SettingCategories.View, SettingKind.String, "none") { Order = 62, ShowInPage = false },

        // ミニマップ (VIEW-35 の仕様 9。全ドキュメント共通)。表示内容・範囲は表示メニューとミニマップの右クリックメニューで変える。
        new("view.minimap.visible", SettingCategories.View, SettingKind.Bool, false) { Order = 63 },
        new("view.minimap.width", SettingCategories.View, SettingKind.Int, 80) { Min = 40, Max = 200, Order = 64 },
        new("view.minimap.content", SettingCategories.View, SettingKind.Choice, "entropy")
        {
            Options = ["entropy", "byteKinds", "byteValue", "zero", "byteTheme"],
            Order = 65,
        },
        new("view.minimap.range", SettingCategories.View, SettingKind.Choice, "whole") { Options = ["whole", "around"], Order = 66 },
        new("view.minimap.exact", SettingCategories.View, SettingKind.Bool, false) { Order = 67 },
        new("view.minimap.hiddenMarks", SettingCategories.View, SettingKind.String, string.Empty) { Order = 67, ShowInPage = false },

        // 画面分割・並列表示 (VIEW-37 の仕様 3、VIEW-39 の仕様 3)。
        new("view.split.syncSettings", SettingCategories.View, SettingKind.Bool, false) { Order = 68 },
        new("view.sync.selection", SettingCategories.View, SettingKind.Bool, false) { Order = 69 },

        // ズーム (UI-08 の仕様 2・3)。全タブ共通の Hex 表示の倍率はズームの操作で変わるので、設定画面には出さない。
        new(ZoomSettings.HexScopeKey, SettingCategories.View, SettingKind.Choice, ZoomSettings.ScopeAll) { Options = [ZoomSettings.ScopeAll, ZoomSettings.ScopeTab], Order = 70 },
        new(ZoomSettings.HexKey, SettingCategories.View, SettingKind.Int, View.ZoomLevels.Default) { Min = 50, Max = 400, Order = 71, ShowInPage = false },
        new(ZoomSettings.UiKey, SettingCategories.View, SettingKind.Int, View.ZoomLevels.Default) { Min = 50, Max = 400, Order = 72 },

        // データインスペクタ (INSP-01〜INSP-19)。行の構成とプリセットはパネルの「行の設定」で変える。
        new("inspector.integerBase", SettingCategories.View, SettingKind.Choice, "decimal") { Options = ["decimal", "hex", "octal"], Order = 100 },
        new("inspector.floatFormat", SettingCategories.View, SettingKind.Choice, "shortest") { Options = ["shortest", "exponent", "hex"], Order = 101 },
        new("inspector.timeZone", SettingCategories.View, SettingKind.Choice, "local") { Options = ["local", "utc"], Order = 102 },
        new("inspector.dateFormat", SettingCategories.View, SettingKind.Choice, "regional") { Options = ["regional", "iso"], Order = 103 },
        new("inspector.digitGrouping", SettingCategories.View, SettingKind.Bool, true) { Order = 104 },
        new("inspector.highlightTarget", SettingCategories.View, SettingKind.Bool, true) { Order = 105 },
        new("inspector.useCursorWithSelection", SettingCategories.View, SettingKind.Bool, false) { Order = 106 },
        new("inspector.rows", SettingCategories.View, SettingKind.String, string.Empty) { Order = 107, ShowInPage = false },
        new("inspector.rowPresets", SettingCategories.View, SettingKind.String, "{}") { Order = 108, ShowInPage = false },

        // ブックマーク (INSP-23 の仕様 2): Ctrl+F2 で付けるときの色 (色の一覧の番号)。
        new(Bookmarks.BookmarkColor.DefaultColorKey, SettingCategories.View, SettingKind.Choice, "1") { Options = ["1", "2", "3", "4", "5", "6", "7", "8"], Order = 110 },

        // 位置マネージャとツールチップ (INSP-31 の仕様 2・4)、注釈の表示 (INSP-32)、色付けルール (INSP-33、INSP-34)。
        new("positionManager.followCursor", SettingCategories.View, SettingKind.Bool, true) { Order = 111 },
        new("bookmarks.richToolTip", SettingCategories.View, SettingKind.Bool, true) { Order = 112 },
        new("annotations.hidden", SettingCategories.View, SettingKind.String, string.Empty) { Order = 113, ShowInPage = false },
        new("annotations.column", SettingCategories.View, SettingKind.Bool, false) { Order = 114 },
        // 注釈の描き方 (出どころごと。INSP-32 の仕様 4)。
        new("annotations.style.bookmark", SettingCategories.View, SettingKind.Choice, "background") { Options = ["background", "border", "underline"], Order = 114 },
        new("annotations.style.yara", SettingCategories.View, SettingKind.Choice, "border") { Options = ["background", "border", "underline"], Order = 114 },
        new("annotations.style.searchResults", SettingCategories.View, SettingKind.Choice, "border") { Options = ["background", "border", "underline"], Order = 114 },
        new("annotations.style.template", SettingCategories.View, SettingKind.Choice, "background") { Options = ["background", "border", "underline"], Order = 114 },
        new("annotations.style.script", SettingCategories.View, SettingKind.Choice, "border") { Options = ["background", "border", "underline"], Order = 114 },
        new("annotations.style.analysis", SettingCategories.View, SettingKind.Choice, "border") { Options = ["background", "border", "underline"], Order = 114 },
        new("coloring.rules", SettingCategories.View, SettingKind.String, string.Empty) { Order = 115, ShowInPage = false },
        new("coloring.highContrastColors", SettingCategories.View, SettingKind.Bool, false) { Order = 116 },

        // ---- 編集 (EDIT-23、EDIT-26、EDIT-27) ----
        new("clipboard.compatFormats", SettingCategories.Editing, SettingKind.Bool, true) { Order = 10 },
        new("edit.pasteSpecial.preferLast", SettingCategories.Editing, SettingKind.Bool, true) { Order = 20 },
        new("edit.paste.detectWithoutConfirmation", SettingCategories.Editing, SettingKind.Bool, false) { Order = 30 },

        // 大文字・小文字の変換の方法 (EDIT-39 の仕様 1。既定は ASCII の英字だけ)。
        new("edit.caseConversion", SettingCategories.Editing, SettingKind.Choice, "ascii") { Options = ["ascii", "encoding"], Order = 40 },

        // 入力・削除・Undo・貼り付け・コピーの設定 (EDIT-10〜EDIT-13、EDIT-19、EDIT-22、EDIT-23)。キーと解釈は View.EditingSettings。
        new(View.EditingSettings.DefaultInputModeKey, SettingCategories.Editing, SettingKind.Choice, "overwrite") { Options = ["overwrite", "insert"], Order = 1 },
        new(View.EditingSettings.OverwriteSelectionTypingKey, SettingCategories.Editing, SettingKind.Choice, "overwriteFromStart") { Options = ["overwriteFromStart", "zeroFirst"], Order = 2 },
        new(View.EditingSettings.TextEnterKey, SettingCategories.Editing, SettingKind.Choice, "none") { Options = ["none", "crlf", "lf", "cr"], Order = 3 },
        new(View.EditingSettings.BackspaceInOverwriteKey, SettingCategories.Editing, SettingKind.Choice, "moveCursor") { Options = ["moveCursor", "zeroAndMove"], Order = 4 },
        new(View.EditingSettings.DeleteKeepsLengthKey, SettingCategories.Editing, SettingKind.Bool, false) { Order = 5 },
        new(View.EditingSettings.UndoCoalesceSecondsKey, SettingCategories.Editing, SettingKind.Int, 2) { Min = 0, Max = 10, Order = 6 },
        new(View.EditingSettings.ClearHistoryOnSaveKey, SettingCategories.Editing, SettingKind.Bool, false) { Order = 7 },

        // 分岐する履歴 (EDIT-21。既定オフ) と、変換ビューでのコピー (EDIT-45 の仕様 12)。
        new(View.EditingSettings.BranchingHistoryKey, SettingCategories.Editing, SettingKind.Bool, false) { Order = 8 },
        new(View.EditingSettings.TransformViewCopyKey, SettingCategories.Editing, SettingKind.Choice, "display") { Options = ["display", "raw"], Order = 44 },
        new(View.EditingSettings.SelectPastedKey, SettingCategories.Editing, SettingKind.Bool, true) { Order = 31 },
        new(View.EditingSettings.FitOverwritePasteKey, SettingCategories.Editing, SettingKind.Bool, false) { Order = 32 },
        new(View.EditingSettings.ClipboardMaxSizeKey, SettingCategories.Editing, SettingKind.Int, View.EditingSettings.DefaultClipboardMaxMiB)
        {
            Min = 1,
            Max = View.EditingSettings.MaxClipboardMaxMiB,
            Order = 40,
        },
        new(View.EditingSettings.HexSeparatorKey, SettingCategories.Editing, SettingKind.String, " ") { Order = 41 },
        new(View.EditingSettings.HexUpperCaseKey, SettingCategories.Editing, SettingKind.Bool, true) { Order = 42 },
        new(View.EditingSettings.HexBytesPerLineKey, SettingCategories.Editing, SettingKind.Int, 0) { Min = 0, Max = 1024, Order = 43 },

        // 選択 (EDIT-05〜EDIT-07、EDIT-17、EDIT-18) とユーザークリップボード (EDIT-28)。
        new(View.EditingSettings.MaxSelectionElementsKey, SettingCategories.Editing, SettingKind.Int, View.EditorState.DefaultMaxSelectionElements)
        {
            Min = 1_000,
            Max = 10_000_000,
            Order = 50,
        },
        new(View.EditingSettings.MaxRectangleRowsKey, SettingCategories.Editing, SettingKind.Int, View.EditorState.DefaultMaxRectangleRows)
        {
            Min = 1,
            Max = 10_000_000,
            Order = 51,
        },
        new(View.EditingSettings.SelectionShiftAmountKey, SettingCategories.Editing, SettingKind.Choice, "selectionLength")
        {
            Options = ["selectionLength", "lastAmount"],
            Order = 52,
        },
        new(View.EditingSettings.SelectionDragDropKey, SettingCategories.Editing, SettingKind.Bool, true) { Order = 53 },
        new(View.EditingSettings.ClipboardHistoryCountKey, SettingCategories.Editing, SettingKind.Int, 20) { Min = 0, Max = 100, Order = 54 },
        new(View.EditingSettings.UserClipboardPersistKey, SettingCategories.Editing, SettingKind.Bool, false) { Order = 55 },

        // 展開の確認を出す大きさ (MiB。EDIT-41 の仕様 7: 圧縮爆弾への対策)。
        new(Editing.Codecs.CodecSettings.DecompressConfirmKey, SettingCategories.Editing, SettingKind.Int, Editing.Codecs.CodecSettings.DefaultDecompressConfirmMiB)
        {
            Min = 1,
            Max = Editing.Codecs.CodecSettings.MaxDecompressConfirmMiB,
            Order = 56,
        },

        // ---- 検索 (FIND-02、FIND-20、FIND-28) ----
        new("search.maxMatchLength", SettingCategories.Search, SettingKind.Int, Search.SearchPattern.DefaultMaxMatchLength) { Min = 1, Max = Search.SearchPattern.MaxMaxMatchLength, Order = 10 },
        new("search.findAll.limit", SettingCategories.Search, SettingKind.Int, 1_000_000) { Min = 1_000, Max = 100_000_000, Order = 20 },
        new("search.results.preview", SettingCategories.Search, SettingKind.Bool, true) { Order = 30 },
        new("search.results.f3Repeats", SettingCategories.Search, SettingKind.Bool, false) { Order = 40 },
        new("search.history.limit", SettingCategories.Search, SettingKind.Int, Search.SearchHistory.DefaultLimit) { Min = 0, Max = Search.SearchHistory.MaxLimit, Order = 50 },
        new("search.history.doNotSave", SettingCategories.Search, SettingKind.Bool, false) { Order = 60 },

        // チャンクの大きさ (KiB。FIND-01 の仕様 2)、読み込みエラーの既定の動作 (FIND-01 の「エラー」)、一致の強調 (FIND-04 の仕様 9)、
        // すべて検索の結果を新しいタブに出すか (FIND-20 の仕様 3)。
        new(Search.SearchSettings.ChunkSizeKey, SettingCategories.Search, SettingKind.Int, Search.SearchSettings.DefaultChunkSizeKiB)
        {
            Min = Search.SearchSettings.MinChunkSizeKiB,
            Max = Search.SearchSettings.MaxChunkSizeKiB,
            Order = 70,
        },
        new(Search.SearchSettings.ReadErrorsKey, SettingCategories.Search, SettingKind.Choice, "ask") { Options = ["ask", "skip", "abort"], Order = 80 },
        new(Search.SearchSettings.HighlightKey, SettingCategories.Search, SettingKind.Bool, true) { Order = 90 },
        new(Search.SearchSettings.NewTabKey, SettingCategories.Search, SettingKind.Bool, false) { Order = 100 },

        // 正規表現の時間の上限 (秒。FIND-18 の仕様 6)、一致しない箇所の最小の繰り返し回数 (FIND-25 の仕様 4)、
        // 複数ファイル検索の並列数 (FIND-30 の仕様 3)。
        new("search.regex.timeLimit", SettingCategories.Search, SettingKind.Number, 2.0) { Min = 0.1, Max = 60, Step = 0.1, Order = 110 },
        new(Search.MismatchSearch.MinRepeatKey, SettingCategories.Search, SettingKind.Int, Search.MismatchSearch.DefaultMinRepeat)
        {
            Min = 1,
            Max = Search.MismatchSearch.MaxMinRepeat,
            Order = 120,
        },
        new(Search.MultiFileSearch.ParallelismKey, SettingCategories.Search, SettingKind.Int, Search.MultiFileSearch.DefaultParallelism)
        {
            Min = 1,
            Max = Search.MultiFileSearch.MaxParallelism,
            Order = 130,
        },

        // YARA のブロックの大きさ (MiB) と時間の上限 (秒) (FIND-33 の仕様 6・7)。
        new(Search.Yara.YaraSettings.BlockSizeKey, SettingCategories.Search, SettingKind.Int, Search.Yara.YaraSettings.DefaultBlockSizeMiB)
        {
            Min = Search.Yara.YaraSettings.MinBlockSizeMiB,
            Max = Search.Yara.YaraSettings.MaxBlockSizeMiB,
            Order = 140,
        },
        new(Search.Yara.YaraSettings.TimeLimitKey, SettingCategories.Search, SettingKind.Int, Search.Yara.YaraSettings.DefaultTimeLimitSeconds)
        {
            Min = 1,
            Max = Search.Yara.YaraSettings.MaxTimeLimitSeconds,
            Order = 150,
        },

        // ---- ファイルと保存 ----
        new("recent.maxItems", SettingCategories.Files, SettingKind.Int, RecentFileList.DefaultMaxItems) { Min = 0, Max = RecentFileList.MaxItemsLimit, Order = 1 },
        new("recent.restorePosition", SettingCategories.Files, SettingKind.Bool, true) { Order = 2 },
        new(ExternalChangeRules.AutoReloadKey, SettingCategories.Files, SettingKind.Bool, true) { Order = 3 },
        // ファイルのロック (ENG-15 の仕様 3)。新しく開くファイルから使う。
        new(Engine.FileLockSettings.Key, SettingCategories.Files, SettingKind.Choice, Engine.FileLockSettings.WhileModified)
        {
            Options = Engine.FileLockSettings.Options, Order = 3,
        },
        new(BackupSettings.EnabledKey, SettingCategories.Files, SettingKind.Bool, false) { Order = 4 },
        new(BackupSettings.LocationKey, SettingCategories.Files, SettingKind.Choice, "sameFolder") { Options = ["sameFolder", "folder"], Order = 5 },
        new(BackupSettings.FolderKey, SettingCategories.Files, SettingKind.String, string.Empty) { Exportable = false, Order = 6 },
        new(BackupSettings.GenerationsKey, SettingCategories.Files, SettingKind.Int, 1) { Min = 1, Max = BackupSettings.MaxGenerations, Order = 7 },
        new("save.recoveryIntervalMinutes", SettingCategories.Files, SettingKind.Int, 1) { Min = 0, Max = 60, Order = 8 },
        new(Saving.SavePlanner.ShiftInPlaceKey, SettingCategories.Files, SettingKind.Bool, false) { Order = 9 },
        new(Formats.EncodedFile.GapFillKey, SettingCategories.Files, SettingKind.String, "FF") { Order = 9 },
        new("storage.tempDirectory", SettingCategories.Files, SettingKind.String, string.Empty) { Exportable = false, RequiresRestart = true, Order = 10 },

        // ---- 解析 (ハッシュパネル ANA-18。パネルの中で変える項目なので設定画面には出さない) ----
        new("hash.autoRecompute", SettingCategories.Advanced, SettingKind.Bool, true) { Order = 100, ShowInPage = false },
        new("hash.algorithms", SettingCategories.Advanced, SettingKind.String, string.Empty) { Order = 101, ShowInPage = false },
        new("hash.sets", SettingCategories.Advanced, SettingKind.String, string.Empty) { Order = 102, ShowInPage = false },

        // 統計 (ANA-10) の「自動で再計算」、ファイル形式の自動判定 (ANA-17 の仕様 4)、分類のしきい値 (ANA-16 の仕様 6)。
        new("statistics.autoRecompute", SettingCategories.Advanced, SettingKind.Bool, true) { Order = 110, ShowInPage = false },
        new("analysis.fileType.autoDetect", SettingCategories.Advanced, SettingKind.Bool, true) { Order = 111, Group = "analysis" },
        new("analysis.classify.constantShare", SettingCategories.Advanced, SettingKind.Number, 0.99) { Min = 0.5, Max = 1, Step = 0.01, Order = 112, Group = "analysis" },
        new("analysis.classify.textShare", SettingCategories.Advanced, SettingKind.Number, 0.95) { Min = 0.5, Max = 1, Step = 0.01, Order = 113, Group = "analysis" },
        new("analysis.classify.encryptedEntropy", SettingCategories.Advanced, SettingKind.Number, 7.9) { Min = 0, Max = 8, Step = 0.05, Order = 114, Group = "analysis" },
        new("analysis.classify.pValueLow", SettingCategories.Advanced, SettingKind.Number, 0.01) { Min = 0, Max = 0.5, Step = 0.01, Order = 115, Group = "analysis" },
        new("analysis.classify.pValueHigh", SettingCategories.Advanced, SettingKind.Number, 0.99) { Min = 0.5, Max = 1, Step = 0.01, Order = 116, Group = "analysis" },
        new("analysis.classify.compressedEntropy", SettingCategories.Advanced, SettingKind.Number, 7.2) { Min = 0, Max = 8, Step = 0.05, Order = 117, Group = "analysis" },
        // ディスク・プロセス (ENG-28 の仕様 5・6、ENG-32 の仕様 3、ENG-33 の仕様 6)。
        new(DeviceSettings.HelperTimeoutKey, SettingCategories.Advanced, SettingKind.Int, 30)
        {
            Min = 5, Max = 300, Order = 120, Group = "devices",
            Distributions = [SettingDistributions.Installer, SettingDistributions.Portable, SettingDistributions.Development],
        },
        new(DeviceSettings.HelperIdleKey, SettingCategories.Advanced, SettingKind.Int, 10)
        {
            Min = 0, Max = 60, Order = 121, Group = "devices",
            Distributions = [SettingDistributions.Installer, SettingDistributions.Portable, SettingDistributions.Development],
        },
        new(DeviceSettings.ProcessReadOnlyKey, SettingCategories.Advanced, SettingKind.Bool, true) { Order = 122, Group = "devices" },
        new(DeviceSettings.MemoryMapRefreshKey, SettingCategories.Advanced, SettingKind.Int, 5) { Min = 0, Max = 60, Order = 123, Group = "devices" },
        new(DeviceSettings.SuggestDiskImageKey, SettingCategories.Files, SettingKind.Bool, true) { Order = 11 },

        // リモートのデータソース (ENG-40〜ENG-42)。
        new(Remote.RemoteSettings.GdbTimeoutKey, SettingCategories.Files, SettingKind.Int, 5) { Min = 1, Max = 120, Order = 12 },
        new(Remote.RemoteSettings.SftpConfirmPartialWriteKey, SettingCategories.Files, SettingKind.Bool, true) { Order = 13 },
        new(Remote.RemoteSettings.UdpFollowEndKey, SettingCategories.Files, SettingKind.Bool, true) { Order = 14 },

        // カスタム CRC (ANA-20 の仕様 5)。CRC カタログの項目名の JSON。
        new("hash.customCrc", SettingCategories.Advanced, SettingKind.String, string.Empty) { Order = 103, ShowInPage = false },

        // ---- 言語 ----
        new("ui.language", SettingCategories.Language, SettingKind.Choice, "system") { Options = Languages, RequiresRestart = true, Order = 10 },

        // 翻訳者向け (UI-41 の仕様 5)。
        new("i18n.showStringKeys", SettingCategories.Language, SettingKind.Bool, false) { RequiresRestart = true, Order = 20, Group = "translators" },

        // ---- 更新 (PKG-17〜PKG-22) ----
        new("update.checkAutomatically", SettingCategories.Update, SettingKind.Bool, true) { Order = 10 },
        new("update.downloadAutomatically", SettingCategories.Update, SettingKind.Bool, true) { Order = 20, Distributions = [SettingDistributions.Installer] },
        // MSIX 版 (Store 版) は安定版だけのため、チャネルを出さない (10 の PKG-21 の仕様 3)。
        new("update.channel", SettingCategories.Update, SettingKind.Choice, "stable")
        {
            Options = ["stable", "preview"], Order = 30,
            Distributions = [SettingDistributions.Installer, SettingDistributions.Portable, SettingDistributions.Development],
        },
        new("update.skippedVersion", SettingCategories.Update, SettingKind.String, string.Empty) { Order = 40, ShowInPage = false },

        // ---- 自動化: スクリプト (08 の AUTO-01、AUTO-02、AUTO-34) ----
        // 実行時間の上限 (秒)。0 は無制限 (AUTO-02 の仕様 4)。
        new("scripts.js.timeoutSeconds", SettingCategories.Automation, SettingKind.Int, 60) { Min = 0, Max = 86400, Order = 10, Group = "scriptsJs" },
        new("scripts.js.memoryMB", SettingCategories.Automation, SettingKind.Int, 512) { Min = 16, Max = 4096, Order = 20, Group = "scriptsJs" },
        new("scripts.js.recursionDepth", SettingCategories.Automation, SettingKind.Int, 1000) { Min = 100, Max = 10000, Order = 30, Group = "scriptsJs" },
        new("scripts.maxConcurrent", SettingCategories.Automation, SettingKind.Int, 4) { Min = 1, Max = 16, Order = 40 },
        // スクリプトの作成と管理 (AUTO-19、AUTO-21、AUTO-24)。
        new("scripts.output.maxLines", SettingCategories.Automation, SettingKind.Int, 10_000) { Min = 100, Max = 1_000_000, Order = 50, Group = "scriptsOutput" },
        new("scripts.output.autoShow", SettingCategories.Automation, SettingKind.Bool, true) { Order = 60, Group = "scriptsOutput" },
        new("scripts.debug.exceptions", SettingCategories.Automation, SettingKind.Choice, "uncaught")
        {
            Options = ["none", "uncaught", "all"], Order = 70, Group = "scriptsEditor",
        },
        // スクリプト一覧に加えるフォルダ (複数。チームで共有するフォルダなど。AUTO-19 の仕様 1)。専用の区画で編集する。
        new("scripts.folders", SettingCategories.Automation, SettingKind.List, new System.Text.Json.Nodes.JsonArray()) { Order = 80, ShowInPage = false },
        // 組織向けの設定 (AUTO-34 の仕様 9)。設定ファイルに書く。設定画面では変更できない。
        new("scripts.policy", SettingCategories.Automation, SettingKind.Choice, "none")
        {
            Options = ["none", "disabled", "signedPluginsOnly"], Order = 90, ShowInPage = false, Exportable = false,
        },

        // ---- 自動化: プラグイン (08 の AUTO-33、AUTO-35) ----
        // 署名されたプラグインだけを読み込む (AUTO-35 の仕様 2。既定オフ)。反映は再起動後 (読み込みは起動時だけ)。
        new("plugins.signedOnly", SettingCategories.Automation, SettingKind.Bool, false) { Order = 100, Group = "plugins", RequiresRestart = true },
        // 開発用のフォルダ (AUTO-33。ビルドの出力先)。プラグインマネージャで編集する。
        new("plugins.devFolders", SettingCategories.Automation, SettingKind.List, new System.Text.Json.Nodes.JsonArray()) { Order = 110, ShowInPage = false, Exportable = false },

        // ---- 自動化: 外部からの自動操作 (08 の AUTO-45)。既定はオフ (仕様 3)。他の PC に持ち出さない ----
        new("automation.rpc.enabled", SettingCategories.Automation, SettingKind.Bool, false) { Order = 120, Group = "rpc", Exportable = false },
        // ---- 構造体テンプレート (07 の TPL-20、TPL-23、TPL-29、TPL-30、TPL-35) ----
        new("templates.autoApply", SettingCategories.Templates, SettingKind.Choice, "suggest") { Options = ["suggest", "always", "none"], Order = 10 },
        new("templates.autoReevaluate", SettingCategories.Templates, SettingKind.Bool, true) { Order = 20 },
        new("templates.coloring", SettingCategories.Templates, SettingKind.Bool, true) { Order = 30 },
        new("templates.coloringOpacity", SettingCategories.Templates, SettingKind.Int, 35) { Min = 5, Max = 100, Order = 40 },
        // 利用者のテンプレートのフォルダ (既定の templates/ の後に探す)・インスペクタ用テンプレート・テンプレートごとの選択は管理画面で変える。
        new("templates.folders", SettingCategories.Templates, SettingKind.List, new JsonArray()) { Order = 50, ShowInPage = false },
        new("templates.inspector", SettingCategories.Templates, SettingKind.List, new JsonArray()) { Order = 60, ShowInPage = false },
        new("templates.preferences", SettingCategories.Templates, SettingKind.String, string.Empty) { Order = 70, ShowInPage = false },
        // オンラインのテンプレート (07 の TPL-36)。リポジトリの一覧は管理画面の「オンライン」で編集する。通信の可否はプライバシーの
        // network.downloads.enabled / network.offline に従う (仕様 7)。
        new("templates.online.repositories", SettingCategories.Templates, SettingKind.List, new JsonArray()) { Order = 80, ShowInPage = false },
        new("templates.online.checkUpdates", SettingCategories.Templates, SettingKind.Bool, true) { Order = 90, Group = "templatesOnline" },
        new("templates.online.autoUpdate", SettingCategories.Templates, SettingKind.Bool, false) { Order = 100, Group = "templatesOnline" },

        // ---- 逆アセンブル (06 の ANA-26。キーと解釈は Disassembly.DisasmSettings) ----
        new(Disassembly.DisasmSettings.SyntaxKey, SettingCategories.Disassembly, SettingKind.Choice, "intel") { Options = ["intel", "att", "masm"], Order = 10 },
        new(Disassembly.DisasmSettings.HexStyleKey, SettingCategories.Disassembly, SettingKind.Choice, "prefix") { Options = ["prefix", "suffix"], Order = 20 },
        new(Disassembly.DisasmSettings.UppercaseHexKey, SettingCategories.Disassembly, SettingKind.Bool, false) { Order = 30 },
        new(Disassembly.DisasmSettings.SignedImmediatesKey, SettingCategories.Disassembly, SettingKind.Bool, false) { Order = 40 },
        new(Disassembly.DisasmSettings.UppercaseMnemonicsKey, SettingCategories.Disassembly, SettingKind.Bool, false) { Order = 50 },
        new(Disassembly.DisasmSettings.ArmRegisterAliasesKey, SettingCategories.Disassembly, SettingKind.Bool, true) { Order = 60 },
        new(Disassembly.DisasmSettings.ShowBytesKey, SettingCategories.Disassembly, SettingKind.Bool, true) { Order = 70 },
        new(Disassembly.DisasmSettings.MaxBytesKey, SettingCategories.Disassembly, SettingKind.Int, 8)
        {
            Min = Disassembly.DisasmFormat.MinBytes,
            Max = Disassembly.DisasmFormat.MaxBytesLimit,
            Order = 80,
        },
        new(Disassembly.DisasmSettings.AddressColumnKey, SettingCategories.Disassembly, SettingKind.Choice, "virtual") { Options = ["virtual", "offset", "both"], Order = 90 },
        new(Disassembly.DisasmSettings.BranchLinesKey, SettingCategories.Disassembly, SettingKind.Bool, true) { Order = 100 },
        new(Disassembly.DisasmSettings.CommentsKey, SettingCategories.Disassembly, SettingKind.Bool, true) { Order = 110 },

        // ---- プライバシー (UI-58) ----
        new("network.offline", SettingCategories.Privacy, SettingKind.Bool, false) { Order = 10 },
        // 機能ごとのスイッチは「ネットワークを使う機能」の一覧の各行に置く (UI-58 の画面。PackagingSections)。
        new("network.downloads.enabled", SettingCategories.Privacy, SettingKind.Bool, true) { Order = 20, ShowInPage = false },
        new("network.translationReport.enabled", SettingCategories.Privacy, SettingKind.Bool, true) { Order = 30, ShowInPage = false },

        // ---- Explorer 連携 (UI-35、UI-54、UI-56) ----
        new("shell.jumpList.enabled", SettingCategories.Explorer, SettingKind.Bool, true) { Order = 10 },
        // 「プログラムから開く」の拡張子は「Explorer 連携」の区画のチェックボックスで選ぶ (UI-56 の画面。インストーラ版・ポータブル版だけ)。
        new("shell.openWith.extensions", SettingCategories.Explorer, SettingKind.String, string.Empty)
        {
            Order = 20, ShowInPage = false, Distributions = [SettingDistributions.Installer, SettingDistributions.Portable],
        },

        // ---- アクセシビリティ (UI-51) ----
        new("a11y.announce.verbosity", SettingCategories.Accessibility, SettingKind.Choice, "full") { Options = ["full", "brief", "offsetOnly", "none"], Order = 10 },

        // ---- 詳細 ----
        new("log.level", SettingCategories.Advanced, SettingKind.Choice, "info") { Options = ["info", "debug"], Order = 10 },
        new("diagnostics.writeMiniDump", SettingCategories.Advanced, SettingKind.Bool, false) { Order = 20 },
        new("diagnostics.hexView.overlay", SettingCategories.Advanced, SettingKind.Bool, false) { Order = 21 },

        // 「コマンドラインから使えるようにする」(10 の PKG-08 の仕様 6)。インストーラ版だけ。値はユーザーの PATH に反映し、他の PC に
        // 持ち出さない (PATH の状態は PC ごと)。
        new("shell.commandLine.enabled", SettingCategories.Advanced, SettingKind.Bool, true) { Order = 30, Distributions = [SettingDistributions.Installer], Exportable = false },

        // アンインストールでデータフォルダも消す (10 の PKG-09 の仕様 2)。インストーラ版だけ。アンインストール前のフックが読む。
        new("uninstall.removeUserData", SettingCategories.Advanced, SettingKind.Bool, false) { Order = 40, Distributions = [SettingDistributions.Installer], Exportable = false },
    ];

    public static JsonNode? DefaultOf(string key) => All.FirstOrDefault(s => s.Key == key)?.Default?.DeepClone();
}
