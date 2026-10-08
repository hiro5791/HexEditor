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
        new("file.save", "file") { Icon = IconSave, DefaultBindings = [K("Ctrl+S")], Condition = "documentOpen" },
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
        new("edit.selectAll", "edit")
        {
            DefaultBindings = [K("Ctrl+A", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen",
        },
        new("edit.toggleInsert", "edit")
        {
            Icon = IconInsert,
            DefaultBindings = [K("Insert", KeyScope.Editor)],
            NativeScopes = [KeyScope.Editor],
            Condition = "documentOpen && !readOnly && canResize",
        },

        // ---- 検索 ----
        new("search.find", "search") { Icon = IconFind, DefaultBindings = [K("Ctrl+F")], Condition = "documentOpen" },
        new("search.findNext", "search") { DefaultBindings = [K("F3")], Condition = "documentOpen" },
        new("search.findPrevious", "search") { DefaultBindings = [K("Shift+F3")], Condition = "documentOpen" },

        // ---- 移動 ----
        new("go.goTo", "go") { Icon = IconGoTo, DefaultBindings = [K("Ctrl+G")], Condition = "documentOpen" },
        new("go.back", "go") { Icon = IconBack, DefaultBindings = [K("Alt+Left")], Condition = "documentOpen && canGoBack" },
        new("go.forward", "go") { Icon = IconForward, DefaultBindings = [K("Alt+Right")], Condition = "documentOpen && canGoForward" },
        new("go.start", "go") { Condition = "documentOpen" },
        new("go.end", "go") { Condition = "documentOpen" },

        // ---- 表示 ----
        new("view.encoding.ascii", "view") { Condition = "documentOpen" },
        new("view.encoding.ansi", "view") { Condition = "documentOpen" },
        new("view.toolbar", "view"),
        new("view.statusBar", "view"),
        new("view.leftPanel", "view"),
        new("view.rightPanel", "view"),
        new("view.bottomPanel", "view"),
        new("view.resetPanelLayout", "view"),
        new("view.customizeToolbar", "view"),
        new("view.panel.inspector", "view") { Icon = IconInspector, DefaultBindings = [K("Ctrl+Shift+I")] },
        new("view.panel.hash", "view"),
        new("view.nextRegion", "view") { DefaultBindings = [K("F6")], NativeScopes = [KeyScope.Editor] },
        new("view.previousRegion", "view") { DefaultBindings = [K("Shift+F6")], NativeScopes = [KeyScope.Editor] },
        new("view.theme.system", "view"),
        new("view.theme.light", "view"),
        new("view.theme.dark", "view"),

        // ---- 解析 ----
        new("analysis.hash", "analysis") { Condition = "documentOpen" },

        // ---- ウィンドウ ----
        new("window.restoreSession", "window"),

        // ---- ツール・設定 ----
        new("settings.open", "settings") { Icon = IconSettings, DefaultBindings = [K("Ctrl+OemComma")] },
        new("settings.keyboard", "settings") { Icon = IconKeyboard },
        new("settings.selectPreset", "settings"),
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
        new("help.about", "help"),
    ];

    private static KeyBinding K(string chord, KeyScope scope = KeyScope.Global) => KeyBinding.Parse(chord, scope);
}
