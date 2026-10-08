using HexEditor.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// スタートページ (UI-38)。操作は画面の外 (MainWindow) に知らせ、ここでは表示と入力の受け渡しだけを行う。
/// </summary>
public sealed partial class StartPage : UserControl
{
    private bool _settingChoices;

    public StartPage()
    {
        InitializeComponent();
    }

    public StartPageViewModel ViewModel { get; } = new();

    public event EventHandler? OpenRequested;

    public event EventHandler? NewRequested;

    public event EventHandler? RestoreSessionRequested;

    public event EventHandler? ShowAllRequested;

    public event EventHandler? WelcomeClosed;

    public event EventHandler<RecentEntryViewModel>? RecentRequested;

    /// <summary>「はじめに」の選択が変わった (種類: language / theme / preset、値)。</summary>
    public event EventHandler<(string Kind, string Value)>? ChoiceChanged;

    /// <summary>「はじめに」の選択肢の今の値を選ぶ (設定の変更としては扱わない)。</summary>
    public void SetChoices(string language, string theme, string preset)
    {
        _settingChoices = true;
        try
        {
            LanguageChoice.SelectedIndex = Math.Max(0, ViewModel.Languages.ToList().FindIndex(c => c.Id == language));
            ThemeChoice.SelectedIndex = Math.Max(0, ViewModel.Themes.ToList().FindIndex(c => c.Id == theme));
            PresetChoice.SelectedIndex = Math.Max(0, ViewModel.Presets.ToList().FindIndex(c => c.Id == preset));
        }
        finally
        {
            _settingChoices = false;
        }
    }

    private string? _openLabel;
    private string? _newLabel;

    /// <summary>
    /// 「開く」「新規作成」のボタンに今のキー割り当てを添える (UI-38 の仕様 1。キー割り当てを変えたら呼び直す)。割り当てがなければ名前だけ。
    /// </summary>
    public void SetShortcuts(string open, string @new)
    {
        _openLabel ??= OpenButton.Content as string ?? string.Empty;
        _newLabel ??= NewButton.Content as string ?? string.Empty;
        Apply(OpenButton, _openLabel, open);
        Apply(NewButton, _newLabel, @new);

        static void Apply(Button button, string label, string keys)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(new TextBlock { Text = label });
            if (keys.Length > 0)
            {
                // 補助の文字の色 (テーマのリソース)。アクセントのボタンの上では文字と同じ色にする。
                var key = new TextBlock { Text = keys, Opacity = 0.8 };
                if (button.Style != (Style)Application.Current.Resources["AccentButtonStyle"])
                {
                    key.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                    key.Opacity = 1;
                }

                content.Children.Add(key);
            }

            button.Content = content;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAcceleratorKey(button, keys);
        }
    }

    /// <summary>ボタンに添えたキーの表記 (テスト用)。</summary>
    public (string Open, string New) ShortcutTexts => (
        Microsoft.UI.Xaml.Automation.AutomationProperties.GetAcceleratorKey(OpenButton),
        Microsoft.UI.Xaml.Automation.AutomationProperties.GetAcceleratorKey(NewButton));

    /// <summary>ページの末尾に要素を加える (テスト用の命令が、文字列の切れの確認用のボタンを置く)。</summary>
    public void AddExtra(UIElement element) => Body.Children.Add(element);

    /// <summary>「はじめに」の欄に項目を足す (他の配布形態の設定の取り込み。PKG-31 の仕様 1)。</summary>
    public void AddWelcomeExtra(UIElement element) => WelcomeItems.Children.Add(element);

    /// <summary>「開く」にフォーカスを置く (F6 でエディタ領域に移ったとき。UI-52)。</summary>
    public bool FocusOpen(FocusState state) => OpenButton.Focus(state);

    private void Open_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);

    private void New_Click(object sender, RoutedEventArgs e) => NewRequested?.Invoke(this, EventArgs.Empty);

    private void RestoreSession_Click(object sender, RoutedEventArgs e) => RestoreSessionRequested?.Invoke(this, EventArgs.Empty);

    private void ShowAll_Click(object sender, RoutedEventArgs e) => ShowAllRequested?.Invoke(this, EventArgs.Empty);

    private void WelcomeClose_Click(object sender, RoutedEventArgs e) => WelcomeClosed?.Invoke(this, EventArgs.Empty);

    /// <summary>クリック・Enter で開く。</summary>
    private void RecentList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentEntryViewModel entry)
        {
            RecentRequested?.Invoke(this, entry);
        }
    }

    /// <summary>
    /// 行の右クリック・アプリケーションキー・Shift+F10 (UI-32 の仕様 4): 「すべて表示…」と同じメニューを出す。メニューの中身は画面の外
    /// (MainWindow) が作り、<see cref="ShowRecentMenu"/> で出す。
    /// </summary>
    public event EventHandler<RecentEntryViewModel>? RecentContextRequested;

    private (FrameworkElement Target, Windows.Foundation.Point? Position)? _contextTarget;

    private void RecentList_ContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        var source = args.OriginalSource as FrameworkElement;
        object? item = source is ListViewItem container ? RecentList.ItemFromContainer(container) : source?.DataContext;
        if (item is not RecentEntryViewModel entry || source is null)
        {
            return;
        }

        args.Handled = true;
        _contextTarget = (source, args.TryGetPosition(RecentList, out Windows.Foundation.Point point) ? point : null);
        RecentContextRequested?.Invoke(this, entry);
    }

    /// <summary>テスト・キーボードの操作: 一覧の <paramref name="index"/> 番目の行でメニューを開く (Shift+F10 と同じ)。</summary>
    public void RequestRecentMenuAt(int index)
    {
        if (index >= 0 && index < ViewModel.RecentItems.Count)
        {
            _contextTarget = (RecentList.ContainerFromIndex(index) as FrameworkElement ?? RecentList, null);
            RecentContextRequested?.Invoke(this, ViewModel.RecentItems[index]);
        }
    }

    /// <summary>右クリックされた行の位置 (キーボードなら行) にメニューを出す。</summary>
    public void ShowRecentMenu(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase menu)
    {
        if (_contextTarget is not { } target)
        {
            return;
        }

        if (target.Position is { } point)
        {
            menu.ShowAt(RecentList, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
        }
        else
        {
            menu.ShowAt(target.Target);
        }
    }

    /// <summary>テスト・キーボードの操作: 一覧の <paramref name="index"/> 番目を開く (Enter と同じ)。</summary>
    public void OpenRecentAt(int index)
    {
        if (index >= 0 && index < ViewModel.RecentItems.Count)
        {
            RecentRequested?.Invoke(this, ViewModel.RecentItems[index]);
        }
    }

    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e) => RaiseChoice("language", LanguageChoice);

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e) => RaiseChoice("theme", ThemeChoice);

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e) => RaiseChoice("preset", PresetChoice);

    private void RaiseChoice(string kind, ComboBox box)
    {
        if (!_settingChoices && box.SelectedItem is StartChoice choice)
        {
            ChoiceChanged?.Invoke(this, (kind, choice.Id));
        }
    }
}
