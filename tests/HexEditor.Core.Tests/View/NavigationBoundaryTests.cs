using System.Numerics;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.View;

/// <summary>VIEW-02・VIEW-25・VIEW-29 の境界値 (BigInteger の基準モデルとの比較)。</summary>
public sealed class NavigationBoundaryTests
{
    private static readonly long[] Lengths = [1L << 31, 1L << 32, 1L << 53, long.MaxValue];

    private static BigInteger RoundDiv(BigInteger n, BigInteger d) => (n + d / 2) / d;

    [Fact]
    [Trait(TC, "TC-VIEW-02-02")]
    public void ScrollMappingMatchesBigIntegerModel()
    {
        // 無作為な値は固定の種の乱数で作る (失敗を再現できるように)。
        var rng = new Random(202);
        foreach (long length in Lengths)
        {
            foreach (int b in new[] { 1, 16, 4096 })
            {
                foreach (int v in new[] { 1, 40 })
                {
                    var layout = new HexLayout(b, length, CanResize: true);
                    long m = layout.MaxTopRow(v);
                    long s = ScrollMapping.Scale(m);
                    Assert.Equal(Math.Min(m, ScrollMapping.MaxScrollValue), s);

                    IEnumerable<long> values = new[] { 0, 1, s - 1, s }.Where(x => x >= 0).Concat(Enumerable.Range(0, 1000).Select(_ => rng.NextInt64(0, s + 1)));
                    foreach (long value in values)
                    {
                        BigInteger expected = s == 0 ? 0 : m <= ScrollMapping.MaxScrollValue ? value : RoundDiv((BigInteger)value * m, s);
                        long row = ScrollMapping.ToRow(value, m);
                        Assert.Equal(expected, (BigInteger)row);
                        Assert.InRange(row, 0, m);
                    }

                    IEnumerable<long> rows = new[] { 0, 1, m - 1, m }.Where(x => x >= 0).Concat(Enumerable.Range(0, 1000).Select(_ => m == long.MaxValue ? rng.NextInt64(long.MaxValue) : rng.NextInt64(0, m + 1)));
                    foreach (long row in rows)
                    {
                        BigInteger expected = s == 0 ? 0 : m <= ScrollMapping.MaxScrollValue ? row : RoundDiv((BigInteger)row * s, m);
                        long value = ScrollMapping.ToValue(row, m);
                        Assert.Equal(expected, (BigInteger)value);
                        Assert.InRange(value, 0, s);
                    }

                    Assert.Equal(0, ScrollMapping.ToRow(0, m));
                    Assert.Equal(m, ScrollMapping.ToRow(s, m));
                }
            }
        }
    }

    public enum Key
    {
        Left,
        Right,
        Up,
        Down,
        Home,
        End,
        PageUp,
        PageDown,
        CtrlHome,
        CtrlEnd,
    }

    /// <summary>VIEW-25 の仕様 2 の表を BigInteger で素直に実装した基準モデル。</summary>
    private static BigInteger Expected(Key key, BigInteger c, BigInteger max, int b, int v)
    {
        BigInteger row = c / b;
        BigInteger page = (BigInteger)b * Math.Max(1, v - 1);
        return key switch
        {
            Key.Left => c == 0 ? 0 : c - 1,
            Key.Right => c == max ? c : c + 1,
            Key.Up => row == 0 ? c : c - b,
            Key.Down => c + b <= max ? c + b : row < max / b ? max : c,
            Key.Home => row * b,
            Key.End => BigInteger.Min(row * b + b - 1, max),
            Key.PageUp => c - page < 0 ? c % b : c - page,
            Key.PageDown => BigInteger.Min(c + page, max),
            Key.CtrlHome => 0,
            _ => max,
        };
    }

    private static void Press(EditorState s, Key key)
    {
        switch (key)
        {
            case Key.Left: s.MoveLeft(); break;
            case Key.Right: s.MoveRight(); break;
            case Key.Up: s.MoveUp(); break;
            case Key.Down: s.MoveDown(); break;
            case Key.Home: s.MoveHome(); break;
            case Key.End: s.MoveEnd(); break;
            case Key.PageUp: s.PageUp(); break;
            case Key.PageDown: s.PageDown(); break;
            case Key.CtrlHome: s.MoveToStart(); break;
            default: s.MoveToEnd(); break;
        }
    }

    [Fact]
    [Trait(TC, "TC-VIEW-25-07")]
    public void CursorMovesMatchBigIntegerModel()
    {
        long[] lengths = [0, 1, 100, 1L << 31, 1L << 32, 1L << 53, long.MaxValue];
        foreach (long length in lengths)
        {
            foreach (bool resizable in new[] { true, false })
            {
                SourceCapabilities caps = resizable ? SourceCapabilities.CanResize | SourceCapabilities.CanWrite : SourceCapabilities.CanWrite;
                using var doc = new Document(new FakeByteSource(length, (o, s) => s.Clear(), caps), Options());
                long max = resizable ? length : Math.Max(0, length - 1);
                foreach (int b in new[] { 1, 16, 24, 4096 })
                {
                    foreach (int v in new[] { 1, 40 })
                    {
                        var s = new EditorState(doc, b) { VisibleRows = v };
                        Assert.Equal(max, s.Layout.MaxCursor);
                        long[] cursors = [0, 1, int.MaxValue, 1L << 31, uint.MaxValue, 1L << 32, 1L << 53, max - 1, max];
                        foreach (long c in cursors.Where(c => c >= 0 && c <= max).Distinct())
                        {
                            foreach (Key key in Enum.GetValues<Key>())
                            {
                                s.Click(c, ActiveColumn.Hex, false, false);
                                Assert.Equal(c, s.Cursor);
                                Press(s, key);
                                Assert.True(Expected(key, c, max, b, v) == s.Cursor,
                                    $"L={length} resizable={resizable} b={b} V={v} c={c} {key}: {s.Cursor} (期待値 {Expected(key, c, max, b, v)})");
                                Assert.InRange(s.Cursor, 0, max);
                            }
                        }
                    }
                }
            }
        }
    }

    [Fact]
    [Trait(TC, "TC-VIEW-29-11")]
    public void GoToExpressionsAndBounds()
    {
        long length = (1L << 53) + 1;
        using var doc = new Document(new FakeByteSource(length, (o, s) => s.Clear(), SourceCapabilities.CanResize | SourceCapabilities.CanWrite), Options());
        var e = new EditorState(doc) { VisibleRows = 20 };
        GoToResult Go(string text) => GoToResolver.Resolve(text, GoToBase.Auto, GoToUnit.Bytes, e);

        // 手順 2
        foreach (string text in new[] { "0x1F00", "1F00h", "$1F00", "0x1_F00", "7936d", "0b1111100000000", "0o17400", "7.75K" })
        {
            GoToResult r = Go(text);
            Assert.True(r.IsValid, text);
            Assert.Equal(0x1F00, r.Offset);
        }

        // 手順 3
        e.GoTo(1L << 32);
        Assert.Equal((1L << 32) + (1L << 31) - 1, Go("+0x7FFFFFFF").Offset);
        Assert.Equal(0, Go("-0x100000000").Offset);
        Assert.True(Go("-0x100000000").IsValid);
        Assert.True(Go("-0x100000001").OutOfRange);

        // 手順 4
        Assert.True(Go("0x20000000000001").IsValid);
        Assert.True(Go("0x20000000000002").OutOfRange);
        Assert.True(Go("0x7FFFFFFFFFFFFFFF").OutOfRange);
        Assert.NotNull(Go("0x10000000000000000").Error);
        Assert.NotNull(Go("1/0").Error);
        Assert.NotNull(Go("(1+2").Error);
        Assert.NotNull(Go("bm.none").Error);
    }
}
