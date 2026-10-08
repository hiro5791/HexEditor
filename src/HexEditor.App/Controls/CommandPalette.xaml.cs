using HexEditor.App.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>コマンドパレットの候補 1 つ (UI-17 の仕様 5)。</summary>
public sealed class PaletteEntry
{
    /// <summary>候補の種類と ID (例: <c>command:file.open</c>、<c>tab:0</c>)。テスト用の命令が使う。</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>表示 (「カテゴリ: 表示名」など)。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary><see cref="Title"/> の中で太字にする文字の位置 (一致した文字。UI-17 の仕様 3)。</summary>
    public IReadOnlyList<int> Highlights { get; init; } = [];

    /// <summary>表示名の横に薄く出す文字列 (英語名で一致したときの英語名。UI-17 の仕様 4)。</summary>
    public string? Secondary { get; init; }

    public IReadOnlyList<int> SecondaryHighlights { get; init; } = [];

    public string Shortcut { get; init; } = string.Empty;

    /// <summary>使えない理由 (使えないコマンドも選べるが、実行するとエラーの InfoBar を出す)。</summary>
    public string Reason { get; init; } = string.Empty;

    public Visibility ReasonVisibility => Reason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>見出し (「最近使ったもの」など)。選べない。</summary>
    public bool IsHeader { get; init; }

    /// <summary>選んだときの処理。</summary>
    public Func<Task>? Invoke { get; init; }

    /// <summary>Tab で入力欄に補完する文字列 (UI-17 の仕様 8)。</summary>
    public string? Completion { get; init; }

    /// <summary>コマンドの候補ならそのコマンド ID (右クリックの「ショートカットを変更」に使う。UI-18 の呼び出し)。</summary>
    public string? CommandId { get; init; }
}

/// <summary>入力に対する候補の一覧と、一覧の上に出す文 (入力式のエラー・移動先など)。</summary>
public sealed record PaletteResult(IReadOnlyList<PaletteEntry> Entries, string? Message = null, bool EnterEnabled = true);

/// <summary>
/// コマンドパレット (UI-17)。表示と入力だけを受け持ち、入力の解釈と候補はウィンドウ (MainWindow.Palette.cs) の
/// <see cref="Query"/> が作る。
/// </summary>
public sealed partial class CommandPalette : UserControl
{
    private PaletteResult _result = new([]);
    private bool _closing;

    public CommandPalette()
    {
        InitializeComponent();
        Input.TextChanged += (_, _) => Refresh();
        Input.PreviewKeyDown += Input_PreviewKeyDown;
        LostFocus += (_, _) => DispatcherQueue.TryEnqueue(CloseIfFocusLeft);
    }

    /// <summary>入力から候補を作る。</summary>
    public Func<string, PaletteResult>? Query { get; set; }

    /// <summary>閉じた (エディタにフォーカスを戻す)。</summary>
    public event EventHandler? Closed;

    /// <summary>候補の右クリックメニューの「ショートカットを変更」を選んだ (UI-18 の呼び出し)。引数はコマンド ID。</summary>
    public event EventHandler<string>? ChangeShortcutRequested;

    /// <summary>候補の右クリックメニュー (コマンドの候補だけ)。項目は 1 つで、開くときに対象の候補を覚える。</summary>
    private MenuFlyout? _entryMenu;
    private PaletteEntry? _menuEntry;

    private MenuFlyout EntryMenu()
    {
        if (_entryMenu is null)
        {
            _entryMenu = new MenuFlyout();
            AutomationProperties.SetAutomationId(_entryMenu, "Palette_EntryMenu");
            var change = new MenuFlyoutItem { Text = Loc.Get("Palette_ChangeShortcut"), Icon = new FontIcon { Glyph = "" } };
            AutomationProperties.SetAutomationId(change, "Palette_ChangeShortcut");
            change.Click += (_, _) =>
            {
                if (_menuEntry?.CommandId is { } id)
                {
                    RequestChangeShortcut(id);
                }
            };
            _entryMenu.Items.Add(change);
        }

        return _entryMenu;
    }

    /// <summary>「ショートカットを変更」: パレットを閉じて、設定画面の「キーボード」をそのコマンドで開く (テスト用の命令からも使う)。</summary>
    public void RequestChangeShortcut(string commandId)
    {
        Close();
        ChangeShortcutRequested?.Invoke(this, commandId);
    }

    /// <summary>選んでいる候補の右クリックメニューを開く (アプリケーションキー・Shift+F10)。</summary>
    private void ShowEntryMenuForSelection()
    {
        if (Results.SelectedItem is PaletteEntry { CommandId: not null } entry && Results.ContainerFromItem(entry) is ListViewItem container)
        {
            _menuEntry = entry;
            EntryMenu().ShowAt(container, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
        }
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public string Text => Input.Text;

    public IReadOnlyList<PaletteEntry> Entries => _result.Entries;

    public string? MessageText => Message.Visibility == Visibility.Visible ? Message.Text : null;

    /// <summary>開く。<paramref name="text"/> は入力欄の初めの文字 (例: コマンドモードの <c>&gt;</c>)。</summary>
    public void Open(string text)
    {
        Frame.MaxWidth = Math.Max(320, (XamlRoot?.Size.Width ?? 800) * 0.9);
        Visibility = Visibility.Visible;
        Input.Text = text;
        Input.SelectionStart = text.Length;
        Refresh();
        Input.Focus(FocusState.Programmatic);
    }

    public void Close()
    {
        if (!IsOpen || _closing)
        {
            return;
        }

        _closing = true;
        Visibility = Visibility.Collapsed;
        Input.Text = string.Empty;
        _closing = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>入力欄の文字を変える (テスト用の命令からも使う)。</summary>
    public void SetText(string text)
    {
        Input.Text = text;
        Input.SelectionStart = text.Length;
        Refresh();
    }

    /// <summary>選んでいる候補を実行する (Enter と同じ)。</summary>
    public async Task<bool> InvokeSelectedAsync()
    {
        if (!_result.EnterEnabled || Results.SelectedItem is not PaletteEntry { IsHeader: false, Invoke: { } invoke })
        {
            return false;
        }

        Close();
        await invoke();
        return true;
    }

    private void Refresh()
    {
        _result = Query?.Invoke(Input.Text) ?? new PaletteResult([]);
        Results.ItemsSource = _result.Entries;
        Message.Text = _result.Message ?? string.Empty;
        Message.Visibility = string.IsNullOrEmpty(_result.Message) ? Visibility.Collapsed : Visibility.Visible;
        SelectFirst();
    }

    private void SelectFirst()
    {
        int index = _result.Entries.ToList().FindIndex(e => !e.IsHeader);
        Results.SelectedIndex = index;
        if (index >= 0)
        {
            Results.ScrollIntoView(_result.Entries[index]);
        }
    }

    private void Move(int delta)
    {
        var entries = _result.Entries;
        int i = Results.SelectedIndex;
        for (int step = 0; step < entries.Count; step++)
        {
            i = (i + delta + entries.Count) % entries.Count;
            if (!entries[i].IsHeader)
            {
                Results.SelectedIndex = i;
                Results.ScrollIntoView(entries[i]);
                return;
            }
        }
    }

    private async void Input_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down when _result.Entries.Count > 0:
                Move(1);
                e.Handled = true;
                break;
            case VirtualKey.Up when _result.Entries.Count > 0:
                Move(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                await InvokeSelectedAsync();
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                Close();
                break;
            case VirtualKey.Tab when Results.SelectedItem is PaletteEntry { Completion: { } completion }:
                e.Handled = true;
                SetText(completion);
                break;
            case VirtualKey.Application:
            case VirtualKey.F10 when Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down):
                if (Results.SelectedItem is PaletteEntry { CommandId: not null })
                {
                    e.Handled = true;
                    ShowEntryMenuForSelection();
                }

                break;
        }
    }

    private async void Results_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PaletteEntry { IsHeader: false } entry)
        {
            Results.SelectedItem = entry;
            await InvokeSelectedAsync();
        }
    }

    /// <summary>一致した文字を太字にし、英語名を薄く添える (UI-17 の仕様 3、4)。</summary>
    private void Results_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not PaletteEntry entry || args.ItemContainer.ContentTemplateRoot is not Grid root
            || root.FindName("TitleText") is not TextBlock title || root.FindName("SecondaryText") is not TextBlock secondaryText)
        {
            return;
        }

        args.ItemContainer.IsEnabled = !entry.IsHeader;

        // コマンドの候補は右クリックで「ショートカットを変更」を出す (UI-18 の呼び出し)。
        args.ItemContainer.ContextFlyout = entry.CommandId is null ? null : EntryMenu();
        args.ItemContainer.ContextRequested -= Item_ContextRequested;
        args.ItemContainer.ContextRequested += Item_ContextRequested;
        AutomationProperties.SetName(args.ItemContainer, entry.Secondary is null ? entry.Title : $"{entry.Title} ({entry.Secondary})");
        AutomationProperties.SetAutomationId(args.ItemContainer, "Palette_" + entry.Key);
        title.Inlines.Clear();
        title.FontWeight = entry.IsHeader ? FontWeights.SemiBold : FontWeights.Normal;
        AddRuns(title, entry.Title, entry.Highlights);
        secondaryText.Inlines.Clear();
        if (entry.Secondary is { } secondary)
        {
            AddRuns(secondaryText, secondary, entry.SecondaryHighlights);
        }
    }

    /// <summary>右クリックメニューを開く前に、対象の候補を覚える (コンテナは使い回されるので、そのときの項目を見る)。</summary>
    private void Item_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        _menuEntry = (sender as ListViewItem)?.Content as PaletteEntry ?? Results.ItemFromContainer(sender) as PaletteEntry;
    }

    private static void AddRuns(TextBlock block, string text, IReadOnlyList<int> bold)
    {
        var set = bold.ToHashSet();
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            bool boundary = i == text.Length || (i > start && set.Contains(i) != set.Contains(start));
            if (boundary && i > start)
            {
                var run = new Run { Text = text[start..i] };
                if (set.Contains(start))
                {
                    run.FontWeight = FontWeights.Bold;
                }

                block.Inlines.Add(run);
                start = i;
            }
        }
    }

    private void CloseIfFocusLeft()
    {
        if (!IsOpen || XamlRoot is null)
        {
            return;
        }

        for (DependencyObject? node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == this)
            {
                return;
            }
        }

        // ポップアップ (候補の項目の右クリックメニューなど) の中にフォーカスがある場合も閉じない。
        if (FocusManager.GetFocusedElement(XamlRoot) is FrameworkElement { Parent: null })
        {
            return;
        }

        Close();
    }
}
