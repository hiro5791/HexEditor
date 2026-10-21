using System.Diagnostics;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Templates.Inference;
using HexEditor.TestData;
using Xunit.Abstractions;

namespace HexEditor.Performance.Tests;

/// <summary>テンプレートの推定 (TPL-38・TPL-39) の性能テスト。</summary>
[Trait("Category", "Performance")]
public sealed class TemplateInferencePerformanceTests(ITestOutputHelper output)
{
    private const string TC = "TC";

    /// <summary>TD-TPL-INFER-1M を、ファイルにせずに位置から計算して返すデータソース (読んだバイト数を数える)。</summary>
    private sealed class CountingRec48Source(TestDataCatalog.InferRec48 layout) : ByteSourceBase
    {
        private long _bytesRead;

        public long BytesRead => Interlocked.Read(ref _bytesRead);

        public override string DisplayName => "TD-TPL-INFER-1M";

        public override string Identity { get; } = "test:" + Guid.NewGuid().ToString("N");

        public override long Length => layout.Length;

        public override SourceCapabilities Capabilities => SourceCapabilities.None;

        public override ReadResult Read(long offset, Span<byte> buffer)
        {
            int count = (int)Math.Clamp(Length - offset, 0, buffer.Length);
            layout.Fill(offset, buffer[..count]);
            Interlocked.Add(ref _bytesRead, count);
            return new ReadResult(count);
        }
    }

    [Fact]
    [Trait(TC, "TC-TPL-38-05")]
    public async Task A_million_records_are_inferred_quickly_reading_at_most_17_MiB()
    {
        TestDataCatalog.InferRec48 layout = TestDataCatalog.InferRec48.Large;
        var source = new CountingRec48Source(layout);
        using var doc = new Document(source, PerfSupport.Options());
        long recordsEnd = 48L * 1_000_000;
        Assert.Equal(0x2DC6C00, recordsEnd);

        // 推定はバックグラウンドのスレッドで行う (UI のスレッドを使わない。TPL-38 の仕様 7)
        var watch = Stopwatch.StartNew();
        TemplateInferenceResult result = await Task.Run(() => TemplateInference.Run(doc.Current,
            new TemplateInferenceRequest { Start = 0, Length = recordsEnd }));
        watch.Stop();
        output.WriteLine($"{watch.ElapsedMilliseconds} ms, read {source.BytesRead:N0} bytes, inferrer {result.BytesRead:N0} bytes");

        Assert.Equal(48, result.RecordLength);
        Assert.Equal(1_000_000, result.RecordCount);
        Assert.Equal(InferredKind.Counter, result.Fields.Single(f => f.Offset == 4).Kind);
        Assert.True(source.BytesRead <= 17L * 1024 * 1024, $"read {source.BytesRead:N0} bytes");
        PerfSupport.TimeLimit(watch.Elapsed < TimeSpan.FromSeconds(2), $"inference took {watch.ElapsedMilliseconds} ms (target 2 s)");
    }
}
