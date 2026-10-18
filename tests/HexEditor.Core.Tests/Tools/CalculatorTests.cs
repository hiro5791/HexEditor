using System.Globalization;
using HexEditor.Core.Calculator;
using HexEditor.Core.Expressions;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Tools;

/// <summary>TOOL-22 電卓、TOOL-23 プログラマモード。</summary>
public sealed class CalculatorTests
{
    private sealed class Context(byte[] data, long cursor = 0x40, bool bigEndian = false) : ICalculatorContext
    {
        public bool HasDocument => true;

        public bool BigEndian { get; } = bigEndian;

        public long Cursor { get; } = cursor;

        public long Length => data.Length;

        public long SelectionStart => 0x10;

        public long SelectionLength => 16;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => name == "header" ? 0x80 : null;

        public bool TryRead(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset + destination.Length > data.Length)
            {
                return false;
            }

            data.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }
    }

    private static readonly byte[] Bytes256 = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    private static CalcResult Eval(string text, ICalculatorContext? context = null, CalculatorOptions? options = null) =>
        CalculatorEvaluator.Evaluate(text, context ?? new Context(Bytes256), options);

    private static Int128 Int(string text, CalculatorOptions? options = null) => Eval(text, options: options).Value.Integer;

    private static ExpressionError Error(string text, ICalculatorContext? context = null) =>
        Assert.Throws<ExpressionException>(() => Eval(text, context)).Error;

    [Fact]
    [Trait(TC, "TC-TOOL-22-01")]
    public void AddsAndMultipliesLikeTheInputExpressionAndShowsEveryRadix()
    {
        CalcResult result = Eval("0x1F00 + 10h * 2", NoDocumentCalculatorContext.Instance);
        Assert.Equal(0x1F20, result.Value.Integer);
        Assert.False(result.Overflow);
        CalculatorRows rows = CalculatorFormat.Rows(result.Value, new CalculatorOptions(), CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("0x1F20", rows.Hex);
        Assert.Equal("7,968", rows.DecimalSigned);
        Assert.Equal("17440", rows.Octal);
        Assert.Equal("0001 1111 0010 0000", rows.Binary);
        Assert.Equal("7.78 KiB", rows.FileSize);
        Assert.Equal("7968", rows.CopyDecimalSigned);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-22-02")]
    public void NamesUseTheActiveDocumentAndAreErrorsWithoutOne()
    {
        Assert.Equal(0xFFF00, Int("end - cur", new Context(new byte[1 << 20], 0x100)));
        Assert.Equal(0xF0, Int("end - cur", new Context(Bytes256, 0x10)));
        Assert.Equal(ExpressionError.NotAvailable, Error("end - cur", NoDocumentCalculatorContext.Instance));
        Assert.Equal(ExpressionError.NotAvailable, Error("u8(0)", NoDocumentCalculatorContext.Instance));
        Assert.Equal(0x80 + 8, Int("bm.header + 8"));
    }

    private static Int128 Int(string text, Context context) => Eval(text, context).Value.Integer;

    [Fact]
    [Trait(TC, "TC-TOOL-22-03")]
    public void ReadFunctionsFollowTheDocumentEndianness()
    {
        Assert.Equal(0x10203, Int("u32be(0)"));
        Assert.Equal(0x3020100, Int("u32(0)", new Context(Bytes256)));
        Assert.Equal(0x10203, Int("u32(0)", new Context(Bytes256, bigEndian: true)));
        Assert.Equal(0x0102, Int("u16(1)", new Context(Bytes256, bigEndian: true)));
        Assert.Equal(-1, Int("s8(0xFF)"));
        Assert.Equal(ExpressionError.Unreadable, Error("u32(255)"));
        Assert.Equal(ExpressionError.UnknownFunction, Error("u12(0)"));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-22-04")]
    public void CalculatorOnlyNotationsAreErrorsInTheOtherInputFields()
    {
        var context = new Context(Bytes256);
        foreach (string expression in new[] { "ans", "popcnt(7)", "align(5, 4)", "min(1, 2)", "u32(0)", "1 == 1", "bswap(1)", "1.5" })
        {
            Assert.False(ExpressionEvaluator.TryEvaluate(expression, context, out _, out ExpressionException? error, DefaultRadix.Decimal), expression);
            Assert.NotNull(error);
        }

        // 電卓の中では使える (ans は直前の結果)。
        CalculatorOptions options = new() { Answer = CalcValue.FromInteger(0x10) };
        Assert.Equal(0x13, Int("ans + popcnt(7)", options));
        Assert.Equal(8, Int("align(5, 4)"));
        Assert.Equal(1, Int("3 >= 3"));
        Assert.Equal(0, Int("3 != 3"));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-22-05")]
    public void OverflowIsFlaggedAndDivisionByZeroIsAnError()
    {
        CalcResult overflow = Eval("0x7FFFFFFFFFFFFFFF + 1");
        Assert.True(overflow.Overflow);
        Assert.Equal(long.MinValue, overflow.Value.Integer);
        CalculatorRows rows = CalculatorFormat.Rows(overflow.Value, new CalculatorOptions(), CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("0x8000000000000000", rows.Hex);
        Assert.Equal("-9,223,372,036,854,775,808", rows.DecimalSigned);

        Assert.Equal(ExpressionError.DivideByZero, Error("1 / 0"));
        Assert.Equal(ExpressionError.DivideByZero, Error("1.5 / 0"));
        Assert.Equal(-3, Int("-7 / 2"));
        Assert.Equal(ExpressionError.Overflow, Error("1 << 64"));
        Assert.Equal(1610612736, Int("1.5G"));
        Assert.False(Eval("1.5G").Value.IsFloat);
        CalcValue floating = Eval("1.5 + 1").Value;
        Assert.True(floating.IsFloat);
        Assert.Equal(2.5, floating.Double);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-22-08")]
    public void DecimalPointIsAlwaysADotAndDisplayFollowsTheRegion()
    {
        CultureInfo de = CultureInfo.GetCultureInfo("de-DE");
        CalcValue three = Eval("1.5 * 2").Value;
        Assert.Equal("3", CalculatorFormat.Rows(three, new CalculatorOptions(), de).DecimalFloat);
        Assert.Equal("2,5", CalculatorFormat.Rows(Eval("1.5 + 1").Value, new CalculatorOptions(), de).DecimalFloat);
        Assert.Equal(ExpressionError.Syntax, Error("1,5 * 2"));

        CalculatorRows k = CalculatorFormat.Rows(Eval("1.5K").Value, new CalculatorOptions(), de);
        Assert.Equal("1.536", k.DecimalSigned);
        Assert.Equal("1536", k.CopyDecimalSigned);
    }

    [Fact]
    public void FunctionsAndOperatorsBehave()
    {
        Assert.Equal(3, Int("min(3, 9)"));
        Assert.Equal(9, Int("max(3, 9)"));
        Assert.Equal(5, Int("abs(-5)"));
        Assert.Equal(0x1000, Int("align(0xFFF, 0x1000)"));
        Assert.Equal(3, Int("popcnt(0b10101 & 0b11)") + 2);
        Assert.Equal(60, Int("clz(0xF)"));
        Assert.Equal(4, Int("ctz(0x10)"));
        Assert.Equal(0x0201, Int("bswap(0x0102)", Programmer(16, signed: false)));
        Assert.Equal(0x03, Int("rol(0x81, 1)", Programmer(8, signed: false)));
        Assert.Equal(0xC0, Int("ror(0x81, 1)", Programmer(8, signed: false)));
        Assert.Equal(7, Int("1 + 2 * 3"));
        Assert.Equal(1, Int("1 + 2 == 3"));
        Assert.Equal(0xFF, Int("0xF0 | 0x0F ^ 0"));
        Assert.Equal(0x20, Int("0x10 << 1"));
        Assert.Equal(-1, Int("~0"));
        Assert.Equal(ExpressionError.UnknownName, Error("nosuchname"));
        Assert.Equal(ExpressionError.UnclosedParenthesis, Error("(1 + 2"));
        Assert.Equal(ExpressionError.Empty, Error("  "));
        Assert.Equal(ExpressionError.NotInteger, Error("1.5 & 1"));
        Assert.Equal(3, Int("0xFF & 3"));
        Assert.Equal(0xFF, Int("FFh"));
        Assert.Equal(255, Int("FF", new CalculatorOptions { Radix = DefaultRadix.Hexadecimal }));
        Assert.Equal(1_000_000, Int("1_000_000"));

        // 位置は式の中の文字位置 (3 文字目)。
        ExpressionException ex = Assert.Throws<ExpressionException>(() => Eval("1 + foo"));
        Assert.Equal(4, ex.Position);
    }

    private static CalculatorOptions Programmer(int width, bool signed) => new() { BitWidth = width, Signed = signed };

    [Fact]
    [Trait(TC, "TC-TOOL-23-01")]
    public void ProgrammerModeWrapsAtTheBitWidth()
    {
        CalculatorOptions signed8 = Programmer(8, signed: true);
        CalcResult result = Eval("0x7F + 1", options: signed8);
        Assert.Equal(-128, result.Value.Integer);
        CalculatorRows rows = CalculatorFormat.Rows(result.Value, signed8, CultureInfo.InvariantCulture, padBinary: true);
        Assert.Equal("0x80", rows.Hex);
        Assert.Equal("-128", rows.CopyDecimalSigned);
        Assert.Equal("128", rows.CopyDecimalUnsigned);
        Assert.Equal("1000 0000", rows.Binary);

        Assert.Equal("0xFF", CalculatorFormat.Rows(Eval("-1", options: signed8).Value, signed8, CultureInfo.InvariantCulture).Hex);
        Assert.Equal(0, Int("0xFFFF + 1", Programmer(16, signed: false)));
        Assert.Equal(0xFFFFFFFF, Int("0 - 1", Programmer(32, signed: false)));
        Assert.Equal(-1, Int("0xFFFFFFFF", Programmer(32, signed: true)));
    }

    [Fact]
    public void BitOperationsOfTheProgrammerPanel()
    {
        Assert.Equal(0x10UL, ProgrammerMode.ToggleBit(0, 4, 32));
        Assert.Equal(0x80000010UL, ProgrammerMode.ToggleBit(0x10, 31, 32));
        Assert.Equal(0x10UL, ProgrammerMode.ToggleBit(0x10, 40, 32));
        Assert.Equal(0xF0UL, ProgrammerMode.Apply(ProgrammerOperation.And, 0xFF, 0xF0, 8, false));
        Assert.Equal(0x0FUL, ProgrammerMode.Apply(ProgrammerOperation.Not, 0xF0, 0, 8, false));
        Assert.Equal(0xFFUL, ProgrammerMode.Apply(ProgrammerOperation.Nand, 0, 0, 8, false));
        Assert.Equal(0x00UL, ProgrammerMode.Apply(ProgrammerOperation.Nor, 0xF0, 0x0F, 8, false));
        Assert.Equal(0xC0UL, ProgrammerMode.Apply(ProgrammerOperation.ShiftRightArithmetic, 0x80, 0, 8, true));
        Assert.Equal(0x40UL, ProgrammerMode.Apply(ProgrammerOperation.ShiftRightLogical, 0x80, 0, 8, false));
        Assert.Equal(0x01UL, ProgrammerMode.Apply(ProgrammerOperation.RotateLeft, 0x80, 0, 8, false));
        Assert.Equal(0x80UL, ProgrammerMode.Apply(ProgrammerOperation.RotateRight, 0x01, 0, 8, false));
        Assert.Equal(0xFFUL, ProgrammerMode.Apply(ProgrammerOperation.Negate, 1, 0, 8, true));
        Assert.Equal(0x78563412UL, ProgrammerMode.Apply(ProgrammerOperation.ByteSwap, 0x12345678, 0, 32, false));
        Assert.Equal(0x01020304UL, ProgrammerMode.FromBytes([1, 2, 3, 4], 32, bigEndian: true));
        Assert.Equal(0x04030201UL, ProgrammerMode.FromBytes([1, 2, 3, 4], 32, bigEndian: false));
        Assert.Null(ProgrammerMode.FromBytes([1, 2, 3], 32, true));
        Assert.Equal([0x04, 0x03, 0x02, 0x01], ProgrammerMode.ToBytes(0x01020304, 32, bigEndian: false));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-23-03")]
    public void BitFieldSelectionShowsValueAndMaskShiftExpression()
    {
        BitFieldSelection field = ProgrammerMode.Select(0xABCD, 4, 7, 32);
        Assert.Equal((0xCUL, "(x >> 4) & 0xF", 4, 7), (field.Value, field.Expression, field.LowBit, field.HighBit));
        BitFieldSelection reselected = ProgrammerMode.Select(0xABCD, 11, 8, 32);
        Assert.Equal((0xBUL, "(x >> 8) & 0xF"), (reselected.Value, reselected.Expression));
        Assert.Equal("x & 0xFF", ProgrammerMode.Select(0xABCD, 0, 7, 32).Expression);
        Assert.Equal("x", ProgrammerMode.Select(0xABCD, 0, 7, 8).Expression);
        Assert.Equal(1UL, ProgrammerMode.Select(0x10, 4, 4, 8).Value);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-22-06")]
    public void HistoryKeepsNewestFirstUpToTheLimitAndRoundTrips()
    {
        IReadOnlyList<CalculatorHistoryEntry> history = [];
        history = CalculatorHistory.Push(history, "1 + 2", "0x3");
        history = CalculatorHistory.Push(history, "0x100 * 3", "0x300");
        history = CalculatorHistory.Push(history, "0x100 * 3", "0x300");
        history = CalculatorHistory.Push(history, "  ", "0x0");
        Assert.Equal(["0x100 * 3", "1 + 2"], history.Select(h => h.Expression));

        IReadOnlyList<CalculatorHistoryEntry> restored = CalculatorHistory.Parse(CalculatorHistory.Serialize(history));
        Assert.Equal(history, restored);

        for (int i = 0; i < CalculatorHistory.Limit + 50; i++)
        {
            history = CalculatorHistory.Push(history, $"{i} + 1", "0x1");
        }

        Assert.Equal(CalculatorHistory.Limit, history.Count);
        Assert.Equal($"{CalculatorHistory.Limit + 49} + 1", history[0].Expression);
        Assert.Empty(CalculatorHistory.Parse("not json"));
    }
}
