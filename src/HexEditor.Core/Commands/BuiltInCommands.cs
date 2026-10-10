namespace HexEditor.Core.Commands;

/// <summary>
/// 組み込みのコマンドの一覧 (UI-16)。メニュー (00-overview.md 7 章) の順に並べる。既定のショートカットは 00-overview.md 8 章の表と
/// 一致させる (TC-UI-39-03 が突き合わせる)。
/// </summary>
/// <remarks>
/// 新しいコマンドを足すときは、ここに 1 行足し、<c>Strings/en</c> と <c>Strings/ja</c> に表示名 (<c>Cmd_&lt;ID の . を _ に&gt;</c>) と
/// 検索用の別名 (<c>CmdAlias_…</c>。<c>ja</c> は読みのひらがな) を書き、App で処理を登録する (<c>CommandHost.Register</c>)。
/// パネルの表示切り替えのコマンドは <c>view.panel.&lt;パネル ID&gt;</c> とし、App のパネルの登録が処理を自動でつなぐ。
/// </remarks>
public static class BuiltInCommands
{
    // Segoe Fluent Icons の文字。
    private const string IconNew = "", IconOpen = "", IconSave = "", IconSaveAs = "", IconUndo = "",
        IconRedo = "", IconCut = "", IconCopy = "", IconPaste = "", IconFind = "", IconGoTo = "",
        IconInsert = "", IconInspector = "", IconSettings = "", IconBack = "", IconForward = "",
        IconClose = "", IconKeyboard = "", IconHelp = "", IconPalette = "";

    public static IReadOnlyList<CommandDefinition> All { get; } =
    [
        // ---- ファイル ----
        new("file.new", "file") { Icon = IconNew, DefaultBindings = [K("Ctrl+N")] },
        new("file.newWithSize", "file"),
        new("file.open", "file") { Icon = IconOpen, DefaultBindings = [K("Ctrl+O")] },
        new("file.openReadOnly", "file"),
        new("file.openAdvanced", "file"),
        new("file.openRange", "file"),
        new("file.openAsIntelHex", "file"),
        new("file.openAsSRecord", "file"),
        new("file.openAsBase64", "file"),
        new("file.openSelectionInNewTab", "file") { Condition = "documentOpen" },
        new("file.openSelectionAsCopy", "file") { Condition = "documentOpen" },
        new("file.save", "file") { Icon = IconSave, DefaultBindings = [K("Ctrl+S")], Condition = "documentOpen && !readOnly" },
        new("file.saveAs", "file") { Icon = IconSaveAs, DefaultBindings = [K("Ctrl+Shift+S")], Condition = "documentOpen" },
        new("file.saveAll", "file") { Condition = "documentOpen" },
        new("file.formatSettings", "file") { Condition = "documentOpen" },
        new("file.import", "file"),
        new("file.export", "file") { Condition = "documentOpen" },
        new("file.exportSelection", "file") { Condition = "documentOpen" },
        new("file.saveSelection", "file") { Condition = "documentOpen" },
        new("workspace.save", "file") { Condition = "documentOpen" },
        new("workspace.open", "file"),
        new("file.reload", "file") { DefaultBindings = [K("Ctrl+R")], Condition = "documentOpen" },
        new("file.discardReload", "file") { Condition = "documentOpen" },
        new("file.reopenClosed", "file") { DefaultBindings = [K("Ctrl+Shift+T")] },
        new("file.recent.clear", "file"),
        new("file.recent.showAll", "file"),
        new("file.close", "file") { Icon = IconClose, DefaultBindings = [K("Ctrl+W"), K("Ctrl+F4")], Condition = "documentOpen" },
        new("file.closeAll", "file") { Condition = "documentOpen" },
        new("file.closeOthers", "file") { Condition = "documentOpen" },
        new("file.closeToRight", "file") { Condition = "documentOpen" },
        new("file.closeSaved", "file") { Condition = "documentOpen" },
        new("file.exit", "file"),

        // ---- 編集 ----
        new("edit.undo", "edit") { Icon = IconUndo, DefaultBindings = [K("Ctrl+Z")], Condition = "documentOpen && !readOnly && canUndo" },
        new("edit.redo", "edit") { Icon = IconRedo, DefaultBindings = [K("Ctrl+Y"), K("Ctrl+Shift+Z")], Condition = "documentOpen && !readOnly && canRedo" },

        // Hex ビューが自分で処理するキー (切り取り・コピー・貼り付けなど) は NativeScopes に Editor を書く。
        new("edit.cut", "edit")
        {
            Icon = IconCut,
            DefaultBindings = [K("Ctrl+X", KeyScope.Editor), K("Shift+Delete", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen && !readOnly && hasSelection",
        },
        new("edit.copy", "edit")
        {
            Icon = IconCopy,
            DefaultBindings = [K("Ctrl+C", KeyScope.Editor), K("Ctrl+Insert", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen && hasSelection",
        },
        new("edit.paste", "edit")
        {
            Icon = IconPaste,
            DefaultBindings = [K("Ctrl+V", KeyScope.Editor), K("Shift+Insert", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen && !readOnly",
        },
        new("edit.pasteOverwrite", "edit")
        {
            DefaultBindings = [K("Ctrl+B", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen && !readOnly",
        },
        new("edit.copyAs", "edit") { DefaultBindings = [K("Ctrl+Shift+C", KeyScope.Editor)], Condition = "documentOpen" },
        new("edit.copyAsLast", "edit") { Condition = "documentOpen" },

        // 形式ごとの「形式を選択してコピー: <形式名>」(EDIT-25 の「呼び出し」。ダイアログを開かずに、前回の設定でその形式でコピーする)。
        .. CopyAsFormatCommands(),
        new("edit.pasteSpecial", "edit") { DefaultBindings = [K("Ctrl+Shift+V", KeyScope.Editor)], Condition = "documentOpen && !readOnly" },
        new("edit.selectAll", "edit")
        {
            DefaultBindings = [K("Ctrl+A", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen",
        },
        new("edit.selectRange", "edit") { DefaultBindings = [K("Ctrl+E", KeyScope.Editor)], Condition = "documentOpen" },
        new("edit.insertBytes", "edit") { Condition = "documentOpen && !readOnly && canResize" },
        new("edit.insertFile", "edit") { Condition = "documentOpen && !readOnly" },
        new("edit.fill", "edit") { Condition = "documentOpen && !readOnly" },
        new("edit.resize", "edit") { Condition = "documentOpen && !readOnly && canResize" },
        new("edit.truncate", "edit") { Condition = "documentOpen && !readOnly && canResize" },
        new("edit.toggleInsert", "edit")
        {
            Icon = IconInsert,
            DefaultBindings = [K("Insert", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen && !readOnly && canResize",
        },
        new("edit.readOnly", "edit") { Condition = "documentOpen" },

        // 選択 (EDIT-05〜EDIT-09、EDIT-17)。ショートカットは既定なし (カーソルの追加の Ctrl+Alt+↑ / ↓ は Hex ビューが処理する)。
        new("edit.selection.shiftNext", "edit") { Condition = "documentOpen && hasSelection" },
        new("edit.selection.shiftPrevious", "edit") { Condition = "documentOpen && hasSelection" },
        new("edit.selection.shiftBy", "edit") { Condition = "documentOpen && hasSelection" },
        new("edit.selection.resize", "edit") { Condition = "documentOpen && hasSelection" },
        new("edit.selection.swapEnds", "edit") { Condition = "documentOpen && hasSelection" },
        new("edit.selection.invert", "edit") { Condition = "documentOpen" },
        new("edit.selection.nextElement", "edit") { Condition = "documentOpen && hasSelection" },
        new("edit.selection.previousElement", "edit") { Condition = "documentOpen && hasSelection" },
        new("edit.selection.startRectangle", "edit") { Condition = "documentOpen" },
        new("edit.selection.toMulti", "edit") { Condition = "documentOpen && hasSelection" },
        new("edit.selection.save", "edit") { Condition = "documentOpen && hasSelection" },
        new("edit.selection.load", "edit") { Condition = "documentOpen" },
        new("edit.selection.export", "edit") { Condition = "documentOpen && hasSelection" },
        new("edit.selection.import", "edit") { Condition = "documentOpen" },
        new("edit.caret.addAbove", "edit")
        {
            DefaultBindings = [K("Ctrl+Alt+Up", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen",
        },
        new("edit.caret.addBelow", "edit")
        {
            DefaultBindings = [K("Ctrl+Alt+Down", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen",
        },
        new("edit.caret.atElements", "edit") { Condition = "documentOpen && hasSelection" },
        new("edit.insertRectangle", "edit") { Condition = "documentOpen && !readOnly && canResize" },

        // ユーザークリップボードとクリップボード履歴 (EDIT-28)。
        .. Enumerable.Range(1, 9).Select(n => new CommandDefinition($"edit.userClipboard.copy{n}", "edit") { Condition = "documentOpen && hasSelection" }),
        .. Enumerable.Range(1, 9).Select(n => new CommandDefinition($"edit.userClipboard.paste{n}", "edit") { Condition = "documentOpen && !readOnly" }),
        new("edit.clipboardHistory.paste", "edit") { Condition = "documentOpen && !readOnly" },

        // ---- 検索 ----
        new("search.find", "search") { Icon = IconFind, DefaultBindings = [K("Ctrl+F")], Condition = "documentOpen" },
        new("search.findNext", "search") { DefaultBindings = [K("F3")], Condition = "documentOpen" },
        new("search.findPrevious", "search") { DefaultBindings = [K("Shift+F3")], Condition = "documentOpen" },
        new("search.replace", "search") { DefaultBindings = [K("Ctrl+H")], Condition = "documentOpen" },

        // 検索バーでは検索バー自身が Alt+Enter を処理する (FIND-20)。
        new("search.findAll", "search") { DefaultBindings = [K("Alt+Enter", KeyScope.FindBar)], NativeScopes = [KeyScope.FindBar], Condition = "documentOpen" },
        new("search.clearHistory", "search"),

        // すべて置換 (FIND-23)、結果一覧の変換・エクスポート (FIND-21)。ショートカットは既定なし。
        new("search.replaceAll", "search") { Condition = "documentOpen && !readOnly" },
        new("search.results.toBookmarks", "search") { Condition = "searchResults" },
        new("search.results.toSelection", "search") { Condition = "searchResults" },
        new("search.results.export", "search") { Condition = "searchResults" },

        // フェーズ 2 の検索 (FIND-08〜FIND-32)。複数ファイル検索は 00-overview 8.2 の Ctrl+Shift+F。
        new("search.multiFile", "search") { DefaultBindings = [K("Ctrl+Shift+F")] },
        new("search.multiFileReplace", "search"),
        new("search.strings", "search") { Condition = "documentOpen" },
        new("search.multiTerm", "search") { Condition = "documentOpen" },
        new("search.mismatch", "search") { Condition = "documentOpen" },

        // ---- 移動 ----
        new("go.goTo", "go") { Icon = IconGoTo, DefaultBindings = [K("Ctrl+G")], Condition = "documentOpen" },
        new("go.nextNibble", "go") { Condition = "documentOpen" },
        new("go.previousNibble", "go") { Condition = "documentOpen" },
        new("go.toggleColumn", "go")
        {
            // Hex ビューが自分で処理する (VIEW-27 の仕様 1。列が 2 つなので順と逆は同じ)。
            DefaultBindings = [K("Tab", KeyScope.Editor), K("Shift+Tab", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen",
        },
        new("go.previousGroup", "go")
        {
            // VIEW-25 の仕様 8。Hex ビューが自分で処理する (Shift を足すと選択範囲を広げる)。
            DefaultBindings = [K("Ctrl+Left", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen",
        },
        new("go.nextGroup", "go")
        {
            DefaultBindings = [K("Ctrl+Right", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen",
        },
        new("view.scrollLineUp", "view")
        {
            // VIEW-25 の仕様 8: カーソルを動かさずに表示だけを 1 行動かす。
            DefaultBindings = [K("Ctrl+Up", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen",
        },
        new("view.scrollLineDown", "view")
        {
            DefaultBindings = [K("Ctrl+Down", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen",
        },
        new("go.back", "go") { Icon = IconBack, DefaultBindings = [K("Alt+Left")], Condition = "documentOpen && canGoBack" },
        new("go.forward", "go") { Icon = IconForward, DefaultBindings = [K("Alt+Right")], Condition = "documentOpen && canGoForward" },
        new("go.history", "go") { Condition = "documentOpen && canGoBack" },
        new("go.start", "go") { Condition = "documentOpen" },
        new("go.end", "go") { Condition = "documentOpen" },

        // ブックマーク (INSP-23〜INSP-26)。番号付きは数字キーの段 (テンキーも KeyDispatcher が同じに扱う。00-overview.md 8.7)。
        new("go.bookmark.toggle", "go") { DefaultBindings = [K("Ctrl+F2")], Condition = "documentOpen" },
        new("go.bookmark.next", "go") { DefaultBindings = [K("F2")], Condition = "documentOpen" },
        new("go.bookmark.previous", "go") { DefaultBindings = [K("Shift+F2")], Condition = "documentOpen" },
        new("go.bookmark.edit", "go") { Condition = "documentOpen" },
        new("go.bookmark.list", "go") { Condition = "documentOpen" },
        .. Enumerable.Range(1, 9).Select(n => new CommandDefinition($"go.bookmark.set{n}", "go") { DefaultBindings = [K($"Ctrl+Shift+{n}")], Condition = "documentOpen" }),
        .. Enumerable.Range(1, 9).Select(n => new CommandDefinition($"go.bookmark.goto{n}", "go") { DefaultBindings = [K($"Ctrl+{n}")], Condition = "documentOpen" }),

        // ---- 表示 ----
        new("view.encoding.ascii", "view") { Condition = "documentOpen" },
        new("view.encoding.ansi", "view") { Condition = "documentOpen" },

        // 表示設定 (VIEW-05〜VIEW-09、VIEW-12〜VIEW-16、VIEW-19〜VIEW-22、VIEW-42、UI-28、UI-51)。
        new("view.offsetColumn", "view") { Condition = "documentOpen" },
        new("view.hexColumn", "view") { Condition = "documentOpen" },
        new("view.textColumn", "view") { Condition = "documentOpen" },
        new("view.hexOnly", "view") { Condition = "documentOpen" },
        new("view.textOnly", "view") { Condition = "documentOpen" },
        new("view.allColumns", "view") { Condition = "documentOpen" },
        new("view.ruler", "view") { Condition = "documentOpen" },
        new("view.currentRow", "view") { Condition = "documentOpen" },
        new("view.lowercase", "view") { Condition = "documentOpen" },
        new("view.dimZeros", "view") { Condition = "documentOpen" },
        new("view.alternate", "view") { Condition = "documentOpen" },
        new("view.modified", "view") { Condition = "documentOpen" },
        new("view.bytesPerRow8", "view") { Condition = "documentOpen" },
        new("view.bytesPerRow16", "view") { Condition = "documentOpen" },
        new("view.bytesPerRow32", "view") { Condition = "documentOpen" },
        new("view.bytesPerRow64", "view") { Condition = "documentOpen" },
        new("view.bytesPerRowAuto", "view") { Condition = "documentOpen" },
        new("view.bytesPerRowCustom", "view") { Condition = "documentOpen", Argument = new("expression") },
        new("view.group1", "view") { Condition = "documentOpen" },
        new("view.group2", "view") { Condition = "documentOpen" },
        new("view.group4", "view") { Condition = "documentOpen" },
        new("view.group8", "view") { Condition = "documentOpen" },
        new("view.group16", "view") { Condition = "documentOpen" },
        new("view.radixHex", "view") { Condition = "documentOpen" },
        new("view.radixDecimal", "view") { Condition = "documentOpen" },
        new("view.radixOctal", "view") { Condition = "documentOpen" },
        new("view.radixSector", "view") { Condition = "documentOpen" },
        new("view.baseAddress", "view") { Condition = "documentOpen" },
        new("view.middleSeparator", "view") { Condition = "documentOpen" },
        new("view.alternateText", "view") { Condition = "documentOpen" },
        new("view.autoPowerOfTwo", "view") { Condition = "documentOpen" },
        new("view.hexDigitSeparator", "view") { Condition = "documentOpen" },
        new("view.alignRows", "view") { Condition = "documentOpen" },
        new("view.continuation", "view") { Condition = "documentOpen" },
        new("view.sectorSize512", "view") { Condition = "documentOpen" },
        new("view.sectorSize1024", "view") { Condition = "documentOpen" },
        new("view.sectorSize2048", "view") { Condition = "documentOpen" },
        new("view.sectorSize4096", "view") { Condition = "documentOpen" },
        new("view.sectorSizeCustom", "view") { Condition = "documentOpen" },
        new("view.rowShift", "view") { Condition = "documentOpen" },
        new("view.setReference", "view") { Condition = "documentOpen" },
        new("view.clearReference", "view") { Condition = "documentOpen" },
        new("view.saveDefault", "view") { Condition = "documentOpen" },
        new("view.resetDefault", "view") { Condition = "documentOpen" },
        new("view.announcePosition", "accessibility") { Condition = "documentOpen" },
        new("view.utf16Odd", "view") { Condition = "documentOpen" },
        .. Enumerable.Range(0, 4).Select(p => new CommandDefinition($"view.utf32Phase{p}", "view") { Condition = "documentOpen" }),
        new("view.encoding.utf8", "view") { Condition = "documentOpen" },
        new("view.encoding.utf16le", "view") { Condition = "documentOpen" },
        new("view.encoding.utf16be", "view") { Condition = "documentOpen" },
        new("view.encoding.utf32le", "view") { Condition = "documentOpen" },
        new("view.encoding.utf32be", "view") { Condition = "documentOpen" },
        new("view.encoding.shiftJis", "view") { Condition = "documentOpen" },
        new("view.encoding.select", "view") { Condition = "documentOpen", Argument = new() },
        new("view.colorScheme", "view") { Argument = new() },
        new("view.toolbar", "view"),
        new("view.statusBar", "view"),
        new("view.leftPanel", "view"),
        new("view.rightPanel", "view"),
        new("view.bottomPanel", "view"),
        new("view.resetPanelLayout", "view"),
        new("view.customizeToolbar", "view"),
        new("view.panel.inspector", "view") { Icon = IconInspector, DefaultBindings = [K("Ctrl+Shift+I")] },
        new("view.panel.hash", "view"),
        new("view.panel.statistics", "view"),
        new("view.panel.fileType", "view"),
        new("view.panel.bookmarks", "view"),
        new("view.panel.searchResults", "view"),
        new("view.panel.history", "view"),
        new("view.panel.clipboard", "view"),
        new("view.panel.formatIssues", "view"),
        new("view.panel.strings", "view"),
        new("view.panel.multiFileSearch", "view"),
        new("view.panel.diffs", "view"),
        new("view.nextRegion", "view") { DefaultBindings = [K("F6")], NativeScopes = [KeyScope.Editor] },
        new("view.previousRegion", "view") { DefaultBindings = [K("Shift+F6")], NativeScopes = [KeyScope.Editor] },

        // ズーム (UI-08)。記号のキーは配列によって押せないので、メニューとコマンドパレットからも実行できる (仕様 6)。
        new("view.zoomIn", "view") { DefaultBindings = [K("Ctrl+OemPlus")] },
        new("view.zoomOut", "view") { DefaultBindings = [K("Ctrl+OemMinus")] },
        new("view.zoomReset", "view") { DefaultBindings = [K("Ctrl+0")] },
        new("view.uiZoomIn", "view"),
        new("view.uiZoomOut", "view"),
        new("view.uiZoomReset", "view"),

        // 全画面表示 (UI-07)。
        new("view.fullScreen", "view") { DefaultBindings = [K("F11")] },
        new("view.theme.system", "view"),
        new("view.theme.light", "view"),
        new("view.theme.dark", "view"),

        // ---- 表示 (フェーズ 2: VIEW-10・VIEW-11・VIEW-17・VIEW-18・VIEW-23・VIEW-24・VIEW-33・VIEW-35・VIEW-37〜VIEW-39) ----
        .. Enum.GetNames<View.CellFormat>().Select(f => new CommandDefinition($"view.cellFormat{f}", "view") { Condition = "documentOpen" }),
        new("view.spacePadding", "view") { Condition = "documentOpen" },
        new("view.endianLittle", "view") { Condition = "documentOpen" },
        new("view.endianBig", "view") { Condition = "documentOpen" },
        new("view.endianToggle", "view") { Condition = "documentOpen" },
        new("view.reverseGroups", "view") { Condition = "documentOpen" },
        new("view.addTextColumn", "view") { Condition = "documentOpen" },
        new("view.removeTextColumn", "view") { Condition = "documentOpen" },
        new("view.byteThemeNone", "view"),
        new("view.byteThemeCategory", "view"),
        new("view.byteThemeGradient", "view"),
        new("view.byteThemeCustom", "view"),
        new("view.encoding.loadTable", "view") { Condition = "documentOpen" },
        new("view.records", "view") { Condition = "documentOpen" },
        new("view.recordSettings", "view") { Condition = "documentOpen" },
        new("view.recordPerRow", "view") { Condition = "documentOpen" },
        new("view.recordNumbers", "view") { Condition = "documentOpen" },
        new("go.nextRecord", "go") { Condition = "documentOpen" },
        new("go.previousRecord", "go") { Condition = "documentOpen" },
        new("view.separatorNone", "view") { Condition = "documentOpen" },
        new("view.separatorSector", "view") { Condition = "documentOpen" },
        new("view.separatorPage", "view") { Condition = "documentOpen" },
        new("view.separatorCustom", "view") { Condition = "documentOpen" },
        new("view.separatorLabels", "view") { Condition = "documentOpen" },
        new("view.pageView", "view") { Condition = "documentOpen" },
        new("go.nextPage", "go") { Condition = "documentOpen" },
        new("go.previousPage", "go") { Condition = "documentOpen" },
        new("view.minimap", "view"),
        .. Enum.GetNames<View.MinimapContent>().Select(c => new CommandDefinition($"view.minimap{c}", "view")),
        new("view.minimapWhole", "view"),
        new("view.minimapAround", "view"),
        new("view.minimapExact", "view") { Condition = "documentOpen" },
        new("view.split", "view") { DefaultBindings = [K("Ctrl+Oem5")], Condition = "documentOpen" },
        new("view.splitHorizontal", "view") { Condition = "documentOpen" },
        new("view.splitVertical", "view") { Condition = "documentOpen" },
        new("view.splitRemove", "view") { Condition = "documentOpen" },
        new("view.splitSync", "view") { Condition = "documentOpen" },
        new("view.nextPane", "view") { Condition = "documentOpen" },
        new("view.newView", "view") { Condition = "documentOpen" },
        new("view.sideBySide", "view") { Condition = "documentOpen", Argument = new() },
        new("view.syncOff", "view") { Condition = "documentOpen" },
        new("view.syncSameOffset", "view") { Condition = "documentOpen" },
        new("view.syncKeepDifference", "view") { Condition = "documentOpen" },
        new("view.syncToggle", "view") { Condition = "documentOpen" },
        new("view.syncDifferences", "view") { Condition = "documentOpen" },
        // ---- データ (EDIT-31〜EDIT-39)。ショートカットは既定なし ----
        new("data.operation", "data") { Condition = "documentOpen && !readOnly" },
        new("data.repeatOperation", "data") { Condition = "documentOpen && !readOnly" },

        // コマンドパレット「データ演算: <演算名>」(EDIT-31 の「呼び出し」)。ID は data.op.<演算名の先頭を小文字にしたもの>。
        .. Enum.GetValues<Editing.Transforms.DataOperationKind>().Select(k =>
            new CommandDefinition("data.op." + char.ToLowerInvariant(k.ToString()[0]) + k.ToString()[1..], "dataOperation")
            {
                Condition = "documentOpen && !readOnly",
            }),
        new("data.convertEncoding", "data") { Condition = "documentOpen && !readOnly" },
        new("data.case.upper", "data") { Condition = "documentOpen && !readOnly" },
        new("data.case.lower", "data") { Condition = "documentOpen && !readOnly" },
        new("data.case.swap", "data") { Condition = "documentOpen && !readOnly" },

        // 変換方法ごとのコマンド (EDIT-39 の「画面」)。
        .. new[] { "upper", "lower", "swap" }.SelectMany(op => new[] { "ascii", "text" }.Select(method =>
            new CommandDefinition($"data.case.{op}.{method}", "data") { Condition = "documentOpen && !readOnly" })),

        // ---- 解析 ----
        new("analysis.hash", "analysis") { Condition = "documentOpen" },

        // ハッシュパネルの照合とコピー (ANA-21、ANA-22 の「呼び出し」)。
        new("analysis.hash.verify", "analysis") { Condition = "documentOpen" },
        new("analysis.hash.verifyFile", "analysis") { Condition = "documentOpen" },
        new("analysis.hash.copy", "analysis") { Condition = "documentOpen" },

        // ---- 比較 (ANA-01〜ANA-08)。次 / 前の差分は 00-overview.md 8.2 のグローバルのキー ----
        new("analysis.compare", "compare"),
        new("analysis.compareSaved", "compare") { Condition = "documentOpen" },
        new("compare.nextDiff", "compare") { DefaultBindings = [K("Alt+F5")] },
        new("compare.previousDiff", "compare") { DefaultBindings = [K("Shift+Alt+F5")] },
        new("compare.recompare", "compare"),
        new("compare.syncScroll", "compare"),
        new("compare.layout", "compare"),
        new("compare.copyRight", "compare"),
        new("compare.copyLeft", "compare"),
        new("compare.copyAllRight", "compare"),
        new("compare.copyAllLeft", "compare"),
        new("compare.showDiffList", "compare"),
        new("compare.saveReport", "compare"),

        // 統計 (ANA-10〜ANA-16) とファイル形式の判定 (ANA-17)。ショートカットは既定なし。
        new("analysis.statistics", "analysis") { Condition = "documentOpen" },
        new("analysis.descriptive", "analysis") { Condition = "documentOpen" },
        new("analysis.entropy", "analysis") { Condition = "documentOpen" },
        new("analysis.entropyGraph", "analysis") { Condition = "documentOpen" },
        new("analysis.digram", "analysis") { Condition = "documentOpen" },
        new("analysis.byteDistribution", "analysis") { Condition = "documentOpen" },
        new("analysis.patterns", "analysis") { Condition = "documentOpen" },
        new("analysis.classify", "analysis") { Condition = "documentOpen" },
        new("analysis.fileType", "analysis") { Condition = "documentOpen" },
        new("analysis.fileType.here", "analysis") { Condition = "documentOpen" },
        new("analysis.fileType.embedded", "analysis") { Condition = "documentOpen" },
        // カスタム CRC (ANA-20)、一致するアルゴリズムを探す (ANA-21 の仕様 5)、カーソル位置に書き込む (ANA-22 の仕様 3)。
        new("analysis.hash.customCrc", "analysis"),
        new("analysis.hash.findAlgorithm", "analysis") { Condition = "documentOpen" },
        new("analysis.hash.writeAtCursor", "analysis") { Condition = "documentOpen && !readOnly" },

        // ---- データインスペクタ (INSP-02 の「呼び出し」: インスペクタ: エンディアンの切り替え) ----
        new("inspector.toggleEndian", "inspector") { Condition = "documentOpen" },

        // ---- ブックマークのグループ・変換・インポート / エクスポート、位置マネージャ、注釈、色付けルール (INSP-27〜INSP-34) ----
        new("edit.selectionToBookmarks", "edit") { Condition = "documentOpen" },
        new("go.bookmark.toSelection", "go") { Condition = "documentOpen" },
        new("go.bookmark.showDescription", "go") { Condition = "documentOpen" },
        new("file.importBookmarks", "file") { Condition = "documentOpen" },
        new("file.exportBookmarks", "file") { Condition = "documentOpen" },
        new("view.panel.positionManager", "view"),
        new("view.panel.coloringRules", "view"),
        new("view.panel.legend", "view"),
        new("view.annotations.toggle", "view"),
        new("view.annotations.column", "view"),
        new("view.annotations.bookmark", "view"),
        new("view.annotations.yara", "view"),
        new("view.annotations.searchResults", "view"),
        new("view.annotations.template", "view"),
        new("view.annotations.script", "view"),
        new("view.coloring.fromSelection", "view") { Condition = "documentOpen" },

        // ---- タブ (UI-09〜UI-11) ----
        new("tab.next", "tab") { DefaultBindings = [K("Ctrl+Tab")] },
        new("tab.previous", "tab") { DefaultBindings = [K("Ctrl+Shift+Tab")] },
        new("tab.nextInOrder", "tab") { DefaultBindings = [K("Ctrl+PageDown")] },
        new("tab.previousInOrder", "tab") { DefaultBindings = [K("Ctrl+PageUp")] },
        new("tab.togglePin", "tab") { Condition = "documentOpen" },
        new("tab.moveLeft", "tab") { Condition = "documentOpen" },
        new("tab.moveRight", "tab") { Condition = "documentOpen" },
        new("tab.moveToNewWindow", "tab") { Condition = "documentOpen" },

        // ---- ウィンドウ (UI-14) ----
        new("window.new", "window") { DefaultBindings = [K("Ctrl+Shift+N")] },
        new("window.next", "window"),
        new("window.restoreSession", "window"),

        // ---- ツール・設定 ----
        new("settings.open", "settings") { Icon = IconSettings, DefaultBindings = [K("Ctrl+OemComma")] },
        new("settings.keyboard", "settings") { Icon = IconKeyboard },
        new("settings.selectPreset", "settings"),
        new("settings.changeLanguage", "settings"),
        new("settings.exportKeybindings", "settings"),
        new("settings.importKeybindings", "settings"),
        new("settings.export", "settings"),
        new("settings.import", "settings"),
        new("settings.resetAll", "settings"),
        new("settings.openFile", "settings"),
        new("settings.openDataFolder", "settings"),

        // ---- ヘルプ ----
        new("help.commandPalette", "help") { Icon = IconPalette, DefaultBindings = [K("Ctrl+Shift+P"), K("F1")] },
        new("help.shortcuts", "help") { Icon = IconKeyboard },
        new("help.documentation", "help") { Icon = IconHelp },
        new("help.reportProblem", "help"),
        new("help.reportTranslation", "help"),
        new("help.checkForUpdates", "help"),
        new("help.about", "help"),
    ];

    private static KeyBinding K(string chord, KeyScope scope = KeyScope.Global) => KeyBinding.Parse(chord, scope);

    /// <summary>形式ごとのコピーのコマンドの ID の接頭辞 (<c>edit.copyAs.hexSpaced</c> など)。</summary>
    public const string CopyAsFormatPrefix = "edit.copyAs.";

    /// <summary>形式ごとのコピーのコマンドの ID。</summary>
    public static string CopyAsFormatId(Clipboard.CopyFormat format)
    {
        string name = format.ToString();
        return CopyAsFormatPrefix + char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static IEnumerable<CommandDefinition> CopyAsFormatCommands() =>
        Enum.GetValues<Clipboard.CopyFormat>().Select(f => new CommandDefinition(CopyAsFormatId(f), "edit") { Condition = "documentOpen" });
}
