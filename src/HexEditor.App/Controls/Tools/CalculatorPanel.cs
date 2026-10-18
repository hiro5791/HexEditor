using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Calculator;
using HexEditor.Core.Expressions;
using HexEditor.Core.View;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App.Controls.Tools;

/// <summary>電卓の式の名前・読み取り関数を、アクティブなビューの状態に結び付ける (TOOL-22 の仕様 2)。</summary>
internal sealed class EditorCalculatorContext(ToolHost host) : ICalculatorContext
{
    private EditorExpressionContext? Inner => host.Editor is { } editor ? new EditorExpressionContext(editor) : null;

    public bool HasDocument => host.Editor is not null;

    public bool BigEndian => host.BigEndian;

    public long Cursor => Inner?.Cursor ?? 0;

    public long Length => Inner?.Length ?? 0;

    public long SelectionStart => Inner?.SelectionStart ?? 0;

    public long SelectionLength => Inner?.SelectionLength ?? 0;

    public int SectorSize => Inner?.SectorSize ?? 512;

    public long? ClusterSize => null;

    public long? RecordLength => Inner?.RecordLength;

    public long RecordStart => Inner?.RecordStart ?? 0;

    public long? Bookmark(string name) => Inner?.Bookmark(name);

    public bool TryRead(long offset, Span<byte> destination) => Inner?.TryRead(offset, destination) ?? false;
}

/// <summary>
/// 電卓パネル (TOOL-22 の画面)。上に入力欄、その下に結果の表(基数ごとの行)、ボタン、履歴。プログラマモード (TOOL-23) では、結果の表の下に
/// ビット幅・符号の選択、ビットの表、ビットフィールドの選択、演算のボタンを置く。
/// </summary>
internal sealed partial class CalculatorPanel : ToolPanelBase
{
    private const string HistoryKey = "calculator.history";
    private const string ModeKey = "calculator.programmer";
    private const string WidthKey = "calculator.width";
    private const string SignedKey = "calculator.signed";

    private readonly EditorCalculatorContext _context;
    private readonly TextBox _input;
    private readonly TextBlock _error;
    private readonly TextBlock _overflow;
    private readonly ToggleButton _programmer;
    private readonly StackPanel _rowsPanel = new() { Spacing = 4 };
    private readonly TextBlock _hex, _dec, _decAlt, _oct, _bin, _size, _float;
    private readonly Grid _decAltRow, _hexRow, _octRow, _binRow, _sizeRow, _floatRow;
    private readonly ComboBox _writeSize = new();
    private readonly ComboBox _endian = new();
    private readonly Button _write;
    private readonly ListView _historyList = new();
    private readonly StackPanel _programmerPanel = new() { Spacing = 8 };
    private readonly ComboBox _widthChoice = new();
    private readonly ToggleButton _signedChoice = new();
    private readonly Panel _bitHost = new StackPanel { Orientation = Orientation.Vertical };
    private readonly List<Button> _bitCells = [];
    private readonly TextBlock _bitField = new();
    private readonly TextBlock _bitExpression = new();
    private readonly TextBox _operand = new();

    private IReadOnlyList<CalculatorHistoryEntry> _history;
    private CalcValue _answer = CalcValue.Zero;
    private CalcValue _current = CalcValue.Zero;
    private bool _hasValue;
    private CalculatorRows? _lastRows;
    private ulong _bits;
    private int _width = ProgrammerMode.DefaultWidth;
    private bool _signed;
    private int _selAnchor = -1, _selLow = -1, _selHigh = -1;
    private int _dragAnchor = -1;
    private bool _dragMoved;
    private bool _updating;

    public CalculatorPanel(ToolHost host)
        : base(host)
    {
        _context = new EditorCalculatorContext(host);
        _history = CalculatorHistory.Parse(AppState.GetString(HistoryKey, string.Empty));

        _input = ToolParts.Box("Calc_Input", Loc.Get("Calc_Input"));
        _input.PlaceholderText = Loc.Get("Calc_Placeholder");
        _input.TextChanged += (_, _) =>
        {
            if (!ToolParts.IsProgrammatic(_input))
            {
                Evaluate();
            }
        };
        _input.KeyDown += Input_KeyDown;
        _error = ToolParts.ErrorText("Calc_Error");
        _overflow = new TextBlock { Foreground = ToolParts.ThemeBrush("SystemFillColorCautionBrush"), Visibility = Visibility.Collapsed, Text = Loc.Get("Calc_Overflow") };
        AutomationProperties.SetAutomationId(_overflow, "Calc_Overflow");

        _programmer = new ToggleButton { Content = Loc.Get("Calc_Programmer") };
        AutomationProperties.SetAutomationId(_programmer, "Calc_Programmer");
        _programmer.Click += (_, _) => SetProgrammer(_programmer.IsChecked == true);

        (Grid hexRow, _hex) = ToolParts.ValueRow("Calc_Hex", "Calc_Row_Hex", "Calc_Copy_Hex", () => _lastRows?.CopyHex ?? string.Empty);
        (Grid decRow, _dec) = ToolParts.ValueRow("Calc_Dec", "Calc_Row_Dec", "Calc_Copy_Dec", () => PrimaryDecimalCopy());
        (_decAltRow, _decAlt) = ToolParts.ValueRow("Calc_DecUnsigned", "Calc_Row_DecUnsigned", "Calc_Copy_DecUnsigned", () => _lastRows?.CopyDecimalUnsigned ?? string.Empty);
        (Grid octRow, _oct) = ToolParts.ValueRow("Calc_Oct", "Calc_Row_Oct", "Calc_Copy_Oct", () => _lastRows?.CopyOctal ?? string.Empty);
        (Grid binRow, _bin) = ToolParts.ValueRow("Calc_Bin", "Calc_Row_Bin", "Calc_Copy_Bin", () => _lastRows?.CopyBinary ?? string.Empty);
        (Grid sizeRow, _size) = ToolParts.ValueRow("Calc_Size", "Calc_Row_Size", "Calc_Copy_Size", () => _lastRows?.CopyFileSize ?? string.Empty);
        (Grid floatRow, _float) = ToolParts.ValueRow("Calc_Float", "Calc_Row_Float", "Calc_Copy_Float", () => _lastRows?.CopyDecimalFloat ?? string.Empty);
        (_hexRow, _octRow, _binRow, _sizeRow, _floatRow) = (hexRow, octRow, binRow, sizeRow, floatRow);
        foreach (Grid row in new[] { hexRow, decRow, _decAltRow, octRow, binRow, sizeRow, floatRow })
        {
            _rowsPanel.Children.Add(row);
        }

        _writeSize.Header = Loc.Get("Calc_WriteSize");
        foreach (int size in new[] { 1, 2, 4, 8 })
        {
            _writeSize.Items.Add(Loc.Format("Calc_Bytes", size));
        }

        _writeSize.SelectedIndex = 2;
        AutomationProperties.SetAutomationId(_writeSize, "Calc_WriteSize");
        AutomationProperties.SetName(_writeSize, Loc.Get("Calc_WriteSize"));
        _endian.Header = Loc.Get("Calc_Endian");
        _endian.Items.Add(Loc.Get("Calc_EndianLittle"));
        _endian.Items.Add(Loc.Get("Calc_EndianBig"));
        _endian.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_endian, "Calc_Endian");
        AutomationProperties.SetName(_endian, Loc.Get("Calc_Endian"));

        Button goTo = ToolParts.TextButton("Calc_GoTo", "Calc_GoTo", GoToResult);
        _write = ToolParts.TextButton("Calc_Write", "Calc_Write", () => WriteResult());
        Button selectLength = ToolParts.TextButton("Calc_SelectLength", "Calc_SelectLength", SelectLength);
        var actions = new WrapPanel { };
        foreach (FrameworkElement e in new FrameworkElement[] { goTo, selectLength })
        {
            e.Margin = new Thickness(0, 0, 8, 8);
            actions.Children.Add(e);
        }

        var writeRow = new WrapPanel { };
        foreach (FrameworkElement e in new FrameworkElement[] { _writeSize, _endian, _write })
        {
            e.Margin = new Thickness(0, 0, 8, 8);
            e.VerticalAlignment = VerticalAlignment.Bottom;
            writeRow.Children.Add(e);
        }

        _historyList.IsItemClickEnabled = true;
        _historyList.SelectionMode = ListViewSelectionMode.None;
        _historyList.MaxHeight = 220;
        _historyList.ItemClick += History_ItemClick;
        AutomationProperties.SetAutomationId(_historyList, "Calc_History");
        AutomationProperties.SetName(_historyList, Loc.Get("Calc_History"));

        BuildProgrammerPanel();

        var body = new StackPanel();
        body.Children.Add(_input);
        body.Children.Add(_error);
        body.Children.Add(_overflow);
        body.Children.Add(_programmer);
        body.Children.Add(_rowsPanel);
        body.Children.Add(actions);
        body.Children.Add(writeRow);
        body.Children.Add(_programmerPanel);
        body.Children.Add(new TextBlock { Text = Loc.Get("Calc_History"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        body.Children.Add(_historyList);
        Content = ToolParts.Frame(body);

        _width = int.TryParse(AppState.GetString(WidthKey, "32"), NumberStyles.None, CultureInfo.InvariantCulture, out int w) && ProgrammerMode.Widths.Contains(w) ? w : 32;
        _signed = AppState.GetString(SignedKey, "0") == "1";
        _widthChoice.SelectedIndex = Array.IndexOf(ProgrammerMode.Widths, _width);
        _signedChoice.IsChecked = _signed;
        RefreshHistory();
        bool programmer = AppState.GetString(ModeKey, "0") == "1";
        _programmer.IsChecked = programmer;
        SetProgrammer(programmer, save: false);
        FlowDirection = FlowDirection.LeftToRight;
    }

    protected override Control FirstFocus => _input;

    public bool IsProgrammer => _programmer.IsChecked == true;

    /// <summary>プログラマモードに切り替える (コマンド「電卓 (プログラマモード)」)。</summary>
    public void ShowProgrammer()
    {
        _programmer.IsChecked = true;
        SetProgrammer(true);
    }

    protected override void OnHostChanged(object? sender, EventArgs e)
    {
        if (!_updating)
        {
            Evaluate(keepInput: true);
        }
    }

    // ---- 評価 ----

    private CalculatorOptions Options => IsProgrammer
        ? new CalculatorOptions { BitWidth = _width, Signed = _signed, Answer = _answer }
        : new CalculatorOptions { Answer = _answer };

    private void Evaluate(bool keepInput = false)
    {
        _ = keepInput;
        if (_updating)
        {
            return;
        }

        string text = _input.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ToolParts.ShowError(_error, _input, null);
            _overflow.Visibility = Visibility.Collapsed;
            SetRowsDim(true);
            return;
        }

        try
        {
            CalcResult result = CalculatorEvaluator.Evaluate(text, _context, Options);
            ToolParts.ShowError(_error, _input, null);
            _current = result.Value;
            _hasValue = true;
            _overflow.Visibility = result.Overflow && !IsProgrammer ? Visibility.Visible : Visibility.Collapsed;
            if (IsProgrammer && !result.Value.IsFloat)
            {
                _bits = CalculatorFormat.Bits(result.Value.Integer, _width);
            }

            ShowRows(result.Value);
            SetRowsDim(false);
            UpdateBits();
        }
        catch (ExpressionException ex)
        {
            ToolParts.ShowError(_error, _input, ToolParts.Describe(ex));
            _overflow.Visibility = Visibility.Collapsed;
            SetRowsDim(true);
        }
    }

    private void SetRowsDim(bool dim) => _rowsPanel.Opacity = dim ? 0.5 : 1.0;

    private string PrimaryDecimalCopy() =>
        _lastRows is null ? string.Empty
        : _lastRows.IsFloat ? _lastRows.CopyDecimalFloat ?? string.Empty
        : IsProgrammer && !_signed ? _lastRows.CopyDecimalUnsigned ?? string.Empty
        : _lastRows.CopyDecimalSigned ?? string.Empty;

    private void ShowRows(CalcValue value)
    {
        _lastRows = CalculatorFormat.Rows(value, Options, CultureInfo.CurrentCulture, padBinary: IsProgrammer);
        if (_lastRows.IsFloat)
        {
            _dec.Text = _lastRows.DecimalFloat ?? string.Empty;
            foreach (FrameworkElement row in new FrameworkElement[] { _hexRow, _decAltRow, _octRow, _binRow, _sizeRow })
            {
                row.Visibility = Visibility.Collapsed;
            }

            _floatRow.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (FrameworkElement row in new FrameworkElement[] { _hexRow, _octRow, _binRow, _sizeRow })
        {
            row.Visibility = Visibility.Visible;
        }

        _floatRow.Visibility = Visibility.Collapsed;
        _hex.Text = _lastRows.Hex ?? string.Empty;
        _dec.Text = (IsProgrammer && !_signed ? _lastRows.DecimalUnsigned : _lastRows.DecimalSigned) ?? string.Empty;
        _oct.Text = _lastRows.Octal ?? string.Empty;
        _bin.Text = _lastRows.Binary ?? string.Empty;
        _size.Text = _lastRows.FileSize ?? string.Empty;
        bool negative = !IsProgrammer && value.Integer < 0;
        _decAltRow.Visibility = negative ? Visibility.Visible : Visibility.Collapsed;
        _decAlt.Text = _lastRows.DecimalUnsigned ?? string.Empty;
    }

    // ---- 確定と履歴 ----

    private void Input_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            Commit();
            e.Handled = true;
        }
    }

    /// <summary>Enter で確定する: 履歴に加え、結果を <c>ans</c> にする。式が正しくなければ何もしない。</summary>
    public void Commit()
    {
        Evaluate();
        if (!_hasValue || _error.Visibility == Visibility.Visible || string.IsNullOrWhiteSpace(_input.Text))
        {
            return;
        }

        _answer = _current;
        _history = CalculatorHistory.Push(_history, _input.Text, CalculatorFormat.Summary(_current, IsProgrammer ? _width : 64));
        AppState.SetString(HistoryKey, CalculatorHistory.Serialize(_history));
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        _historyList.Items.Clear();
        foreach (CalculatorHistoryEntry entry in _history)
        {
            var item = new TextBlock { Text = $"{entry.Expression}  =  {entry.Result}", FontFamily = ToolParts.Mono, FlowDirection = FlowDirection.LeftToRight, TextTrimming = TextTrimming.CharacterEllipsis };
            AutomationProperties.SetName(item, $"{entry.Expression} = {entry.Result}");
            _historyList.Items.Add(item);
        }
    }

    private void History_ItemClick(object sender, ItemClickEventArgs e)
    {
        int index = _historyList.Items.IndexOf(e.ClickedItem);
        if (index >= 0 && index < _history.Count)
        {
            UseHistory(index);
        }
    }

    /// <summary>履歴の項目の式を入力欄に戻す (クリック)。</summary>
    public void UseHistory(int index)
    {
        _input.Text = _history[index].Expression;
        _input.Focus(FocusState.Programmatic);
        _input.SelectionStart = _input.Text.Length;
    }

    // ---- 結果の使いみち (仕様 8) ----

    private void GoToResult()
    {
        if (!_hasValue || _current.IsFloat || _current.Integer < 0 || _current.Integer > long.MaxValue)
        {
            Host.Notify(Loc.Get("Calc_Error_NoOffset"), true);
            return;
        }

        Host.GoTo((long)_current.Integer);
    }

    private void SelectLength()
    {
        if (Host.Editor is not { } editor)
        {
            Host.Notify(Loc.Get("Calc_Error_NoDocument"), true);
            return;
        }

        if (!_hasValue || _current.IsFloat || _current.Integer < 0 || editor.Cursor + (long)_current.Integer > editor.Document.Length)
        {
            Host.Notify(Loc.Get("Calc_Error_NoLength"), true);
            return;
        }

        editor.Select(editor.Cursor, (long)_current.Integer);
    }

    /// <summary>「カーソル位置に書き込む」: サイズ (1 / 2 / 4 / 8 バイト) とエンディアンを選んだ値。Undo できる。</summary>
    public bool WriteResult()
    {
        if (!_hasValue || _error.Visibility == Visibility.Visible)
        {
            return false;
        }

        if (Host.Editor is null)
        {
            Host.Notify(Loc.Get("Calc_Error_NoDocument"), true);
            return false;
        }

        int size = new[] { 1, 2, 4, 8 }[Math.Clamp(_writeSize.SelectedIndex, 0, 3)];
        bool big = _endian.SelectedIndex == 1;
        byte[]? bytes = BytesOf(_current, size, big);
        if (bytes is null)
        {
            Host.Notify(Loc.Format("Calc_Error_TooBig", size), true);
            return false;
        }

        return Host.Write(bytes, "電卓の値の書き込み", false);
    }

    private static byte[]? BytesOf(CalcValue value, int size, bool bigEndian)
    {
        ulong bits;
        if (value.IsFloat)
        {
            switch (size)
            {
                case 4:
                    bits = BitConverter.SingleToUInt32Bits((float)value.Double);
                    break;
                case 8:
                    bits = BitConverter.DoubleToUInt64Bits(value.Double);
                    break;
                default:
                    return null;
            }
        }
        else
        {
            Int128 v = value.Integer;
            int width = size * 8;
            Int128 min = -(Int128.One << (width - 1));
            Int128 max = (Int128.One << width) - 1;
            if (v < min || v > max)
            {
                return null;
            }

            bits = (ulong)(v & ulong.MaxValue);
        }

        byte[] bytes = new byte[size];
        for (int i = 0; i < size; i++)
        {
            bytes[bigEndian ? size - 1 - i : i] = (byte)(bits >> (8 * i));
        }

        return bytes;
    }

    // ---- プログラマモード (TOOL-23) ----

    private void SetProgrammer(bool on, bool save = true)
    {
        _programmerPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (save)
        {
            AppState.SetString(ModeKey, on ? "1" : "0");
        }

        if (on && _hasValue && !_current.IsFloat)
        {
            _bits = CalculatorFormat.Bits(_current.Integer, _width);
        }

        BuildBits();
        Evaluate();
    }

    private void BuildProgrammerPanel()
    {
        _widthChoice.Header = Loc.Get("Calc_Width");
        foreach (int w in ProgrammerMode.Widths)
        {
            _widthChoice.Items.Add(Loc.Format("Calc_Bits", w));
        }

        AutomationProperties.SetAutomationId(_widthChoice, "Calc_Width");
        AutomationProperties.SetName(_widthChoice, Loc.Get("Calc_Width"));
        _widthChoice.SelectionChanged += (_, _) =>
        {
            if (_widthChoice.SelectedIndex < 0 || _updating)
            {
                return;
            }

            SetWidth(ProgrammerMode.Widths[_widthChoice.SelectedIndex]);
        };
        _signedChoice.Content = Loc.Get("Calc_Signed");
        AutomationProperties.SetAutomationId(_signedChoice, "Calc_Signed");
        _signedChoice.Click += (_, _) => SetSigned(_signedChoice.IsChecked == true);
        var choices = new WrapPanel();
        foreach (FrameworkElement e in new FrameworkElement[] { _widthChoice, _signedChoice })
        {
            e.Margin = new Thickness(0, 0, 8, 8);
            e.VerticalAlignment = VerticalAlignment.Bottom;
            choices.Children.Add(e);
        }

        Button read = ToolParts.TextButton("Calc_ReadCursor", "Calc_ReadCursor", ReadFromCursor);
        read.Margin = new Thickness(0, 0, 8, 8);
        choices.Children.Add(read);
        _programmerPanel.Children.Add(choices);

        _bitHost.FlowDirection = FlowDirection.LeftToRight;
        AutomationProperties.SetAutomationId(_bitHost, "Calc_Bits");
        AutomationProperties.SetName(_bitHost, Loc.Get("Calc_BitTable"));
        _bitHost.KeyDown += Bits_KeyDown;
        _bitHost.PointerMoved += Bits_PointerMoved;
        _bitHost.PointerReleased += Bits_PointerReleased;
        _programmerPanel.Children.Add(_bitHost);

        _bitField.FontFamily = ToolParts.Mono;
        _bitField.IsTextSelectionEnabled = true;
        _bitField.FlowDirection = FlowDirection.LeftToRight;
        AutomationProperties.SetAutomationId(_bitField, "Calc_BitFieldValue");
        _bitExpression.FontFamily = ToolParts.Mono;
        _bitExpression.IsTextSelectionEnabled = true;
        _bitExpression.FlowDirection = FlowDirection.LeftToRight;
        AutomationProperties.SetAutomationId(_bitExpression, "Calc_BitFieldExpression");
        Button copyExpression = ToolParts.CopyButton("Calc_Copy_BitFieldExpression", Loc.Get("Calc_CopyExpression"), () => _bitExpression.Text);
        var field = new StackPanel { Spacing = 4 };
        field.Children.Add(_bitField);
        var expressionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        expressionRow.Children.Add(_bitExpression);
        expressionRow.Children.Add(copyExpression);
        field.Children.Add(expressionRow);
        _programmerPanel.Children.Add(field);

        _operand.Header = Loc.Get("Calc_Operand");
        _operand.FontFamily = ToolParts.Mono;
        _operand.FlowDirection = FlowDirection.LeftToRight;
        AutomationProperties.SetAutomationId(_operand, "Calc_Operand");
        AutomationProperties.SetName(_operand, Loc.Get("Calc_Operand"));
        _programmerPanel.Children.Add(_operand);
        var ops = new WrapPanel();
        foreach ((ProgrammerOperation op, string key) in new[]
        {
            (ProgrammerOperation.And, "AND"), (ProgrammerOperation.Or, "OR"), (ProgrammerOperation.Xor, "XOR"), (ProgrammerOperation.Not, "NOT"),
            (ProgrammerOperation.Nand, "NAND"), (ProgrammerOperation.Nor, "NOR"), (ProgrammerOperation.ShiftLeft, "ShiftLeft"),
            (ProgrammerOperation.ShiftRightLogical, "ShiftRight"), (ProgrammerOperation.ShiftRightArithmetic, "ShiftRightArithmetic"),
            (ProgrammerOperation.RotateLeft, "RotateLeft"), (ProgrammerOperation.RotateRight, "RotateRight"),
            (ProgrammerOperation.Negate, "Negate"), (ProgrammerOperation.ByteSwap, "ByteSwap"),
        })
        {
            ProgrammerOperation captured = op;
            var button = new Button { Content = Loc.Get("Calc_Op_" + key), Margin = new Thickness(0, 0, 6, 6) };
            AutomationProperties.SetAutomationId(button, "Calc_Op_" + key);
            button.Click += (_, _) => ApplyOperation(captured);
            ops.Children.Add(button);
        }

        _programmerPanel.Children.Add(ops);
    }

    private void SetWidth(int width)
    {
        _width = width;
        AppState.SetString(WidthKey, width.ToString(CultureInfo.InvariantCulture));
        _bits = ProgrammerMode.Mask(_bits, width);
        ClearBitSelection();
        BuildBits();
        Evaluate();
    }

    private void SetSigned(bool signed)
    {
        _signed = signed;
        AppState.SetString(SignedKey, signed ? "1" : "0");
        Evaluate();
    }

    /// <summary>ビット幅と符号(テスト用に直接指定できる)。</summary>
    internal void SetWidthAndSign(int width, bool signed)
    {
        _updating = true;
        _widthChoice.SelectedIndex = Array.IndexOf(ProgrammerMode.Widths, width);
        _signedChoice.IsChecked = signed;
        _updating = false;
        _signed = signed;
        SetWidth(width);
    }

    private void BuildBits()
    {
        _bitHost.Children.Clear();
        _bitCells.Clear();
        if (!IsProgrammer)
        {
            return;
        }

        // 上位から 4 ビットごとの組。1 行に 16 ビット(4 組)。各ビットの下にビット番号を表示する。
        int perRow = 16;
        for (int rowTop = _width - 1; rowTop >= 0; rowTop -= perRow)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 6) };
            for (int groupTop = rowTop; groupTop > rowTop - perRow && groupTop >= 0; groupTop -= 4)
            {
                var group = new StackPanel { Orientation = Orientation.Horizontal };
                for (int bit = groupTop; bit > groupTop - 4; bit--)
                {
                    var cell = new Button
                    {
                        Width = 26,
                        Height = 30,
                        Padding = new Thickness(0),
                        Content = "0",
                        FontFamily = ToolParts.Mono,
                        Tag = bit,
                        BorderThickness = new Thickness(1),
                    };
                    AutomationProperties.SetAutomationId(cell, "Calc_Bit_" + bit.ToString(CultureInfo.InvariantCulture));
                    int index = bit;
                    cell.Click += (_, _) => Bit_Click(index);
                    Button focusCell = cell;
                    cell.GotFocus += (_, _) =>
                    {
                        // 実際にフォーカスのあるセルだけを記録する(遅れて届いたイベントで、動かした後のフォーカスを上書きしない)。
                        if (FocusManager.GetFocusedElement(focusCell.XamlRoot) != focusCell)
                        {
                            return;
                        }

                        // 矢印キー以外(Tab やクリックなど)でフォーカスが来たら、範囲選択の起点をそのビットにし直す。
                        // 矢印キーによる移動 (FocusBit) で来たフォーカスは、遅れて届いても起点を変えない。
                        if (_expectedFocusBit == index)
                        {
                            _expectedFocusBit = -1;
                        }
                        else
                        {
                            _selAnchor = -1;
                        }

                        _bitFocus = index;
                    };
                    cell.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => Bit_PointerPressed(index, e)), true);
                    _bitCells.Add(cell);
                    var label = new TextBlock
                    {
                        Text = bit.ToString(CultureInfo.InvariantCulture),
                        FontSize = 10,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = ToolParts.ThemeBrush("TextFillColorSecondaryBrush"),
                    };
                    AutomationProperties.SetAccessibilityView(label, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                    var column = new StackPanel { Spacing = 0 };
                    column.Children.Add(cell);
                    column.Children.Add(label);
                    group.Children.Add(column);
                }

                row.Children.Add(group);
            }

            _bitHost.Children.Add(row);
        }

        UpdateBits();
    }

    /// <summary>ビットのセルの表示と名前(スクリーンリーダー用「ビット 31、値 1」)を、現在の値に合わせる。</summary>
    private void UpdateBits()
    {
        if (!IsProgrammer)
        {
            return;
        }

        foreach (Button cell in _bitCells)
        {
            int bit = (int)cell.Tag;
            int value = ProgrammerMode.GetBit(_bits, bit) ? 1 : 0;
            cell.Content = value.ToString(CultureInfo.InvariantCulture);
            AutomationProperties.SetName(cell, Loc.Format("Calc_BitName", bit, value));
            bool selected = _selLow >= 0 && bit >= _selLow && bit <= _selHigh;
            cell.Background = selected ? ToolParts.ThemeBrush("AccentFillColorSecondaryBrush") : ToolParts.ThemeBrush("ControlFillColorDefaultBrush");
            cell.Foreground = selected ? ToolParts.ThemeBrush("TextOnAccentFillColorPrimaryBrush") : ToolParts.ThemeBrush("TextFillColorPrimaryBrush");
            cell.BorderThickness = new Thickness(selected ? 2 : 1);
            cell.FontWeight = value == 1 ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        }

        if (_selLow >= 0)
        {
            BitFieldSelection field = ProgrammerMode.Select(_bits, _selLow, _selHigh, _width);
            _bitField.Text = Loc.Format("Calc_BitFieldValue", _selLow, _selHigh, "0x" + field.Value.ToString("X", CultureInfo.InvariantCulture),
                field.Value.ToString("N0", CultureInfo.CurrentCulture));
            _bitExpression.Text = field.Expression;
        }
        else
        {
            _bitField.Text = string.Empty;
            _bitExpression.Text = string.Empty;
        }
    }

    /// <summary>ビットを反転する(クリック、Space)。値は式の欄に 16 進で入れ直し、他の基数の表示も更新する。</summary>
    public void ToggleBit(int index)
    {
        _bits = ProgrammerMode.ToggleBit(_bits, index, _width);
        SetValueFromBits();
    }

    private void SetValueFromBits()
    {
        _updating = true;
        ToolParts.SetText(_input, "0x" + _bits.ToString("X", CultureInfo.InvariantCulture));
        _updating = false;
        Evaluate();
    }

    private void Bit_Click(int index)
    {
        if (_dragMoved)
        {
            _dragMoved = false;
            return;
        }

        ToggleBit(index);
        FocusBit(index);
    }

    private void FocusBit(int index)
    {
        _expectedFocusBit = index;
        _bitFocus = index;
        _bitCells.FirstOrDefault(c => (int)c.Tag == index)?.Focus(FocusState.Keyboard);
    }

    /// <summary>矢印キーで移したフォーカスの行き先(その GotFocus では範囲選択の起点を変えない)。</summary>
    private int _expectedFocusBit = -1;

    /// <summary>フォーカスのあるビット(なければ -1)。</summary>
    private int _bitFocus = -1;

    private void Bit_PointerPressed(int index, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(_bitHost).Properties.IsLeftButtonPressed)
        {
            _dragAnchor = index;
            _dragMoved = false;
        }
    }

    private void Bits_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragAnchor < 0 || !e.GetCurrentPoint(_bitHost).Properties.IsLeftButtonPressed)
        {
            return;
        }

        foreach (UIElement element in VisualTreeHelper.FindElementsInHostCoordinates(e.GetCurrentPoint(null).Position, _bitHost))
        {
            if (element is Button { Tag: int bit } && bit != _dragAnchor)
            {
                _dragMoved = true;
                SelectBits(_dragAnchor, bit);
                return;
            }
        }
    }

    private void Bits_PointerReleased(object sender, PointerRoutedEventArgs e) => _dragAnchor = -1;

    /// <summary>ビットの範囲 [a, b] を選ぶ(ドラッグ、Shift+矢印)。値とマスク・シフトの式を表示する。</summary>
    public void SelectBits(int a, int b)
    {
        _selAnchor = a;
        _selLow = Math.Min(a, b);
        _selHigh = Math.Max(a, b);
        UpdateBits();
    }

    private void ClearBitSelection()
    {
        _selAnchor = _selLow = _selHigh = -1;
    }

    private void Bits_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { Tag: int bit })
        {
            return;
        }

        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        e.Handled = HandleBitKey(bit, e.Key, shift);
    }

    /// <summary>
    /// ビットの表のキー操作: ← / → で隣のビットへ (左が上位)、Home / End で最上位 / 最下位、Shift 付きで範囲を選ぶ、Esc で選択を解除。
    /// フォーカスのあるビットが <paramref name="bit"/>。処理したら true。
    /// </summary>
    internal bool HandleBitKey(int bit, VirtualKey key, bool shift)
    {
        int next = bit;
        switch (key)
        {
            case VirtualKey.Left:
                next = Math.Min(_width - 1, bit + 1);
                break;
            case VirtualKey.Right:
                next = Math.Max(0, bit - 1);
                break;
            case VirtualKey.Home:
                next = _width - 1;
                break;
            case VirtualKey.End:
                next = 0;
                break;
            case VirtualKey.Escape:
                ClearBitSelection();
                UpdateBits();
                return true;
            default:
                return false;
        }

        if (shift)
        {
            int anchor = _selAnchor >= 0 ? _selAnchor : bit;
            SelectBits(anchor, next);
        }
        else
        {
            ClearBitSelection();
            UpdateBits();
        }

        FocusBit(next);
        return true;
    }

    private void ReadFromCursor()
    {
        byte[]? bytes = Host.ReadAtCursor(8, length: _width / 8);
        if (bytes is null || bytes.Length < _width / 8)
        {
            Host.Notify(Loc.Get("Calc_Error_NoData"), true);
            return;
        }

        if (ProgrammerMode.FromBytes(bytes, _width, _endian.SelectedIndex == 1) is { } bits)
        {
            _bits = bits;
            SetValueFromBits();
        }
    }

    private void ApplyOperation(ProgrammerOperation op)
    {
        ulong operand = 0;
        if (op is ProgrammerOperation.And or ProgrammerOperation.Or or ProgrammerOperation.Xor or ProgrammerOperation.Nand or ProgrammerOperation.Nor)
        {
            if (!CalculatorEvaluator.TryEvaluate(_operand.Text, _context, out CalcResult? result, out ExpressionException? error, Options) || result!.Value.IsFloat)
            {
                ToolParts.ShowError(_error, null, error is null ? Loc.Get("GoTo_Error_NotInteger") : ToolParts.Describe(error));
                return;
            }

            operand = CalculatorFormat.Bits(result.Value.Integer, _width);
        }

        _bits = ProgrammerMode.Apply(op, _bits, operand, _width, _signed);
        SetValueFromBits();
    }
}
