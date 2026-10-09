using System.Diagnostics;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Sources;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>
/// ハッシュの計算の速さ (ANA-18 の「巨大ファイル・長時間処理」、ANA-20 の「テーブル方式の CRC-32 相当で 1 GB/s 以上」)。
/// ストレージの速さに左右されないよう、メモリ上の 256 MiB を計算エンジンで直接計算する。
/// </summary>
[Trait("Category", "Performance")]
public sealed class HashPerformanceTests(ITestOutputHelper output)
{
    private const int Length = 256 * 1024 * 1024;
    private static readonly Lazy<byte[]> Data = new(() =>
    {
        byte[] data = new byte[Length];
        new Random(18).NextBytes(data);
        return data;
    });

    private double MeasureGigabytesPerSecond(params string[] ids)
    {
        using var doc = new Document(new MemoryByteSource(Data.Value), new DocumentOptions
        {
            TempDirectory = Path.Combine(Path.GetTempPath(), "HexEditorTests", "recovery"),
        });
        var request = new HashRequest { Algorithms = [.. ids.Select(id => new HashAlgorithmChoice(HashCatalog.Get(id)))] };
        HashEngine.Compute(doc.Current, request with { Ranges = [new HashRange(0, 16 * 1024 * 1024)] });
        var watch = Stopwatch.StartNew();
        HashEngine.Compute(doc.Current, request);
        double speed = Length / watch.Elapsed.TotalSeconds / 1e9;
        output.WriteLine($"{string.Join(" + ", ids)}: {speed:F2} GB/s");
        return speed;
    }

    [Theory]
    [InlineData("crc32")]
    [InlineData("crc32c")]
    public void TableCrcIsAtLeastOneGigabytePerSecond(string id) => TimeLimit(MeasureGigabytesPerSecond(id) is var speed && speed >= 1.0, $"{speed:F2} GB/s");

    [Fact]
    public void Sha256IsAtLeastOneGigabytePerSecond()
    {
        // 目標は SHA 拡張命令のある CPU のもの (ANA-18)。.NET は OS (CNG) の実装を使い、命令があれば使う。
        double speed = MeasureGigabytesPerSecond("sha256");
        TimeLimit(speed >= 1.0, $"{speed:F2} GB/s");
    }

    [Fact]
    public void CommonSetRunsInParallel()
    {
        // よく使う 4 つは並列に計算するため、全体の速さは最も遅いもの (MD5) と同程度になる。
        double all = MeasureGigabytesPerSecond("crc32", "md5", "sha1", "sha256");
        double md5 = MeasureGigabytesPerSecond("md5");
        TimeLimit(all >= md5 * 0.6, $"{all:F2} GB/s (MD5 alone {md5:F2} GB/s)");
    }
}
