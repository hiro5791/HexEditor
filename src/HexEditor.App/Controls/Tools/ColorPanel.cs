using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls.Tools;

/// <summary>
/// カラーピッカー (TOOL-27 の画面)。WinUI の色の選択部品で色を選び(RGB・HSV・Hex)、その色を各ピクセル形式のバイト列で同時に表示する。
/// バイト列から色を読み、書き込み・検索にも使える。色の値は色の見本の横に文字でも表示する(色だけで情報を伝えない)。
/// </summary>
internal sealed partial class ColorPanel : ToolPanelBase
{
    private const string FormatKey = "colorpicker.format";
    private const string EndianKey = "colorpicker.bigEndian";

    private sealed record FormatRow(PixelFormatInfo Format, TextBlock Bytes, ToggleButton? Endian);

    private readonly ColorPicker _picker = new();
    private readonly TextBox _hex;
    private readonly TextBlock _hexError;
    private readonly Border _swatch = new() { Width = 64, Height = 64, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
    private readonly TextBlock _swatchText = new();
    private readonly TextBlock _components = new();
    private readonly ComboBox _format = new();
    private readonly List<FormatRow> _rows = [];
    private ColorValue _color = new(255, 128, 0);
    private bool _updating;

    public ColorPanel(ToolHost host)
        : base(host)
    {
        _picker.IsAlphaEnabled = true;
        _picker.IsHexInputVisible = true;
        _picker.IsColorChannelTextInputVisible = true;
        _picker.IsMoreButtonVisible = true;
        _picker.ColorSpectrumShape = ColorSpectrumShape.Box;
        AutomationProperties.SetAutomationId(_picker, "Color_Picker");
        AutomationProperties.SetName(_picker, Loc.Get("Color_Picker"));
        _picker.ColorChanged += (_, e) =>
        {
            if (!_updating)
            {
                SetColor(new ColorValue(e.NewColor.R, e.NewColor.G, e.NewColor.B, e.NewColor.A), fromPicker: true);
            }
        };

        _hex = ToolParts.Box("Color_Hex", Loc.Get("Color_Hex"));
        _hex.PlaceholderText = "#RRGGBB / #AARRGGBB";
        _hex.TextChanged += (_, _) =>
        {
            if (!_updating)
            {
                HexEdited();
            }
        };
        _hexError = ToolParts.ErrorText("Color_HexError");

        AutomationProperties.SetAutomationId(_swatch, "Color_Swatch");
        AutomationProperties.SetName(_swatch, Loc.Get("Color_Swatch"));
        _swatchText.FontFamily = ToolParts.Mono;
        _swatchText.IsTextSelectionEnabled = true;
        AutomationProperties.SetAutomationId(_swatchText, "Color_Text");
        _components.FontFamily = ToolParts.Mono;
        AutomationProperties.SetAutomationId(_components, "Color_Components");
        var swatchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        swatchRow.Children.Add(_swatch);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        texts.Children.Add(_swatchText);
        texts.Children.Add(_components);
        swatchRow.Children.Add(texts);

        var body = new StackPanel();
        body.Children.Add(_picker);
        body.Children.Add(swatchRow);
        body.Children.Add(_hex);
        body.Children.Add(_hexError);
        body.Children.Add(new TextBlock { Text = Loc.Get("Color_Formats"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });

        foreach (PixelFormatInfo format in ColorFormats.All)
        {
            body.Children.Add(BuildRow(format));
        }

        _format.Header = Loc.Get("Color_Format");
        foreach (PixelFormatInfo format in ColorFormats.All)
        {
            _format.Items.Add(format.Name);
        }

        string saved = AppState.GetString(FormatKey, "rgba8888");
        _format.SelectedIndex = Math.Max(0, ColorFormats.All.ToList().FindIndex(f => f.Id == saved));
        AutomationProperties.SetAutomationId(_format, "Color_Format");
        AutomationProperties.SetName(_format, Loc.Get("Color_Format"));
        _format.SelectionChanged += (_, _) => AppState.SetString(FormatKey, SelectedFormat.Id);

        var actions = new WrapPanel();
        foreach (FrameworkElement e in new FrameworkElement[]
        {
            _format,
            ToolParts.TextButton("Color_ReadCursor", "Color_ReadCursor", () => ReadFromCursor()),
            ToolParts.TextButton("Color_WriteCursor", "Color_WriteCursor", () => WriteAtCursor()),
            ToolParts.TextButton("Color_Search", "Color_Search", SearchColor),
        })
        {
            e.Margin = new Thickness(0, 0, 8, 8);
            e.VerticalAlignment = VerticalAlignment.Bottom;
            actions.Children.Add(e);
        }

        body.Children.Add(actions);
        Content = ToolParts.Frame(body);
        FlowDirection = FlowDirection.LeftToRight;
        SetColor(_color, fromPicker: false);
    }

    protected override Control FirstFocus => _hex;

    private PixelFormatInfo SelectedFormat => ColorFormats.All[Math.Max(0, _format.SelectedIndex)];

    private Grid BuildRow(PixelFormatInfo format)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { Text = format.Name, VerticalAlignment = VerticalAlignment.Center };
        var bytes = new TextBlock { FontFamily = ToolParts.Mono, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(bytes, "Color_Bytes_" + format.Id);
        AutomationProperties.SetName(bytes, format.Name);
        ToggleButton? endian = null;
        if (format.HasEndian)
        {
            endian = new ToggleButton { Content = "LE", VerticalAlignment = VerticalAlignment.Center, MinWidth = 44 };
            endian.IsChecked = AppState.GetString(EndianKey + "." + format.Id, "0") == "1";
            endian.Content = endian.IsChecked == true ? "BE" : "LE";
            AutomationProperties.SetAutomationId(endian, "Color_Endian_" + format.Id);
            AutomationProperties.SetName(endian, Loc.Format("Color_EndianOf", format.Name));
            ToggleButton captured = endian;
            endian.Click += (_, _) =>
            {
                captured.Content = captured.IsChecked == true ? "BE" : "LE";
                AppState.SetString(EndianKey + "." + format.Id, captured.IsChecked == true ? "1" : "0");
                Refresh();
            };
        }

        Button copy = ToolParts.CopyButton("Color_Copy_" + format.Id, Loc.Format("Tool_CopyValue", format.Name), () => bytes.Text);
        Grid.SetColumn(bytes, 1);
        Grid.SetColumn(copy, 3);
        grid.Children.Add(name);
        grid.Children.Add(bytes);
        if (endian is not null)
        {
            Grid.SetColumn(endian, 2);
            grid.Children.Add(endian);
        }

        grid.Children.Add(copy);
        _rows.Add(new FormatRow(format, bytes, endian));
        return grid;
    }

    private bool IsBigEndian(PixelFormatInfo format) => _rows.First(r => r.Format.Id == format.Id).Endian?.IsChecked == true;

    // ---- 色の設定 ----

    public ColorValue Color => _color;

    /// <summary>色を設定して、すべての表示を更新する。</summary>
    public void SetColor(ColorValue color, bool fromPicker)
    {
        _color = color;
        _updating = true;
        if (!fromPicker)
        {
            _picker.Color = Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);
        }

        _hex.Text = color.ToHex();
        ToolParts.ShowError(_hexError, _hex, null);
        _updating = false;
        Refresh();
    }

    private void HexEdited()
    {
        if (ColorFormats.ParseHex(_hex.Text) is not { } color)
        {
            ToolParts.ShowError(_hexError, _hex, Loc.Get("Color_Error_Hex"));
            return;
        }

        ToolParts.ShowError(_hexError, _hex, null);
        _color = color;
        _updating = true;
        _picker.Color = Windows.UI.Color.FromArgb(color.A, color.R, color.G, color.B);
        _updating = false;
        Refresh();
    }

    private void Refresh()
    {
        _swatch.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(_color.A, _color.R, _color.G, _color.B));
        _swatch.BorderBrush = ToolParts.ThemeBrush("ControlStrongStrokeColorDefaultBrush");
        _swatchText.Text = _color.ToHex();
        _components.Text = Loc.Format("Color_Components", _color.R, _color.G, _color.B, _color.A);
        foreach (FormatRow row in _rows)
        {
            row.Bytes.Text = ColorFormats.BytesText(ColorFormats.Encode(_color, row.Format.Id, row.Endian?.IsChecked == true));
        }
    }

    // ---- 文書との連携 (仕様 3〜5) ----

    /// <summary>「カーソル位置から読む」: 選んだ形式のバイト列を色として解釈して表示する。</summary>
    public bool ReadFromCursor()
    {
        PixelFormatInfo format = SelectedFormat;
        byte[]? bytes = Host.ReadAtCursor(format.Bytes, format.Bytes);
        if (bytes is null || ColorFormats.Decode(bytes, format.Id, IsBigEndian(format)) is not { } color)
        {
            Host.Notify(Loc.Get("Color_Error_NoData"), true);
            return false;
        }

        SetColor(color, fromPicker: false);
        return true;
    }

    public bool WriteAtCursor()
    {
        PixelFormatInfo format = SelectedFormat;
        return Host.Write(ColorFormats.Encode(_color, format.Id, IsBigEndian(format)), "色の書き込み", false);
    }

    /// <summary>「この色を検索」: 選んだ形式のバイト列を検索バーに送る。</summary>
    public void SearchColor()
    {
        PixelFormatInfo format = SelectedFormat;
        Host.SendToSearch(ColorFormats.Encode(_color, format.Id, IsBigEndian(format)));
    }

    // ---- テスト用 ----

    internal void SetHexText(string text) => _hex.Text = text;

    internal void SelectFormat(string id)
    {
        int index = ColorFormats.All.ToList().FindIndex(f => f.Id == id);
        if (index >= 0)
        {
            _format.SelectedIndex = index;
        }
    }

    internal void SetEndian(string formatId, bool big)
    {
        if (_rows.First(r => r.Format.Id == formatId).Endian is { } toggle)
        {
            toggle.IsChecked = big;
            toggle.Content = big ? "BE" : "LE";
            Refresh();
        }
    }

    internal string BytesOf(string formatId) => _rows.First(r => r.Format.Id == formatId).Bytes.Text;

    internal string SwatchText => _swatchText.Text;

    internal (byte R, byte G, byte B, byte A)? SwatchColor =>
        _swatch.Background is SolidColorBrush { Color: var c } ? (c.R, c.G, c.B, c.A) : null;

    internal bool HexInvalid => _hexError.Visibility == Visibility.Visible;

    internal string Components => _components.Text;

    internal static string Group(string text) => text.Replace(" ", string.Empty, StringComparison.Ordinal).ToString(CultureInfo.InvariantCulture);
}
