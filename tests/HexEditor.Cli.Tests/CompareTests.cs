using System.Diagnostics;
using System.Text.Json;
using HexEditor.Cli.Tests.Support;
using HexEditor.Core.Compare;
using HexEditor.Core.Sources;
using HexEditor.TestData;

namespace HexEditor.Cli.Tests;

/// <summary>ファイルの比較 (AUTO-40)。</summary>
public sealed class CompareTests
{
    [Fact]
    [Trait("TC", "TC-AUTO-40-01")]
    public void Same_files_exit_with_0_and_files_differing_by_one_byte_exit_with_1()
    {
        using var dir = new TempDir();
        string seq = TestDataCatalog.Get("TD-SEQ-1M");
        string copy = dir.Copy("TD-SEQ-1M", "b.bin");
        string diff1 = TestDataCatalog.Get("TD-AUTO-SEQ-1M-DIFF1");

        CliResult same = Hexed.Run(dir.Path, "compare", seq, copy, "--lang", "en");
        Assert.Equal(0, same.ExitCode);
        Assert.Equal(["0 difference(s), 0 byte(s) in total"], same.OutLines);

        CliResult different = Hexed.Run(dir.Path, "compare", seq, diff1, "--lang", "en");
        Assert.Equal(1, different.ExitCode);
        Assert.Equal(["changed  0x80000  0x80000  1  00  FF", "1 difference(s), 1 byte(s) in total"], different.OutLines);

        CliResult japanese = Hexed.Run(dir.Path, "compare", seq, diff1, "--lang", "ja");
        Assert.Equal(["変更  0x80000  0x80000  1  00  FF", "差分 1 件、合計 1 バイト"], japanese.OutLines);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-40-01")]
    public void Different_lengths_report_the_rest_as_present_in_one_file_only()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("a.bin"), [1, 2, 3, 4, 5]);
        File.WriteAllBytes(dir.File("b.bin"), [1, 2, 3]);

        CliResult result = Hexed.Run(dir.Path, "compare", "a.bin", "b.bin", "--lang", "en");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("deleted  0x3  0x3  2  04 05  -", result.OutLines[0]);

        CliResult reversed = Hexed.Run(dir.Path, "compare", "b.bin", "a.bin", "--lang", "en");
        Assert.Equal("inserted  0x3  0x3  2  -  04 05", reversed.OutLines[0]);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-40-02")]
    public void Brief_stops_at_the_first_difference_of_a_100_gib_file_within_a_second()
    {
        using var dir = new TempDir();
        string a = TestDataCatalog.Get("TD-SPARSE-100G");
        string b = TestDataCatalog.Get("TD-AUTO-SPARSE-100G-DIFF0");

        var watch = Stopwatch.StartNew();
        CliResult result = Hexed.Run(dir.Path, "compare", a, b, "--brief", "--lang", "en");
        TimeSpan elapsed = watch.Elapsed;

        Assert.Equal(1, result.ExitCode);
        Assert.Single(result.OutLines);
        Assert.Contains("differ", result.OutLines[0]);
        Assert.Contains("0x0", result.OutLines[0]);
        PerfLimit.Check(elapsed < TimeSpan.FromSeconds(1), $"compare --brief took {elapsed.TotalSeconds:F2} s");

        // 同じファイルどうしは先頭から最後まで比べる (スパースの未割り当ての範囲でもストリームで読む)。
        CliResult same = Hexed.Run(dir.Path, "compare", "--brief", TestDataCatalog.Get("TD-MARKERS-1G"), TestDataCatalog.Get("TD-MARKERS-1G"), "--lang", "en");
        Assert.Equal(0, same.ExitCode);
        Assert.Equal("The files are the same.", same.OutLines[0]);
    }

    [Theory]
    [InlineData("byte", CompareMethod.Simple)]
    [InlineData("resync", CompareMethod.InsertDelete)]
    [Trait("TC", "TC-AUTO-40-03")]
    public void Json_differences_equal_the_result_list_exported_by_the_comparison_view(string mode, CompareMethod method)
    {
        using var dir = new TempDir();
        string a = TestDataCatalog.Get("TD-AUTO-CMP-A");
        string b = TestDataCatalog.Get("TD-AUTO-CMP-B");

        CliResult cli = Hexed.Run(dir.Path, "compare", a, b, "--format", "json", "--mode", mode, "--max", "100000");
        Assert.True(cli.ExitCode == 1, cli.Err);
        using JsonDocument document = JsonDocument.Parse(cli.OutBytes);
        Assert.Equal("hexeditor.compare/1", document.RootElement.GetProperty("schema").GetString());
        JsonElement differences = document.RootElement.GetProperty("differences");

        // GUI の比較結果の一覧のエクスポート (DiffExport) と同じ呼び出しで作った差分と比べる。
        using FileByteSource left = FileByteSource.Open(a);
        using FileByteSource right = FileByteSource.Open(b);
        using var result = new CompareResult(method, CompareRange.Whole(CompareData.FromSource(left)), CompareRange.Whole(CompareData.FromSource(right)));
        DataComparer.Run(new CompareOptions { Method = method }, result);
        using var export = new MemoryStream();
        DiffExport.WriteJson(result, Enumerable.Range(0, (int)result.Diffs.Count).Select(i => (long)i), export);
        using JsonDocument expected = JsonDocument.Parse(export.ToArray());

        Assert.Equal(expected.RootElement.GetRawText(), differences.GetRawText());
        Assert.Equal(result.Diffs.Count, document.RootElement.GetProperty("count").GetInt64());
        Assert.True(differences.GetArrayLength() >= 2);
        if (method == CompareMethod.InsertDelete)
        {
            Assert.Contains(differences.EnumerateArray(), d => d.GetProperty("kind").GetString() == "inserted");
            Assert.Contains(differences.EnumerateArray(), d => d.GetProperty("kind").GetString() == "deleted");
        }
    }

    [Fact]
    public void Max_limits_the_listed_differences_but_not_the_count_and_ranges_select_parts()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("a.bin"), new byte[64]);
        byte[] changed = new byte[64];
        for (int i = 0; i < 64; i += 4)
        {
            changed[i] = 1;
        }

        File.WriteAllBytes(dir.File("b.bin"), changed);

        CliResult limited = Hexed.Run(dir.Path, "compare", "a.bin", "b.bin", "--max", "3", "--lang", "en");
        Assert.Equal(1, limited.ExitCode);
        Assert.Equal(5, limited.OutLines.Length);
        Assert.Contains("13 more", limited.OutLines[3]);
        Assert.Equal("16 difference(s), 16 byte(s) in total", limited.OutLines[4]);

        CliResult ranged = Hexed.Run(dir.Path, "compare", "a.bin", "b.bin", "--range1", "1:3", "--range2", "1:3", "--lang", "en");
        Assert.Equal(0, ranged.ExitCode);

        CliResult shifted = Hexed.Run(dir.Path, "compare", "a.bin", "b.bin", "--range1", "1:3", "--range2", "0:3", "--lang", "en");
        Assert.Equal(1, shifted.ExitCode);
        Assert.StartsWith("changed  0x1  0x0  1", shifted.OutLines[0]);
    }

    [Fact]
    public void Compare_needs_two_paths_and_a_known_mode_and_reports_unreadable_files_with_3()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("a.bin"), [1]);
        Assert.Equal(2, Hexed.Run(dir.Path, "compare", "a.bin").ExitCode);
        Assert.Equal(2, Hexed.Run(dir.Path, "compare", "a.bin", "a.bin", "--mode", "x").ExitCode);
        Assert.Equal(3, Hexed.Run(dir.Path, "compare", "a.bin", "none.bin").ExitCode);
    }
}

/// <summary>時間の目標 (テスト方針 6.6): 性能テスト用の環境 (HEXEDITOR_PERF_MACHINE=1) では失敗にし、それ以外では警告にする。</summary>
internal static class PerfLimit
{
    public static bool IsPerfMachine => Environment.GetEnvironmentVariable("HEXEDITOR_PERF_MACHINE") == "1";

    public static void Check(bool ok, string message)
    {
        if (ok)
        {
            return;
        }

        Assert.False(IsPerfMachine, message);
        Console.WriteLine($"WARNING: {message} (a failure only on the performance machine: HEXEDITOR_PERF_MACHINE=1)");
    }
}
