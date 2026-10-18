using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Expressions;
using HexEditor.Core.Printing;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls.Tools;

/// <summary>印刷ダイアログの使い方。</summary>
internal enum PrintDialogMode
{
    /// <summary>印刷 (プリンタと部数を選ぶ。「PDF として保存...」のボタンもある)。</summary>
    Print,

    /// <summary>PDF への出力 (プリンタと部数は使わない。用紙は A4 / Letter / A3 / Legal)。</summary>
    Pdf,
}

/// <summary>
/// 印刷ダイアログの中身 (TOOL-01 の仕様 1・2): 左に設定、右にプレビュー (TOOL-02)。設定を変えるたびに <see cref="SettingsChanged"/> を起こし、
/// 呼び出し側が計画とプレビューを作り直す。
/// </summary>
internal sealed class PrintDialogContent : UserControl
{
    private readonly PrintDialogMode _mode;
    private readonly bool _inches;
    private readonly ViewSettings _view;
    private readonly IReadOnlyList<(long Start, long Length)> _selection;
    private readonly long _documentLength;
    private readonly IExpressionContext _context;
    private readonly ComboBox _printer = new();
    private readonly RadioButton _rangeAll;
    private readonly RadioButton _rangeSelection;
    private readonly RadioButton _rangeOffsets;
    private readonly RadioButton _rangePages;
    private readonly TextBox _offsets;
    private readonly TextBox _pages;
    private readonly ComboBox _paper;
    private readonly ComboBox _orientation;
    private readonly TextBox[] _margins = new TextBox[4];
    private readonly ComboBox _font;
    private readonly ComboBox _fontSize;
    private readonly ComboBox _bytesPerRow;
    private readonly CheckBox _colOffset;
    private readonly CheckBox _colHex;
    private readonly CheckBox _colText;
    private readonly RadioButton _bw;
    private readonly RadioButton _color;
    private readonly TextBox[] _header = new TextBox[3];
    private readonly TextBox[] _footer = new TextBox[3];
    private readonly TextBox _copies;
    private readonly TextBlock _status = new();
    private readonly TextBlock _error = ToolParts.ErrorText("Print_Error");
    private readonly IReadOnlyList<PaperKind> _papers;
    private bool _loading;

    public PrintDialogContent(PrintDialogMode mode, PrintSettings initial, ViewSettings view, IReadOnlyList<(long Start, long Length)> selection, long documentLength,
        IReadOnlyList<string> printers, IExpressionContext context, PrintPreview preview)
    {
        _mode = mode;
        _view = view;
        _selection = selection;
        _documentLength = documentLength;
        _context = context;
        Preview = preview;
        _inches = !SafeIsMetric();
        _papers = mode == PrintDialogMode.Pdf ? PaperSizes.PdfKinds : [PaperKind.A4, PaperKind.Letter, PaperKind.A3, PaperKind.Legal, PaperKind.A5];

        // プリンタ
        foreach (string name in printers)
        {
            _printer.Items.Add(name);
        }

        _printer.Header = Loc.Get("Print_Printer");
        _printer.MinWidth = 260;
        AutomationProperties.SetAutomationId(_printer, "Print_Printer");
        _printer.SelectedIndex = initial.Printer is { } chosen && printers.ToList().IndexOf(chosen) is var at and >= 0 ? at
            : GdiPrinter.DefaultName() is { } def && printers.ToList().IndexOf(def) is var d and >= 0 ? d : printers.Count > 0 ? 0 : -1;
        _printer.SelectionChanged += (_, _) => Changed();

        // 印刷範囲
        bool hasSelection = selection.Count > 0;
        PrintRangeKind kind = initial.Range == PrintRangeKind.Selection && !hasSelection ? PrintRangeKind.All : initial.Range;
        _rangeAll = DialogParts.Radio("Print_RangeAll", Loc.Get("Print_RangeAll"), "PrintRange", kind == PrintRangeKind.All);
        _rangeSelection = DialogParts.Radio("Print_RangeSelection", Loc.Get("Print_RangeSelection"), "PrintRange", kind == PrintRangeKind.Selection);
        _rangeSelection.IsEnabled = hasSelection;
        _rangeOffsets = DialogParts.Radio("Print_RangeOffsets", Loc.Get("Print_RangeOffsets"), "PrintRange", kind == PrintRangeKind.Offsets);
        _rangePages = DialogParts.Radio("Print_RangePages", Loc.Get("Print_RangePages"), "PrintRange", kind == PrintRangeKind.Pages);
        _offsets = DialogParts.Field("Print_Offsets", Loc.Get("Print_Offsets"), initial.Offsets.Length > 0 ? $"0x{initial.Offsets.Start:X}:0x{initial.Offsets.Length:X}" : string.Empty);
        _pages = DialogParts.Field("Print_Pages", Loc.Get("Print_PagesHint"), initial.Pages);
        foreach (RadioButton radio in new[] { _rangeAll, _rangeSelection, _rangeOffsets, _rangePages })
        {
            radio.Checked += (_, _) => Changed();
        }

        _offsets.TextChanged += (_, _) => Changed();
        _pages.TextChanged += (_, _) => Changed();

        // 用紙・向き・余白
        _paper = DialogParts.Combo("Print_Paper", Loc.Get("Print_Paper"), _papers.Select(p => p.ToString()), Math.Max(0, _papers.ToList().IndexOf(initial.Paper)));
        _orientation = DialogParts.Combo("Print_Orientation", Loc.Get("Print_Orientation"), [Loc.Get("Print_Portrait"), Loc.Get("Print_Landscape")], initial.Landscape ? 1 : 0);
        string[] marginNames = ["Top", "Bottom", "Left", "Right"];
        double[] marginValues = [initial.MarginTopMm, initial.MarginBottomMm, initial.MarginLeftMm, initial.MarginRightMm];
        for (int i = 0; i < 4; i++)
        {
            _margins[i] = DialogParts.Field("Print_Margin" + marginNames[i], Loc.Get("Print_Margin" + marginNames[i]) + (_inches ? " (in)" : " (mm)"),
                MarginText(marginValues[i]), monospace: false);
            _margins[i].MinWidth = 90;
            _margins[i].TextChanged += (_, _) => Changed();
        }

        _paper.SelectionChanged += (_, _) => Changed();
        _orientation.SelectionChanged += (_, _) => Changed();

        // フォント
        var fonts = FontCatalog.Families().Where(f => f.Monospaced).Select(f => f.Name).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!fonts.Contains(initial.FontFamily, StringComparer.OrdinalIgnoreCase))
        {
            fonts.Insert(0, initial.FontFamily);
        }

        _font = DialogParts.Combo("Print_Font", Loc.Get("Print_Font"), fonts, Math.Max(0, fonts.FindIndex(f => string.Equals(f, initial.FontFamily, StringComparison.OrdinalIgnoreCase))));
        _fontSize = DialogParts.Combo("Print_FontSize", Loc.Get("Print_FontSize"), PrintSettings.FontSizes.Select(s => s.ToString("0.##", CultureInfo.CurrentCulture) + " pt"),
            Math.Max(0, PrintSettings.FontSizes.ToList().IndexOf(Math.Round(initial.FontSize))));
        _fontSize.MinWidth = 100;
        int[] bytes = [0, 4, 8, 12, 16, 24, 32, 48, 64];
        _bytesPerRow = DialogParts.Combo("Print_BytesPerRow", Loc.Get("Print_BytesPerRow"),
            bytes.Select(b => b == 0 ? Loc.Get("Print_FitToPaper") : b.ToString(CultureInfo.CurrentCulture)), Math.Max(0, Array.IndexOf(bytes, initial.BytesPerRow)));
        _bytesPerRow.MinWidth = 140;
        _font.SelectionChanged += (_, _) => Changed();
        _fontSize.SelectionChanged += (_, _) => Changed();
        _bytesPerRow.SelectionChanged += (_, _) => Changed();

        // 列
        _colOffset = DialogParts.Check("Print_ColOffset", Loc.Get("Print_ColOffset"), view.ShowOffsetColumn);
        _colHex = DialogParts.Check("Print_ColHex", Loc.Get("Print_ColHex"), view.ShowHexColumn);
        _colText = DialogParts.Check("Print_ColText", Loc.Get("Print_ColText"), view.ShowTextColumn);
        foreach (CheckBox box in new[] { _colOffset, _colHex, _colText })
        {
            box.Checked += (_, _) => Changed();
            box.Unchecked += (_, _) => Changed();
        }

        // 色
        _bw = DialogParts.Radio("Print_ColorBW", Loc.Get("Print_ColorBW"), "PrintColor", initial.Color == PrintColorMode.BlackWhite);
        _color = DialogParts.Radio("Print_ColorColor", Loc.Get("Print_ColorColor"), "PrintColor", initial.Color == PrintColorMode.Color);
        _bw.Checked += (_, _) => Changed();
        _color.Checked += (_, _) => Changed();

        // ヘッダ・フッタ
        string[] slots = ["Left", "Center", "Right"];
        string[] header = [initial.Header.Left, initial.Header.Center, initial.Header.Right];
        string[] footer = [initial.Footer.Left, initial.Footer.Center, initial.Footer.Right];
        for (int i = 0; i < 3; i++)
        {
            _header[i] = DialogParts.Field("Print_Header" + slots[i], Loc.Get("Print_Header" + slots[i]), header[i], monospace: false);
            _footer[i] = DialogParts.Field("Print_Footer" + slots[i], Loc.Get("Print_Footer" + slots[i]), footer[i], monospace: false);
            _header[i].MinWidth = _footer[i].MinWidth = 100;
            _header[i].TextChanged += (_, _) => Changed();
            _footer[i].TextChanged += (_, _) => Changed();
        }

        _copies = DialogParts.Field("Print_Copies", Loc.Get("Print_Copies"), initial.Copies.ToString(CultureInfo.CurrentCulture), monospace: false);
        _copies.MinWidth = 90;
        _copies.TextChanged += (_, _) => Changed();

        Button saveDefaults = ToolParts.TextButton("Print_SaveDefaults", "Print_SaveDefaults", () => SaveDefaultsRequested?.Invoke(this, EventArgs.Empty));
        _status.TextWrapping = TextWrapping.Wrap;
        _status.FlowDirection = FlowDirection.LeftToRight;
        AutomationProperties.SetAutomationId(_status, "Print_Status");

        TextBlock Heading(string key) => new() { Text = Loc.Get(key), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        StackPanel Row(params UIElement[] items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (UIElement item in items)
            {
                row.Children.Add(item);
            }

            return row;
        }

        var left = new StackPanel { Spacing = 8, Width = 400 };
        if (mode == PrintDialogMode.Print)
        {
            left.Children.Add(_printer);
        }

        foreach (UIElement element in new UIElement[]
        {
            Heading("Print_Range"), _rangeAll, _rangeSelection, _rangeOffsets, _offsets, _rangePages, _pages,
            Row(_paper, _orientation), Heading("Print_Margins"), Row(_margins[0], _margins[1], _margins[2], _margins[3]),
            Row(_font, _fontSize), _bytesPerRow, Heading("Print_Columns"), Row(_colOffset, _colHex, _colText),
            Heading("Print_ColorMode"), Row(_bw, _color), Heading("Print_HeaderFooter"),
            Row(_header[0], _header[1], _header[2]), Row(_footer[0], _footer[1], _footer[2]),
        })
        {
            left.Children.Add(element);
        }

        if (mode == PrintDialogMode.Print)
        {
            left.Children.Add(_copies);
        }

        left.Children.Add(Row(saveDefaults));
        left.Children.Add(_status);
        left.Children.Add(_error);

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 560, Padding = new Thickness(0, 0, 12, 0) });
        Grid.SetColumn(preview, 1);
        preview.MinWidth = 380;
        preview.MinHeight = 480;
        grid.Children.Add(preview);
        Content = grid;
    }

    public PrintPreview Preview { get; }

    /// <summary>設定が変わった。</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>「既定として保存」が押された。</summary>
    public event EventHandler? SaveDefaultsRequested;

    private static bool SafeIsMetric()
    {
        try
        {
            return RegionInfo.CurrentRegion.IsMetric;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private string MarginText(double mm) => _inches ? (mm / 25.4).ToString("0.##", CultureInfo.CurrentCulture) : mm.ToString("0.##", CultureInfo.CurrentCulture);

    private bool TryMargin(TextBox box, out double mm)
    {
        mm = 0;
        if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double value) || value < 0)
        {
            return false;
        }

        mm = _inches ? value * 25.4 : value;
        return mm <= 50.0001;
    }

    private void Changed()
    {
        if (!_loading)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>プリンタの名前 (選んでいなければ null)。</summary>
    public string? SelectedPrinter => _printer.SelectedItem as string;

    /// <summary>部数 (1〜999。読めなければ null)。</summary>
    public int? Copies => int.TryParse(_copies.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out int n) && n is >= 1 and <= 999 ? n : null;

    /// <summary>状態の表示 (ページ数・警告)。</summary>
    public void SetStatus(string text, string? error)
    {
        _status.Text = text;
        ToolParts.ShowError(_error, null, error);
    }

    /// <summary>
    /// 画面の設定から印刷の設定を作る。読めない値 (余白の数値・オフセットの範囲・ページの範囲) があれば null と、その説明。
    /// </summary>
    public PrintSettings? Collect(out string? problem)
    {
        problem = null;
        double[] margins = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!TryMargin(_margins[i], out margins[i]))
            {
                problem = Loc.Get("Print_Error_Margin");
                _margins[i].Focus(FocusState.Programmatic);
                return null;
            }
        }

        PrintRangeKind kind = _rangeSelection.IsChecked == true ? PrintRangeKind.Selection
            : _rangeOffsets.IsChecked == true ? PrintRangeKind.Offsets
            : _rangePages.IsChecked == true ? PrintRangeKind.Pages : PrintRangeKind.All;
        (long Start, long Length) offsets = (0, 0);
        if (kind == PrintRangeKind.Offsets)
        {
            if (!RangeExpression.TryEvaluate(_offsets.Text, _context, DefaultRadix.Hexadecimal, out long start, out long length, out ExpressionException? error))
            {
                problem = error is null ? Loc.Get("Print_Error_Offsets") : DialogParts.ExpressionError(error);
                return null;
            }

            offsets = (start, length);
        }

        int[] sizes = [.. PrintSettings.FontSizes.Select(s => (int)s)];
        int[] bytes = [0, 4, 8, 12, 16, 24, 32, 48, 64];
        return new PrintSettings
        {
            Range = kind,
            Selection = _selection,
            Offsets = offsets,
            Pages = _pages.Text,
            Paper = _papers[Math.Max(0, _paper.SelectedIndex)],
            Landscape = _orientation.SelectedIndex == 1,
            MarginTopMm = margins[0],
            MarginBottomMm = margins[1],
            MarginLeftMm = margins[2],
            MarginRightMm = margins[3],
            FontFamily = _font.SelectedItem as string ?? SystemFontCatalog.DefaultFamily,
            FontSize = PrintSettings.FontSizes[Math.Max(0, _fontSize.SelectedIndex)],
            BytesPerRow = bytes[Math.Max(0, _bytesPerRow.SelectedIndex)],
            View = _view with { ShowOffsetColumn = _colOffset.IsChecked == true, ShowHexColumn = _colHex.IsChecked == true, ShowTextColumn = _colText.IsChecked == true },
            Color = _color.IsChecked == true ? PrintColorMode.Color : PrintColorMode.BlackWhite,
            Header = new HeaderFooterText(_header[0].Text, _header[1].Text, _header[2].Text),
            Footer = new HeaderFooterText(_footer[0].Text, _footer[1].Text, _footer[2].Text),
            Copies = Copies ?? 1,
            Printer = SelectedPrinter,
        }.Normalize() with { Offsets = offsets };
    }

    // ---- テスト用の操作 (利用者が入力欄・選択肢を変えたのと同じ) ----

    internal void TestSetFontSize(double size)
    {
        int index = PrintSettings.FontSizes.ToList().IndexOf(size);
        if (index >= 0)
        {
            _fontSize.SelectedIndex = index;
        }
    }

    internal void TestSetRange(PrintRangeKind kind, string? text = null)
    {
        switch (kind)
        {
            case PrintRangeKind.All:
                _rangeAll.IsChecked = true;
                break;
            case PrintRangeKind.Selection:
                _rangeSelection.IsChecked = true;
                break;
            case PrintRangeKind.Offsets:
                _offsets.Text = text ?? _offsets.Text;
                _rangeOffsets.IsChecked = true;
                break;
            default:
                _pages.Text = text ?? _pages.Text;
                _rangePages.IsChecked = true;
                break;
        }
    }

    internal void TestSetColor(bool color) => (color ? _color : _bw).IsChecked = true;

    internal void TestSetText(string slot, string text)
    {
        TextBox? box = slot switch
        {
            "headerLeft" => _header[0],
            "headerCenter" => _header[1],
            "headerRight" => _header[2],
            "footerLeft" => _footer[0],
            "footerCenter" => _footer[1],
            "footerRight" => _footer[2],
            _ => null,
        };
        if (box is not null)
        {
            box.Text = text;
        }
    }

    internal void TestSetPaper(PaperKind kind)
    {
        int index = _papers.ToList().IndexOf(kind);
        if (index >= 0)
        {
            _paper.SelectedIndex = index;
        }
    }

    internal void TestSetBytesPerRow(int value) => _bytesPerRow.SelectedIndex = Math.Max(0, new[] { 0, 4, 8, 12, 16, 24, 32, 48, 64 }.ToList().IndexOf(value));

    internal bool TestSelectionEnabled => _rangeSelection.IsEnabled;

    internal PrintRangeKind TestRange => _rangeSelection.IsChecked == true ? PrintRangeKind.Selection
        : _rangeOffsets.IsChecked == true ? PrintRangeKind.Offsets
        : _rangePages.IsChecked == true ? PrintRangeKind.Pages : PrintRangeKind.All;

    internal PrintDialogMode Mode => _mode;

    internal long DocumentLength => _documentLength;
}
