using HexEditor.Core.Sources;
using HexEditor.Core.Templates;
using HexEditor.Core.Tests.Support;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>
/// エクスポートのメモリ (TPL-25)。プロセス全体のメモリを測るので、並列に動く他のテストの確保を数えないよう、他のテストと並列に動かさない。
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class TemplateExportMemoryTests
{
    [Fact]
    [Trait(TC, "TC-TPL-25-04")]
    [Trait("Category", "Nightly")]
    public void TenMillionElementExportDoesNotGrowMemory()
    {
        var source = new VirtualByteSource(1L << 30, VirtualContent.Zero);
        TemplateResult r = Run(Load("struct P { u32 a; u32 b; }; P arr[10000000];"), TemplateDataSource.FromByteSource(source));
        foreach (TemplateExportFormat format in new[] { TemplateExportFormat.Json, TemplateExportFormat.Csv })
        {
            long start = GC.GetTotalMemory(true);
            long peak = start;
            // 回収済みでないごみ (世代 0 の予算は環境で大きく違う) を数えないよう、一定の件数ごとに回収してから測る。
            long nextSample = 250_000;
            var options = new TemplateExportOptions
            {
                Indented = false,
                Progress = n =>
                {
                    if (n >= nextSample)
                    {
                        nextSample = n + 250_000;
                        peak = Math.Max(peak, GC.GetTotalMemory(true));
                    }
                },
            };
            var sink = new TemplateToolingTests.CountingStream();
            TemplateResultExporter.Write(r.Root, sink, format, options);
            Assert.True(peak - start < 256L * 1024 * 1024, $"{format}: {(peak - start) / 1024 / 1024} MB");
            Assert.True(sink.Length > 10_000_000);
        }
    }
}
