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

    /// <summary>state.json に保存する入力の履歴のキー (セッションをまたいで保存する。VIEW-29 の仕様 12)。</summary>
    public const string HistoryStateKey = "goTo.history";

    // 履歴はアプリ全体で 1 つ (どのウィンドウの移動バーからも同じ履歴を呼び出せる)。
    private static List<string>? s_history;
    private int _historyIndex = -1;

    /// <summary>入力の履歴 (新しい順)。初めて使うときに state.json から読む。</summary>
    private static List<string> History
    {
        get
        {
            if (s_history is null)
            {
                s_history = [];
                try
                {
                    if (global::HexEditor.App.Commands.CommandService.State?.Get(HistoryStateKey) is System.Text.Json.Nodes.JsonArray saved)
                    {
                        foreach (System.Text.Json.Nodes.JsonNode? item in saved)
                        {
                            if (item?.GetValueKind() == System.Text.Json.JsonValueKind.String && item.GetValue<string>() is { Length: > 0 } text
                                && !s_history.Contains(text) && s_history.Count < HistoryLimit)
                            {
                                s_history.Add(text);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException)
                {
                    AppLog.Info($"Go-to history not loaded: {ex.Message}");
                }
            }

            return s_history;
        }
    }

    private static void SaveHistory()
    {
        try
        {
            global::HexEditor.App.Commands.CommandService.State?.Set(HistoryStateKey,
                new System.Text.Json.Nodes.JsonArray([.. History.Select(h => (System.Text.Json.Nodes.JsonNode?)h)]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Info($"Go-to history not saved: {ex.Message}");
        }
    }

    /// <summary>入力の履歴 (テスト用の読み出し)。</summary>
    public static IReadOnlyList<string> SavedHistory => History;

    public GoToBar()
    {
        InitializeComponent();
        AutomationProperties.SetName(Input, Loc.Get("GoTo_Input_Name"));
        AutomationProperties.SetName(BaseChoice, Loc.Get("GoTo_Base_Name"));
        AutomationProperties.SetName(UnitChoice, Loc.Get("GoTo_Unit_Name"));
        AutomationProperties.SetName(AddressChoice, Loc.Get("GoTo_Address_Name"));
        AutomationProperties.SetName(CloseButton, Loc.Get("GoTo_Close_Name"));
        ToolTipService.SetToolTip(CloseButton, Loc.Get("GoTo_Close_Name"));
        AutomationProperties.SetName(HistoryButton, Loc.Get("GoTo_History_Name"));
        ToolTipService.SetToolTip(HistoryButton, Loc.Get("GoTo_History_Name"));
    }

    /// <summary>選択項目を 2 段目に折り返しているか (幅が足りないとき。VIEW-29 の「画面」)。</summary>
    public bool IsWrapped { get; private set; }

    /// <summary>
    /// 幅に応じて、選択項目を 1 段目の右か 2 段目に置く (訳文が長い言語でも項目が切れない)。親 (縦の StackPanel) は中身が
    /// 広いとその幅で並べるため、配置後の大きさではなく、測るときに使える幅で決める。
    /// </summary>
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        if (!double.IsInfinity(availableSize.Width))
        {
            UpdateWrap(availableSize.Width);
        }

        return base.MeasureOverride(availableSize);
    }

    private void UpdateWrap(double width)
    {
        Options.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double fixedWidth = 0;
        foreach (FrameworkElement part in (FrameworkElement[])[Input, HistoryButton, GoButton, CloseButton])
        {
            part.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            fixedWidth += part.DesiredSize.Width;
        }

        // 解釈結果の最小幅 120、列の間隔 8 × 5、左右の余白 12 × 2。
        double needed = fixedWidth + Options.DesiredSize.Width + 120 + 8 * 5 + Root.Padding.Left + Root.Padding.Right;
        bool wrap = width < needed;
        if (wrap == IsWrapped)
        {
            return;
        }

        IsWrapped = wrap;
        Grid.SetRow(Options, wrap ? 1 : 0);
        Grid.SetColumn(Options, wrap ? 0 : 3);
        Grid.SetColumnSpan(Options, wrap ? 6 : 1);
        Options.HorizontalAlignment = wrap ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
    }

    /// <summary>履歴のドロップダウンを開く直前に、新しい順の一覧を作る (VIEW-29 の仕様 12)。</summary>
    private void HistoryMenu_Opening(object? sender, object e)
    {
        HistoryMenu.Items.Clear();
        if (History.Count == 0)
        {
            HistoryMenu.Items.Add(new MenuFlyoutItem { Text = Loc.Get("GoTo_History_Empty"), IsEnabled = false });
            return;
        }

        for (int i = 0; i < History.Count; i++)
        {
            string text = History[i];
            var item = new MenuFlyoutItem { Text = text, FontFamily = Input.FontFamily };
            AutomationProperties.SetAutomationId(item, "GoTo_History_" + i);
            item.Click += (_, _) => Recall(text);
            HistoryMenu.Items.Add(item);
        }
    }

    /// <summary>履歴の項目を入力欄に入れる。</summary>
    public void Recall(string text)
    {
        Input.Text = text;
        Input.Focus(FocusState.Programmatic);
        Input.SelectAll();
    }

    /// <summary>移動の対象のビュー。</summary>
    public EditorState? Editor { get; set; }

    /// <summary>移動バーを閉じた (エディタにフォーカスを戻すため)。</summary>
    public event EventHandler? Closed;

    /// <summary>移動バーを開き、前回の入力を全選択した状態で入力欄にフォーカスを移す (VIEW-29 の仕様 1)。</summary>
    public void Open()
    {
        // ベースアドレスを設定しているときは「アドレスで指定」を出し、既定にする (VIEW-20 の仕様 4)。
        bool hasBase = Editor?.View.BaseAddress is > 0;
        if (hasBase && AddressChoice.Visibility != Visibility.Visible)
        {
            AddressChoice.SelectedIndex = 0;
        }

        AddressChoice.Visibility = hasBase ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
        if (Input.Text.Length == 0 && History.Count > 0)
        {
            // 前回の入力 (別のウィンドウ・前のセッションのものを含む) を入れておく (VIEW-29 の仕様 1・12)。
            Input.Text = History[0];
        }

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
            Editor.View.Radix == OffsetRadix.Decimal ? Core.Expressions.DefaultRadix.Decimal : Core.Expressions.DefaultRadix.Hexadecimal,
            byAddress: AddressChoice.Visibility == Visibility.Visible && AddressChoice.SelectedIndex == 0);

    /// <summary>解釈結果を常に表示する (VIEW-29 の仕様 6・7)。</summary>
    private void Update()
    {
        // 読み込み中 (SelectedIndex の初期化で SelectionChanged が来たとき) は、まだ要素がそろっていない。
        if (Input is null || BaseChoice is null || UnitChoice is null || AddressChoice is null || Interpretation is null || GoButton is null)
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
        List<string> history = History;
        history.Remove(text);
        history.Insert(0, text);
        if (history.Count > HistoryLimit)
        {
            history.RemoveAt(history.Count - 1);
        }

        _historyIndex = -1;
        SaveHistory();
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
            case VirtualKey.Up when History.Count > 0:
                _historyIndex = Math.Min(_historyIndex + 1, History.Count - 1);
                Input.Text = History[_historyIndex];
                Input.SelectAll();
                return true;
            case VirtualKey.Down when _historyIndex > 0:
                _historyIndex = Math.Min(_historyIndex - 1, History.Count - 1);
                Input.Text = History[_historyIndex];
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
