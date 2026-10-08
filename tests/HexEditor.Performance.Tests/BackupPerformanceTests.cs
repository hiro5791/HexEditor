using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>バックアップの作成の性能 (ENG-26 の受け入れ基準 3)。安全な保存では名前の変更だけで終わり、ファイルサイズに依存しない。</summary>
[Trait("Category", "Performance")]
public sealed class BackupPerformanceTests(ITestOutputHelper output) : IDisposable
{
    private const string TC = "TC";
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>1 バイト挿入して (安全な保存)、バックアップを作って保存する。バックアップの作成 (名前の変更) にかかった時間を返す。</summary>
    private TimeSpan SaveWithBackup(string id)
    {
        string path = _folder.Generate(id);
        using var doc = new Document(FileByteSource.Open(path), Options() with { TempDirectory = System.IO.Path.Combine(_folder.Path, "recovery") });
        doc.Insert(0, [0xAB]);
        SavePlan plan = SavePlanner.Plan(doc, null, new SaveSettings
        {
            JournalDirectory = System.IO.Path.Combine(_folder.Path, "journal"),
            Backup = new BackupSettings(),
        });
        Assert.Equal(SaveMethod.Safe, plan.Method);
        Assert.True(plan.CanExecute, $"{plan.Method} {plan.Issue}");
        SaveResult result = SavePlanner.Execute(plan);
        SavePlanner.Complete(plan, result);
        Assert.True(File.Exists(path + ".bak"));
        return result.BackupTime ?? throw new InvalidOperationException("バックアップを作っていません。");
    }

    [Fact]
    [Trait(TC, "TC-ENG-26-02")]
    public void BackupOfSafeSaveDoesNotDependOnFileSize()
    {
        // 1. TD-SEQ-1M、2. TD-ENG-SPARSE-10G。
        TimeSpan small = SaveWithBackup("TD-SEQ-1M");
        TimeSpan large = SaveWithBackup("TD-ENG-SPARSE-10G");
        output.Report($"バックアップの作成: 1 MiB {Ms(small)}、10 GiB {Ms(large)}");
        Assert.True(small <= TimeSpan.FromSeconds(1), Ms(small));
        Assert.True(large <= TimeSpan.FromSeconds(1), Ms(large));
        Assert.True((large - small).Duration() < TimeSpan.FromMilliseconds(500), $"{Ms(small)} / {Ms(large)}");
    }
}
