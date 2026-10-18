using HexEditor.Core.Editing.TransformView;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Editing;

/// <summary>EDIT-45 変換ビュー (変換の定義、表示どおりの読み込み、逆変換しての書き込み)。</summary>
public sealed class TransformViewTests
{
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    private static byte[] View(TransformDefinition definition, int length)
    {
        using var doc = new Document(new MemoryByteSource(new byte[length]), Options());
        doc.SetViewTransform(definition.Create());
        return Read(doc.Current, 0, length);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-45-02")]
    public void MultiByteKeyPositions()
    {
        // 手順 1: 鍵 01 02、起点 0。
        Assert.Equal("0102010201020102", Hex(View(TransformDefinition.Xor([1, 2]), 8)));

        // 手順 2: 起点を 3 に。起点より前も mod を 0 以上で計算する。
        Assert.Equal("0201020102010201", Hex(View(TransformDefinition.Xor([1, 2], TransformKeyOrigin.Offset, 3), 8)));

        // 手順 3: 範囲 4〜7、起点は範囲の先頭。
        var ranged = TransformDefinition.Xor([1, 2], TransformKeyOrigin.RangeStart) with { RangeStart = 4, RangeLength = 4 };
        Assert.Equal("0000000001020102", Hex(View(ranged, 8)));
    }

    [Fact]
    public void ReadingFromMiddleAndAcrossBoundariesMatchesWholeRead()
    {
        var def = new TransformDefinition
        {
            Ops =
            [
                new TransformOp { Kind = TransformOpKind.Add, Size = 4, Operand = 0x01020304, Increment = 7 },
                new TransformOp { Kind = TransformOpKind.ByteSwap16 },
                TransformOp.XorKey([0x5A, 0x3C, 0x11]),
                new TransformOp { Kind = TransformOpKind.RotateLeft, Size = 2, Bits = 3, BigEndian = true },
            ],
        };
        byte[] data = new byte[1001];
        new Random(45).NextBytes(data);
        using var doc = new Document(new MemoryByteSource(data), Options());
        doc.SetViewTransform(def.Create());
        byte[] whole = Read(doc.Current, 0, data.Length);
        for (int start = 0; start < 40; start++)
        {
            for (int len = 1; len < 23; len += 3)
            {
                Assert.Equal(whole.AsSpan(start, len).ToArray(), Read(doc.Current, start, len));
            }
        }

        // 最後の端数 (要素 4 バイトに満たない 1 バイト) は加算されない。
        byte[] raw = Read(doc.DataSnapshot, 1000, 1);
        Assert.NotNull(raw);
    }

    [Theory]
    [InlineData(TransformOpKind.Xor)]
    [InlineData(TransformOpKind.Add)]
    [InlineData(TransformOpKind.Subtract)]
    [InlineData(TransformOpKind.Not)]
    [InlineData(TransformOpKind.RotateLeft)]
    [InlineData(TransformOpKind.RotateRight)]
    [InlineData(TransformOpKind.ByteSwap16)]
    [InlineData(TransformOpKind.ByteSwap32)]
    [InlineData(TransformOpKind.ByteSwap64)]
    [InlineData(TransformOpKind.ReverseBits)]
    [InlineData(TransformOpKind.SwapNibbles)]
    public void EveryOperationHasInverse(TransformOpKind kind)
    {
        foreach (int size in new[] { 1, 2, 4, 8 })
        {
            var op = new TransformOp { Kind = kind, Size = size, Operand = 0x1234_5678_9ABC_DEF1, Increment = 3, Bits = Math.Min(5, size * 8 - 1), BigEndian = size == 4 };
            var def = new TransformDefinition { Ops = [op] };
            Assert.Equal(TransformError.None, def.Validate());
            ViewTransformChain chain = def.Create();
            Assert.True(chain.CanInverse);
            byte[] data = new byte[67];
            new Random(size).NextBytes(data);
            byte[] work = (byte[])data.Clone();
            chain.Forward(0, work);
            chain.Inverse(0, work);
            Assert.Equal(data, work);
        }
    }

    [Fact]
    public void ValidationOfKeysAndCounts()
    {
        Assert.Equal(TransformError.KeyEmpty, TransformDefinition.Xor([]).Validate());
        Assert.Equal(TransformError.KeyTooLong, TransformDefinition.Xor(new byte[257]).Validate());
        Assert.Equal(TransformError.None, TransformDefinition.Xor(new byte[256]).Validate());
        Assert.Equal(TransformError.OpCount, new TransformDefinition().Validate());
        Assert.Equal(TransformError.OpCount, new TransformDefinition { Ops = [.. Enumerable.Repeat(new TransformOp { Kind = TransformOpKind.Not }, 9)] }.Validate());
        Assert.Equal(TransformError.BitCountOutOfRange, new TransformDefinition { Ops = [new TransformOp { Kind = TransformOpKind.RotateLeft, Bits = 8 }] }.Validate());
        Assert.Equal("XOR 5A 3C", TransformDefinition.Xor([0x5A, 0x3C]).Label());
    }

    [Fact]
    public void JsonRoundTrip()
    {
        var def = new TransformDefinition
        {
            Ops = [TransformOp.XorKey([1, 2, 3]), new TransformOp { Kind = TransformOpKind.Add, Size = 2, Operand = 5, Increment = ulong.MaxValue }],
            RangeStart = 10,
            RangeLength = 20,
            KeyOrigin = TransformKeyOrigin.Offset,
            KeyOriginOffset = 7,
            Name = "mine",
        };
        TransformDefinition back = TransformDefinition.FromJson(def.ToJson())!;
        Assert.Equal(def.ToJson().ToJsonString(), back.ToJson().ToJsonString());
        Assert.Null(TransformDefinition.FromJson(new System.Text.Json.Nodes.JsonObject()));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-45-01")]
    public void ViewDoesNotChangeDataOrModifiedState()
    {
        using var doc = new Document(new MemoryByteSource(new byte[1024 * 1024]), Options());
        doc.SetViewTransform(TransformDefinition.Xor([0xFF]).Create());
        Assert.Equal(0xFF, Read(doc.Current, 0, 1)[0]);
        Assert.Equal(0xFF, Read(doc.Current, 0xFFFFF, 1)[0]);
        Assert.False(doc.IsModified);
        Assert.Equal(1, doc.History.Count);
        Assert.All(Read(doc.DataSnapshot, 0, 4096), b => Assert.Equal(0, b));
        Assert.Equal("XOR FF", doc.ViewTransform!.Label);
        Assert.False(doc.CanResize);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-45-03")]
    public void TypingInViewWritesInverseAndRemainsAfterClearing()
    {
        using var doc = new Document(new MemoryByteSource(new byte[1024 * 1024]), Options());
        doc.Overwrite(0x10, [0x1B, 0x1B]);
        int before = doc.History.Count;
        doc.SetViewTransform(TransformDefinition.Xor([0x5A]).Create());
        Assert.Equal(0x41, Read(doc.Current, 0x10, 1)[0]);

        var state = new EditorState(doc) { VisibleRows = 20 };
        state.ToggleColumn();
        state.Click(0x11, ActiveColumn.Text, false, false);
        Assert.Equal(EditResult.Done, state.TypeText("B"));
        Assert.Equal(0x42, Read(doc.Current, 0x11, 1)[0]);

        doc.SetViewTransform(null);
        Assert.Equal(0x1B, Read(doc.Current, 0x10, 1)[0]);
        Assert.Equal(0x18, Read(doc.Current, 0x11, 1)[0]);

        // 入力の編集が 1 件。変換ビューの設定・解除は履歴にない。
        Assert.Equal(before + 1, doc.History.Count);
    }

    [Fact]
    public void LengthChangesAreRefusedAndElementsAreRewrittenWhole()
    {
        using var doc = new Document(new MemoryByteSource(new byte[16]), Options());
        doc.SetViewTransform(new TransformDefinition { Ops = [new TransformOp { Kind = TransformOpKind.Add, Size = 2, Operand = 0x0101 }] }.Create());
        Assert.Throws<FixedLengthException>(() => doc.Insert(0, [1]));
        Assert.Throws<FixedLengthException>(() => doc.Delete(0, 1));

        // 表示は 01 01 …。2 バイト目だけを 05 にすると、要素 (0101 → 0501) を逆変換して 0400 を書く。
        doc.Overwrite(1, [0x05]);
        Assert.Equal("0105", Hex(Read(doc.Current, 0, 2)));
        Assert.Equal("0004", Hex(Read(doc.DataSnapshot, 0, 2)));

        // 塗りつぶし (パターンのピース) も表示上の値として書き込む。
        doc.OverwritePattern(4, 8, [0x11]);
        Assert.Equal("1111111111111111", Hex(Read(doc.Current, 4, 8)));
        Assert.Equal("1010101010101010", Hex(Read(doc.DataSnapshot, 4, 8)));
    }

    [Fact]
    public void ViewWithoutInverseIsReadOnly()
    {
        using var doc = new Document(new MemoryByteSource(new byte[16]), Options());
        var plugin = new ForwardOnly();
        doc.SetViewTransform(new TransformDefinition { Ops = [new TransformOp { Kind = TransformOpKind.Plugin, PluginId = "p.t" }] }.Create(_ => plugin));
        Assert.True(doc.IsReadOnly);
        Assert.True(doc.IsTransformReadOnly);
        Assert.Equal(0x01, Read(doc.Current, 3, 1)[0]);
        Assert.Throws<DocumentReadOnlyException>(() => doc.Overwrite(0, [1]));
        doc.SetViewTransform(null);
        Assert.False(doc.IsReadOnly);
    }

    [Fact]
    public void CopyPasteWithinViewUsesDisplayedValues()
    {
        using var doc = new Document(new MemoryByteSource([1, 2, 3, 4, 0, 0, 0, 0]), Options());
        doc.SetViewTransform(TransformDefinition.Xor([0xF0, 0x0F]).Create());
        byte[] shown = Read(doc.Current, 0, 4);
        doc.OverwriteFrom(4, doc.Current, 0, 4);
        Assert.Equal(shown, Read(doc.Current, 4, 4));
    }

    private sealed class ForwardOnly : IPluginTransform
    {
        public bool CanInverse => false;

        public void Forward(long offset, Span<byte> data) => data.Fill(1);

        public void Inverse(long offset, Span<byte> data) => throw new NotSupportedException();
    }
}
