using HexEditor.Core.Expressions;

namespace HexEditor.Core.Tests.Expressions;

/// <summary>入力式 (00-overview 6 章)。</summary>
public sealed class ExpressionEvaluatorTests
{
    private sealed class Context : IExpressionContext
    {
        public byte[] Data { get; init; } = new byte[0x100];

        public long Cursor { get; init; } = 0x40;

        public long Length => Data.Length;

        public long SelectionStart { get; init; } = 0x10;

        public long SelectionLength { get; init; } = 16;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength { get; init; }

        public long? Bookmark(string name) => name == "header" ? 0x80 : null;

        public bool TryRead(long offset, Span<byte> destination)
        {
            if (offset + destination.Length > Data.Length)
            {
                return false;
            }

            Data.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }
    }

    private static long Eval(string text, DefaultRadix radix = DefaultRadix.Hexadecimal) =>
        ExpressionEvaluator.Evaluate(text, new Context(), radix);

    [Theory]
    [InlineData("0x1F00", 0x1F00)]
    [InlineData("1F00h", 0x1F00)]
    [InlineData("$1F00", 0x1F00)]
    [InlineData("1F00", 0x1F00)]
    [InlineData("FF", 0xFF)]
    [InlineData("0b1010", 10)]
    [InlineData("0o777", 511)]
    [InlineData("4096d", 4096)]
    [InlineData("0x1_0000", 0x10000)]
    [InlineData("4K", 4096)]
    [InlineData("1.5G", 1610612736)]
    public void NumberFormats(string text, long expected) => Assert.Equal(expected, Eval(text));

    [Fact]
    public void PlainNumbersFollowDefaultRadix()
    {
        Assert.Equal(0x10, Eval("10"));
        Assert.Equal(10, Eval("10", DefaultRadix.Decimal));
    }

    [Theory]
    [InlineData("end-0x10", 0xF0)]
    [InlineData("cur+sector*4", 0x40 + 2048)]
    [InlineData("sel.end", 0x20)]
    [InlineData("sel.last", 0x1F)]
    [InlineData("sel.len", 16)]
    [InlineData("bm.header+8", 0x88)]
    [InlineData("(1+2)*3", 9)]
    [InlineData("1 << 4 | 1", 0x11)]
    [InlineData("~0 & 0xFF", 0xFF)]
    [InlineData("-0x10 + 0x20", 0x10)]
    public void OperatorsAndNames(string text, long expected) => Assert.Equal(expected, Eval(text));

    [Fact]
    public void ReadFunctionsFollowPointers()
    {
        var ctx = new Context();
        ctx.Data[0x3C] = 0x80;
        ctx.Data[0x80] = 0xFE;
        ctx.Data[0x81] = 0xFF;
        Assert.Equal(0x80, ExpressionEvaluator.Evaluate("u32le(0x3C)", ctx));
        Assert.Equal(0x84, ExpressionEvaluator.Evaluate("u32le(0x3C)+4", ctx));
        Assert.Equal(-2, ExpressionEvaluator.Evaluate("s16le(0x80)", ctx));
        Assert.Equal(0xFEFF, ExpressionEvaluator.Evaluate("u16be(0x80)", ctx));
    }

    [Theory]
    [InlineData("", ExpressionError.Empty)]
    [InlineData("(1+2", ExpressionError.UnclosedParenthesis)]
    [InlineData("foo", ExpressionError.UnknownName)]
    [InlineData("bm.missing", ExpressionError.UnknownBookmark)]
    [InlineData("u33le(0)", ExpressionError.UnknownFunction)]
    [InlineData("1/0", ExpressionError.DivideByZero)]
    [InlineData("0x7FFFFFFFFFFFFFFF+1", ExpressionError.Overflow)]
    [InlineData("0xFFFFFFFFFFFFFFFFF", ExpressionError.Overflow)]
    [InlineData("u32le(0x1000)", ExpressionError.Unreadable)]
    [InlineData("rec", ExpressionError.NotAvailable)]
    [InlineData("12xyz", ExpressionError.InvalidNumber)]
    public void ErrorsAreReported(string text, ExpressionError expected)
    {
        Assert.False(ExpressionEvaluator.TryEvaluate(text, new Context(), out _, out ExpressionException? error));
        Assert.Equal(expected, error!.Error);
    }
}
