#if HEX_TEST_HOOKS
using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.Core.Calculator;
using HexEditor.Core.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls.Tools;

// テスト用の命令の通り道 (MainWindow.ToolsCalcTestCommands.cs) が使う、電卓と変換・参照ツールのパネルの読み書き。
// 利用者の入力欄・ボタンの操作と同じ処理を呼び (マウスは使わない)、見えている内容を JSON で返す。

internal sealed partial class CalculatorPanel
{
    internal void TestSetInput(string text)
    {
        _input.Text = text;
        Evaluate();
    }

    internal void TestConfigureWrite(int size, bool big)
    {
        _writeSize.SelectedIndex = Array.IndexOf(new[] { 1, 2, 4, 8 }, size);
        _endian.SelectedIndex = big ? 1 : 0;
    }

    internal void TestSetProgrammer(bool on)
    {
        _programmer.IsChecked = on;
        SetProgrammer(on);
    }

    internal void TestOperand(string text) => _operand.Text = text;

    internal void TestGoTo() => GoToResult();

    internal void TestSelectLength() => SelectLength();

    internal void TestReadCursor(bool big)
    {
        _endian.SelectedIndex = big ? 1 : 0;
        ReadFromCursor();
    }

    internal bool TestBitKey(string key, bool shift) =>
        _bitFocus >= 0 && HandleBitKey(_bitFocus, Enum.Parse<Windows.System.VirtualKey>(key, ignoreCase: true), shift);

    internal int FocusedBit => _bitFocus;

    internal void TestOperation(string name) => ApplyOperation(Enum.Parse<ProgrammerOperation>(name, ignoreCase: true));

    internal JsonObject TestState()
    {
        var state = new JsonObject
        {
            ["input"] = _input.Text,
            ["hex"] = _hex.Text,
            ["dec"] = _dec.Text,
            ["decAltVisible"] = _decAltRow.Visibility == Visibility.Visible,
            ["decAlt"] = _decAlt.Text,
            ["oct"] = _oct.Text,
            ["bin"] = _bin.Text,
            ["size"] = _size.Text,
            ["error"] = _error.Visibility == Visibility.Visible ? _error.Text : string.Empty,
            ["errorVisible"] = _error.Visibility == Visibility.Visible,
            ["inputInvalid"] = _input.BorderBrush is not null && _error.Visibility == Visibility.Visible,
            ["overflow"] = _overflow.Visibility == Visibility.Visible,
            ["overflowText"] = _overflow.Text,
            ["dimmed"] = _rowsPanel.Opacity < 1,
            ["programmer"] = IsProgrammer,
            ["width"] = _width,
            ["signed"] = _signed,
            ["bitField"] = _bitField.Text,
            ["bitExpression"] = _bitExpression.Text,
            ["isFloat"] = _hasValue && _current.IsFloat,
            ["bitsValue"] = "0x" + _bits.ToString("X", CultureInfo.InvariantCulture),
            ["focusedBit"] = _bitFocus,
            ["selection"] = _selLow < 0 ? null : new JsonObject { ["low"] = _selLow, ["high"] = _selHigh },
            ["copyHex"] = _lastRows?.CopyHex,
            ["copyDec"] = PrimaryDecimalCopy(),
        };
        state["history"] = new JsonArray([.. _history.Select(h => (JsonNode?)new JsonObject { ["expression"] = h.Expression, ["result"] = h.Result })]);
        state["bits"] = new JsonArray([.. _bitCells.Select(c => (JsonNode?)new JsonObject
        {
            ["index"] = (int)c.Tag,
            ["value"] = c.Content?.ToString(),
            ["name"] = AutomationProperties.GetName(c),
        })]);
        return state;
    }

    internal string TestCopy(string id)
    {
        // コピーのボタンが置く文字列(桁区切りなし)。
        return id switch
        {
            "hex" => _lastRows?.CopyHex ?? string.Empty,
            "dec" => PrimaryDecimalCopy(),
            "oct" => _lastRows?.CopyOctal ?? string.Empty,
            "bin" => _lastRows?.CopyBinary ?? string.Empty,
            "size" => _lastRows?.CopyFileSize ?? string.Empty,
            _ => string.Empty,
        };
    }

    internal Button BitCell(int index) => _bitCells.First(c => (int)c.Tag == index);

    /// <summary>ビットのセルにフォーカスを移す(範囲選択の起点もそのビットにする)。</summary>
    internal void TestFocusBit(int index)
    {
        _selAnchor = -1;
        _expectedFocusBit = -1;
        FocusBit(index);
        _expectedFocusBit = -1;
    }

    internal void TestInvokeCopy(string automationId) => ToolParts.InvokeCopyButton(automationId);
}

internal sealed partial class ConverterPanel
{
    internal JsonObject TestState()
    {
        var fields = new JsonObject();
        var errors = new JsonObject();
        foreach (FieldRow row in _rows)
        {
            fields[row.Id] = row.Box.Text;
            if (row.Message.Visibility == Visibility.Visible)
            {
                errors[row.Id] = row.Message.Text;
            }
        }

        return new JsonObject
        {
            ["fields"] = fields,
            ["errors"] = errors,
            ["endianBig"] = _endian.SelectedIndex == 1,
            ["codePage"] = _converter.CodePageId,
        };
    }
}

internal sealed partial class FloatPanel
{
    internal JsonObject TestState()
    {
        var values = new JsonObject();
        foreach ((string key, TextBlock value) in _values)
        {
            values[key] = value.Text;
        }

        var cells = new JsonArray([.. _cells.Select(c => (JsonNode?)new JsonObject { ["index"] = (int)c.Tag, ["value"] = c.Content?.ToString(), ["name"] = AutomationProperties.GetName(c) })]);
        var labels = new JsonArray();
        foreach (UIElement child in _bitSections.Children)
        {
            if (child is StackPanel { Children.Count: > 0 } group && group.Children[0] is TextBlock label)
            {
                labels.Add(label.Text);
            }
        }

        return new JsonObject
        {
            ["format"] = _layout.Id,
            ["values"] = values,
            ["cells"] = cells,
            ["labels"] = labels,
            ["inputInvalid"] = _inputError.Visibility == Visibility.Visible,
            ["inputError"] = _inputError.Text,
            ["decimal"] = _decimal.Text,
            ["hex"] = _hexInput.Text,
            ["class"] = _analysis?.Class.ToString(),
            ["bits"] = FloatAnalyzer.HexText(_layout, _bits),
        };
    }
}

internal sealed partial class CharTablePanel
{
    internal JsonObject TestState()
    {
        var detail = new JsonObject();
        foreach ((string key, TextBlock value) in _detail)
        {
            detail[key] = value.Text;
        }

        return new JsonObject
        {
            ["encoding"] = _table?.Entry.Id,
            ["multiByte"] = _table?.IsMultiByte,
            ["trailVisible"] = _secondHost.Visibility == Visibility.Visible,
            ["detail"] = detail,
            ["focused"] = _first.FocusedValue,
            ["list"] = _listMode.IsChecked == true,
            ["listRows"] = _listHost.Children.Count,
        };
    }

    internal JsonObject TestCell(int value, bool trail)
    {
        Button cell = trail ? _second.Cells[value] : _first.Cells[value];
        return new JsonObject
        {
            ["label"] = cell.Content?.ToString(),
            ["name"] = AutomationProperties.GetName(cell),
            ["id"] = AutomationProperties.GetAutomationId(cell),
        };
    }

    /// <summary>キーボードで矢印キーを押したのと同じ移動(フォーカスのあるセルから)。</summary>
    internal int TestMoveFocus(int delta)
    {
        int from = _first.FocusedValue;
        int next = Math.Clamp(from + delta, 0, 255);
        _first.FocusCell(next);
        return _first.FocusedValue;
    }

    internal string FocusedName() => _first.FocusedValue >= 0 ? AutomationProperties.GetName(_first.Cells[_first.FocusedValue]) : string.Empty;
}

internal sealed partial class ColorPanel
{
    internal JsonObject TestState()
    {
        var bytes = new JsonObject();
        foreach (FormatRow row in _rows)
        {
            bytes[row.Format.Id] = row.Bytes.Text;
        }

        var swatch = SwatchColor;
        return new JsonObject
        {
            ["hex"] = _hex.Text,
            ["swatchText"] = _swatchText.Text,
            ["components"] = _components.Text,
            ["swatch"] = swatch is { } s ? new JsonObject { ["r"] = s.R, ["g"] = s.G, ["b"] = s.B, ["a"] = s.A } : null,
            ["bytes"] = bytes,
            ["hexInvalid"] = _hexError.Visibility == Visibility.Visible,
            ["format"] = SelectedFormat.Id,
        };
    }
}

internal sealed partial class TimePanel
{
    internal JsonObject TestState()
    {
        var rows = new JsonObject();
        foreach (FormatRow row in _rows)
        {
            rows[row.Format.Id] = new JsonObject
            {
                ["decimal"] = row.Decimal.Text,
                ["hex"] = row.Hex.Text,
                ["bytes"] = row.Bytes.Text,
                ["status"] = row.Status.Visibility == Visibility.Visible ? row.Status.Text : string.Empty,
            };
        }

        return new JsonObject
        {
            ["date"] = _date.Text,
            ["iso"] = _iso.Text,
            ["regional"] = _regional.Text,
            ["dateInvalid"] = _dateError.Visibility == Visibility.Visible,
            ["rows"] = rows,
            ["format"] = SelectedFormat.Id,
            ["bigEndian"] = _converter.BigEndian,
        };
    }
}
#endif
