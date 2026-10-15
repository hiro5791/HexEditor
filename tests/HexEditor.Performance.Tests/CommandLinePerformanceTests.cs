using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HexEditor.TestData;
using Microsoft.Win32.SafeHandles;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>コマンドライン hexed の性能テスト (AUTO-38 の「巨大ファイル・長時間処理」)。hexed.exe をプロセスとして起動する。</summary>
[Trait("Category", "Performance")]
public sealed class CommandLinePerformanceTests(ITestOutputHelper output)
{
    private const string TC = "TC";

    /// <summary>テスト用のビルドの hexed.exe (環境変数 HEXED_PATH、なければ src/HexEditor.Cli/bin の下で最も新しいもの)。</summary>
    private static string HexedPath()
    {
        if (Environment.GetEnvironmentVariable("HEXED_PATH") is { Length: > 0 } configured)
        {
            return configured;
        }

        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "HexEditor.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        string bin = Path.Combine(dir ?? throw new InvalidOperationException("The repository root was not found."), "src", "HexEditor.Cli", "bin");
        return Directory.EnumerateFiles(bin, "hexed.exe", SearchOption.AllDirectories).Where(p => p.Contains("Debug", StringComparison.Ordinal)).OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    /// <summary>
    /// TC-AUTO-38-03: 10 GiB のファイルの SHA-256 が、バッファなしの連続読み込み (1 MiB 単位) の速度の 80% 以上で計算される。3 回の中央値で判定する。
    /// 性能テスト用の計測機でだけ実行する (10 GiB のテストデータを実際に書き、OS のファイルキャッシュを消すため)。
    /// </summary>
    [PerfMachineFact]
    [Trait(TC, "TC-AUTO-38-03")]
    public void Sha256OfATenGigabyteFileKeepsUpWithTheDisk()
    {
        string path = TestDataCatalog.Get("TD-AUTO-RANDOM-10G");
        long length = new FileInfo(path).Length;
        string expected = ExpectedSha256(length);

        var readSpeeds = new List<double>();
        var hashSpeeds = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            // 1. バッファなしで全体を読む時間 (基準)。
            FileCache.Purge();
            readSpeeds.Add(length / MiB / ReadUnbuffered(path).TotalSeconds);

            // 2. hexed hash -a sha256 -q の実行時間。
            FileCache.Purge();
            var info = new ProcessStartInfo(HexedPath(), ["hash", path, "-a", "sha256", "-q", "--format", "gnu"])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            info.Environment["HEXED_TEST_PROFILE"] = Path.Combine(Path.GetTempPath(), "HexEditorTests", "hexed-profile");
            var watch = Stopwatch.StartNew();
            using var process = Process.Start(info)!;
            string stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            watch.Stop();
            Assert.Equal(0, process.ExitCode);
            Assert.StartsWith(expected, stdout, StringComparison.Ordinal);
            hashSpeeds.Add(length / MiB / watch.Elapsed.TotalSeconds);
        }

        double read = readSpeeds.Order().ElementAt(1);
        double hash = hashSpeeds.Order().ElementAt(1);
        output.WriteLine($"SHA-256 {hash:F0} MiB/s、バッファなしの読み込み {read:F0} MiB/s");
        TimeLimit(hash >= read * 0.8, $"SHA-256 {hash:F0} MiB/s、読み込み {read:F0} MiB/s");
    }

    /// <summary>TD-AUTO-RANDOM-10G の SHA-256 (生成規則から求める。ファイルは読まない)。</summary>
    private static string ExpectedSha256(long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[4 * MiB];
        for (long offset = 0; offset < length; offset += buffer.Length)
        {
            int n = (int)Math.Min(buffer.Length, length - offset);
            TestDataCatalog.Random(TestDataCatalog.AutoRandom10GSeed, offset, buffer.AsSpan(0, n));
            hash.AppendData(buffer, 0, n);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>ファイル全体をバッファなし (FILE_FLAG_NO_BUFFERING) で 1 MiB ずつ読む時間。</summary>
    private static unsafe TimeSpan ReadUnbuffered(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, (FileOptions)0x20000000 | FileOptions.SequentialScan);
        int size = (int)MiB;
        byte* aligned = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
        try
        {
            var span = new Span<byte>(aligned, size);
            var watch = Stopwatch.StartNew();
            long offset = 0;
            int read;
            while ((read = RandomAccess.Read(handle, span, offset)) > 0)
            {
                offset += read;
            }

            return watch.Elapsed;
        }
        finally
        {
            NativeMemory.AlignedFree(aligned);
        }
    }
}
