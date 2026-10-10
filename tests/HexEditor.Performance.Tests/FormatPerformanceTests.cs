using System.Diagnostics;
using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>インポート (TOOL-04、TOOL-05) と範囲の切り出しと保存 (TOOL-16) の性能テスト。</summary>
[Trait("Category", "Performance")]
public sealed class FormatPerformanceTests(ITestOutputHelper output) : IDisposable
{
    private const string TC = "TC";
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>処理の間、200 ms ごとにプライベートバイトを記録し、最大値を返す。</summary>
    private static long PeakPrivateBytes(Action action)
    {
        long peak = 0;
        using var done = new ManualResetEventSlim();
        var sampler = new Thread(() =>
        {
            using Process process = Process.GetCurrentProcess();
            do
            {
                process.Refresh();
                peak = Math.Max(peak, process.PrivateMemorySize64);
            }
            while (!done.Wait(200));
        }) { IsBackground = true };
        sampler.Start();
        try
        {
            action();
        }
        finally
        {
            done.Set();
            sampler.Join();
        }

        return peak;
    }

    [Fact]
    [Trait(TC, "TC-TOOL-04-02")]
    public void Importing_1_gb_of_intel_hex_stays_under_the_memory_limit()
    {
        string path = TestDataCatalog.Get("TD-TOOL-IHEX-1G");
        long baseline = PrivateBytesAfterGc();
        ImportResult? result = null;
        long peak = PeakPrivateBytes(() => result = Importer.DecodeFile(path, new ImportOptions { Format = FormatIds.IntelHex },
            Path.Combine(_folder.Path, "temp")));
        using (result)
        {
            output.Report($"peak {peak / MiB} MiB (baseline {baseline / MiB} MiB)");
            Assert.True(peak - baseline < GiB, $"{(peak - baseline) / MiB} MiB");
            Assert.Equal(TestDataCatalog.IhexLargeData, result!.Length);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] a = new byte[MiB], b = new byte[MiB];
            for (long offset = 0; offset < result.Length; offset += a.Length)
            {
                result.Image!.Read(offset, a);
                TestDataCatalog.Random(TestDataCatalog.IhexLargeSeed, offset, b);
                hash.AppendData(a);
                expected.AppendData(b);
            }

            Assert.Equal(expected.GetHashAndReset(), hash.GetHashAndReset());
        }
    }

    [Fact]
    [Trait(TC, "TC-TOOL-05-02")]
    public void Sparse_i32hex_imports_quickly_with_little_memory()
    {
        string path = TestDataCatalog.Get("TD-TOOL-IHEX-SPARSE");
        string temp = Path.Combine(_folder.Path, "temp");
        long baseline = PrivateBytesAfterGc();
        ImportResult? result = null;
        long peak = 0;
        TimeSpan time = Time(() => peak = PeakPrivateBytes(() => result = Importer.DecodeFile(path,
            new ImportOptions { Format = FormatIds.IntelHex, Placement = AddressPlacement.Absolute }, temp)));
        using (result)
        {
            TimeLimit(time < TimeSpan.FromSeconds(1), Ms(time));
            Assert.True(peak - baseline < 50 * MiB, $"{(peak - baseline) / MiB} MiB");
            Assert.True(Directory.GetFiles(temp).Sum(f => new FileInfo(f).Length) < MiB);
            SparseImage image = result!.Image!;
            Assert.Equal(0xFFFF0010, image.Length);
            byte[] b = new byte[16];
            image.Read(0, b);
            Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i), b);
            image.Read(0xFFFF0000, b);
            Assert.Equal(Enumerable.Range(0xF0, 16).Select(i => (byte)i), b);
            byte[] one = new byte[1];
            image.Read(0x7FFFFFFF, one);
            Assert.Equal(0xFF, one[0]);
        }
    }

    [PerfMachineFact]
    [Trait(TC, "TC-TOOL-16-02")]
    public void Saving_a_50_gb_selection_does_not_grow_memory()
    {
        using var doc = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-SPARSE-100G")), Options());
        DocumentSnapshot snapshot = doc.Current;
        var source = new ExportSource { Read = (o, d) => snapshot.Read(o, d), Length = snapshot.Length };
        string target = Path.Combine(_folder.Path, "part.bin");
        long baseline = PrivateBytesAfterGc();
        long peak = PeakPrivateBytes(() => Exporter.WriteFile(target, stream =>
            Exporter.Write(source, [(10 * GiB, 50 * GiB)], new ExportOptions { Format = FormatIds.Binary }, stream)));
        Assert.True(peak - baseline < 512 * MiB, $"{(peak - baseline) / MiB} MiB");
        Assert.Equal(50 * GiB, new FileInfo(target).Length);
        Assert.Equal(TestDataCatalog.Marker(10 * GiB), ReadFile(target, 0, TestDataCatalog.MarkerLength));
    }

    /// <summary>
    /// TC-ENG-38-03: 1 GB の Base64 を開くと、デコードの完了前に先頭が表示され (デコード中は読み取り専用)、メモリ使用量が上限 (1 GiB) を
    /// 超えず、完了後は編集でき、デコード結果が元の乱数と一致する。アプリと同じ順 (デコードを長時間処理として別のスレッドで行い、書き終えた
    /// 先頭を表示用のデータソースとして取る) でエンジンを使う。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-ENG-38-03")]
    public void Decoding_1_gb_of_base64_shows_the_head_early_with_bounded_memory()
    {
        string path = TestDataCatalog.Get("TD-ENG-BASE64-1G");
        string temp = Path.Combine(_folder.Path, "temp");
        long baseline = PrivateBytesAfterGc();
        ImportResult? result = null;
        TimeSpan? headTime = null;
        byte[]? head = null;
        bool previewReadOnly = false;
        bool finishedBeforeHead = false;
        long peak = PeakPrivateBytes(() =>
        {
            var watch = Stopwatch.StartNew();
            var progress = new ImportProgress();
            Task<ImportResult> decoding = Task.Run(() => Importer.DecodeFile(path, EncodedFile.OpenOptions(FormatIds.Base64), temp, progress));

            // 1・3. 表示のタブの代わりに、書き終えた先頭を 0.1 秒ごとに見る (アプリは 0.5 秒ごと)。
            while (head is null && !decoding.IsCompleted)
            {
                if (progress.Preview("preview") is { } image)
                {
                    using (image)
                    using (var preview = new Document(image, Options()))
                    {
                        if (preview.Length >= 64)
                        {
                            headTime = watch.Elapsed;
                            head = Read(preview, 0, 64);
                            previewReadOnly = !image.Capabilities.HasFlag(SourceCapabilities.CanWrite);
                            break;
                        }
                    }
                }

                Thread.Sleep(100);
            }

            finishedBeforeHead = head is null;
            result = decoding.GetAwaiter().GetResult();
        });

        using (result)
        {
            output.Report($"head {headTime?.TotalMilliseconds:F0} ms, peak {peak / MiB} MiB (baseline {baseline / MiB} MiB)");

            // デコードの完了前に先頭が表示され、元の乱数の先頭と一致する。デコード中は書き込めない。
            Assert.False(finishedBeforeHead, "デコードが先頭の表示より先に終わりました");
            byte[] expectedHead = new byte[64];
            TestDataCatalog.Random(TestDataCatalog.Base64LargeSeed, 0, expectedHead);
            Assert.Equal(expectedHead, head);
            Assert.True(previewReadOnly);
            TimeLimit(headTime < TimeSpan.FromSeconds(1), $"先頭の表示まで {Ms(headTime!.Value)}");

            // 2. メモリ使用量の最大値が 1 GiB (既定の上限) を超えない。
            Assert.True(peak - baseline < GiB, $"{(peak - baseline) / MiB} MiB");

            // 3〜4. 完了後は編集でき、SHA-256 が元の乱数と一致する。
            using Document doc = EncodedFile.CreateDocument(result!, FormatIds.Base64, Options());
            Assert.Equal(TestDataCatalog.Base64LargeData, doc.Length);
            Assert.False(doc.IsReadOnly);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] a = new byte[MiB], b = new byte[MiB];
            for (long offset = 0; offset < doc.Length; offset += a.Length)
            {
                Assert.True(doc.Current.Read(offset, a).IsComplete);
                TestDataCatalog.Random(TestDataCatalog.Base64LargeSeed, offset, b);
                hash.AppendData(a);
                expected.AppendData(b);
            }

            Assert.Equal(expected.GetHashAndReset(), hash.GetHashAndReset());
            doc.Overwrite(0, [0x00]);
            Assert.True(doc.IsModified);
        }
    }
}
