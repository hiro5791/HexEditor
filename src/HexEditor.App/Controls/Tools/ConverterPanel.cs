using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Tools;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls.Tools;

/// <summary>
/// 「基数・データ型の変換」パネル (TOOL-24 の画面)。欄を縦に並べ、各欄の横にコピーのボタンを置く。どの欄に入力しても他の欄を更新する。
/// 解釈できない入力は欄の赤枠と説明で示し、変換しない。
/// </summary>
internal sealed partial class ConverterPanel : ToolPanelBase
{
    private const string EndianKey = "converter.bigEndian";
    private const string CodePageKey = "converter.codePage";

    private sealed record FieldRow(ConverterField Field, string Id, TextBox Box, TextBlock Message);

    private readonly ValueConverter _converter = new();
    private readonly List<FieldRow> _rows = [];
    private readonly ComboBox _endian = new();
    private readonly ComboBox _codePage = new();
    private readonly IReadOnlyList<EncodingEntry> _codePages;
    private bool _updating;

    public ConverterPanel(ToolHost host)
        : base(host)
    {
        _codePages = [.. EncodingCatalog.All.Where(e => e.CodePage > 1 && e.Group != EncodingGroup.Custom && !e.Id.StartsWith("table:", StringComparison.OrdinalIgnoreCase))];

        _endian.Header = Loc.Get("Conv_Endian");
        _endian.Items.Add(Loc.Get("Conv_EndianLittle"));
        _endian.Items.Add(Loc.Get("Conv_EndianBig"));
        AutomationProperties.SetAutomationId(_endian, "Conv_Endian");
        AutomationProperties.SetName(_endian, Loc.Get("Conv_Endian"));
        _endian.SelectedIndex = AppState.GetString(EndianKey, "0") == "1" ? 1 : 0;
        _converter.BigEndian = _endian.SelectedIndex == 1;
        _endian.SelectionChanged += (_, _) =>
        {
            _converter.BigEndian = _endian.SelectedIndex == 1;
            AppState.SetString(EndianKey, _converter.BigEndian ? "1" : "0");
            Refresh(null);
        };

        _codePage.Header = Loc.Get("Conv_CodePageChoice");
        foreach (EncodingEntry entry in _codePages)
        {
            _codePage.Items.Add(MainWindow.EncodingDisplayText(entry));
        }

        AutomationProperties.SetAutomationId(_codePage, "Conv_CodePageChoice");
        AutomationProperties.SetName(_codePage, Loc.Get("Conv_CodePageChoice"));
        string saved = AppState.GetString(CodePageKey, "cp437");
        int index = FindCodePage(saved);
        _codePage.SelectedIndex = index >= 0 ? index : 0;
        _converter.CodePageId = _codePages[Math.Max(0, _codePage.SelectedIndex)].Id;
        _codePage.SelectionChanged += (_, _) =>
        {
            if (_codePage.SelectedIndex < 0)
            {
                return;
            }

            _converter.CodePageId = _codePages[_codePage.SelectedIndex].Id;
            AppState.SetString(CodePageKey, _converter.CodePageId);
            Refresh(null);
        };

        var body = new StackPanel();
        var options = new WrapPanel();
        foreach (FrameworkElement e in new FrameworkElement[] { _endian, _codePage })
        {
            e.Margin = new Thickness(0, 0, 8, 8);
            options.Children.Add(e);
        }

        body.Children.Add(options);
        AddField(body, ConverterField.Hex, "Conv_Hex");
        AddField(body, ConverterField.HexInteger, "Conv_HexInteger");
        AddField(body, ConverterField.UnsignedDecimal, "Conv_UDec");
        AddField(body, ConverterField.SignedDecimal, "Conv_SDec");
        AddField(body, ConverterField.Octal, "Conv_Oct");
        AddField(body, ConverterField.Binary, "Conv_Bin");
        AddField(body, ConverterField.Half, "Conv_Half");
        AddField(body, ConverterField.Float, "Conv_Float");
        AddField(body, ConverterField.Double, "Conv_Double");
        AddField(body, ConverterField.Ascii, "Conv_Ascii");
        AddField(body, ConverterField.Utf8, "Conv_Utf8");
        AddField(body, ConverterField.Utf16Le, "Conv_Utf16Le");
        AddField(body, ConverterField.Utf16Be, "Conv_Utf16Be");
        AddField(body, ConverterField.Utf32Le, "Conv_Utf32Le");
        AddField(body, ConverterField.Utf32Be, "Conv_Utf32Be");
        AddField(body, ConverterField.CodePage, "Conv_CodePage");
        AddField(body, ConverterField.Base64, "Conv_Base64");

        var actions = new WrapPanel();
        foreach (Button b in new[]
        {
            ToolParts.TextButton("Conv_ReadCursor", "Conv_ReadCursor", () => ReadFromCursor()),
            ToolParts.TextButton("Conv_WriteCursor", "Conv_WriteCursor", () => WriteAtCursor()),
            ToolParts.TextButton("Conv_SendSearch", "Conv_SendSearch", SendToSearch),
        })
        {
            b.Margin = new Thickness(0, 0, 8, 8);
            actions.Children.Add(b);
        }

        body.Children.Add(actions);
        Content = ToolParts.Frame(body);
        FlowDirection = FlowDirection.LeftToRight;
        Refresh(null);
    }

    protected override Control FirstFocus => _rows[0].Box;

    private int FindCodePage(string id)
    {
        for (int i = 0; i < _codePages.Count; i++)
        {
            if (string.Equals(_codePages[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private void AddField(StackPanel body, ConverterField field, string id)
    {
        string label = Loc.Get(id);
        TextBox box = ToolParts.Box(id, label);
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        TextBlock message = ToolParts.ErrorText(id + "_Error");
        message.Visibility = Visibility.Collapsed;
        var row = new FieldRow(field, id, box, message);
        _rows.Add(row);
        box.TextChanged += (_, _) =>
        {
            if (!_updating)
            {
                Edited(row);
            }
        };

        Button copy = ToolParts.CopyButton(id.Replace("Conv_", "Conv_Copy_", StringComparison.Ordinal), Loc.Format("Tool_CopyValue", label), () => box.Text);
        copy.VerticalAlignment = VerticalAlignment.Bottom;
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(box);
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(grid);
        stack.Children.Add(message);
        body.Children.Add(stack);
    }

    /// <summary>欄に入力した。誤りがなければ他の欄を更新する(入力した欄は更新しない)。</summary>
    private void Edited(FieldRow row)
    {
        ConversionError? error = _converter.SetFromField(row.Field, row.Box.Text);
        if (error is not null)
        {
            ToolParts.ShowError(row.Message, row.Box, Describe(error));
            return;
        }

        ToolParts.ShowError(row.Message, row.Box, null);
        Refresh(row);
    }

    private static string Describe(ConversionError error) => Loc.Format("Conv_Error_" + error.Code, error.Detail ?? string.Empty);

    /// <summary>すべての欄を基準のバイト列から作り直す。<paramref name="except"/> は入力中の欄(そのまま残す)。</summary>
    private void Refresh(FieldRow? except)
    {
        _updating = true;
        foreach (FieldRow row in _rows)
        {
            if (row == except)
            {
                continue;
            }

            row.Box.Text = _converter.Get(row.Field);
            ToolParts.ShowError(row.Message, row.Box, null);
            if (_converter.HasWarning(row.Field))
            {
                // 不正なバイト列は置換文字と警告で示す(誤りではないので赤枠にはしない)。
                row.Message.Text = Loc.Get("Conv_Warning_Invalid");
                row.Message.Visibility = Visibility.Visible;
                row.Message.Foreground = ToolParts.ThemeBrush("SystemFillColorCautionBrush");
            }
            else
            {
                row.Message.Foreground = ToolParts.ThemeBrush("SystemFillColorCriticalBrush");
            }
        }

        _updating = false;
    }

    // ---- 文書との連携 (仕様 3) ----

    /// <summary>「カーソル位置から読む」: 選択範囲があれば選択範囲(最大 64 バイト)、なければカーソル位置から 8 バイト。</summary>
    public bool ReadFromCursor()
    {
        byte[]? bytes = Host.ReadAtCursor(ValueConverter.MaxBytes, Host.Editor?.HasSelection == true ? null : 8);
        if (bytes is null || bytes.Length == 0)
        {
            Host.Notify(Loc.Get("Conv_Error_NoData"), true);
            return false;
        }

        _converter.SetBytes(bytes);
        Refresh(null);
        return true;
    }

    public bool WriteAtCursor()
    {
        byte[] bytes = _converter.ToArray();
        if (bytes.Length == 0)
        {
            return false;
        }

        return Host.Write(bytes, "変換の値の書き込み", false);
    }

    public void SendToSearch()
    {
        byte[] bytes = _converter.ToArray();
        if (bytes.Length > 0)
        {
            Host.SendToSearch(bytes);
        }
    }

    /// <summary>テスト用: 欄にユーザーが入力したのと同じ処理。</summary>
    internal void TypeInto(string id, string text)
    {
        FieldRow row = _rows.First(r => r.Id == id);
        row.Box.Text = text;
        if (_updating)
        {
            return;
        }

        // TextChanged は同期で呼ばれるが、同じ文字列のときは呼ばれないので、直接処理する。
        Edited(row);
    }

    internal void SelectCodePage(string encodingId)
    {
        int index = FindCodePage(encodingId);
        if (index >= 0)
        {
            _codePage.SelectedIndex = index;
        }
    }

    internal void SelectEndian(bool big) => _endian.SelectedIndex = big ? 1 : 0;

    internal string Text(string id) => _rows.First(r => r.Id == id).Box.Text;
}
