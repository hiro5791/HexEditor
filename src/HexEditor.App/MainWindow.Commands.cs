using System.Runtime.InteropServices;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Commands;
using HexEditor.Core.View;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace HexEditor.App;

/// <summary>
/// コマンド (UI-16): このウィンドウでのコマンドの処理の登録、メニューとのつなぎ、キーの振り分け (UI-18、UI-20)。
/// </summary>
/// <remarks>
/// 新しいコマンドの処理は <see cref="RegisterCommandHandlers"/> に足す (または自分の partial ファイルから
/// <c>Commands.Register(id, …)</c> を呼ぶメソッドを作り、ここから呼ぶ)。
/// </remarks>
public sealed partial class MainWindow
{
    private MenuBinder _menus = null!;
    private KeyDispatcher _keys = null!;

    /// <summary>このウィンドウのコマンドの処理。</summary>
    public CommandHost Commands { get; } = new();

    private void InitializeCommands()
    {
        Commands.ShowError = message => ShowNotice(message, InfoBarSeverity.Error);
        RegisterCommandHandlers();
        _menus = new MenuBinder(Commands);
        foreach (MenuBarItem menu in MainMenu.Items)
        {
            _menus.Bind(menu.Items);
        }

        if (Toolbar.ContextFlyout is MenuFlyout toolbarMenu)
        {
            _menus.Bind(toolbarMenu.Items);
        }

        _menus.RefreshShortcuts();
        _keys = new KeyDispatcher(Commands);

        // ウィンドウのルートで、フォーカスのある部品より先にキーを受け取る。
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Root_PreviewKeyDown), handledEventsToo: false);

        Action bindingsChanged = () => DispatcherQueue.TryEnqueue(() =>
        {
            _menus.RefreshShortcuts();
            UpdatePackagingMenu();
            RefreshToolbar();
            RefreshShortcutsPage();
        });
        CommandService.BindingsChanged += bindingsChanged;
        Closed += (_, _) => CommandService.BindingsChanged -= bindingsChanged;
        Commands.StatesChanged += UpdateCommandStates;

        // キーボード配列が変わったらキーの表示を作り直す (UI-20 の仕様 2)。
        Activated += (_, _) => KeyboardLayout.Refresh();
        HookInputLanguageChange();
    }

    /// <summary>使えない理由 (UI-16 の「エラー」、UI-17 の仕様 5)。</summary>
    private CommandState NeedsDocument() =>
        Vm.Selected is null ? CommandState.Unavailable(Loc.Get("Command_NoDocument")) : CommandState.Available;

    private CommandState NeedsEditable(Func<DocumentViewModel, string?>? more = null)
    {
        if (Vm.Selected is not { } doc)
        {
            return CommandState.Unavailable(Loc.Get("Command_NoDocument"));
        }

        if (doc.Editor.ReadOnly)
        {
            return CommandState.Unavailable(Loc.Get("Command_ReadOnly"));
        }

        return more?.Invoke(doc) is { } reason ? CommandState.Unavailable(reason) : CommandState.Available;
    }

    private CommandState NeedsDocument(Func<DocumentViewModel, string?> more) =>
        Vm.Selected is not { } doc ? CommandState.Unavailable(Loc.Get("Command_NoDocument"))
        : more(doc) is { } reason ? CommandState.Unavailable(reason) : CommandState.Available;

    private static CommandState Toggle(bool on) => new(true, null, on);

    /// <summary>組み込みのコマンドの処理 (既存のメニューの処理をそのまま呼ぶ)。</summary>
    private void RegisterCommandHandlers()
    {
        var e = new RoutedEventArgs();
        string noSelection = Loc.Get("Command_NoSelection");

        // ---- ファイル ----
        Commands.Register("file.new", () => New_Click(this, e));
        Commands.Register("file.newWithSize", () => NewWithSize_Click(this, e));
        Commands.Register("file.open", () => Open_Click(this, e));
        Commands.Register("file.save", () => Save_Click(this, e),
            () => NeedsDocument(d => d.Document.IsReadOnly ? Loc.Get("Command_ReadOnly") : null));
        Commands.Register("file.saveAs", () => SaveAs_Click(this, e), NeedsDocument);
        Commands.Register("file.saveAll", () => SaveAll_Click(this, e), NeedsDocument);
        Commands.Register("file.close", () => Close_Click(this, e), NeedsDocument);
        Commands.Register("file.closeAll", () => CloseAll_Click(this, e), NeedsDocument);
        Commands.Register("file.closeOthers", async () =>
        {
            if (Vm.Selected is { } doc)
            {
                await CloseAsync(Vm.Documents.Where(d => d != doc).ToList());
            }
        }, NeedsDocument);
        Commands.Register("file.closeToRight", async () =>
        {
            if (Vm.Selected is { } doc)
            {
                await CloseAsync(Vm.Documents.Skip(Vm.Documents.IndexOf(doc) + 1).ToList());
            }
        }, NeedsDocument);
        Commands.Register("file.exit", () => Exit_Click(this, e));
        RegisterFilesCommands();
        RegisterTabCommands();

        // ---- 編集 ----
        Commands.Register("edit.undo", () => Undo_Click(this, e),
            () => NeedsEditable(d => d.Document.History.CanUndo ? null : Loc.Get("Command_NothingToUndo")));
        Commands.Register("edit.redo", () => Redo_Click(this, e),
            () => NeedsEditable(d => d.Document.History.CanRedo ? null : Loc.Get("Command_NothingToRedo")));
        Commands.Register("edit.cut", () => RunEditorCommandAsync(EditorCommand.Cut),
            () => NeedsEditable(d => !d.Editor.HasSelection ? noSelection : !d.Document.CanResize ? Loc.Get("Notice_FixedLength") : null));
        Commands.Register("edit.copy", () => RunEditorCommandAsync(EditorCommand.Copy), () => NeedsDocument(d => d.Editor.HasSelection ? null : noSelection));
        Commands.Register("edit.paste", () => RunEditorCommandAsync(EditorCommand.Paste), () => NeedsEditable());
        Commands.Register("edit.pasteOverwrite", () => RunEditorCommandAsync(EditorCommand.PasteOverwrite), () => NeedsEditable());
        Commands.Register("edit.selectAll", () => SelectAll_Click(this, e),
            () => NeedsDocument(d => d.Document.Length > 0 ? null : Loc.Get("Command_EmptyDocument")));
        Commands.Register("edit.toggleInsert", () => ToggleInsert_Click(this, e),
            () => NeedsEditable(d => d.Document.CanResize ? null : Loc.Get("Notice_FixedLength")));
        RegisterEditCommands();

        // ---- 検索・移動 ----
        Commands.Register("search.find", () => Find_Click(this, e), NeedsDocument);
        Commands.Register("search.findNext", () => FindNext_Click(this, e), NeedsDocument);
        Commands.Register("search.findPrevious", () => FindPrevious_Click(this, e), NeedsDocument);
        RegisterSearchCommands();
        Commands.Register("go.goTo", () => GoTo_Click(this, e), NeedsDocument);
        // ニブル単位の移動 (VIEW-26) と列の切り替え (VIEW-27) のコマンドパレットの項目 (ショートカットは既定なし。キーは Hex ビューが処理する)。
        Commands.Register("go.nextNibble", () => Editor?.NextNibble(), NeedsDocument);
        Commands.Register("go.previousNibble", () => Editor?.PreviousNibble(), NeedsDocument);
        Commands.Register("go.toggleColumn", () => Editor?.ToggleColumn(), NeedsDocument);
        Commands.Register("go.previousGroup", () => Editor?.MovePreviousGroup(), NeedsDocument);
        Commands.Register("go.nextGroup", () => Editor?.MoveNextGroup(), NeedsDocument);
        Commands.Register("view.scrollLineUp", () => Editor?.ScrollRows(-1), NeedsDocument);
        Commands.Register("view.scrollLineDown", () => Editor?.ScrollRows(1), NeedsDocument);
        Commands.Register("go.back", () => GoBack_Click(this, e),
            () => NeedsDocument(d => d.Editor.CanGoBack ? null : Loc.Get("Command_NoHistory")));
        Commands.Register("go.forward", () => GoForward_Click(this, e),
            () => NeedsDocument(d => d.Editor.CanGoForward ? null : Loc.Get("Command_NoHistory")));
        // 「移動: 履歴の一覧」(VIEW-31 の仕様 8): パレットに最新 20 件を出し、選ぶとその位置に移る。
        Commands.Register("go.history", ShowHistoryInPalette,
            () => NeedsDocument(d => d.Editor.RecentJumps.Count > 0 ? null : Loc.Get("Command_NoHistory")));
        Commands.Register("go.start", () => GoStart_Click(this, e), NeedsDocument);
        Commands.Register("go.end", () => GoEnd_Click(this, e), NeedsDocument);
        RegisterBookmarkCommands();

        // ---- 表示 ----
        foreach (string theme in new[] { "system", "light", "dark" })
        {
            Commands.Register("view.theme." + theme, () =>
            {
                App.Settings.SetString(Appearance.ThemeKey, theme, Appearance.ThemeDefault);
                ApplyAppearance();
            }, () => Toggle(App.Settings.GetString(Appearance.ThemeKey, Appearance.ThemeDefault) is var t
                && (t == theme || (theme == "system" && t is not ("light" or "dark")))));
        }

        foreach ((string id, string encoding) in new[] { ("view.encoding.ascii", "ascii"), ("view.encoding.ansi", "ansi") })
        {
            Commands.Register(id, () => SetEncoding(encoding),
                () => Vm.Selected is null ? NeedsDocument() : Toggle(Editor?.TextEncoding.Id == encoding));
        }

        Commands.Register("view.statusBar", () =>
        {
            App.Settings.SetBool(StatusBarVisibleKey, !App.Settings.GetBool(StatusBarVisibleKey, true), true);
            UpdateThemeMenu();
        }, () => Toggle(App.Settings.GetBool(StatusBarVisibleKey, true)));
        Commands.Register("view.nextRegion", () => MoveToRegion(forward: true));
        Commands.Register("view.previousRegion", () => MoveToRegion(forward: false));
        RegisterToolbarCommands();
        RegisterPanelCommands();

        // ---- 解析 ----
        RegisterHashCommands();

        // ---- 設定・ヘルプ ----
        RegisterSettingsCommands();
        Commands.Register("help.commandPalette", () => OpenPalette(">"));
        Commands.Register("help.shortcuts", OpenShortcutsPage);
        Commands.Register("help.documentation", () => Documentation_Click(this, e));
        Commands.Register("help.reportProblem", () => ReportProblem_Click(this, e));
        RegisterPackagingCommands();
        Commands.Register("help.about", () => About_Click(this, e));
    }

    /// <summary>メニュー・ツールバーの有効状態とチェックの状態を更新する (UI-03 の仕様 3)。</summary>
    private void RefreshCommandUi()
    {
        _menus?.RefreshStates();
        RefreshToolbarStates();
        UpdatePackagingMenu();
    }

    // ---- キーの振り分け ----

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        DispatchResult result = _keys.Dispatch((int)e.Key, CurrentModifiers(), CurrentKeyContext());
        if (result.Handled)
        {
            e.Handled = true;
        }
    }

    private static KeyModifiers CurrentModifiers()
    {
        static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        return (Down(VirtualKey.Control) ? KeyModifiers.Ctrl : 0) | (Down(VirtualKey.Shift) ? KeyModifiers.Shift : 0)
            | (Down(VirtualKey.Menu) ? KeyModifiers.Alt : 0) | (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows) ? KeyModifiers.Win : 0);
    }

    /// <summary>フォーカスのある場所から有効範囲を決める (UI-18 の仕様 5)。</summary>
    private KeyContext CurrentKeyContext()
    {
        if (Root.XamlRoot is null || FocusManager.GetFocusedElement(Root.XamlRoot) is not DependencyObject focused)
        {
            return new KeyContext(KeyScope.Global, false);
        }

        bool text = focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox || focused is NumberBox;
        for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            switch (node)
            {
                case Controls.HexView view:
                    // テキスト列は文字を入力できる場所 (AltGr の文字入力を優先する。UI-20 の仕様 4)。
                    return new KeyContext(KeyScope.Editor, view.Editor?.ActiveColumn == ActiveColumn.Text);
                case Controls.FindBar or Controls.GoToBar:
                    return new KeyContext(KeyScope.FindBar, text);
                case Controls.PanelDockArea:
                    return new KeyContext(KeyScope.Panel, text);
                case Controls.CommandPalette:
                    return new KeyContext(KeyScope.Global, true);
            }
        }

        return new KeyContext(KeyScope.Global, text);
    }

    // ---- キーボード配列の変更 (WM_INPUTLANGCHANGE。UI-20 の仕様 2) ----

    private const uint WmInputLangChange = 0x0051;
    private SubclassProc? _subclass;

    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);

    private void HookInputLanguageChange()
    {
        _subclass = (hwnd, msg, wParam, lParam, id, data) =>
        {
            if (msg == WmInputLangChange)
            {
                DispatcherQueue.TryEnqueue(KeyboardLayout.Refresh);
            }

            return DefSubclassProc(hwnd, msg, wParam, lParam);
        };
        SetWindowSubclass(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id), _subclass, 0x4845, 0);
    }

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);
}
