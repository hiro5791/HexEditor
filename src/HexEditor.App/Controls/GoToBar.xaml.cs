using HexEditor.App.Services;
using HexEditor.Core.Expressions;
using HexEditor.Core.View;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace HexEditor.App.Controls;

/// <summary>移動バー (VIEW-29)。入力式で指定した位置にカーソルを移す。</summary>
public sealed partial class GoToBar : UserControl
{
    /// <summary>入力の履歴の最大件数 (VIEW-29 の仕様 12)。</summary>
    private const int HistoryLimit = 20;

    private readonly List<string> _history = [];
    private int _historyIndex = -1;

    public GoToBar()
    {
        InitializeComponent();
        AutomationProperties.SetName(Input, Loc.Get("GoTo_Input_Name"));
        AutomationProperties.SetName(BaseChoice, Loc.Get("GoTo_Base_Name"));
        AutomationProperties.SetName(UnitChoice, Loc.Get("GoTo_Unit_Name"));
        AutomationProperties.SetName(CloseButton, Loc.Get("GoTo_Close_Name"));
        ToolTipService.SetToolTip(CloseButton, Loc.Get("GoTo_Close_Name"));
    }

    /// <summary>移動の対象のビュー。</summary>
    public EditorState? Editor { get; set; }

    /// <summary>移動バーを閉じた (エディタにフォーカスを戻すため)。</summary>
    public event EventHandler? Closed;

    /// <summary>移動バーを開き、前回の入力を全選択した状態で入力欄にフォーカスを移す (VIEW-29 の仕様 1)。</summary>
    public void Open()
    {
        Visibility = Visibility.Visible;
        Input.Focus(FocusState.Programmatic);
        Input.SelectAll();
        Update();
    }

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>接頭辞のない数値は、オフセットの基数が 10 進なら 10 進、それ以外は 16 進として解釈する (00-overview 6.1、VIEW-19 の仕様 8)。</summary>
    private GoToResult? Resolve() =>
        Editor is null ? null : GoToResolver.Resolve(Input.Text, (GoToBase)BaseChoice.SelectedIndex, (GoToUnit)UnitChoice.SelectedIndex, Editor,
            Editor.View.Radix == OffsetRadix.Decimal ? Core.Expressions.DefaultRadix.Decimal : Core.Expressions.DefaultRadix.Hexadecimal);

    /// <summary>解釈結果を常に表示する (VIEW-29 の仕様 6・7)。</summary>
    private void Update()
    {
        // 読み込み中 (SelectedIndex の初期化で SelectionChanged が来たとき) は、まだ要素がそろっていない。
        if (Input is null || BaseChoice is null || UnitChoice is null || Interpretation is null || GoButton is null)
        {
            return;
        }

        GoToResult? result = Resolve();
        if (result is null || Input.Text.Trim().Length == 0)
        {
            Interpretation.Text = string.Empty;
            GoButton.IsEnabled = false;
            return;
        }

        GoToResult r = result.Value;
        GoButton.IsEnabled = r.IsValid;
        if (r.Error is { } error)
        {
            Interpretation.Text = Loc.Format("GoTo_Error_" + error.Error, error.Detail);
        }
        else if (r.OutOfRange)
        {
            Interpretation.Text = r.Offset < 0
                ? Loc.Get("GoTo_BeforeStart")
                : Loc.Format("GoTo_BeyondEnd", "0x" + Editor!.Layout.MaxCursor.ToString("X"));
        }
        else
        {
            // 16 進の表記は大文字・小文字の設定に従う (VIEW-12 の仕様 2)。0x の x は常に小文字。
            Interpretation.Text = $"= {OffsetFormat.Hex(r.Offset, Editor!.View.LowercaseHex)} ({r.Offset:N0})";
        }

        // 不正な入力は赤枠でも示す。
        Input.BorderBrush = r.IsValid ? null : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
    }

    private bool Go()
    {
        GoToResult? result = Resolve();
        if (result is not { IsValid: true } r || Editor is null)
        {
            return false;
        }

        Remember(Input.Text.Trim());
        Editor.GoTo(r.Offset, SelectChoice.IsChecked == true);
        return true;
    }

    private void Remember(string text)
    {
        _history.Remove(text);
        _history.Insert(0, text);
        if (_history.Count > HistoryLimit)
        {
            _history.RemoveAt(_history.Count - 1);
        }

        _historyIndex = -1;
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e) => Update();

    private void Option_Changed(object sender, SelectionChangedEventArgs e) => Update();

    private void Input_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (HandleInputKey(e.Key, shift))
        {
            e.Handled = true;
        }
    }

    /// <summary>入力欄のキー 1 つを処理する。処理したら true (テスト用のビルドのキー入力の注入からも呼ぶ)。</summary>
    private bool HandleInputKey(VirtualKey key, bool shift)
    {
        switch (key)
        {
            // Enter は移動して閉じる。Shift+Enter は開いたまま (VIEW-29 の仕様 8)。
            case VirtualKey.Enter:
                if (Go() && !shift)
                {
                    Close();
                }

                return true;
            case VirtualKey.Escape:
                Close();
                return true;
            case VirtualKey.Up when _history.Count > 0:
                _historyIndex = Math.Min(_historyIndex + 1, _history.Count - 1);
                Input.Text = _history[_historyIndex];
                Input.SelectAll();
                return true;
            case VirtualKey.Down when _historyIndex > 0:
                _historyIndex--;
                Input.Text = _history[_historyIndex];
                Input.SelectAll();
                return true;
            default:
                return false;
        }
    }

#if HEX_TEST_HOOKS
    /// <summary>入力欄でキーを押したのと同じ処理 (テスト方針 7.2。フォーカスや実際のキーボードを使わない)。</summary>
    public bool InjectKey(VirtualKey key, bool shift) => HandleInputKey(key, shift);
#endif

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        if (Go())
        {
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
