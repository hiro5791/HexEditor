using FsCheck;
using FsCheck.Fluent;
using HexEditor.Core.Editing;
using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Editing;

/// <summary>
/// データ演算 (EDIT-31〜EDIT-36) のオペランドの範囲 (符号なし 8 バイト、float の範囲) と、まとめて処理する経路 (パターンとのビット演算、
/// 飛ばしのない並べ替え) の結果が要素ごとの計算と同じであること。
/// </summary>
public sealed class DataOperationRangeTests
{
    private static (byte[] Data, DataOperationStats Stats) Run(byte[] data, DataOperationSpec spec, IReadOnlyList<TargetRange>? ranges = null)
    {
        using var doc = new Document(new MemoryByteSource(data), Options());
        ranges ??= [new TargetRange(0, data.Length)];
        DataOperationResult result = DataOperationRunner.For(doc).Run(doc.Current, ranges, spec);
        TransformApplier.Apply(doc, result.Replacements, "データ演算");
        return (ReadAll(doc.Current), result.Stats);
    }

    private sealed class NoContext : IExpressionContext
    {
        public static NoContext Instance { get; } = new();

        public long Cursor => 0;

        public long Length => 8;

        public long SelectionStart => 0;

        public long SelectionLength => 0;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination)
        {
            destination.Fill(0xFF);
            return offset + destination.Length <= Length;
        }
    }

    // ---- 符号なし 8 バイトのオペランド (EDIT-31 の仕様 5、EDIT-36) ----

    [Fact]
    [Trait(TC, "TC-EDIT-31-01")]
    public void Unsigned_eight_byte_operands_at_or_above_two_to_the_63_can_be_entered()
    {
        // 入力式は 128 bit で評価すれば 0xFFFFFFFFFFFFFFFF を書ける (64 bit の評価では桁あふれ)。
        Assert.Equal((Int128)ulong.MaxValue, ExpressionEvaluator.EvaluateWide("0xFFFFFFFFFFFFFFFF", NoContext.Instance));
        Assert.Equal((Int128)ulong.MaxValue, ExpressionEvaluator.EvaluateWide("18446744073709551615", NoContext.Instance, DefaultRadix.Decimal));
        Assert.Equal((Int128)1 << 63, ExpressionEvaluator.EvaluateWide("1 << 0x3F", NoContext.Instance));
        Assert.Equal((Int128)ulong.MaxValue, ExpressionEvaluator.EvaluateWide("u64le(0)", NoContext.Instance));
        Assert.Equal(-(Int128)1, ExpressionEvaluator.EvaluateWide("-1", NoContext.Instance));
        Assert.False(ExpressionEvaluator.TryEvaluate("0xFFFFFFFFFFFFFFFF", NoContext.Instance, out _, out ExpressionException? narrow));
        Assert.Equal(ExpressionError.Overflow, narrow!.Error);
        Assert.False(ExpressionEvaluator.TryEvaluateWide("0x1_0000_0000_0000_0000", NoContext.Instance, out _, out ExpressionException? tooBig));
        Assert.Equal(ExpressionError.Overflow, tooBig!.Error);

        // AND 0xFFFFFFFFFFFFFFFF は値を変えない演算、XOR は全ビットを反転する。
        var and = new DataOperationSpec { Kind = DataOperationKind.And, Size = 8, Operand = ulong.MaxValue };
        Assert.Equal(DataOperationError.None, and.Validate());
        Assert.True(and.IsIdentity());
        byte[] data = BitConverter.GetBytes(0x0123456789ABCDEFUL);
        Assert.Equal(BitConverter.GetBytes(~0x0123456789ABCDEFUL), Run(data, and with { Kind = DataOperationKind.Xor }).Data);

        // 2^63 以上の加算・制限。
        var add = new DataOperationSpec { Kind = DataOperationKind.Add, Size = 8, Operand = (Int128)1 << 63 };
        Assert.Equal(BitConverter.GetBytes(0x8000000000000001UL), Run(BitConverter.GetBytes(1UL), add).Data);
        var clamp = new DataOperationSpec { Kind = DataOperationKind.Clamp, Size = 8, Min = (Int128)1 << 63, Max = (Int128)0xFFFFFFFFFFFFFFF0UL };
        Assert.Equal(DataOperationError.None, clamp.Validate());
        byte[] clamped = Run([.. BitConverter.GetBytes(5UL), .. BitConverter.GetBytes(ulong.MaxValue)], clamp).Data;
        Assert.Equal(0x8000000000000000UL, BitConverter.ToUInt64(clamped, 0));
        Assert.Equal(0xFFFFFFFFFFFFFFF0UL, BitConverter.ToUInt64(clamped, 8));

        // 型と大きさで表せない値は範囲外 (符号なしの負の値、2^64、符号付き 8 バイトの 2^63)。
        Assert.Equal(DataOperationError.OperandOutOfRange, (add with { Operand = (Int128)1 << 64 }).Validate());
        Assert.Equal(DataOperationError.OperandOutOfRange, (add with { Operand = -1 }).Validate());
        Assert.Equal(DataOperationError.OperandOutOfRange, (add with { Type = ElementType.Signed }).Validate());
        Assert.Equal(DataOperationError.OperandOutOfRange, (clamp with { Max = (Int128)1 << 64 }).Validate());
    }

    // ---- float の範囲 (EDIT-31 の仕様 5) ----

    [Fact]
    [Trait(TC, "TC-EDIT-32-02")]
    public void Float32_values_beyond_the_float_range_are_out_of_range_instead_of_infinity()
    {
        var f32 = new DataOperationSpec { Kind = DataOperationKind.Add, Type = ElementType.Float, Size = 4, FloatOperand = 1e39 };
        Assert.Equal(DataOperationError.OperandOutOfRange, f32.Validate());
        Assert.Equal(DataOperationError.OperandOutOfRange, (f32 with { FloatOperand = -1e39 }).Validate());
        Assert.Equal(DataOperationError.OperandOutOfRange, (f32 with { FloatOperand = 1, FloatIncrement = 1e39 }).Validate());
        Assert.Equal(DataOperationError.None, (f32 with { FloatOperand = 3.4e38 }).Validate());
        Assert.Equal(DataOperationError.None, (f32 with { Size = 8 }).Validate());

        var clamp = new DataOperationSpec { Kind = DataOperationKind.Clamp, Type = ElementType.Float, Size = 4, FloatMax = 1e39 };
        Assert.Equal(DataOperationError.OperandOutOfRange, clamp.Validate());
        Assert.Equal(DataOperationError.OperandOutOfRange, (clamp with { FloatMax = null, FloatMin = -1e40 }).Validate());
        Assert.Equal(DataOperationError.None, (clamp with { Size = 8 }).Validate());
        Assert.True(clamp.FitsFloat(null));
        Assert.False(clamp.FitsFloat(1e39));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-32-02")]
    public void Float_reverse_division_leaves_zero_elements_unchanged_and_counts_them()
    {
        // 2.0、0.0、-0.0 を 8.0 で逆除算: 2.0 は 4.0、±0 は変えない (EDIT-32 の仕様 1 の表と仕様 3)。
        byte[] data = [.. BitConverter.GetBytes(2.0f), .. BitConverter.GetBytes(0.0f), .. BitConverter.GetBytes(-0.0f)];
        var spec = new DataOperationSpec { Kind = DataOperationKind.ReverseDivide, Type = ElementType.Float, Size = 4, FloatOperand = 8 };
        (byte[] result, DataOperationStats stats) = Run(data, spec);
        Assert.Equal(4.0f, BitConverter.ToSingle(result, 0));
        Assert.Equal(data[4..], result[4..]);
        Assert.Equal(2, stats.ZeroSkipped);
        Assert.Equal(0, stats.Infinities);
    }

    // ---- まとめて処理する経路 (EDIT-31 の「巨大ファイル」2) ----

    [Fact]
    [Trait(TC, "TC-EDIT-33-01")]
    public void Bitwise_and_reorder_fast_paths_match_an_element_by_element_reference()
    {
        DataOperationKind[] kinds =
        [
            DataOperationKind.And, DataOperationKind.Or, DataOperationKind.Xor, DataOperationKind.Not, DataOperationKind.Nand,
            DataOperationKind.Nor, DataOperationKind.Xnor, DataOperationKind.ByteSwap16, DataOperationKind.ByteSwap32,
            DataOperationKind.ByteSwap64, DataOperationKind.WordSwap32, DataOperationKind.ReverseBits, DataOperationKind.SwapNibbles,
        ];
        Gen<(byte[] Data, int Start, DataOperationSpec Spec)> gen =
            from length in Gen.Choose(0, 9000)
            from data in Gen.Choose(0, 255).Select(i => (byte)i).ArrayOf(length)
            from start in Gen.Choose(0, 7)
            from kind in Gen.Elements(kinds)
            from size in Gen.Elements(1, 2, 4, 8)
            from big in Gen.Elements(false, true)
            from operand in Gen.Choose(int.MinValue, int.MaxValue)
            from useKey in Gen.Elements(false, true)
            from keyLength in Gen.Choose(1, 40)
            from key in Gen.Choose(0, 255).Select(i => (byte)i).ArrayOf(keyLength)
            from zeroOrigin in Gen.Elements(false, true)
            from skip in Gen.Elements(0, 0, 1, 3)
            select (data, Math.Min(start, length), new DataOperationSpec
            {
                Kind = kind,
                Size = size,
                BigEndian = big,
                Operand = (uint)operand & (size == 8 ? uint.MaxValue : (uint)((1UL << (size * 8)) - 1)),
                OperandSource = useKey ? OperandSource.KeyBytes : OperandSource.Number,
                Key = key,
                KeyOrigin = zeroOrigin ? PatternOrigin.OffsetZero : PatternOrigin.RangeStart,
                SkipCount = skip,
            });
        Prop.ForAll(gen.ToArbitrary(), c =>
        {
            TargetRange range = new(c.Start, c.Data.Length - c.Start);
            byte[] expected = Reference(c.Data, range, c.Spec, out long changed);
            if (range.Length == 0)
            {
                return true;
            }

            (byte[] actual, DataOperationStats stats) = Run(c.Data, c.Spec, [range]);
            bool countOk = c.Spec.Category != DataOperationCategory.Reorder || stats.ChangedElements == changed;
            return expected.AsSpan().SequenceEqual(actual) && stats.Changed == (changed > 0) && countOk;
        }).QuickCheckThrowOnFailure();
    }

    [Theory]
    [Trait(TC, "TC-EDIT-33-01")]
    [InlineData(DataOperationKind.Xor, 7, true)]
    [InlineData(DataOperationKind.Xor, 5000, false)]
    [InlineData(DataOperationKind.ByteSwap32, 0, false)]
    [InlineData(DataOperationKind.ByteSwap64, 0, false)]
    public void Fast_paths_are_correct_across_chunk_boundaries(DataOperationKind kind, int keyLength, bool zeroOrigin)
    {
        byte[] data = new byte[(5 * 1024 * 1024 / 2) + 13];
        GeneratedData.FillRandom(21, 0, data);
        byte[] key = new byte[Math.Max(1, keyLength)];
        GeneratedData.FillRandom(22, 0, key);
        var spec = new DataOperationSpec
        {
            Kind = kind,
            OperandSource = keyLength > 0 ? OperandSource.KeyBytes : OperandSource.Number,
            Key = key,
            KeyOrigin = zeroOrigin ? PatternOrigin.OffsetZero : PatternOrigin.RangeStart,
        };
        var range = new TargetRange(3, data.Length - 3);
        byte[] expected = Reference(data, range, spec, out _);
        Assert.True(expected.AsSpan().SequenceEqual(Run(data, spec, [range]).Data));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-35-01")]
    public void Byte_swaps_that_change_nothing_are_reported_as_unchanged()
    {
        (byte[] same, DataOperationStats unchanged) = Run([1, 1, 2, 2, 3, 3], new DataOperationSpec { Kind = DataOperationKind.ByteSwap16 });
        Assert.Equal(new byte[] { 1, 1, 2, 2, 3, 3 }, same);
        Assert.False(unchanged.Changed);

        (_, DataOperationStats one) = Run([1, 1, 1, 2], new DataOperationSpec { Kind = DataOperationKind.ByteSwap16 });
        Assert.True(one.Changed);
        Assert.Equal(1, one.ChangedElements);
    }

    /// <summary>要素ごとに計算する基準の実装 (ビット演算はバイトごと、並べ替えは要素の中のバイトの入れ替え)。</summary>
    private static byte[] Reference(byte[] data, TargetRange range, DataOperationSpec spec, out long changedElements)
    {
        byte[] result = (byte[])data.Clone();
        int size = spec.EffectiveSize;
        long cycle = (long)spec.ProcessCount + spec.SkipCount;
        long processed = 0;
        changedElements = 0;
        byte[] operand = new byte[size];
        ulong value = (ulong)(UInt128)spec.Operand;
        for (int i = 0; i < size; i++)
        {
            operand[spec.BigEndian ? size - 1 - i : i] = (byte)(value >> (8 * i));
        }

        for (long e = 0; (e + 1) * size <= range.Length; e++)
        {
            if (e % cycle >= spec.ProcessCount)
            {
                continue;
            }

            int at = (int)(range.Offset + e * size);
            Span<byte> element = result.AsSpan(at, size);
            byte[] before = element.ToArray();
            if (spec.Category == DataOperationCategory.Reorder)
            {
                switch (spec.Kind)
                {
                    case DataOperationKind.WordSwap32:
                        (element[0], element[1], element[2], element[3]) = (element[2], element[3], element[0], element[1]);
                        break;
                    case DataOperationKind.ReverseBits:
                        element[0] = ReverseByte(element[0]);
                        break;
                    case DataOperationKind.SwapNibbles:
                        element[0] = (byte)((element[0] << 4) | (element[0] >> 4));
                        break;
                    default:
                        element.Reverse();
                        break;
                }
            }
            else
            {
                for (int i = 0; i < size; i++)
                {
                    byte k = spec.UsesKey
                        ? spec.Key![(int)((spec.KeyOrigin == PatternOrigin.OffsetZero ? at + i : processed) % spec.Key.Length)]
                        : operand[i];
                    byte x = element[i];
                    element[i] = spec.Kind switch
                    {
                        DataOperationKind.And => (byte)(x & k),
                        DataOperationKind.Or => (byte)(x | k),
                        DataOperationKind.Xor => (byte)(x ^ k),
                        DataOperationKind.Not => (byte)~x,
                        DataOperationKind.Nand => (byte)~(x & k),
                        DataOperationKind.Nor => (byte)~(x | k),
                        _ => (byte)~(x ^ k),
                    };
                }
            }

            changedElements += element.SequenceEqual(before) ? 0 : 1;
            processed++;
        }

        return result;
    }

    private static byte ReverseByte(byte b)
    {
        int r = 0;
        for (int i = 0; i < 8; i++)
        {
            r |= ((b >> i) & 1) << (7 - i);
        }

        return (byte)r;
    }
}
