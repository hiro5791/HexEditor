using System.Diagnostics;
using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Scripting;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>性能テスト用の環境 (HEXEDITOR_PERF_MACHINE=1) でだけ実行する。それ以外では理由を付けてスキップする。</summary>
public sealed class PerfMachineFactAttribute : FactAttribute
{
    public PerfMachineFactAttribute()
    {
        if (!Support.TimeBudget.IsPerfMachine)
        {
            Skip = "性能テスト用の固定の環境 (テスト方針 6.6) でだけ実行する (HEXEDITOR_PERF_MACHINE=1 で実行する)。";
        }
    }
}

/// <summary>一括処理 API の性能 (AUTO-09 の受け入れ基準 1・2)。10 GB は仮想のデータソースで作る。</summary>
[Collection(SerialCollection.Name)]
public sealed class ScriptPerformanceTests
{
    private const long TenGigabytes = 10L << 30;

    [PerfMachineFact]
    [Trait("TC", "TC-AUTO-09-01")]
    [Trait("Category", "Performance")]
    [Trait("Category", "Nightly")]
    public async Task Transform_of_10_gb_is_within_10_percent_of_the_data_operation()
    {
        using var t = new ScriptTestHost(new ScriptLimits { Time = null });
        CoreDocumentHost doc = t.Open(new VirtualByteSource(TenGigabytes, VirtualContent.Random, seed: 9), "random10g.bin");
        var direct = new List<TimeSpan>();
        var script = new List<TimeSpan>();
        for (int i = 0; i < 3; i++)
        {
            // メニューのデータ演算と同じ処理 (DataOperationRunner) を直接呼ぶ。
            var watch = Stopwatch.StartNew();
            var spec = new DataOperationSpec { Kind = DataOperationKind.Xor, Operand = 0xFF };
            DataOperationResult result = await Task.Run(() => DataOperationRunner.For(doc.Document).Run(doc.Document.Current, [new TargetRange(0, TenGigabytes)], spec));
            t.OnUi(() => TransformApplier.Apply(doc.Document, result.Replacements, "データ演算"));
            direct.Add(watch.Elapsed);
            t.OnUi(() => doc.Document.Undo());

            watch.Restart();
            ScriptTestHost.AssertSucceeded(await t.RunCodeAsync("await doc.transform('all', 'xor', 'FF');", "xorall.js"));
            script.Add(watch.Elapsed);
            t.OnUi(() => doc.Document.Undo());
        }

        direct.Sort();
        script.Sort();
        Support.TimeBudget.Limit(script[1].Ticks <= direct[1].Ticks * 1.1,
            $"スクリプトの中央値 {script[1].TotalSeconds:0.0} 秒、データ演算の中央値 {direct[1].TotalSeconds:0.0} 秒");
    }

    [PerfMachineFact]
    [Trait("TC", "TC-AUTO-09-02")]
    [Trait("Category", "Performance")]
    [Trait("Category", "Nightly")]
    public async Task FindAll_over_10_gb_stays_under_1_gb_of_private_bytes()
    {
        using var t = new ScriptTestHost(new ScriptLimits { Time = null });
        t.Open(new VirtualByteSource(TenGigabytes, VirtualContent.Random, seed: 11), "random10g.bin");
        using var process = Process.GetCurrentProcess();
        long max = 0;
        using var stop = new CancellationTokenSource();
        Task sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                max = Math.Max(max, process.PrivateMemorySize64);
                await Task.Delay(100, CancellationToken.None);
            }
        });

        ScriptRunResult r = await t.RunCodeAsync("const r = await doc.findAll('DE AD BE EF 01 23 45 67 89 AB CD EF FE ED FA CE'); console.log('count=' + r.length);", "findall.js");
        stop.Cancel();
        await sampler;
        ScriptTestHost.AssertSucceeded(r);
        Assert.Contains("count=0", t.OutputText);
        Assert.True(max <= 1L << 30, $"プライベートバイト数の最大 {max / (1024 * 1024)} MB");
    }
}
