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
        new("file.save", "file") { Icon = IconSave, DefaultBindings = [K("Ctrl+S")], Condition = "documentOpen && !readOnly" },
        new("file.saveAs", "file") { Icon = IconSaveAs, DefaultBindings = [K("Ctrl+Shift+S")], Condition = "documentOpen" },
        new("file.saveAll", "file") { Condition = "documentOpen" },
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
        new("search.results.export", "search") { Condition = "searchResults" },

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
        new("view.panel.bookmarks", "view"),
        new("view.panel.searchResults", "view"),
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

        // ---- 解析 ----
        new("analysis.hash", "analysis") { Condition = "documentOpen" },

        // ハッシュパネルの照合とコピー (ANA-21、ANA-22 の「呼び出し」)。「一致するアルゴリズムを探す」と「カーソル位置に書き込む」はフェーズ 2 (F2-14)。
        new("analysis.hash.verify", "analysis") { Condition = "documentOpen" },
        new("analysis.hash.verifyFile", "analysis") { Condition = "documentOpen" },
        new("analysis.hash.copy", "analysis") { Condition = "documentOpen" },

        // ---- データインスペクタ (INSP-02 の「呼び出し」: インスペクタ: エンディアンの切り替え) ----
        new("inspector.toggleEndian", "inspector") { Condition = "documentOpen" },

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
