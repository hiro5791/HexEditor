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
