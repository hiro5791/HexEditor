using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Editing;

/// <summary>ビット単位の挿入と削除 (EDIT-37)。</summary>
public sealed class BitShiftTests
{
    private static byte[] Bytes(params byte[] b) => b;

    private static DataOperationSpec Insert(long bits, bool one = false, BitShiftScope scope = BitShiftScope.ToEnd, long offset = 0, int bitIndex = 7) =>
        new() { Kind = DataOperationKind.InsertBits, BitCount = bits, FillWithOne = one, BitScope = scope, BitOffset = offset, BitIndex = bitIndex };

    private static DataOperationSpec Delete(long bits, BitShiftScope scope = BitShiftScope.ToEnd, long offset = 0, int bitIndex = 7) =>
        new() { Kind = DataOperationKind.DeleteBits, BitCount = bits, BitScope = scope, BitOffset = offset, BitIndex = bitIndex };

    private static byte[] Run(byte[] data, DataOperationSpec spec, TargetRange? selection = null, bool canResize = true)
    {
        using var doc = new Document(new MemoryByteSource(data, capabilities: canResize ? SourceCapabilities.CanResize | SourceCapabilities.CanWrite : SourceCapabilities.CanWrite), Options());
        using BitEditPlan plan = BitShifter.For(doc).Build(doc.Current, doc.CanResize, selection ?? new TargetRange(0, data.Length), spec);
        int before = doc.History.Count;
        plan.Apply(doc, "ビット");
        Assert.Equal(before + 1, doc.History.Count);
        Assert.Equal(plan.NewLength, doc.Length);
        byte[] result = ReadAll(doc.Current);
        doc.Undo();
        Assert.Equal(data, ReadAll(doc.Current));
        return result;
    }

    // ---- TC-EDIT-37-01 ----

    [Fact]
    [Trait(TC, "TC-EDIT-37-01")]
    public void Inserting_and_deleting_bits()
    {
        Assert.Equal(Bytes(0x7F, 0x80, 0x00), Run(Bytes(0xFF, 0x00), Insert(1)));
        Assert.Equal(Bytes(0xFF, 0x00, 0x00), Run(Bytes(0x7F, 0x80, 0x00), Delete(1)));
        Assert.Equal(Bytes(0x7F, 0x80), Run(Bytes(0xFF, 0x00), Insert(1, scope: BitShiftScope.SelectionOnly)));
        Assert.Equal(Bytes(0xFE, 0x00), Run(Bytes(0xFF, 0x00), Delete(1, scope: BitShiftScope.SelectionOnly)));
        Assert.Equal(Bytes(0xFF, 0xF0, 0x00), Run(Bytes(0xFF, 0x00), Insert(4, one: true)));
    }

    /// <summary>1 ビットずつ計算する基準の実装 (最上位ビットから)。</summary>
    private static byte[] Reference(byte[] data, DataOperationSpec spec, TargetRange selection)
    {
        var bits = new List<int>();
        foreach (byte b in data)
        {
            for (int i = 0; i < 8; i++)
            {
                bits.Add(spec.LsbFirst ? (b >> i) & 1 : (b >> (7 - i)) & 1);
            }
        }

        int p = (int)BitShifter.LinearPosition(spec);
        int k = (int)spec.BitCount;
        bool toEnd = spec.BitScope == BitShiftScope.ToEnd;
        int regionEnd = toEnd ? bits.Count : (int)selection.End * 8;
        List<int> tail = bits.GetRange(p, regionEnd - p);
        List<int> after = bits.GetRange(regionEnd, bits.Count - regionEnd);
        if (spec.Kind == DataOperationKind.InsertBits)
        {
            tail.InsertRange(0, Enumerable.Repeat(spec.FillWithOne ? 1 : 0, k));
        }
        else
        {
            tail.RemoveRange(0, k);
        }

        if (!toEnd)
        {
            int length = regionEnd - p;
            tail = tail.Count >= length ? tail.GetRange(0, length) : [.. tail, .. Enumerable.Repeat(0, length - tail.Count)];
        }

        List<int> all = [.. bits.GetRange(0, p), .. tail, .. after];
        while (all.Count % 8 != 0)
        {
            all.Add(0);
        }

        byte[] result = new byte[all.Count / 8];
        for (int i = 0; i < all.Count; i++)
        {
            int bit = spec.LsbFirst ? i % 8 : 7 - (i % 8);
            result[i / 8] |= (byte)(all[i] << bit);
        }

        return result;
    }

    [Theory]
    [Trait(TC, "TC-EDIT-37-01")]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(24)]
    public void Results_match_a_bitwise_reference(int bitCount)
    {
        var random = new Random(bitCount);
        for (int round = 0; round < 60; round++)
        {
            byte[] data = new byte[random.Next(4, 40)];
            random.NextBytes(data);
            bool insert = random.Next(2) == 0;
            bool lsb = random.Next(2) == 0;
            bool toEnd = random.Next(2) == 0;
            int selStart = random.Next(0, data.Length / 2);
            int selEnd = random.Next(selStart + 1, data.Length + 1);
            int offset = toEnd ? random.Next(0, data.Length) : random.Next(selStart, selEnd);
            var spec = new DataOperationSpec
            {
                Kind = insert ? DataOperationKind.InsertBits : DataOperationKind.DeleteBits,
                BitCount = bitCount,
                FillWithOne = random.Next(2) == 0,
                LsbFirst = lsb,
                BitScope = toEnd ? BitShiftScope.ToEnd : BitShiftScope.SelectionOnly,
                BitOffset = offset,
                BitIndex = random.Next(0, 8),
            };
            var selection = new TargetRange(selStart, selEnd - selStart);
            BitShiftInfo info = BitShifter.Analyze(data.Length, true, selection, spec);
            if (info.Error != DataOperationError.None)
            {
                continue;
            }

            Assert.Equal(Reference(data, spec, selection), Run(data, spec, selection));
        }
    }

    [Fact]
    [Trait(TC, "TC-EDIT-37-01")]
    public void Fixed_length_documents_allow_only_the_selection_scope()
    {
        Assert.Equal(DataOperationError.FixedLength, BitShifter.Analyze(2, canResize: false, new TargetRange(0, 2), Insert(1)).Error);
        Assert.Equal(Bytes(0x7F, 0x80), Run(Bytes(0xFF, 0x00), Insert(1, scope: BitShiftScope.SelectionOnly), canResize: false));
        Assert.Equal(Bytes(0x00, 0xFF), Run(Bytes(0xFF, 0x00), Insert(8, scope: BitShiftScope.SelectionOnly), canResize: false));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-37-02")]
    public void Multiples_of_eight_bits_use_piece_operations_on_a_huge_document()
    {
        const long length = 100L * 1024 * 1024 * 1024;
        using var doc = new Document(new VirtualByteSource(length), Options());
        BitShiftInfo info = BitShifter.Analyze(length, true, null, Insert(8, one: true, offset: 0x10));
        Assert.True(info.UsesPieces);
        Assert.Equal(0, info.RewriteBytes);
        using BitEditPlan plan = BitShifter.For(doc).Build(doc.Current, true, null, Insert(8, one: true, offset: 0x10));
        plan.Apply(doc, "ビットの挿入");
        Assert.Equal(length + 1, doc.Length);
        Assert.Equal(0xFF, Read(doc.Current, 0x10, 1)[0]);
        Assert.Equal(Read(doc.Current, 0x40000001, 8), BitConverter.GetBytes(0x40000000L));
        Assert.True(BitShifter.Analyze(length, true, null, Insert(9, offset: 0x10)).RewriteBytes > BitShifter.ConfirmLimit);
    }
}
