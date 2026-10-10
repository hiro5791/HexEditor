using System.Numerics;
using FsCheck;
using FsCheck.Fluent;
using HexEditor.Core.Editing;
using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Editing;

/// <summary>データ演算 (EDIT-31〜EDIT-36)。</summary>
public sealed class DataOperationTests
{
    private static Document Doc(params byte[] data) => new(new MemoryByteSource(data), Options());

    /// <summary>全体 (または指定した範囲) に演算を行い、結果のバイト列と件数を返す。</summary>
    private static (byte[] Data, DataOperationStats Stats) Run(byte[] data, DataOperationSpec spec, IReadOnlyList<TargetRange>? ranges = null)
    {
        using Document doc = Doc(data);
        ranges ??= [new TargetRange(0, data.Length)];
        DataOperationResult result = DataOperationRunner.For(doc).Run(doc.Current, ranges, spec);
        TransformApplier.Apply(doc, result.Replacements, "データ演算");
        Assert.Equal(data.Length, doc.Length);
        return (ReadAll(doc.Current), result.Stats);
    }

    private static byte[] Bytes(params byte[] b) => b;

    private static DataOperationSpec Add(long operand, int size = 1, bool bigEndian = false, long increment = 0) =>
        new() { Kind = DataOperationKind.Add, Size = size, BigEndian = bigEndian, Operand = operand, Increment = increment };

    // ---- TC-EDIT-31-01 ----

    [Fact]
    [Trait(TC, "TC-EDIT-31-01")]
    public void Element_size_endianness_stride_and_increment()
    {
        // 1. 2 バイト LE / BE
        Assert.Equal(Bytes(0x02, 0x00, 0x03, 0x00), Run(Bytes(0x01, 0x00, 0x02, 0x00), Add(1, size: 2)).Data);
        Assert.Equal(Bytes(0x01, 0x01, 0x02, 0x01), Run(Bytes(0x01, 0x00, 0x02, 0x00), Add(1, size: 2, bigEndian: true)).Data);

        // 2. 1 要素を処理し 1 要素を飛ばす
        var xor = new DataOperationSpec { Kind = DataOperationKind.Xor, Operand = 0xFF, ProcessCount = 1, SkipCount = 1 };
        Assert.Equal(Bytes(0xEE, 0x22, 0xCC, 0x44), Run(Bytes(0x11, 0x22, 0x33, 0x44), xor).Data);

        // 3. 増分
        Assert.Equal(Bytes(0, 1, 2, 3), Run(new byte[4], Add(0, increment: 1)).Data);

        // 4. 増分は処理した要素だけで進む
        Assert.Equal(Bytes(0, 0, 1, 0, 2, 0), Run(new byte[6], Add(0, increment: 1) with { SkipCount = 1 }).Data);

        // 5. オペランドのあふれは要素の大きさでラップする
        Assert.Equal(Bytes(0xFE, 0xFF, 0x00, 0x01), Run(new byte[4], Add(0xFE, increment: 1)).Data);

        // 6. 端数は変えない
        (byte[] data, DataOperationStats stats) = Run(Bytes(1, 2, 3, 4, 5), Add(1, size: 2));
        Assert.Equal(Bytes(2, 2, 4, 4, 5), data);
        Assert.Equal(1, stats.TrailingBytes);

        // 7. マルチ選択: 範囲ごとにやり直す / 範囲をまたいで続ける
        TargetRange[] ranges = [new(0, 2), new(4, 2)];
        Assert.Equal(Bytes(0, 1, 0, 0, 0, 1), Run(new byte[6], Add(0, increment: 1), ranges).Data);
        Assert.Equal(Bytes(0, 1, 0, 0, 2, 3),
            Run(new byte[6], Add(0, increment: 1) with { IncrementScope = IncrementScope.Continuous }, ranges).Data);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-31-01")]
    public void Random_add_and_xor_match_a_biginteger_reference()
    {
        Gen<(byte[] Data, DataOperationSpec Spec)> gen =
            from length in Gen.Choose(0, 4096)
            from data in Gen.Choose(0, 255).Select(i => (byte)i).ArrayOf(length)
            from signed in Gen.Elements(false, true)
            from size in Gen.Elements(1, 2, 4, 8)
            from big in Gen.Elements(false, true)
            from operand in Gen.Choose(-1000, 100000)
            from increment in Gen.Choose(-100, 100)
            from n in Gen.Choose(1, 8)
            from m in Gen.Choose(0, 8)
            from xor in Gen.Elements(false, true)
            select (data, new DataOperationSpec
            {
                Kind = xor ? DataOperationKind.Xor : DataOperationKind.Add,
                Type = signed ? ElementType.Signed : ElementType.Unsigned,
                Size = size,
                BigEndian = big,
                Operand = signed || operand >= 0 ? operand : -operand,
                Increment = increment,
                ProcessCount = n,
                SkipCount = m,
            });
        Prop.ForAll(gen.ToArbitrary(), c =>
        {
            DataOperationSpec spec = c.Spec;
            if (spec.Size == 1 && (spec.Operand > 255 || (spec.Type == ElementType.Signed && (spec.Operand > 127 || spec.Operand < -128))))
            {
                spec = spec with { Operand = spec.Operand & 0x7F };
            }

            if (spec.Size == 2 && spec.Operand > (spec.Type == ElementType.Signed ? 32767 : 65535))
            {
                spec = spec with { Operand = spec.Operand & 0x7FFF };
            }

            byte[] expected = Reference(c.Data, spec);
            byte[] actual = Run(c.Data, spec).Data;
            return expected.AsSpan().SequenceEqual(actual);
        }).QuickCheckThrowOnFailure();
    }

    /// <summary>BigInteger で要素ごとに計算する基準の実装 (ラップ)。</summary>
    private static byte[] Reference(byte[] data, DataOperationSpec spec)
    {
        byte[] result = (byte[])data.Clone();
        int size = spec.Size;
        BigInteger modulus = BigInteger.One << (size * 8);
        long processed = 0;
        for (long e = 0; (e + 1) * size <= data.Length; e++)
        {
            if (e % (spec.ProcessCount + spec.SkipCount) >= spec.ProcessCount)
            {
                continue;
            }

            byte[] element = result.AsSpan((int)(e * size), size).ToArray();
            if (spec.BigEndian)
            {
                Array.Reverse(element);
            }

            var x = new BigInteger(element, isUnsigned: true);
            BigInteger op = (((BigInteger)(long)spec.Operand + (BigInteger)(long)spec.Increment * processed) % modulus + modulus) % modulus;
            BigInteger r = spec.Kind == DataOperationKind.Xor ? x ^ op : (x + op) % modulus;
            byte[] bytes = new byte[size];
            r.TryWriteBytes(bytes, out _, isUnsigned: true);
            if (spec.BigEndian)
            {
                Array.Reverse(bytes);
            }

            bytes.CopyTo(result, e * size);
            processed++;
        }

        return result;
    }

    // ---- TC-EDIT-32-01 ----

    [Fact]
    [Trait(TC, "TC-EDIT-32-01")]
    public void Integer_arithmetic_and_overflow()
    {
        // 1. 符号なし FF + 2
        (byte[] wrap, DataOperationStats s1) = Run(Bytes(0xFF), Add(2));
        Assert.Equal(Bytes(0x01), wrap);
        Assert.Equal(1, s1.Overflows);
        Assert.Equal(Bytes(0xFF), Run(Bytes(0xFF), Add(2) with { Overflow = OverflowMode.Saturate }).Data);

        // 2. 符号付き・飽和
        var signedSat = new DataOperationSpec { Type = ElementType.Signed, Overflow = OverflowMode.Saturate, Operand = 1 };
        (byte[] a, DataOperationStats s2a) = Run(Bytes(0x7F), signedSat with { Kind = DataOperationKind.Add });
        (byte[] b, DataOperationStats s2b) = Run(Bytes(0x80), signedSat with { Kind = DataOperationKind.Subtract });
        Assert.Equal(Bytes(0x7F), a);
        Assert.Equal(Bytes(0x80), b);
        Assert.Equal(2, s2a.Overflows + s2b.Overflows);

        // 3. 符号付き 2 バイト LE の -7 を 2 で割る・剰余
        var s16 = new DataOperationSpec { Type = ElementType.Signed, Size = 2, Operand = 2 };
        Assert.Equal(Bytes(0xFD, 0xFF), Run(Bytes(0xF9, 0xFF), s16 with { Kind = DataOperationKind.Divide }).Data);
        Assert.Equal(Bytes(0xFF, 0xFF), Run(Bytes(0xF9, 0xFF), s16 with { Kind = DataOperationKind.Modulo }).Data);

        // 4. 符号付きの最小値の符号反転・絶対値
        foreach (DataOperationKind kind in new[] { DataOperationKind.Negate, DataOperationKind.Abs })
        {
            var spec = new DataOperationSpec { Kind = kind, Type = ElementType.Signed };
            (byte[] w, DataOperationStats sw) = Run(Bytes(0x80), spec);
            (byte[] s, DataOperationStats ss) = Run(Bytes(0x80), spec with { Overflow = OverflowMode.Saturate });
            Assert.Equal(Bytes(0x80), w);
            Assert.Equal(Bytes(0x7F), s);
            Assert.Equal(1, sw.Overflows);
            Assert.Equal(1, ss.Overflows);
        }

        // 5. 逆除算: 0 の要素は変えず、件数を数える
        (byte[] rdiv, DataOperationStats s5) = Run(Bytes(0, 2, 4), new DataOperationSpec { Kind = DataOperationKind.ReverseDivide, Operand = 8 });
        Assert.Equal(Bytes(0, 4, 2), rdiv);
        Assert.Equal(1, s5.ZeroSkipped);

        // 6. 逆減算
        Assert.Equal(Bytes(0x20, 0x10), Run(Bytes(0x10, 0x20), new DataOperationSpec { Kind = DataOperationKind.ReverseSubtract, Operand = 0x30 }).Data);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-32-01")]
    public void Division_by_zero_is_an_input_error_including_through_the_increment()
    {
        var div = new DataOperationSpec { Kind = DataOperationKind.Divide, Operand = 0 };
        Assert.Equal(DataOperationError.DivideByZero, div.Validate());
        Assert.Equal(DataOperationError.DivideByZero, (div with { Kind = DataOperationKind.Modulo }).Validate());

        // オペランド 3、増分 -1 なら 4 番目 (i = 3) の要素で 0 になる。
        DataOperationSpec stepping = div with { Operand = 3, Increment = -1 };
        Assert.Equal(DataOperationError.DivisorBecomesZero, DataOperationRunner.Validate([new TargetRange(0, 4)], stepping));
        Assert.Equal(DataOperationError.None, DataOperationRunner.Validate([new TargetRange(0, 3)], stepping));

        // 2 バイトでオペランド 1、増分 2 は 0 にならない (奇数のまま)。
        Assert.Equal(DataOperationError.None,
            DataOperationRunner.Validate([new TargetRange(0, 1 << 20)], div with { Size = 2, Operand = 1, Increment = 2 }));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-32-01")]
    public void Unsigned_64_bit_multiplication_detects_overflow()
    {
        var spec = new DataOperationSpec { Kind = DataOperationKind.Multiply, Size = 8, Operand = 3 };
        (byte[] data, DataOperationStats stats) = Run(BitConverter.GetBytes(0x8000000000000001UL), spec);
        Assert.Equal(0x8000000000000003UL, BitConverter.ToUInt64(data));
        Assert.Equal(1, stats.Overflows);
    }

    // ---- TC-EDIT-32-02 ----

    [Fact]
    [Trait(TC, "TC-EDIT-32-02")]
    public void Floating_point_arithmetic()
    {
        var f32 = new DataOperationSpec { Type = ElementType.Float, Size = 4 };
        Assert.Equal(Bytes(0x00, 0x00, 0x00, 0x3F),
            Run(Bytes(0x00, 0x00, 0x80, 0x3F), f32 with { Kind = DataOperationKind.Multiply, FloatOperand = 0.5 }).Data);

        Assert.True(DataOperationSpec.TryParseFloat("1e-3", out double milli));
        var f64 = new DataOperationSpec { Type = ElementType.Float, Size = 8, BigEndian = true, Kind = DataOperationKind.Add, FloatOperand = milli };
        Assert.Equal(Bytes(0x3F, 0xF0, 0x04, 0x18, 0x93, 0x74, 0xBC, 0x6A),
            Run(Bytes(0x3F, 0xF0, 0, 0, 0, 0, 0, 0), f64).Data);

        (byte[] div, DataOperationStats stats) = Run(Bytes(0x00, 0x00, 0x80, 0x3F, 0, 0, 0, 0), f32 with { Kind = DataOperationKind.Divide, FloatOperand = 0 });
        Assert.Equal(Bytes(0x00, 0x00, 0x80, 0x7F), div[..4]);
        Assert.True(float.IsNaN(BitConverter.ToSingle(div, 4)));
        Assert.Equal(1, stats.Infinities);
        Assert.Equal(1, stats.NaNs);

        Assert.False(DataOperationSpec.TryParseFloat("0,5", out _));
    }

    // ---- TC-EDIT-33-01 ----

    [Fact]
    [Trait(TC, "TC-EDIT-33-01")]
    public void Xor_key_not_and_other_bitwise_operations()
    {
        var key = new DataOperationSpec { Kind = DataOperationKind.Xor, OperandSource = OperandSource.KeyBytes, Key = [0xAA, 0x55] };
        Assert.Equal(Bytes(0xAA, 0xAA, 0xA5), Run(Bytes(0x00, 0xFF, 0x0F), key).Data);

        // 2. オフセット 0 から数える (オフセット 1 には鍵の 2 バイト目)
        byte[] offsetZero = Run(Bytes(0x11, 0x00, 0xFF, 0x0F), key with { KeyOrigin = PatternOrigin.OffsetZero }, [new TargetRange(1, 3)]).Data;
        Assert.Equal(Bytes(0x11, 0x55, 0x55, 0x5A), offsetZero);

        // 3. 鍵の増分
        Assert.Equal(Bytes(1, 2, 3), Run(new byte[3], key with { Key = [0x01], KeyIncrement = 1 }).Data);

        // 4. NOT
        Assert.Equal(Bytes(0xF0), Run(Bytes(0x0F), new DataOperationSpec { Kind = DataOperationKind.Not }).Data);

        // 5. AND / OR / NAND / NOR / XNOR
        (DataOperationKind Kind, byte Expected)[] cases =
        [
            (DataOperationKind.And, 0x0C), (DataOperationKind.Or, 0x3F), (DataOperationKind.Nand, 0xF3),
            (DataOperationKind.Nor, 0xC0), (DataOperationKind.Xnor, 0xCC),
        ];
        foreach ((DataOperationKind kind, byte expected) in cases)
        {
            Assert.Equal(Bytes(expected), Run(Bytes(0x0F), new DataOperationSpec { Kind = kind, Operand = 0x3C }).Data);
        }
    }

    [Theory]
    [Trait(TC, "TC-EDIT-33-01")]
    [InlineData(0)]
    [InlineData(3)]
    public void Xor_twice_restores_the_data(int keyIncrement)
    {
        byte[] original = new byte[1024 * 1024 + 5];
        GeneratedData.FillRandom(16, 0, original);
        var spec = new DataOperationSpec
        {
            Kind = DataOperationKind.Xor,
            OperandSource = OperandSource.KeyBytes,
            Key = [0xDE, 0xAD, 0xBE, 0xEF, 0x01],
            KeyIncrement = keyIncrement,
        };
        using Document doc = Doc(original);
        DataOperationRunner runner = DataOperationRunner.For(doc);
        TargetRange[] all = [new(0, original.Length)];
        TransformApplier.Apply(doc, runner.Run(doc.Current, all, spec).Replacements, "XOR");
        Assert.False(ReadAll(doc.Current).AsSpan().SequenceEqual(original));
        TransformApplier.Apply(doc, runner.Run(doc.Current, all, spec).Replacements, "XOR");
        Assert.True(ReadAll(doc.Current).AsSpan().SequenceEqual(original));
    }

    // ---- TC-EDIT-34-01 ----

    [Fact]
    [Trait(TC, "TC-EDIT-34-01")]
    public void Shifts_rotations_and_graded_rotation()
    {
        DataOperationSpec Op(DataOperationKind kind, int bits) => new() { Kind = kind, ShiftBits = bits };
        Assert.Equal(Bytes(0x03), Run(Bytes(0x81), Op(DataOperationKind.RotateLeft, 1)).Data);
        Assert.Equal(Bytes(0xC0), Run(Bytes(0x81), Op(DataOperationKind.RotateRight, 1)).Data);

        Assert.Equal(Bytes(0xC0), Run(Bytes(0x80), Op(DataOperationKind.ShiftRightArithmetic, 1) with { Type = ElementType.Signed }).Data);
        Assert.Equal(Bytes(0x40), Run(Bytes(0x80), Op(DataOperationKind.ShiftRightLogical, 1) with { Type = ElementType.Signed }).Data);

        Assert.Equal(Bytes(2, 4, 8), Run(Bytes(1, 1, 1), Op(DataOperationKind.RotateLeft, 1) with { RotateIncrement = 1 }).Data);

        Assert.Equal(Bytes(0x00), Run(Bytes(0x81), Op(DataOperationKind.ShiftLeft, 8)).Data);
        Assert.Equal(Bytes(0xFF), Run(Bytes(0x80), Op(DataOperationKind.ShiftRightArithmetic, 9) with { Type = ElementType.Signed }).Data);

        Assert.Equal(DataOperationError.BitCountOutOfRange, Op(DataOperationKind.RotateLeft, 8).Validate());

        Assert.Equal(Bytes(0x03, 0x00), Run(Bytes(0x01, 0x80), Op(DataOperationKind.RotateLeft, 1) with { Size = 2 }).Data);
    }

    // ---- TC-EDIT-35-01 ----

    [Fact]
    [Trait(TC, "TC-EDIT-35-01")]
    public void Reorder_operations_and_trailing_bytes()
    {
        DataOperationSpec Op(DataOperationKind kind) => new() { Kind = kind };
        Assert.Equal(Bytes(2, 1), Run(Bytes(1, 2), Op(DataOperationKind.ByteSwap16)).Data);
        Assert.Equal(Bytes(4, 3, 2, 1), Run(Bytes(1, 2, 3, 4), Op(DataOperationKind.ByteSwap32)).Data);
        Assert.Equal(Bytes(8, 7, 6, 5, 4, 3, 2, 1), Run(Bytes(1, 2, 3, 4, 5, 6, 7, 8), Op(DataOperationKind.ByteSwap64)).Data);
        Assert.Equal(Bytes(3, 4, 1, 2), Run(Bytes(1, 2, 3, 4), Op(DataOperationKind.WordSwap32)).Data);
        Assert.Equal(Bytes(3, 2, 1), Run(Bytes(1, 2, 3), Op(DataOperationKind.ReverseRange)).Data);
        Assert.Equal(Bytes(0x80), Run(Bytes(0x01), Op(DataOperationKind.ReverseBits)).Data);
        Assert.Equal(Bytes(0x21), Run(Bytes(0x12), Op(DataOperationKind.SwapNibbles)).Data);

        (byte[] data, DataOperationStats stats) = Run(Bytes(1, 2, 3, 4, 5, 6), Op(DataOperationKind.ByteSwap32));
        Assert.Equal(Bytes(4, 3, 2, 1, 5, 6), data);
        Assert.Equal(2, stats.TrailingBytes);

        // マルチ選択は要素ごとに反転する。
        Assert.Equal(Bytes(3, 2, 1, 0, 6, 5),
            Run(Bytes(1, 2, 3, 0, 5, 6), Op(DataOperationKind.ReverseRange), [new TargetRange(0, 3), new TargetRange(4, 2)]).Data);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-35-01")]
    public void Reversing_a_range_larger_than_a_chunk_uses_a_temporary_file_and_twice_restores()
    {
        byte[] original = new byte[3 * 1024 * 1024 + 17];
        GeneratedData.FillRandom(5, 0, original);
        using Document doc = Doc(original);
        DataOperationRunner runner = DataOperationRunner.For(doc);
        var spec = new DataOperationSpec { Kind = DataOperationKind.ReverseRange };
        TargetRange[] all = [new(0, original.Length)];
        DataOperationResult first = runner.Run(doc.Current, all, spec);
        Assert.True(first.Replacements[0].Content.HasTemporaryFile);
        TransformApplier.Apply(doc, first.Replacements, "反転");
        byte[] reversed = (byte[])original.Clone();
        Array.Reverse(reversed);
        Assert.True(ReadAll(doc.Current).AsSpan().SequenceEqual(reversed));
        TransformApplier.Apply(doc, runner.Run(doc.Current, all, spec).Replacements, "反転");
        Assert.True(ReadAll(doc.Current).AsSpan().SequenceEqual(original));
    }

    // ---- TC-EDIT-36-01 ----

    [Fact]
    [Trait(TC, "TC-EDIT-36-01")]
    public void Clamping_to_min_and_max()
    {
        (byte[] u, DataOperationStats su) = Run(Bytes(0x00, 0x50, 0xFF), new DataOperationSpec { Kind = DataOperationKind.Clamp, Min = 0x10, Max = 0x80 });
        Assert.Equal(Bytes(0x10, 0x50, 0x80), u);
        Assert.Equal(2, su.ChangedElements);

        (byte[] s, DataOperationStats ss) = Run(Bytes(0x80, 0x00, 0x7F),
            new DataOperationSpec { Kind = DataOperationKind.Clamp, Type = ElementType.Signed, Min = -1, Max = 1 });
        Assert.Equal(Bytes(0xFF, 0x00, 0x01), s);
        Assert.Equal(2, ss.ChangedElements);

        (byte[] f, DataOperationStats sf) = Run(Bytes(0x00, 0x00, 0xC0, 0x7F, 0x00, 0x00, 0x00, 0x40),
            new DataOperationSpec { Kind = DataOperationKind.Clamp, Type = ElementType.Float, Size = 4, FloatMax = 1.0 });
        Assert.Equal(Bytes(0x00, 0x00, 0xC0, 0x7F, 0x00, 0x00, 0x80, 0x3F), f);
        Assert.Equal(1, sf.ChangedElements);

        Assert.Equal(DataOperationError.MinGreaterThanMax, new DataOperationSpec { Kind = DataOperationKind.Clamp, Min = 5, Max = 4 }.Validate());
    }

    // ---- 共通 ----

    [Fact]
    [Trait(TC, "TC-EDIT-31-01")]
    public void Identity_operations_are_detected_and_change_nothing()
    {
        Assert.True(Add(0).IsIdentity());
        Assert.False(Add(0, increment: 1).IsIdentity());
        (byte[] data, DataOperationStats stats) = Run(Bytes(1, 2, 3), new DataOperationSpec { Kind = DataOperationKind.Clamp, Min = 0, Max = 10 });
        Assert.Equal(Bytes(1, 2, 3), data);
        Assert.False(stats.Changed);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-31-03")]
    public async Task Cancelling_leaves_the_document_unchanged_and_one_undo_restores()
    {
        using var doc = new Document(new VirtualByteSource(64L * 1024 * 1024), Options());
        DataOperationRunner runner = DataOperationRunner.For(doc);
        var spec = new DataOperationSpec { Kind = DataOperationKind.Xor, Operand = 0xFF };
        TargetRange[] range = [new(0, 40L * 1024 * 1024)];
        var center = new OperationCenter();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => center.RunAsync("XOR", OperationKind.ModifiesDocument, doc, range[0].Length,
            op =>
            {
                op.Cancel();
                return Task.FromResult(runner.Run(doc.Current, range, spec, op));
            }));
        Assert.Equal(1, doc.History.Count);
        Assert.Empty(Directory.Exists(TempFolder(doc)) ? Directory.GetFiles(TempFolder(doc), "dataop-*") : []);

        byte before = ReadAll(doc.Current)[0x40];
        TransformApplier.Apply(doc, runner.Run(doc.Current, range, spec).Replacements, "XOR");
        Assert.Equal((byte)(before ^ 0xFF), Read(doc.Current, 0x40, 1)[0]);
        Assert.Equal(Read(doc.Current, range[0].Length, 1)[0], ((VirtualByteSource)doc.Source).ValueAt(range[0].Length));
        doc.Undo();
        Assert.Equal(before, Read(doc.Current, 0x40, 1)[0]);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-31-03")]
    public async Task Large_ranges_read_the_next_chunk_ahead_while_the_current_one_is_processed()
    {
        // 2 つのバッファで先読みする: 最初のチャンクの進捗を報告する時点で、次のチャンクの読み込みがもう始まっている。
        int chunk = DataOperationRunner.ChunkSize;
        var source = new OffsetRecordingSource(new VirtualByteSource(3L * chunk + 24)) { HoldFrom = chunk };
        source.Hold.Reset();
        using var doc = new Document(source, Options());
        DataOperationRunner runner = DataOperationRunner.For(doc);
        var spec = new DataOperationSpec { Kind = DataOperationKind.Add, Size = 8, Operand = 3, Increment = 1 };
        TargetRange[] range = [new(0, doc.Length)];
        bool readAheadSeen = false;
        var center = new OperationCenter();
        DataOperationResult result = await center.RunAsync("加算", OperationKind.ModifiesDocument, doc, range[0].Length, op =>
        {
            op.ProgressChanged += (_, _) =>
            {
                if (!source.Hold.IsSet)
                {
                    readAheadSeen = source.MaxRequestedEnd > chunk;
                    source.Hold.Set();
                }
            };
            return Task.Run(() => runner.Run(doc.Current, range, spec, op));
        });
        Assert.True(readAheadSeen);

        // 結果はチャンクの境界をまたいでも、メモリ上で 1 度に計算したものと同じ。
        byte[] original = ReadAll(doc.Current);
        TransformApplier.Apply(doc, result.Replacements, "加算");
        byte[] expected = (byte[])original.Clone();
        var reference = new DataOperationStats();
        var processor = new ElementProcessor(spec, reference);
        processor.StartRange();
        processor.Process(expected, 0);
        Assert.True(ReadAll(doc.Current).AsSpan().SequenceEqual(expected));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-31-03")]
    public void A_read_error_in_a_chunk_read_ahead_stops_the_operation_and_removes_the_temporary_file()
    {
        int chunk = DataOperationRunner.ChunkSize;
        var faulty = new FaultyByteSource(new VirtualByteSource(4L * chunk));
        faulty.AddReadError(2L * chunk + 100, 4);
        using var doc = new Document(faulty, Options());
        DataOperationRunner runner = DataOperationRunner.For(doc);
        var spec = new DataOperationSpec { Kind = DataOperationKind.Xor, Operand = 0x5A };
        DataReadException e = Assert.Throws<DataReadException>(() => runner.Run(doc.Current, [new TargetRange(0, doc.Length)], spec));
        Assert.Equal(2L * chunk + 100, e.Offset);
        Assert.Equal(1, doc.History.Count);
        Assert.Empty(Directory.Exists(TempFolder(doc)) ? Directory.GetFiles(TempFolder(doc), "dataop-*") : []);
    }

    /// <summary>読み込みの位置を記録し、<see cref="HoldFrom"/> より後ろの読み込みを <see cref="Hold"/> が開くまで待たせる。</summary>
    private sealed class OffsetRecordingSource(IByteSource inner) : ByteSourceBase
    {
        private long _maxEnd;

        public ManualResetEventSlim Hold { get; } = new(initialState: true);

        public long HoldFrom { get; init; } = long.MaxValue;

        public long MaxRequestedEnd => Interlocked.Read(ref _maxEnd);

        public override string DisplayName => inner.DisplayName;

        public override string Identity => inner.Identity;

        public override long Length => inner.Length;

        public override SourceCapabilities Capabilities => inner.Capabilities;

        public override ReadResult Read(long offset, Span<byte> buffer)
        {
            long end = offset + buffer.Length;
            long seen;
            while ((seen = Interlocked.Read(ref _maxEnd)) < end && Interlocked.CompareExchange(ref _maxEnd, end, seen) != seen)
            {
            }

            if (end > HoldFrom)
            {
                Hold.Wait();
            }

            return inner.Read(offset, buffer);
        }
    }

    private static string TempFolder(Document doc) => Path.Combine(doc.Options.TempDirectory, doc.Id.ToString("N"));

    [Fact]
    [Trait(TC, "TC-EDIT-31-02")]
    public void Preview_marks_changed_bytes_and_reports_the_trailing_bytes()
    {
        using Document doc = Doc(Enumerable.Range(0x10, 16).Select(i => (byte)i).ToArray());
        DataOperationPreview preview = DataOperationRunner.Preview(doc.Current, [new TargetRange(0, 5)], Add(1, size: 2));
        Assert.Equal(Bytes(0x10, 0x11, 0x12, 0x13, 0x14), preview.Before);
        Assert.Equal(Bytes(0x11, 0x11, 0x13, 0x13, 0x14), preview.After);
        Assert.True(preview.IsChanged(0));
        Assert.False(preview.IsChanged(1));
        Assert.Equal(1, preview.TrailingBytes);
        Assert.Equal(["4368", "4882"], preview.BeforeValues);
    }
}
