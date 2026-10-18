using System.Security.Cryptography;
using System.Text;
using HexEditor.Core.Hashing;
using HexEditor.Core.Tools.Checksums;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Tools;

/// <summary>TOOL-18 チェックサムファイルの作成、TOOL-19 チェックサムファイルの検証。</summary>
public sealed class ChecksumFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-sums").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>テストデータのフォルダを作業用にコピーする (チェックサムファイルを書き込むため)。</summary>
    private string CopyFolder(string id)
    {
        string source = TestDataCatalog.FolderOf(id);
        string target = Path.Combine(_dir, id);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }

    private static ChecksumCreateResult Create(string folder, ChecksumCreateOptions options)
    {
        ChecksumPlan plan = ChecksumCreator.Plan([folder], options);
        return ChecksumCreator.Create(plan, options);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-18-01")]
    public void GnuFormatIsUtf8WithoutBomAndLfAndEveryFileHasALine()
    {
        string folder = CopyFolder("TD-TOOL-TREE");
        ChecksumCreateResult result = Create(folder, new ChecksumCreateOptions());
        string sums = Path.Combine(folder, "SHA256SUMS");
        Assert.Equal([sums], result.Written);
        Assert.Empty(result.Skipped);
        Assert.Equal(5, result.FileCount);

        byte[] bytes = File.ReadAllBytes(sums);
        Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.DoesNotContain((byte)'\r', bytes);
        string[] lines = Encoding.UTF8.GetString(bytes).TrimEnd('\n').Split('\n');
        Assert.Equal(5, lines.Length);

        // sha256sum -c が読む形: "<小文字の Hex>  <相対パス>"。値は .NET の SHA-256 と一致する。
        foreach (string line in lines)
        {
            Assert.Equal(' ', line[64]);
            Assert.Equal(' ', line[65]);
            string relative = line[66..];
            Assert.DoesNotContain('\\', relative);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, relative)))), line[..64]);
        }

        Assert.Equal(TestDataCatalog.ToolTreeFiles.OrderBy(x => x, StringComparer.Ordinal), lines.Select(l => l[66..]).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-18-02")]
    public void BsdFormatLinesHaveTheTagParenthesisedPathAndValue()
    {
        string folder = CopyFolder("TD-TOOL-TREE");
        Create(folder, new ChecksumCreateOptions { Format = ChecksumFormat.Bsd });
        string[] lines = File.ReadAllText(Path.Combine(folder, "CHECKSUM.SHA256")).TrimEnd('\n').Split('\n');
        Assert.Equal(5, lines.Length);
        foreach (string line in lines)
        {
            Assert.Matches(@"^SHA256 \(.+\) = [0-9a-f]{64}$", line);
        }

        Assert.Contains(lines, l => l.StartsWith("SHA256 (a.bin) = ", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-18-03")]
    public void JapaneseFileNamesAreWrittenAsUtf8()
    {
        string folder = CopyFolder("TD-TOOL-TREE");
        Create(folder, new ChecksumCreateOptions());
        byte[] bytes = File.ReadAllBytes(Path.Combine(folder, "SHA256SUMS"));
        byte[] expectedPath = Encoding.UTF8.GetBytes("日本語/テスト ファイル①.bin");
        Assert.True(bytes.AsSpan().IndexOf(expectedPath) > 0, "UTF-8 のパスのバイト列が見つかりません。");
        string text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain('\uFFFD', text);
        Assert.DoesNotContain('?', text);
    }

    [Fact]
    public void OptionsChangeSeparatorLineEndingBinaryMarkAndSubfolders()
    {
        string folder = CopyFolder("TD-TOOL-TREE");
        Create(folder, new ChecksumCreateOptions { Separator = ChecksumSeparator.Backslash, LineEnding = ChecksumLineEnding.CrLf, BinaryMark = true, IncludeSubfolders = false });
        string text = File.ReadAllText(Path.Combine(folder, "SHA256SUMS"));
        string[] lines = text.TrimEnd('\n').Split("\r\n");
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.Equal(" *", l.Substring(64, 2)));

        Create(folder, new ChecksumCreateOptions { Separator = ChecksumSeparator.Backslash, OutputFileName = "all.sums" });
        string all = File.ReadAllText(Path.Combine(folder, "all.sums"));
        Assert.Contains("sub\\c.bin", all, StringComparison.Ordinal);
        Assert.Contains("日本語\\テスト ファイル①.bin", all, StringComparison.Ordinal);
    }

    [Fact]
    public void SidecarSfvAndExistingOutputsAndUnreadableFiles()
    {
        string folder = CopyFolder("TD-TOOL-TREE");
        string a = Path.Combine(folder, "a.bin");
        ChecksumCreateOptions sidecar = new() { Format = ChecksumFormat.Sidecar, AlgorithmId = "md5" };
        ChecksumPlan plan = ChecksumCreator.Plan([a], sidecar);
        Assert.Equal([a + ".md5"], plan.OutputPaths);
        ChecksumCreator.Create(plan, sidecar);
        Assert.Equal($"{Convert.ToHexStringLower(MD5.HashData(File.ReadAllBytes(a)))}  a.bin\n", File.ReadAllText(a + ".md5"));
        Assert.Equal([a + ".md5"], ChecksumCreator.Plan([a], sidecar).ExistingOutputs);

        ChecksumCreateOptions sfv = new() { Format = ChecksumFormat.Sfv, AlgorithmId = "crc32" };
        ChecksumCreator.Create(ChecksumCreator.Plan([folder], sfv), sfv);
        string sfvFile = Path.Combine(folder, "TD-TOOL-TREE.sfv");
        Assert.Contains("a.bin 29058C73", File.ReadAllText(sfvFile), StringComparison.Ordinal);
        Assert.Matches(@"^[^\n]+ [0-9A-F]{8}$", File.ReadAllText(sfvFile).Split('\n')[0]);

        // 他のプログラムが共有なしで開いているファイルは飛ばし、最後に一覧にする。チェックサムファイルには含めない。
        using (new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ChecksumCreateOptions gnu = new() { OutputFileName = "partial.sums" };
            ChecksumCreateResult result = ChecksumCreator.Create(ChecksumCreator.Plan([folder], gnu), gnu);
            Assert.Single(result.Skipped);
            Assert.Equal(a, result.Skipped[0].Path);
            Assert.DoesNotContain("  a.bin\n", File.ReadAllText(Path.Combine(folder, "partial.sums")), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CancellationStopsHashing()
    {
        string folder = CopyFolder("TD-TOOL-TREE");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        ChecksumCreateOptions options = new();
        Assert.Throws<OperationCanceledException>(() => ChecksumCreator.Create(ChecksumCreator.Plan([folder], options), options, cancellationToken: cts.Token));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-19-02")]
    public void SumsWithStarLinesAreRecognisedAndVerified()
    {
        string folder = CopyFolder("TD-TOOL-SUMS-STAR");
        ChecksumDocument document = ChecksumReader.ReadFile(Path.Combine(folder, "SHA256SUMS"));
        Assert.Equal(ChecksumFormat.Gnu, document.Format);
        Assert.Equal("sha256", document.FileAlgorithmId);
        Assert.Empty(document.Errors);
        Assert.Equal([false, true, false], document.Entries.Select(e => e.Binary));

        IReadOnlyList<ChecksumResultRow> rows = ChecksumVerifier.Verify(document, folder);
        Assert.All(rows, r => Assert.Equal(ChecksumStatus.Match, r.Status));
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-19-01")]
    public void OnlyTheTamperedFileIsAMismatch()
    {
        string folder = CopyFolder("TD-TOOL-TREE");
        Create(folder, new ChecksumCreateOptions());
        using (var stream = new FileStream(Path.Combine(folder, "a.bin"), FileMode.Open, FileAccess.Write))
        {
            stream.Position = 10;
            stream.WriteByte(0xEE);
        }

        var seen = new List<ChecksumResultRow>();
        ChecksumDocument document = ChecksumReader.ReadFile(Path.Combine(folder, "SHA256SUMS"));
        IReadOnlyList<ChecksumResultRow> rows = ChecksumVerifier.Verify(document, folder, onRow: seen.Add);
        Assert.Equal(rows, seen);
        Assert.Equal(["a.bin"], rows.Where(r => r.Status == ChecksumStatus.Mismatch).Select(r => r.Path));
        Assert.Equal(4, rows.Count(r => r.Status == ChecksumStatus.Match));
        Assert.Equal(new ChecksumSummary(4, 1, 0, 0), ChecksumSummary.Of(rows));
        ChecksumResultRow mismatch = rows.Single(r => r.Status == ChecksumStatus.Mismatch);
        Assert.NotEqual(mismatch.Expected, mismatch.Actual);
        Assert.Equal(256, mismatch.Size);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-19-04")]
    public void FourStatesAreReportedForMixedSums()
    {
        string folder = CopyFolder("TD-TOOL-SUMS-MIXED");
        ChecksumDocument document = ChecksumReader.ReadFile(Path.Combine(folder, "SHA256SUMS"));
        using var locked = new FileStream(Path.Combine(folder, "locked.bin"), FileMode.Open, FileAccess.Read, FileShare.None);
        IReadOnlyList<ChecksumResultRow> rows = ChecksumVerifier.Verify(document, folder);
        Assert.Equal(
            [ChecksumStatus.Match, ChecksumStatus.Mismatch, ChecksumStatus.ReadError, ChecksumStatus.Missing],
            rows.Select(r => r.Status));
        Assert.Equal(new ChecksumSummary(1, 1, 1, 1), ChecksumSummary.Of(rows));
    }

    [Fact]
    public void DetectsFormatAndAlgorithmFromNamesAndShapes()
    {
        string hash = new('a', 32);
        ChecksumDocument md5 = ChecksumReader.Parse($"{hash}  x.bin\n", "x.bin.md5");
        Assert.Equal("md5", md5.Entries[0].AlgorithmId);

        ChecksumDocument bsd = ChecksumReader.Parse($"SHA512 (dir/f x.bin) = {new string('b', 128)}\r\n", "CHECKSUM");
        Assert.Equal(ChecksumFormat.Bsd, bsd.Format);
        Assert.Equal("sha512", bsd.Entries[0].AlgorithmId);
        Assert.Equal("dir/f x.bin", bsd.Entries[0].Path);

        ChecksumDocument sfv = ChecksumReader.Parse("; comment\nfile one.bin 0A1B2C3D\n", "x.sfv");
        Assert.Equal(ChecksumFormat.Sfv, sfv.Format);
        Assert.Equal(("file one.bin", "crc32"), (sfv.Entries[0].Path, sfv.Entries[0].AlgorithmId));

        // 判定できない値の長さ (10 バイト) は、アルゴリズムを選ばせる。
        ChecksumDocument unknown = ChecksumReader.Parse($"{new string('c', 20)}  y.bin\n", "sums.txt");
        Assert.True(unknown.NeedsAlgorithm);

        // 書式の誤りの行は、行番号を示して飛ばす。
        ChecksumDocument broken = ChecksumReader.Parse($"{hash}  ok.bin\nthis is not a checksum\n\n{hash}  ok2.bin\n", "SHA256SUMS");
        Assert.Equal(2, broken.Entries.Count);
        Assert.Equal([2], broken.Errors.Select(e => e.LineNumber));

        // coreutils のエスケープ付きの行 (名前に \ や改行を含む)。
        ChecksumDocument escaped = ChecksumReader.Parse($"\\{hash}  a\\\\b\\nc.bin\n", "SUMS");
        Assert.Equal("a\\b\nc.bin", escaped.Entries[0].Path);
    }

    [Fact]
    public void ChecksumFileNamesAreRecognisedAndSidecarsFound()
    {
        Assert.True(ChecksumReader.IsChecksumFile("C:\\x\\SHA256SUMS"));
        Assert.True(ChecksumReader.IsChecksumFile("a.bin.md5"));
        Assert.True(ChecksumReader.IsChecksumFile("CHECKSUM.SHA256"));
        Assert.True(ChecksumReader.IsChecksumFile("backup.sfv"));
        Assert.False(ChecksumReader.IsChecksumFile("notes.txt"));
        Assert.Equal("sha256", ChecksumReader.AlgorithmFromName("SHA256SUMS"));
        Assert.Null(ChecksumReader.AlgorithmFromName("sums.txt"));

        string folder = CopyFolder("TD-TOOL-MD5");
        Assert.Equal(Path.Combine(folder, "x.bin.md5"), ChecksumReader.FindSidecar(Path.Combine(folder, "x.bin")));
        Assert.Null(ChecksumReader.FindSidecar(Path.Combine(folder, "none.bin")));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-19-03")]
    public void Md5SidecarVerifiesAndIsMd5()
    {
        string folder = CopyFolder("TD-TOOL-MD5");
        ChecksumDocument document = ChecksumReader.ReadFile(Path.Combine(folder, "x.bin.md5"));
        IReadOnlyList<ChecksumResultRow> rows = ChecksumVerifier.Verify(document, folder);
        ChecksumResultRow row = Assert.Single(rows);
        Assert.Equal((ChecksumStatus.Match, "x.bin", "md5"), (row.Status, row.Path, row.AlgorithmId));
    }

    [Fact]
    public void FallbackAlgorithmIsUsedForUnknownLines()
    {
        string folder = CopyFolder("TD-TOOL-MD5");
        byte[] data = File.ReadAllBytes(Path.Combine(folder, "x.bin"));
        byte[] hash = HashEngine.ComputeBytes(HashCatalog.Get("sha1"), data);
        ChecksumDocument document = ChecksumReader.Parse(Convert.ToHexString(hash) + "  x.bin\n", "list.txt");
        Assert.Equal("sha1", document.Entries[0].AlgorithmId);

        ChecksumDocument ambiguous = ChecksumReader.Parse(Convert.ToHexString(hash)[..16] + "  x.bin\n", "list.txt");
        Assert.True(ambiguous.NeedsAlgorithm);
        IReadOnlyList<ChecksumResultRow> rows = ChecksumVerifier.Verify(ambiguous, folder);
        Assert.Equal(ChecksumStatus.ReadError, rows[0].Status);
        IReadOnlyList<ChecksumResultRow> withFallback = ChecksumVerifier.Verify(ambiguous, folder, "crc64-xz");
        Assert.NotEqual(ChecksumStatus.ReadError, withFallback[0].Status);
    }
}
