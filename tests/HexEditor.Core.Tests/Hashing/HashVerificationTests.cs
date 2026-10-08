using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Hashing;

/// <summary>ANA-21 期待値との照合、ANA-22 結果のコピーと書き込み。</summary>
public sealed class HashVerificationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-hash").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static HashResultRow Row(string id, byte[] data, HashParameters? parameters = null, long start = 0) =>
        new(new HashAlgorithmChoice(HashCatalog.Get(id), parameters ?? HashParameters.Default),
            HashEngine.ComputeBytes(HashCatalog.Get(id), data, parameters), [new HashRange(start, data.Length)]);

    private static HashMatch Verify(HashResultRow row, string expected)
    {
        Assert.True(ExpectedHash.TryParse(expected, out ExpectedHash? e), expected);
        return e!.Compare(row);
    }

    [Fact]
    [Trait(TC, "TC-ANA-21-02")]
    public void ReversedCrcIsReportedAsByteOrderMatch()
    {
        HashResultRow crc = Row("crc32", "123456789"u8.ToArray());
        Assert.Equal("CBF43926", Convert.ToHexString(crc.Value));
        Assert.Equal(HashMatch.MatchReversed, Verify(crc, "2639F4CB"));
        Assert.Equal(HashMatch.Match, Verify(crc, "0xCBF43926"));
        Assert.Equal(HashMatch.MatchReversed, Verify(crc, "Jjn0yw=="));
        Assert.Equal(HashMatch.None, Verify(crc, "12345678"));
    }

    [Fact]
    public void ExpectedValuesAcceptTheDocumentedNotations()
    {
        // ANA-21 の仕様 2・3 (TC-ANA-21-01 の Core 側): 大文字・小文字、区切り、チェックサムファイルの 1 行。
        HashResultRow sha = Row("sha256", "123456789"u8.ToArray());
        const string Lower = "15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225";
        string colons = string.Join(':', Enumerable.Range(0, 32).Select(i => Lower.Substring(i * 2, 2).ToUpperInvariant()));
        Assert.Equal(HashMatch.Match, Verify(sha, Lower));
        Assert.Equal(HashMatch.Match, Verify(sha, colons));
        Assert.Equal(HashMatch.Match, Verify(sha, colons.Replace(':', '-')));
        Assert.Equal(HashMatch.Match, Verify(sha, string.Join(' ', colons.Split(':'))));
        Assert.Equal(HashMatch.Match, Verify(sha, $"{Lower} *check9.bin"));
        Assert.Equal(HashMatch.Match, Verify(sha, $"{Lower}  check9.bin"));
        Assert.Equal(HashMatch.Match, Verify(sha, $"SHA256 (check9.bin) = {Lower}"));
        Assert.True(ExpectedHash.TryParse($"SHA256 (check9.bin) = {Lower}", out ExpectedHash? bsd));
        Assert.Equal(("check9.bin", "SHA256"), (bsd!.FileName, bsd.AlgorithmHint));

        // 64 bit を超える値はバイト順を逆にして比べない。
        Assert.Equal(HashMatch.None, Verify(sha, Convert.ToHexString([.. sha.Value.Reverse()])));

        Assert.False(ExpectedHash.TryParse("xyz", out _));
        Assert.False(ExpectedHash.TryParse("ABC", out _));
        Assert.False(ExpectedHash.TryParse("  ", out _));
    }

    [Fact]
    [Trait(TC, "TC-ANA-21-03")]
    public void Sha256SumFileVerifiesTheDocument()
    {
        // TD-ANA-SHA256SUM は Git for Windows の sha256sum で TD-RANDOM-16M について作ったもの (なければ同じ形を .NET で作る)。
        string sumPath = TestDataCatalog.Generate("TD-ANA-SHA256SUM", _dir);
        string dataPath = Path.Combine(_dir, "TD-RANDOM-16M.bin");
        using var doc = new Document(FileByteSource.Open(dataPath), Options());

        IReadOnlyList<ChecksumEntry> entries = ChecksumFile.Parse(File.ReadAllText(sumPath), Path.GetExtension(sumPath));
        ChecksumEntry entry = Assert.IsType<ChecksumEntry>(ChecksumFile.FindEntry(entries, dataPath));
        (HashAlgorithmInfo algorithm, ExpectedHash expected) = ChecksumFile.Resolve(entry)!.Value;
        Assert.Equal("sha256", algorithm.Id);
        HashComputation result = HashEngine.Compute(doc.Current, new HashRequest { Algorithms = [new HashAlgorithmChoice(algorithm)] });
        Assert.Equal(HashMatch.Match, expected.Compare(result.Rows[0]));

        // 2. ファイル名の行を別の名前にすると、このファイルの行は見つからず、ファイル内の行を選べる一覧が残る。
        string renamed = File.ReadAllText(sumPath).Replace("TD-RANDOM-16M.bin", "other-name.bin", StringComparison.Ordinal);
        IReadOnlyList<ChecksumEntry> others = ChecksumFile.Parse(renamed, ".sha256");
        Assert.Null(ChecksumFile.FindEntry(others, dataPath));
        Assert.Equal("1: other-name.bin", ChecksumFile.Describe(Assert.Single(others)));
    }

    [Fact]
    public void ChecksumFileFormatsAreRecognised()
    {
        const string Md5 = "25f9e794323b453885f5181f1b624d0b";
        Assert.Equal("md5", Assert.Single(ChecksumFile.Parse($"{Md5} *a.bin\n", ".md5")).AlgorithmId);
        Assert.Equal("md5", Assert.Single(ChecksumFile.Parse($"MD5 (a.bin) = {Md5}\r\n", ".txt")).AlgorithmId);
        Assert.Equal("md5", Assert.Single(ChecksumFile.Parse($"{Md5}  dir/a.bin", null)).AlgorithmId);
        ChecksumEntry sfv = Assert.Single(ChecksumFile.Parse("; comment\nmy file.bin CBF43926\n", ".sfv"));
        Assert.Equal(("my file.bin", "crc32", "CBF43926"), (sfv.FileName, sfv.AlgorithmId, Convert.ToHexString(sfv.Value)));
        Assert.NotNull(ChecksumFile.FindEntry([sfv], @"C:\x\MY FILE.BIN"));
        Assert.NotNull(ChecksumFile.FindEntry(ChecksumFile.Parse($"{Md5}  dir/a.bin", null), "a.bin"));
    }

    [Fact]
    public void SavedSfvFileCanBeReadBack()
    {
        // ANA-22 の仕様 2: CRC-32 は .sfv (「ファイル名 CRC」) で保存し、ANA-21 の検証で読み戻せる。
        string dataPath = Path.Combine(_dir, "check.bin");
        File.WriteAllBytes(dataPath, Encoding.ASCII.GetBytes("123456789"));
        using var doc = new Document(FileByteSource.Open(dataPath), Options());
        HashComputation result = HashEngine.Compute(doc.Current, new HashRequest { Algorithms = [new HashAlgorithmChoice(HashCatalog.Get("crc32"))] });
        string sfv = Path.Combine(_dir, "check" + HashExport.ChecksumExtension(HashCatalog.Get("crc32")));
        HashExport.WriteChecksumFile(sfv, result.Rows, dataPath);
        Assert.Equal("check.bin CBF43926\n", File.ReadAllText(sfv));
        ChecksumEntry entry = Assert.Single(ChecksumFile.Parse(File.ReadAllText(sfv), ".sfv"));
        Assert.Equal("check.bin", entry.FileName);
        Assert.Equal(Convert.FromHexString("CBF43926"), entry.Value);
    }

    [Sha256SumFact]
    [Trait(TC, "TC-ANA-22-02")]
    public void SavedChecksumFilePassesSha256SumCheck()
    {
        string dataPath = TestDataCatalog.Generate("TD-RANDOM-16M", _dir);
        using var doc = new Document(FileByteSource.Open(dataPath), Options());
        HashComputation result = HashEngine.Compute(doc.Current, new HashRequest { Algorithms = [new HashAlgorithmChoice(HashCatalog.Get("sha256"))] });
        string sumPath = Path.Combine(_dir, "TD-RANDOM-16M" + HashExport.ChecksumExtension(HashCatalog.Get("sha256")));
        HashExport.WriteChecksumFile(sumPath, result.Rows, dataPath);

        // BOM なしの UTF-8、改行は LF。
        byte[] bytes = File.ReadAllBytes(sumPath);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.EndsWith(" *TD-RANDOM-16M.bin\n", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        var start = new ProcessStartInfo(TestDataCatalog.FindSha256Sum()!, "-c " + Path.GetFileName(sumPath))
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("OK", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyFormatsIncludeRangeAndParameters()
    {
        // TC-ANA-22-01 の Core 側: すべての行の JSON に範囲とパラメータが入る。
        byte[] data = new byte[0x400];
        TestDataCatalog.Sequence(0x100, data);
        HashResultRow[] rows =
        [
            Row("crc32", data, start: 0x100),
            Row("xxh64", data, new HashParameters { Seed = 0x1234 }, start: 0x100),
            Row("sha256", data, start: 0x100),
        ];
        var display = HashDisplayOptions.Default;
        JsonArray json = JsonNode.Parse(HashExport.Json(rows, display))!.AsArray();
        Assert.Equal(3, json.Count);
        Assert.All(json, item =>
        {
            Assert.Equal(256, item!["range"]!["start"]!.GetValue<long>());
            Assert.Equal(1024, item["range"]!["length"]!.GetValue<long>());
            Assert.NotNull(item["value"]);
        });
        Assert.Equal("xxHash64", json[1]!["algorithm"]!.GetValue<string>());
        Assert.Equal(4660UL, json[1]!["parameters"]!["seed"]!.GetValue<ulong>());

        Assert.Equal($"CRC-32: {Convert.ToHexString(rows[0].Value)}\nxxHash64 (seed=0x1234): {Convert.ToHexString(rows[1].Value)}\n",
            HashExport.NameValue(rows[..2], display));
        Assert.Equal($"{Convert.ToHexStringLower(rows[2].Value)} *a.bin\n", HashExport.ChecksumLines(rows[2..], @"C:\dir\a.bin"));
        string[] csv = HashExport.Csv(rows, display).TrimEnd('\n').Split('\n');
        Assert.Equal("algorithm,value,start,length", csv[0]);
        Assert.Equal($"CRC-32,{Convert.ToHexString(rows[0].Value)},256,1024", csv[1]);
        Assert.Equal(Convert.ToHexStringLower(rows[0].Value), HashExport.Value(rows[0], new HashDisplayOptions(HashValueFormat.HexLower)));
    }

    [Fact]
    public void WriteBackBytesAndWarning()
    {
        // ANA-22 の仕様 3・4 (フェーズ 2 の UI の Core 側)。
        HashResultRow crc = Row("crc32", "123456789"u8.ToArray());
        Assert.Equal("2639F4CB", Convert.ToHexString(HashWriteBack.Bytes(crc, littleEndian: true)));
        Assert.Equal("CBF43926", Convert.ToHexString(HashWriteBack.Bytes(crc, littleEndian: false)));
        Assert.True(HashWriteBack.OverlapsTarget(0x10, 4, [new(0, 0x14)], []));
        Assert.False(HashWriteBack.OverlapsTarget(0x10, 4, [new(0, 9)], []));
        Assert.False(HashWriteBack.OverlapsTarget(0x10, 4, [new(0, 0x14)], [new(0x10, 4)]));
        Assert.True(HashWriteBack.OverlapsTarget(0x10, 4, [new(0, 0x14)], [new(0x10, 2)]));
        Assert.Equal(2, HashWriteBack.Shortage(0x12, 4, 0x14));
        Assert.Equal(0, HashWriteBack.Shortage(0x10, 4, 0x14));
    }

    /// <summary>Git for Windows の sha256sum がない環境では実行しない (テストケースの環境: Git for Windows の sha256sum を使う)。</summary>
    private sealed class Sha256SumFactAttribute : FactAttribute
    {
        public Sha256SumFactAttribute()
        {
            if (TestDataCatalog.FindSha256Sum() is null)
            {
                Skip = "Git for Windows の sha256sum がありません。";
            }
        }
    }
}
