using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Inspector;
using HexEditor.Core.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls.Tools;

/// <summary>
/// 日時の変換パネル (TOOL-28 の画面)。日時の入力欄と、形式ごとの行 (数値の 10 進・16 進、バイト列、コピーのボタン)。どの欄に入力しても
/// 他の欄を更新する。表せない値は「表せません」と表示する。データインスペクタ (INSP-13・INSP-14) と同じ定義を使う。
/// </summary>
internal sealed partial class TimePanel : ToolPanelBase
{
    private const string ZoneKey = "datetime.zone";
    private const string EndianKey = "datetime.bigEndian";
    private const string FormatKey = "datetime.format";

    private sealed record FormatRow(TimestampFormat Format, TextBox Decimal, TextBox Hex, TextBox Bytes, TextBlock Status);

    private readonly TimestampConverter _converter;
    private readonly TextBox _date;
    private readonly TextBlock _dateError;
    private readonly TextBlock _iso = new();
    private readonly TextBlock _regional = new();
    private readonly ComboBox _zone = new();
    private readonly ComboBox _endian = new();
    private readonly ComboBox _format = new();
    private readonly List<TimeZoneInfo> _zones = [];
    private readonly List<FormatRow> _rows = [];
    private bool _updating;

    public TimePanel(ToolHost host)
        : base(host)
    {
        _converter = new TimestampConverter(BuildOptions(TimeZoneInfo.Utc, utc: true));
        _converter.SetNow();

        _zone.Header = Loc.Get("Time_Zone");
        _zone.Items.Add(Loc.Get("Time_Zone_Utc"));
        _zone.Items.Add(Loc.Get("Time_Zone_Local"));
        foreach (TimeZoneInfo zone in TimeZoneInfo.GetSystemTimeZones())
        {
            _zones.Add(zone);
            _zone.Items.Add(zone.DisplayName);
        }

        AutomationProperties.SetAutomationId(_zone, "Time_Zone");
        AutomationProperties.SetName(_zone, Loc.Get("Time_Zone"));
        _zone.SelectedIndex = ZoneIndex(AppState.GetString(ZoneKey, "utc"));
        _zone.SelectionChanged += (_, _) =>
        {
            ApplyZone();
            AppState.SetString(ZoneKey, ZoneId());
            Refresh(null);
        };

        _endian.Header = Loc.Get("Time_Endian");
        _endian.Items.Add(Loc.Get("Time_EndianLittle"));
        _endian.Items.Add(Loc.Get("Time_EndianBig"));
        _endian.SelectedIndex = AppState.GetString(EndianKey, "0") == "1" ? 1 : 0;
        _converter.BigEndian = _endian.SelectedIndex == 1;
        AutomationProperties.SetAutomationId(_endian, "Time_Endian");
        AutomationProperties.SetName(_endian, Loc.Get("Time_Endian"));
        _endian.SelectionChanged += (_, _) =>
        {
            _converter.BigEndian = _endian.SelectedIndex == 1;
            AppState.SetString(EndianKey, _converter.BigEndian ? "1" : "0");
            Refresh(null);
        };

        _date = ToolParts.Box("Time_Date", Loc.Get("Time_Date"));
        _date.PlaceholderText = "2024-02-29T12:34:56Z";
        _date.TextChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }

            if (_converter.SetFromText(_date.Text) is { } error)
            {
                ToolParts.ShowError(_dateError, _date, Loc.Get("Time_Error_" + error.Code));
                return;
            }

            ToolParts.ShowError(_dateError, _date, null);
            Refresh(null, keepDate: true);
        };
        _dateError = ToolParts.ErrorText("Time_DateError");

        var body = new StackPanel();
        var options = new WrapPanel();
        foreach (FrameworkElement e in new FrameworkElement[] { _zone, _endian })
        {
            e.Margin = new Thickness(0, 0, 8, 8);
            options.Children.Add(e);
        }

        body.Children.Add(options);
        body.Children.Add(_date);
        body.Children.Add(_dateError);
        body.Children.Add(DisplayRow("Time_Iso", "Time_Row_Iso", _iso));
        body.Children.Add(DisplayRow("Time_Regional", "Time_Row_Regional", _regional));
        body.Children.Add(ToolParts.TextButton("Time_Now", "Time_Now", () =>
        {
            _converter.SetNow();
            Refresh(null);
        }));

        foreach (TimestampFormat format in TimestampFormats.All)
        {
            body.Children.Add(BuildRow(format));
        }

        _format.Header = Loc.Get("Time_Format");
        foreach (TimestampFormat format in TimestampFormats.All)
        {
            _format.Items.Add(Loc.Get("Time_Format_" + format.Id));
        }

        _format.SelectedIndex = Math.Max(0, TimestampFormats.All.ToList().FindIndex(f => f.Id == AppState.GetString(FormatKey, "unix32")));
        AutomationProperties.SetAutomationId(_format, "Time_Format");
        AutomationProperties.SetName(_format, Loc.Get("Time_Format"));
        _format.SelectionChanged += (_, _) => AppState.SetString(FormatKey, SelectedFormat.Id);
        var actions = new WrapPanel();
        foreach (FrameworkElement e in new FrameworkElement[]
        {
            _format,
            ToolParts.TextButton("Time_ReadCursor", "Time_ReadCursor", () => ReadFromCursor()),
            ToolParts.TextButton("Time_WriteCursor", "Time_WriteCursor", () => WriteAtCursor()),
        })
        {
            e.Margin = new Thickness(0, 0, 8, 8);
            e.VerticalAlignment = VerticalAlignment.Bottom;
            actions.Children.Add(e);
        }

        body.Children.Add(actions);
        Content = ToolParts.Frame(body);
        FlowDirection = FlowDirection.LeftToRight;
        ApplyZone();
        Refresh(null);
    }

    protected override Control FirstFocus => _date;

    private TimestampFormat SelectedFormat => TimestampFormats.All[Math.Max(0, _format.SelectedIndex)];

    private static InspectorOptions BuildOptions(TimeZoneInfo zone, bool utc) => new()
    {
        TimeZoneMode = utc ? DateTimeZoneMode.Utc : DateTimeZoneMode.Local,
        LocalTimeZone = zone,
        DateTimeStyle = DateTimeStyle.Regional,
        Culture = CultureInfo.CurrentCulture,
        Language = CultureInfo.CurrentUICulture.Name,
        Text = InspectorStrings.Current,
        Time = InspectorViewModel.TestableTime,
    };

    private int ZoneIndex(string id)
    {
        if (id == "utc")
        {
            return 0;
        }

        if (id == "local")
        {
            return 1;
        }

        int index = _zones.FindIndex(z => z.Id == id);
        return index >= 0 ? index + 2 : 0;
    }

    private string ZoneId() => _zone.SelectedIndex switch
    {
        <= 0 => "utc",
        1 => "local",
        var i => _zones[i - 2].Id,
    };

    private void ApplyZone()
    {
        int i = Math.Max(0, _zone.SelectedIndex);
        TimeZoneInfo zone = i switch { 0 => TimeZoneInfo.Utc, 1 => InspectorViewModel.LocalTimeZone, _ => _zones[i - 2] };
        _converter.Options = BuildOptions(zone, utc: i == 0);
    }

    private static Grid DisplayRow(string id, string labelKey, TextBlock value)
    {
        string label = Loc.Get(labelKey);
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        value.FontFamily = ToolParts.Mono;
        value.IsTextSelectionEnabled = true;
        value.TextWrapping = TextWrapping.Wrap;
        value.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(value, id);
        AutomationProperties.SetName(value, label);
        Button copy = ToolParts.CopyButton(id.Replace("Time_", "Time_Copy_", StringComparison.Ordinal), Loc.Format("Tool_CopyValue", label), () => value.Text);
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(value, 1);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(value);
        grid.Children.Add(copy);
        return grid;
    }

    private FrameworkElement BuildRow(TimestampFormat format)
    {
        string name = Loc.Get("Time_Format_" + format.Id);
        string id = "Time_" + format.Id + "_";
        TextBox decimalBox = ToolParts.Box(id + "Dec", name + " — " + Loc.Get("Time_Decimal"));
        TextBox hexBox = ToolParts.Box(id + "Hex", name + " — " + Loc.Get("Time_Hex"));
        TextBox bytesBox = ToolParts.Box(id + "Bytes", name + " — " + Loc.Get("Time_Bytes"));
        var status = new TextBlock { Foreground = ToolParts.ThemeBrush("TextFillColorSecondaryBrush"), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(status, id + "Status");
        var row = new FormatRow(format, decimalBox, hexBox, bytesBox, status);
        _rows.Add(row);
        decimalBox.TextChanged += (_, _) => RowEdited(row, row.Decimal, () => _converter.SetFromNumber(format, row.Decimal.Text));
        hexBox.TextChanged += (_, _) => RowEdited(row, row.Hex, () => _converter.SetFromNumber(format, row.Hex.Text));
        bytesBox.TextChanged += (_, _) => RowEdited(row, row.Bytes, () => _converter.SetFromHexBytes(format, row.Bytes.Text));

        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        var fields = new WrapPanel();
        foreach (TextBox box in new[] { decimalBox, hexBox, bytesBox })
        {
            box.Margin = new Thickness(0, 0, 8, 4);
            box.MinWidth = 140;
            fields.Children.Add(box);
        }

        Button copy = ToolParts.CopyButton(id + "Copy", Loc.Format("Tool_CopyValue", name), () => row.Decimal.Text);
        copy.VerticalAlignment = VerticalAlignment.Bottom;
        copy.Margin = new Thickness(0, 0, 8, 4);
        fields.Children.Add(copy);
        panel.Children.Add(fields);
        panel.Children.Add(status);
        return panel;
    }

    private void RowEdited(FormatRow row, TextBox source, Func<TimestampError?> apply)
    {
        if (_updating || string.IsNullOrWhiteSpace(source.Text))
        {
            return;
        }

        if (apply() is { } error)
        {
            DialogParts.MarkInvalid(source, true);
            row.Status.Text = Loc.Get("Time_Error_" + error.Code);
            row.Status.Visibility = Visibility.Visible;
            return;
        }

        DialogParts.MarkInvalid(source, false);
        Refresh(source);
    }

    /// <summary>すべての欄を基準の日時から作り直す。<paramref name="except"/> は入力中の欄(そのまま残す)。</summary>
    private void Refresh(TextBox? except, bool keepDate = false)
    {
        _updating = true;
        if (!keepDate)
        {
            _date.Text = _converter.Iso();
            ToolParts.ShowError(_dateError, _date, null);
        }

        _iso.Text = _converter.Iso();
        _regional.Text = _converter.Regional();
        foreach (FormatRow row in _rows)
        {
            TimestampRow r = _converter.Row(row.Format);
            row.Status.Visibility = r.Representable ? Visibility.Collapsed : Visibility.Visible;
            row.Status.Text = r.Representable ? string.Empty : Loc.Get("Time_NotRepresentable");
            foreach ((TextBox box, string text) in new[] { (row.Decimal, r.Decimal), (row.Hex, r.Hex), (row.Bytes, r.Representable ? HexOf(r.Bytes) : string.Empty) })
            {
                if (box != except)
                {
                    box.Text = text;
                    DialogParts.MarkInvalid(box, false);
                }
            }
        }

        _updating = false;
    }

    private static string HexOf(byte[] bytes) => Core.Clipboard.HexText.Format(bytes);

    // ---- 文書との連携 (仕様 4) ----

    /// <summary>「カーソル位置から読む」: 選んだ形式とエンディアンで読む。</summary>
    public bool ReadFromCursor()
    {
        TimestampFormat format = SelectedFormat;
        byte[]? bytes = Host.ReadAtCursor(format.Bytes, format.Bytes);
        if (bytes is null || _converter.SetFromBytes(format, bytes) is not null)
        {
            Host.Notify(Loc.Get("Time_Error_ReadFailed"), true);
            return false;
        }

        Refresh(null);
        return true;
    }

    /// <summary>「カーソル位置に書き込む」: 選んだ形式のバイト列。表せない値は書かない。</summary>
    public bool WriteAtCursor()
    {
        TimestampRow row = _converter.Row(SelectedFormat);
        if (!row.Representable)
        {
            Host.Notify(Loc.Get("Time_NotRepresentable"), true);
            return false;
        }

        return Host.Write(row.Bytes, "日時の書き込み", false);
    }

    // ---- テスト用 ----

    internal void SetDateText(string text) => _date.Text = text;

    internal void SetField(string formatId, string field, string text)
    {
        FormatRow row = _rows.First(r => r.Format.Id == formatId);
        TextBox box = field switch { "dec" => row.Decimal, "hex" => row.Hex, _ => row.Bytes };
        box.Text = text;
    }

    internal void SelectFormat(string id)
    {
        int index = TimestampFormats.All.ToList().FindIndex(f => f.Id == id);
        if (index >= 0)
        {
            _format.SelectedIndex = index;
        }
    }

    internal void SelectEndian(bool big) => _endian.SelectedIndex = big ? 1 : 0;

    internal void SelectZone(string id) => _zone.SelectedIndex = ZoneIndex(id);

    internal string DateText => _date.Text;

    internal string IsoText => _iso.Text;

    internal string RegionalText => _regional.Text;

    internal bool DateInvalid => _dateError.Visibility == Visibility.Visible;

    internal (string Decimal, string Hex, string Bytes, string Status) RowOf(string formatId)
    {
        FormatRow row = _rows.First(r => r.Format.Id == formatId);
        return (row.Decimal.Text, row.Hex.Text, row.Bytes.Text, row.Status.Visibility == Visibility.Visible ? row.Status.Text : string.Empty);
    }
}
