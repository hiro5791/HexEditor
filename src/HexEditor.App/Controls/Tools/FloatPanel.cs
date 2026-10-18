using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls.Tools;

/// <summary>
/// IEEE 754 浮動小数点のビジュアライザ (TOOL-25 の画面)。ビットの列(符号・指数部・仮数部を、色に加えて区間のラベルで区別する)、
/// 各部分の意味、分類、値の表記、隣の値と ULP を示す。ビットのクリックで値の変化を確かめられる。
/// </summary>
internal sealed partial class FloatPanel : ToolPanelBase
{
    private const string FormatKey = "floatviz.format";
    private const string EndianKey = "floatviz.bigEndian";

    private readonly ComboBox _format = new();
    private readonly ComboBox _endian = new();
    private readonly TextBox _decimal;
    private readonly TextBox _hexInput;
    private readonly TextBlock _inputError;
    private readonly StackPanel _bitSections = new() { Spacing = 6 };
    private readonly List<Button> _cells = [];
    private readonly Dictionary<string, TextBlock> _values = [];
    private readonly TextBlock _sectionNote = new();
    private FloatLayout _layout = FloatLayout.Single;
    private UInt128 _bits;
    private string _roundingError = string.Empty;
    private bool _updating;
    private FloatAnalysis? _analysis;

    public FloatPanel(ToolHost host)
        : base(host)
    {
        _format.Header = Loc.Get("Float_Format");
        foreach (FloatLayout layout in FloatLayout.All)
        {
            _format.Items.Add(Loc.Get("Float_Format_" + layout.Id));
        }

        AutomationProperties.SetAutomationId(_format, "Float_Format");
        AutomationProperties.SetName(_format, Loc.Get("Float_Format"));
        FloatLayout saved = FloatLayout.Find(AppState.GetString(FormatKey, "float")) ?? FloatLayout.Single;
        _layout = saved;
        _format.SelectedIndex = FloatLayout.All.ToList().IndexOf(saved);
        _format.SelectionChanged += (_, _) => FormatChanged();

        _endian.Header = Loc.Get("Float_Endian");
        _endian.Items.Add(Loc.Get("Float_EndianLittle"));
        _endian.Items.Add(Loc.Get("Float_EndianBig"));
        _endian.SelectedIndex = AppState.GetString(EndianKey, "0") == "1" ? 1 : 0;
        AutomationProperties.SetAutomationId(_endian, "Float_Endian");
        AutomationProperties.SetName(_endian, Loc.Get("Float_Endian"));
        _endian.SelectionChanged += (_, _) => AppState.SetString(EndianKey, _endian.SelectedIndex == 1 ? "1" : "0");

        _decimal = ToolParts.Box("Float_Decimal", Loc.Get("Float_Decimal"));
        _decimal.TextChanged += (_, _) =>
        {
            if (!_updating && !ToolParts.IsProgrammatic(_decimal))
            {
                DecimalEdited();
            }
        };
        _hexInput = ToolParts.Box("Float_HexInput", Loc.Get("Float_HexInput"));
        _hexInput.TextChanged += (_, _) =>
        {
            if (!_updating && !ToolParts.IsProgrammatic(_hexInput))
            {
                HexEdited();
            }
        };
        _inputError = ToolParts.ErrorText("Float_InputError");

        var body = new StackPanel();
        var options = new WrapPanel();
        foreach (FrameworkElement e in new FrameworkElement[] { _format, _endian })
        {
            e.Margin = new Thickness(0, 0, 8, 8);
            options.Children.Add(e);
        }

        body.Children.Add(options);
        body.Children.Add(_decimal);
        body.Children.Add(_hexInput);
        body.Children.Add(_inputError);
        AutomationProperties.SetAutomationId(_bitSections, "Float_Bits");
        AutomationProperties.SetName(_bitSections, Loc.Get("Float_BitTable"));
        body.Children.Add(_bitSections);

        foreach ((string id, string key) in new[]
        {
            ("Float_BitsHex", "Float_Row_BitsHex"), ("Float_Sign", "Float_Row_Sign"), ("Float_Exponent", "Float_Row_Exponent"),
            ("Float_Mantissa", "Float_Row_Mantissa"), ("Float_Class", "Float_Row_Class"), ("Float_Shortest", "Float_Row_Shortest"),
            ("Float_Exact", "Float_Row_Exact"), ("Float_HexFloat", "Float_Row_HexFloat"), ("Float_NextUp", "Float_Row_NextUp"),
            ("Float_NextDown", "Float_Row_NextDown"), ("Float_Ulp", "Float_Row_Ulp"), ("Float_Error", "Float_Row_Error"),
        })
        {
            string captured = id;
            (Grid row, TextBlock value) = ToolParts.ValueRow(id, key, id.Replace("Float_", "Float_Copy_", StringComparison.Ordinal), () => _values[captured].Text);
            _values[id] = value;
            body.Children.Add(row);
        }

        var actions = new WrapPanel();
        foreach (Button b in new[]
        {
            ToolParts.TextButton("Float_ReadCursor", "Float_ReadCursor", () => ReadFromCursor()),
            ToolParts.TextButton("Float_WriteCursor", "Float_WriteCursor", () => WriteAtCursor()),
        })
        {
            b.Margin = new Thickness(0, 0, 8, 8);
            actions.Children.Add(b);
        }

        body.Children.Add(actions);
        Content = ToolParts.Frame(body);
        FlowDirection = FlowDirection.LeftToRight;
        Rebuild();
    }

    protected override Control FirstFocus => _decimal;

    public FloatLayout Layout => _layout;

    // ---- 値の設定 ----

    /// <summary>形式とビット列を設定する(インスペクタの右クリックから開く)。</summary>
    public void Show(FloatLayout layout, UInt128 bits)
    {
        _updating = true;
        _layout = layout;
        _format.SelectedIndex = FloatLayout.All.ToList().IndexOf(layout);
        _updating = false;
        _bits = bits;
        _roundingError = string.Empty;
        Rebuild();
    }

    private void FormatChanged()
    {
        if (_updating || _format.SelectedIndex < 0)
        {
            return;
        }

        FloatLayout next = FloatLayout.All[_format.SelectedIndex];
        AppState.SetString(FormatKey, next.Id);

        // 形式を切り替えたら、同じ値を新しい形式で表した値と丸めの誤差を示す(仕様 5)。
        FloatRounding converted = FloatAnalyzer.ConvertTo(_layout, _bits, next);
        _layout = next;
        _bits = converted.Bits;
        _roundingError = converted.Error;
        Rebuild();
    }

    private void DecimalEdited()
    {
        string text = _decimal.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ToolParts.ShowError(_inputError, _decimal, null);
            return;
        }

        (FloatRounding? rounded, FloatInputError? error) = FloatAnalyzer.FromDecimalText(_layout, text);
        if (error is not null)
        {
            ToolParts.ShowError(_inputError, _decimal, Loc.Get("Float_Error_" + error.Code));
            return;
        }

        ToolParts.ShowError(_inputError, _decimal, null);
        DialogParts.MarkInvalid(_hexInput, false);
        _bits = rounded!.Bits;
        _roundingError = rounded.Error;
        Refresh(keepDecimal: true, keepHex: false);
    }

    private void HexEdited()
    {
        if (string.IsNullOrWhiteSpace(_hexInput.Text))
        {
            return;
        }

        UInt128? parsed = FloatAnalyzer.ParseHex(_layout, _hexInput.Text);
        if (parsed is null)
        {
            ToolParts.ShowError(_inputError, _hexInput, Loc.Get("Float_Error_InvalidHex"));
            return;
        }

        ToolParts.ShowError(_inputError, _hexInput, null);
        DialogParts.MarkInvalid(_decimal, false);
        _bits = parsed.Value;
        _roundingError = string.Empty;
        Refresh(keepDecimal: false, keepHex: true);
    }

    /// <summary>ビットを反転する(クリック)。</summary>
    public void ToggleBit(int index)
    {
        _bits = FloatAnalyzer.ToggleBit(_layout, _bits, index);
        _roundingError = string.Empty;
        Refresh(keepDecimal: false, keepHex: false);
    }

    // ---- 表示 ----

    /// <summary>ビットの列を作り直す(形式が変わったとき)。</summary>
    private void Rebuild()
    {
        _bitSections.Children.Clear();
        _cells.Clear();
        int total = _layout.TotalBits;
        (string Key, int From, int To, string Brush)[] sections =
        [
            ("Float_Section_Sign", total - 1, total - 1, "SystemFillColorCriticalBackgroundBrush"),
            ("Float_Section_Exponent", total - 2, _layout.MantissaBits, "SystemFillColorCautionBackgroundBrush"),
            ("Float_Section_Mantissa", _layout.MantissaBits - 1, 0, "SystemFillColorSuccessBackgroundBrush"),
        ];
        foreach ((string key, int from, int to, string brush) in sections)
        {
            int count = from - to + 1;
            var group = new StackPanel { Spacing = 2 };
            var label = new TextBlock
            {
                Text = Loc.Format(key, count),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                FlowDirection = FlowDirection.LeftToRight,
            };
            AutomationProperties.SetAutomationId(label, key + "Label");
            AutomationProperties.SetName(label, label.Text);
            group.Children.Add(label);

            // 区間は、ラベルと区切りの線(括弧)でも示す。色だけに頼らない。
            var frame = new Border
            {
                BorderBrush = ToolParts.ThemeBrush("ControlStrongStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(2, 2, 2, 0),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Padding = new Thickness(2),
                Background = ToolParts.ThemeBrush(brush),
            };
            var cells = new WrapPanel();
            for (int bit = from; bit >= to; bit--)
            {
                var cell = new Button
                {
                    Width = 24,
                    Height = 28,
                    Padding = new Thickness(0),
                    Content = "0",
                    FontFamily = ToolParts.Mono,
                    Tag = bit,
                    Margin = new Thickness(0, 0, bit % 4 == 0 && bit != to ? 6 : 0, 0),
                };
                AutomationProperties.SetAutomationId(cell, "Float_Bit_" + bit.ToString(CultureInfo.InvariantCulture));
                int captured = bit;
                cell.Click += (_, _) => ToggleBit(captured);
                _cells.Add(cell);
                cells.Children.Add(cell);
            }

            frame.Child = cells;
            group.Children.Add(frame);
            _bitSections.Children.Add(group);
        }

        Refresh(keepDecimal: false, keepHex: false);
    }

    private void Refresh(bool keepDecimal, bool keepHex)
    {
        FloatAnalysis a = FloatAnalyzer.Analyze(_layout, _bits);
        _analysis = a;
        foreach (Button cell in _cells)
        {
            int bit = (int)cell.Tag;
            int value = (int)((_bits >> bit) & 1);
            cell.Content = value.ToString(CultureInfo.InvariantCulture);
            cell.FontWeight = value == 1 ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            string section = Loc.Format(FloatAnalyzer.SectionOf(_layout, bit) switch { 0 => "Float_Name_Sign", 1 => "Float_Name_Exponent", _ => "Float_Name_Mantissa" },
                FloatAnalyzer.IndexInSection(_layout, bit), value);
            AutomationProperties.SetName(cell, section);
        }

        _updating = true;
        if (!keepDecimal)
        {
            ToolParts.SetText(_decimal, a.Class is FloatClass.Invalid ? string.Empty : a.Shortest);
            ToolParts.ShowError(_inputError, _decimal, null);
        }

        if (!keepHex)
        {
            ToolParts.SetText(_hexInput, FloatAnalyzer.HexText(_layout, _bits)[2..]);
        }

        _updating = false;

        _values["Float_BitsHex"].Text = FloatAnalyzer.HexText(_layout, _bits);
        _values["Float_Sign"].Text = a.Negative ? "1 (−)" : "0 (+)";
        _values["Float_Exponent"].Text = ExponentText(a);
        _values["Float_Mantissa"].Text = $"{a.MantissaBitString}  ({a.SignificandText})";
        _values["Float_Class"].Text = ClassText(a);
        _values["Float_Shortest"].Text = a.Shortest;
        _values["Float_Exact"].Text = a.ExactDecimal ?? string.Empty;
        _values["Float_HexFloat"].Text = a.HexFloat;
        _values["Float_NextUp"].Text = a.NextUp;
        _values["Float_NextDown"].Text = a.NextDown;
        _values["Float_Ulp"].Text = a.Ulp;
        _values["Float_Error"].Text = _roundingError;
    }

    private static string ExponentText(FloatAnalysis a)
    {
        string raw = ((ulong)a.ExponentRaw).ToString(CultureInfo.InvariantCulture);
        return a.UnbiasedExponent is { } unbiased
            ? Loc.Format("Float_ExponentText", a.ExponentBitString, raw, unbiased)
            : Loc.Format("Float_ExponentTextSpecial", a.ExponentBitString, raw);
    }

    private static string ClassText(FloatAnalysis a)
    {
        string name = Loc.Get("Float_Class_" + a.Class);
        return a.Class switch
        {
            FloatClass.Zero => name + (a.Negative ? " (−0)" : " (+0)"),
            FloatClass.Infinity => name + (a.Negative ? " (−∞)" : " (+∞)"),
            FloatClass.NaN => Loc.Format(a.IsQuietNaN ? "Float_Class_NaNQuiet" : "Float_Class_NaNSignaling", "0x" + a.NanPayload.ToString("X", CultureInfo.InvariantCulture)),
            _ => name,
        };
    }

    // ---- 文書との連携 (仕様 4) ----

    public bool ReadFromCursor()
    {
        byte[]? bytes = Host.ReadAtCursor(_layout.Bytes, _layout.Bytes);
        if (bytes is null || FloatAnalyzer.FromBytes(_layout, bytes, _endian.SelectedIndex == 1) is not { } bits)
        {
            Host.Notify(Loc.Get("Float_Error_NoData"), true);
            return false;
        }

        _bits = bits;
        _roundingError = string.Empty;
        Refresh(keepDecimal: false, keepHex: false);
        return true;
    }

    public bool WriteAtCursor() =>
        Host.Write(FloatAnalyzer.ToBytes(_layout, _bits, _endian.SelectedIndex == 1), "浮動小数点の値の書き込み", false);

    internal void SetDecimal(string text)
    {
        _decimal.Text = text;
        DecimalEdited();
    }

    internal void SetHex(string text)
    {
        _hexInput.Text = text;
        HexEdited();
    }

    internal void SelectFormat(string id)
    {
        int index = FloatLayout.All.ToList().FindIndex(f => f.Id == id);
        if (index >= 0)
        {
            _format.SelectedIndex = index;
        }
    }

    internal FloatAnalysis? Analysis => _analysis;

    internal string Value(string id) => _values[id].Text;

    internal bool InputInvalid => _inputError.Visibility == Visibility.Visible;

    internal IReadOnlyList<Button> Cells => _cells;

    internal TextBox DecimalBox => _decimal;

    internal TextBox HexBox => _hexInput;

    internal string SectionLabel(string key, int count) => Loc.Format(key, count);
}
