using HexEditor.Core.Tools;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Tools;

/// <summary>TOOL-20 フォルダの比較。</summary>
public sealed class FolderCompareTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-fcmp").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static IReadOnlyList<FolderCompareRow> Compare(string left, string right, FolderCompareOptions? options = null) =>
        FolderComparer.Compare(left, right, options ?? new FolderCompareOptions());

    [Fact]
    [Trait(TC, "TC-TOOL-20-01")]
    public void DifferentAndOneSidedFilesAreReportedWithTheFirstDifference()
    {
        IReadOnlyList<FolderCompareRow> rows = Compare(TestDataCatalog.FolderOf("TD-TOOL-FOLDER-L"), TestDataCatalog.FolderOf("TD-TOOL-FOLDER-R"));
        FolderCompareRow[] shown = [.. rows.Where(r => r.Status != FolderCompareStatus.Same)];
        Assert.Equal(3, shown.Length);

        FolderCompareRow different = Assert.Single(shown, r => r.Status == FolderCompareStatus.Different);
        Assert.Equal(("sub/diff.bin", 0x1234L, 64 * 1024L, 64 * 1024L), (different.RelativePath, different.FirstDifference, different.LeftSize, different.RightSize));
        FolderCompareRow left = Assert.Single(shown, r => r.Status == FolderCompareStatus.LeftOnly);
        Assert.Equal(("left-only.bin", 16L, (long?)null), (left.RelativePath, left.LeftSize, left.RightSize));
        Assert.Single(shown, r => r.Status == FolderCompareStatus.RightOnly && r.RelativePath == "right-only.bin");

        // SAME.BIN と same.bin は大文字・小文字を区別せずに対応付け、同じ内容として「同じ」になる。
        FolderCompareRow same = Assert.Single(rows, r => r.Status == FolderCompareStatus.Same);
        Assert.Equal("SAME.BIN", same.RelativePath);
        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public void SizeDifferenceDoesNotReadContentsAndHashModeShowsValues()
    {
        string a = Directory.CreateDirectory(Path.Combine(_dir, "a")).FullName, b = Directory.CreateDirectory(Path.Combine(_dir, "b")).FullName;
        File.WriteAllBytes(Path.Combine(a, "x.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(b, "x.bin"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(a, "y.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(b, "y.bin"), [1, 9, 3]);
        File.WriteAllBytes(Path.Combine(a, "z.txt"), [7]);
        File.WriteAllBytes(Path.Combine(b, "z.txt"), [7]);

        IReadOnlyList<FolderCompareRow> rows = Compare(a, b);
        Assert.Equal((FolderCompareStatus.Different, (long?)null), (rows.Single(r => r.RelativePath == "x.bin").Status, rows.Single(r => r.RelativePath == "x.bin").FirstDifference));
        Assert.Equal(1L, rows.Single(r => r.RelativePath == "y.bin").FirstDifference);

        IReadOnlyList<FolderCompareRow> hashed = Compare(a, b, new FolderCompareOptions { UseHash = true, Pattern = "*.bin;*.txt" });
        FolderCompareRow y = hashed.Single(r => r.RelativePath == "y.bin");
        Assert.Equal(FolderCompareStatus.Different, y.Status);
        Assert.Equal(64, y.LeftHash!.Length);
        Assert.NotEqual(y.LeftHash, y.RightHash);
        Assert.Null(y.FirstDifference);

        IReadOnlyList<FolderCompareRow> onlyTxt = Compare(a, b, new FolderCompareOptions { Pattern = "*.TXT" });
        Assert.Equal(["z.txt"], onlyTxt.Select(r => r.RelativePath));
    }

    [Fact]
    public void SubfoldersCanBeLeftOutAndMissingFolderContentsAreOneSided()
    {
        string a = Directory.CreateDirectory(Path.Combine(_dir, "a", "deep", "er")).FullName, b = Directory.CreateDirectory(Path.Combine(_dir, "b")).FullName;
        File.WriteAllBytes(Path.Combine(a, "f.bin"), [1]);
        File.WriteAllBytes(Path.Combine(_dir, "a", "top.bin"), [1]);
        File.WriteAllBytes(Path.Combine(b, "top.bin"), [1]);

        IReadOnlyList<FolderCompareRow> rows = Compare(Path.Combine(_dir, "a"), b);
        Assert.Equal(("deep/er/f.bin", FolderCompareStatus.LeftOnly), (rows.Single(r => r.Status == FolderCompareStatus.LeftOnly).RelativePath, FolderCompareStatus.LeftOnly));

        IReadOnlyList<FolderCompareRow> shallow = Compare(Path.Combine(_dir, "a"), b, new FolderCompareOptions { IncludeSubfolders = false });
        Assert.Equal(["top.bin"], shallow.Select(r => r.RelativePath));
    }

    [Fact]
    public void UnreadableFilesAreReadErrorsAndTheComparisonContinues()
    {
        string a = Directory.CreateDirectory(Path.Combine(_dir, "a")).FullName, b = Directory.CreateDirectory(Path.Combine(_dir, "b")).FullName;
        File.WriteAllBytes(Path.Combine(a, "locked.bin"), [1, 2]);
        File.WriteAllBytes(Path.Combine(b, "locked.bin"), [1, 2]);
        File.WriteAllBytes(Path.Combine(a, "z.bin"), [1, 2]);
        File.WriteAllBytes(Path.Combine(b, "z.bin"), [1, 2]);
        using var locked = new FileStream(Path.Combine(a, "locked.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        IReadOnlyList<FolderCompareRow> rows = Compare(a, b);
        Assert.Equal(FolderCompareStatus.ReadError, rows.Single(r => r.RelativePath == "locked.bin").Status);
        Assert.Equal(FolderCompareStatus.Same, rows.Single(r => r.RelativePath == "z.bin").Status);
    }

    [Fact]
    public void CancellationStopsTheComparison()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => FolderComparer.Compare(TestDataCatalog.FolderOf("TD-TOOL-FOLDER-L"), TestDataCatalog.FolderOf("TD-TOOL-FOLDER-R"),
            new FolderCompareOptions(), cancellationToken: cts.Token));
    }

    [Fact]
    public void FirstDifferenceInALargeFileIsFoundWithoutReadingTheRest()
    {
        // 8 MiB の同じ内容の先頭 1 MiB 以降で違う場合でも、違いが見つかった時点で止める (オフセットが正しい)。
        string a = Directory.CreateDirectory(Path.Combine(_dir, "a")).FullName, b = Directory.CreateDirectory(Path.Combine(_dir, "b")).FullName;
        byte[] data = new byte[3 * 1024 * 1024 + 5];
        File.WriteAllBytes(Path.Combine(a, "big.bin"), data);
        data[2 * 1024 * 1024 + 3] = 1;
        File.WriteAllBytes(Path.Combine(b, "big.bin"), data);
        Assert.Equal(2L * 1024 * 1024 + 3, Compare(a, b).Single().FirstDifference);
    }
}
