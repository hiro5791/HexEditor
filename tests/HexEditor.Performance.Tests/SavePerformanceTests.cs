using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>
/// 保存の性能テスト (ENG-20、ENG-23)。100 GiB のスパースファイルをテスト用フォルダに作り (共有のテストデータは書き換えない)、
/// アプリの保存と同じ順 (<see cref="SavePlanner"/> の計画・実行・完了) で保存する。
/// </summary>
[Trait("Category", "Performance")]
public sealed class SavePerformanceTests(ITestOutputHelper output) : IDisposable
{
    private const string TC = "TC";
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private Document OpenCopy(out string path)
    {
        path = _folder.Generate("TD-SPARSE-100G");
        return new Document(FileByteSource.Open(path), Options() with { TempDirectory = System.IO.Path.Combine(_folder.Path, "recovery") });
    }

    /// <summary>Ctrl+S: 計画を立てて実行し、完了を反映するまでの時間と、その間のこのプロセスの書き込み量。</summary>
    private (TimeSpan Time, long Written, SaveMethod Method) Save(Document doc)
    {
        var settings = new SaveSettings { JournalDirectory = System.IO.Path.Combine(_folder.Path, "journal") };
        (_, long writtenBefore) = IoCounters();
        SaveMethod method = SaveMethod.NoChanges;
        TimeSpan time = Time(() =>
        {
            SavePlan plan = SavePlanner.Plan(doc, null, settings);
            Assert.True(plan.CanExecute, $"{plan.Method} {plan.Issue}");
            SavePlanner.Complete(plan, SavePlanner.Execute(plan));
            method = plan.Method;
        });
        (_, long writtenAfter) = IoCounters();
        return (time, writtenAfter - writtenBefore, method);
    }

    [Fact]
    [Trait(TC, "TC-ENG-20-01")]
    public void SavingOneByteOfHundredGigabytesIsFast()
    {
        using Document doc = OpenCopy(out string path);

        // 計る前に 1 度保存しておく (保存の処理の初めての JIT と、新しく作ったファイルの初めての書き出しの分を計らない)。
        doc.Overwrite(1L << 20, [0xFE]);
        Save(doc);

        // 手順 1〜3
        doc.Overwrite(1L << 32, [0xFF]);
        (TimeSpan time, long written, SaveMethod method) = Save(doc);
        output.Report($"100 GiB の 1 バイトの保存 ({method}): {Ms(time)}、書き込み量 {written:N0} バイト");
        Assert.Equal(SaveMethod.InPlace, method);
        Assert.True(time <= TimeSpan.FromSeconds(1), Ms(time));
        Assert.Equal([0xFF], ReadFile(path, 1L << 32, 1));
        Assert.Equal(100 * GiB, new FileInfo(path).Length);
        Assert.False(doc.IsModified);
    }

    [Fact]
    [Trait(TC, "TC-ENG-23-01")]
    public void SavingTenChangesOfHundredGigabytesWritesLittle()
    {
        using Document doc = OpenCopy(out string path);
        long length = doc.Length;
        long[] offsets = [0, (1L << 31) - 2, 1L << 31, (1L << 32) - 1, 1L << 32, 10 * GiB, 30 * GiB, 50 * GiB, 70 * GiB, length - 4];

        // 手順 1: 10 か所に 4 バイトずつ上書きする (重なる箇所は後に書いた値になる)。
        var expected = new Dictionary<long, byte>();
        for (int i = 0; i < offsets.Length; i++)
        {
            byte[] value = [(byte)(0xA0 + i), (byte)(0xB0 + i), (byte)(0xC0 + i), (byte)(0xD0 + i)];
            doc.Overwrite(offsets[i], value);
            for (int k = 0; k < 4; k++)
            {
                expected[offsets[i] + k] = value[k];
            }
        }

        // 手順 2
        (TimeSpan time, long written, SaveMethod method) = Save(doc);
        output.Report($"100 GiB の 10 か所の保存 ({method}): {Ms(time)}、書き込み量 {written:N0} バイト");
        Assert.Equal(SaveMethod.InPlace, method);
        Assert.True(time <= TimeSpan.FromSeconds(1), Ms(time));
        Assert.True(written <= MiB, $"書き込み量 {written:N0} バイト");

        // 手順 3
        foreach ((long offset, byte value) in expected)
        {
            Assert.Equal(value, ReadFile(path, offset, 1)[0]);
        }

        Assert.Equal(100 * GiB, new FileInfo(path).Length);
    }
}
