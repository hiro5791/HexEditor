using System.Diagnostics;
using HexEditor.Core.Compare;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Compare;

/// <summary>ANA-08 保存済みの内容との比較 (ピースツリーから差分を求める方式)。</summary>
public sealed class SavedContentDiffTests
{
    [Fact]
    [Trait(TC, "TC-ANA-08-01")]
    public void ThreeEditsInA100GiBFileAreFoundFromThePiecesAlone()
    {
        // TD-SPARSE-100G の代わりに、読み込んだバイト数を数える 100 GiB のデータソース (内容は 0)。
        const long Length = 100L << 30;
        var source = new FakeByteSource(Length, (_, s) => s.Clear(), SourceCapabilities.CanWrite | SourceCapabilities.CanResize);
        using var doc = new Document(source, Options());
        doc.Insert(0x10, [0xAA, 0xBB, 0xCC, 0xDD]);
        doc.Overwrite(0x80000000, [0x11, 0x22]);
        doc.Delete(0x1000000000, 0x100);
        source.Reads.Clear();

        var watch = Stopwatch.StartNew();
        IReadOnlyList<DiffRange> diffs = SavedContentDiff.Compute(doc.Current);
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(
        [
            new DiffRange(DiffKind.Inserted, 0x10, 0, 0x10, 4),
            new DiffRange(DiffKind.Changed, 0x80000000 - 4, 2, 0x80000000, 2),
            new DiffRange(DiffKind.Deleted, 0x1000000000 - 4, 0x100, 0x1000000000, 0),
        ], diffs);
        Assert.True(source.Reads.Sum(r => (long)r.Length) < 1 << 20);
    }

    [Fact]
    public void NoEditsMeansNoDifferences()
    {
        using var doc = new Document(new MemoryByteSource(new byte[1000]), Options());
        Assert.Empty(SavedContentDiff.Compute(doc.Current));
        doc.Overwrite(10, [1]);
        doc.Undo();
        Assert.Empty(SavedContentDiff.Compute(doc.Current));
    }

    [Fact]
    public void KindsFollowThePieces()
    {
        // 置き換え (長さの違う上書き) は短い方を「変更」、残りを「挿入」「削除」にする。移動した内容は削除と挿入になる。
        using var doc = new Document(new MemoryByteSource(new byte[1000]), Options());
        doc.Delete(100, 10);
        doc.Insert(100, [1, 2, 3, 4]);
        Assert.Equal(
        [
            new DiffRange(DiffKind.Changed, 100, 4, 100, 4),
            new DiffRange(DiffKind.Deleted, 104, 6, 104, 0),
        ], SavedContentDiff.Compute(doc.Current));

        using var moved = new Document(new MemoryByteSource(new byte[1000]), Options());
        moved.InsertCopy(1000, 0, 100);
        moved.Delete(0, 100);
        Assert.Equal(
        [
            new DiffRange(DiffKind.Deleted, 0, 100, 0, 0),
            new DiffRange(DiffKind.Inserted, 1000, 0, 900, 100),
        ], SavedContentDiff.Compute(moved.Current));

        using var truncated = new Document(new MemoryByteSource(new byte[1000]), Options());
        truncated.Delete(900, 100);
        truncated.Insert(0, [7]);
        Assert.Equal(
        [
            new DiffRange(DiffKind.Inserted, 0, 0, 0, 1),
            new DiffRange(DiffKind.Deleted, 900, 100, 901, 0),
        ], SavedContentDiff.Compute(truncated.Current));
    }
}
