using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Compare;
using HexEditor.Core.Expressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using CompareOptions = HexEditor.Core.Compare.CompareOptions;

namespace HexEditor.App.Controls;

/// <summary>
/// 比較タブのツールバーの「オプション」(ANA-04 の「画面」): 比較の単位、近い差分をまとめる、再同期ウィンドウ (W)、最小一致長 (M) を変えて
/// 再比較する。値の範囲はダイアログ (ANA-01) と同じく入力欄で拒否する (ANA-03 の「エラー」)。変えた値は次回の既定にもする (ANA-01 の仕様 4)。
/// </summary>
public sealed partial class CompareView
{
    private ComboBox? _optUnit;
    private TextBox? _optMergeGap;
    private TextBox? _optWindow;
    private TextBox? _optMinMatch;
    private TextBlock? _optError;
    private StackPanel? _optInsertDelete;
    private Flyout? _optFlyout;

    /// <summary>オプションを変えて再比較した (ウィンドウが次回の既定として記憶する)。</summary>
    public event EventHandler<CompareOptions>? OptionsApplied;

    private AppBarButton CreateOptionsButton()
    {
        string name = Loc.Get("Compare_Toolbar_Options");
        var button = new AppBarButton { Label = name, Icon = new FontIcon { Glyph = "" } };
        AutomationProperties.SetAutomationId(button, "CompareToolbar_Options");
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);

        _optUnit = DialogParts.Combo("CompareOptions_Unit", Loc.Get("Compare_Option_Unit"),
            CompareOptions.Units.Select(u => Loc.Format("Compare_Option_UnitValue", u)), 0);
        _optMergeGap = DialogParts.Field("CompareOptions_MergeGap", Loc.Get("Compare_Option_MergeGap"));
        _optWindow = DialogParts.Field("CompareOptions_Window", Loc.Get("Compare_Option_Window"));
        _optMinMatch = DialogParts.Field("CompareOptions_MinMatch", Loc.Get("Compare_Option_MinMatch"));
        _optError = DialogParts.Caption("CompareOptions_Error");
        _optError.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        _optInsertDelete = new StackPanel { Spacing = 8 };
        _optInsertDelete.Children.Add(_optWindow);
        _optInsertDelete.Children.Add(_optMinMatch);
        var apply = new Button { Content = Loc.Get("Compare_Options_Apply"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetAutomationId(apply, "CompareOptions_Apply");
        apply.Click += async (_, _) =>
        {
            if (await ApplyOptionsAsync())
            {
                _optFlyout?.Hide();
            }
        };

        var panel = new StackPanel { Spacing = 8, MinWidth = 280 };
        panel.Children.Add(_optUnit);
        panel.Children.Add(_optMergeGap);
        panel.Children.Add(_optInsertDelete);
        panel.Children.Add(_optError);
        panel.Children.Add(apply);
        _optFlyout = new Flyout { Content = panel };
        _optFlyout.Opening += (_, _) => LoadOptions();
        button.Flyout = _optFlyout;
        return button;
    }

    /// <summary>今の比較のオプションを入力欄に入れる (フライアウトを開くとき)。</summary>
    internal void LoadOptions()
    {
        if (_optUnit is null)
        {
            return;
        }

        CompareOptions o = Session.Options;
        _optUnit.SelectedIndex = Math.Max(0, CompareOptions.Units.ToList().IndexOf(o.Unit));
        _optMergeGap!.Text = o.MergeGap.ToString(CultureInfo.InvariantCulture);
        _optWindow!.Text = o.Window.ToString(CultureInfo.InvariantCulture);
        _optMinMatch!.Text = o.MinMatch.ToString(CultureInfo.InvariantCulture);
        _optInsertDelete!.Visibility = o.Method == CompareMethod.InsertDelete ? Visibility.Visible : Visibility.Collapsed;
        _optError!.Text = string.Empty;
        foreach (TextBox box in new[] { _optMergeGap, _optWindow, _optMinMatch })
        {
            DialogParts.MarkInvalid(box, false);
        }
    }

    /// <summary>入力欄の値 (テスト用の命令からも設定する)。null の項目は変えない。</summary>
    internal void SetOptionInputs(int? unit, string? mergeGap, string? window, string? minMatch)
    {
        if (unit is { } u && CompareOptions.Units.ToList().IndexOf(u) is var i and >= 0)
        {
            _optUnit!.SelectedIndex = i;
        }

        if (mergeGap is not null)
        {
            _optMergeGap!.Text = mergeGap;
        }

        if (window is not null)
        {
            _optWindow!.Text = window;
        }

        if (minMatch is not null)
        {
            _optMinMatch!.Text = minMatch;
        }
    }

    /// <summary>入力欄の値で再比較する。値が範囲外なら理由を出して false。</summary>
    internal async Task<bool> ApplyOptionsAsync()
    {
        if (_optUnit is null)
        {
            return false;
        }

        string? error = null;
        var context = new CompareDialog.LengthContext(0);
        long Number(TextBox box, long min, long max, string key, long fallback, bool used)
        {
            if (!used)
            {
                return ExpressionEvaluator.TryEvaluate(box.Text, context, out long kept, out _, DefaultRadix.Decimal) && kept >= min && kept <= max ? kept : fallback;
            }

            if (!ExpressionEvaluator.TryEvaluate(box.Text, context, out long v, out _, DefaultRadix.Decimal) || v < min || v > max)
            {
                error ??= Loc.Format(key, min.ToString("N0", CultureInfo.CurrentCulture), max.ToString("N0", CultureInfo.CurrentCulture));
                DialogParts.MarkInvalid(box, true);
                return min;
            }

            DialogParts.MarkInvalid(box, false);
            return v;
        }

        CompareOptions current = Session.Options;
        bool insertDelete = current.Method == CompareMethod.InsertDelete;
        long gap = Number(_optMergeGap!, 0, CompareOptions.MaxMergeGap, "Compare_Option_MergeGapError", current.MergeGap, true);
        long window = Number(_optWindow!, CompareOptions.MinWindow, CompareOptions.MaxWindow, "Compare_Option_WindowError", current.Window, insertDelete);
        long minMatch = Number(_optMinMatch!, CompareOptions.MinMinMatch, CompareOptions.MaxMinMatch, "Compare_Option_MinMatchError", current.MinMatch, insertDelete);
        _optError!.Text = error ?? string.Empty;
        if (error is not null)
        {
            return false;
        }

        CompareOptions options = current with
        {
            Unit = CompareOptions.Units[Math.Max(0, _optUnit.SelectedIndex)],
            MergeGap = (int)gap,
            Window = (int)window,
            MinMatch = (int)minMatch,
        };
        OptionsApplied?.Invoke(this, options);
        await Session.RecompareWithAsync(options);
        return true;
    }

    /// <summary>オプションの入力欄の状態 (テスト用)。</summary>
    internal System.Text.Json.Nodes.JsonObject OptionsState() => new()
    {
        ["unit"] = _optUnit is { SelectedIndex: >= 0 } u ? CompareOptions.Units[u.SelectedIndex] : null,
        ["mergeGap"] = _optMergeGap?.Text,
        ["window"] = _optWindow?.Text,
        ["minMatch"] = _optMinMatch?.Text,
        ["insertDeleteVisible"] = _optInsertDelete?.Visibility == Visibility.Visible,
        ["error"] = _optError?.Text,
    };
}
