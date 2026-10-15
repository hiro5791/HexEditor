using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>
/// 結果ツリーの値の編集欄 (TPL-22)。型に応じた欄 (整数・浮動小数点・テキストは入力欄、列挙型はドロップダウンと「数値で入力」、
/// フラグ型はチェックボックスの一覧、bool はチェックボックス、日時は UTC / 地域の時刻の選択、色はカラーピッカーと <c>#RRGGBB</c>)。
/// Enter で確定、Esc で取り消す (仕様 7)。入力が不正なら赤枠と理由を出し、確定できない (「エラー」)。
/// </summary>
public sealed class TemplateValueEditorFlyout
{
    private readonly TemplateValueEditSession _session;
    private readonly Flyout _flyout;
    private readonly TextBox? _text;
    private readonly ComboBox? _enum;
    private readonly List<CheckBox> _flags = [];
    private readonly CheckBox? _bool;
    private readonly CheckBox? _pad;
    private readonly ComboBox? _zone;
    private readonly TextBlock _error;
    private readonly Brush? _normalBorder;
    private readonly TaskCompletionSource<TemplateEditPlan?> _done = new();

    public TemplateValueEditorFlyout(TemplateValueEditSession session)
    {
        _session = session;
        var panel = new StackPanel { Spacing = 6, MinWidth = 260, MaxWidth = 420 };
        AutomationProperties.SetAutomationId(panel, "Tpl_Editor");
        if (session.EditsRawValue)
        {
            // read だけの変換: 生の値の編集になる (TPL-14 の仕様 5)
            var note = new TextBlock { Text = Loc.Get("Tpl_EditRawNote"), TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
            AutomationProperties.SetAutomationId(note, "Tpl_EditRawNote");
            panel.Children.Add(note);
        }

        switch (session.Kind)
        {
            case TemplateEditorKind.Enum:
                _enum = new ComboBox { MinWidth = 220 };
                AutomationProperties.SetAutomationId(_enum, "Tpl_EditEnum");
                AutomationProperties.SetName(_enum, Loc.Get("Tpl_EditValue"));
                foreach (TemplateEditChoice c in session.Choices)
                {
                    _enum.Items.Add(c.Name);
                }

                _enum.Items.Add(Loc.Get("Tpl_EditEnumNumber"));
                _enum.SelectedItem = session.Choices.FirstOrDefault(c => c.IsChecked)?.Name ?? _enum.Items[^1];
                panel.Children.Add(_enum);
                _text = NewTextBox(session.InitialText);
                _text.Visibility = _enum.SelectedIndex == _enum.Items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
                _enum.SelectionChanged += (_, _) =>
                {
                    _text.Visibility = _enum.SelectedIndex == _enum.Items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
                    Validate();
                };
                panel.Children.Add(_text);
                break;
            case TemplateEditorKind.Flags:
                var list = new StackPanel { Spacing = 2 };
                AutomationProperties.SetAutomationId(list, "Tpl_EditFlags");
                foreach (TemplateEditChoice c in session.Choices)
                {
                    var box = new CheckBox { Content = c.Name + " (0x" + ((UInt128)c.Value).ToString("X", CultureInfo.InvariantCulture) + ")", IsChecked = c.IsChecked, Tag = c.Name };
                    AutomationProperties.SetAutomationId(box, "Tpl_EditFlag_" + c.Name);
                    box.KeyDown += Editor_KeyDown;
                    _flags.Add(box);
                    list.Children.Add(box);
                }

                panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320 });
                break;
            case TemplateEditorKind.Bool:
                _bool = new CheckBox { Content = Loc.Get("Tpl_EditBool"), IsChecked = session.BoolValue };
                AutomationProperties.SetAutomationId(_bool, "Tpl_EditBool");
                _bool.KeyDown += Editor_KeyDown;
                panel.Children.Add(_bool);
                break;
            default:
                _text = NewTextBox(session.InitialText);
                panel.Children.Add(_text);
                if (session.Kind == TemplateEditorKind.String)
                {
                    _pad = new CheckBox { Content = Loc.Get("Tpl_EditPadZero"), IsChecked = true };
                    AutomationProperties.SetAutomationId(_pad, "Tpl_EditPad");
                    _pad.Click += (_, _) => Validate();
                    panel.Children.Add(_pad);
                }
                else if (session.Kind == TemplateEditorKind.DateTime)
                {
                    _zone = new ComboBox { Items = { Loc.Get("Tpl_EditUtc"), Loc.Get("Tpl_EditLocal") }, SelectedIndex = 0 };
                    AutomationProperties.SetAutomationId(_zone, "Tpl_EditTimeZone");
                    AutomationProperties.SetName(_zone, Loc.Get("Tpl_EditTimeZoneName"));
                    _zone.SelectionChanged += (_, _) => Validate();
                    panel.Children.Add(_zone);
                }
                else if (session.Kind == TemplateEditorKind.Color)
                {
                    var picker = new ColorPicker { IsAlphaEnabled = false, IsMoreButtonVisible = false, ColorSpectrumShape = ColorSpectrumShape.Ring };
                    if (uint.TryParse(session.InitialText.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
                    {
                        picker.Color = Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                    }

                    AutomationProperties.SetAutomationId(picker, "Tpl_EditColor");
                    picker.ColorChanged += (_, e) => _text.Text = "#" + e.NewColor.R.ToString("X2", CultureInfo.InvariantCulture)
                        + e.NewColor.G.ToString("X2", CultureInfo.InvariantCulture) + e.NewColor.B.ToString("X2", CultureInfo.InvariantCulture);
                    panel.Children.Add(picker);
                }
                else if (session.Kind == TemplateEditorKind.Bitfield && session.BitSize is { } bits)
                {
                    panel.Children.Add(new TextBlock { Text = Loc.Format("Tpl_EditBitRange", bits, (UInt128.One << bits) - 1), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
                }

                break;
        }

        _error = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_error, "Tpl_EditError");
        AutomationProperties.SetLiveSetting(_error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        panel.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = Loc.Get("Tpl_EditOk"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetAutomationId(ok, "Tpl_EditOk");
        ok.Click += (_, _) => Commit();
        var cancel = new Button { Content = Loc.Get("Common_Cancel") };
        AutomationProperties.SetAutomationId(cancel, "Tpl_EditCancel");
        cancel.Click += (_, _) => Cancel();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        OkButton = ok;
        _normalBorder = _text?.BorderBrush;
        _flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Bottom };
        _flyout.Closed += (_, _) => _done.TrySetResult(null);
        _flyout.Opened += (_, _) => ((Control?)_enum ?? (Control?)_text ?? (Control?)_bool ?? _flags.FirstOrDefault())?.Focus(FocusState.Programmatic);
        Validate();
    }

    public Button OkButton { get; }

    /// <summary>入力欄 (テスト用の命令)。</summary>
    internal TextBox? TextInput => _text;

    internal ComboBox? EnumInput => _enum;

    internal IReadOnlyList<CheckBox> FlagInputs => _flags;

    /// <summary>今の入力の誤り (なければ空)。</summary>
    internal string ErrorText => _error.Visibility == Visibility.Visible ? _error.Text : string.Empty;

    internal bool CanCommit => OkButton.IsEnabled;

    internal TemplateValueEditSession Session => _session;

    private TextBox NewTextBox(string text)
    {
        var box = new TextBox { Text = text, FlowDirection = FlowDirection.LeftToRight, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        AutomationProperties.SetAutomationId(box, "Tpl_EditBox");
        AutomationProperties.SetName(box, Loc.Get("Tpl_EditValue"));
        box.TextChanged += (_, _) => Validate();
        box.KeyDown += Editor_KeyDown;
        box.SelectAll();
        return box;
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            Commit();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    /// <summary>今の入力を検証した結果。</summary>
    internal TemplateEditPlan Plan()
    {
        if (_session.Kind == TemplateEditorKind.Flags)
        {
            return _session.PlanFlags(_flags.Where(f => f.IsChecked == true).Select(f => (string)f.Tag));
        }

        if (_session.Kind == TemplateEditorKind.Bool)
        {
            return _session.PlanBool(_bool!.IsChecked == true);
        }

        if (_enum is not null && _enum.SelectedIndex >= 0 && _enum.SelectedIndex < _enum.Items.Count - 1)
        {
            return _session.Plan((string)_enum.SelectedItem);
        }

        return _session.Plan(_text?.Text ?? string.Empty, padWithZeros: _pad?.IsChecked != false, localTime: _zone?.SelectedIndex == 1);
    }

    internal void Validate()
    {
        TemplateEditPlan plan = Plan();
        bool ok = plan.Success;
        OkButton.IsEnabled = ok;
        _error.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        _error.Text = ok ? string.Empty : ErrorMessage(plan);
        if (_text is not null)
        {
            _text.BorderBrush = ok ? _normalBorder : (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        }
    }

    private string ErrorMessage(TemplateEditPlan plan) => plan.Error switch
    {
        TemplateEditError.OutOfRange => _session.BitSize is { } bits ? Loc.Format("Tpl_EditBitRange", bits, (UInt128.One << bits) - 1) : Loc.Get("Tpl_EditOutOfRange"),
        TemplateEditError.TooLong => Loc.Format("Tpl_EditTooLong", _session.MaxBytes),
        TemplateEditError.WriteFunctionFailed => Loc.Format("Tpl_EditWriteFailed", plan.Detail ?? string.Empty),
        TemplateEditError.Blocked => Loc.Get("Tpl_EditBlock_NotAValue"),
        _ => Loc.Get("Tpl_EditInvalid"),
    };

    /// <summary>確定する (入力が正しいときだけ)。</summary>
    internal bool Commit()
    {
        TemplateEditPlan plan = Plan();
        if (!plan.Success)
        {
            Validate();
            return false;
        }

        _done.TrySetResult(plan);
        _flyout.Hide();
        return true;
    }

    internal void Cancel()
    {
        _done.TrySetResult(null);
        _flyout.Hide();
    }

    public Task<TemplateEditPlan?> ShowAsync(FrameworkElement anchor)
    {
        _flyout.ShowAt(anchor);
        return _done.Task;
    }
}
