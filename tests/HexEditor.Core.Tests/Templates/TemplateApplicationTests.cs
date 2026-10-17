using System.Diagnostics;
using System.Text;
using HexEditor.Core.Templates;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>適用の実行 (TPL-20)、追従 (TPL-26)。</summary>
public sealed class TemplateApplicationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "tpl-app-" + Guid.NewGuid().ToString("N"));

    public TemplateApplicationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] Bytes256 => Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    private static TemplateResult Wait(TemplateApplication app, Func<TemplateResult, bool> done, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (app.Result is { } r && r.Completion.IsCompleted && done(r))
            {
                return r;
            }

            Thread.Sleep(20);
        }

        throw new Xunit.Sdk.XunitException("結果が変わりません。");
    }

    [Theory]
    [Trait(TC, "TC-TPL-20-05")]
    [InlineData(true)]
    [InlineData(false)]
    public void SavingTheTemplateFileReevaluates(bool replace)
    {
        string path = Path.Combine(_dir, "ext.hxt");
        File.WriteAllText(path, "u8 a;");
        byte[] data = Bytes256;
        using var app = new TemplateApplication(new TemplateApplicationInfo { TemplatePath = path }, () => TemplateDataSource.FromBytes(data));
        app.Start();
        Wait(app, r => r.FindByPath("a") is { Size: 1 }, TimeSpan.FromSeconds(10));
        if (replace)
        {
            string temp = Path.Combine(_dir, "ext.hxt.tmp");
            File.WriteAllText(temp, "u16 a; u8 b;", new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        else
        {
            using var stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite);
            stream.Write("u16 a; u8 b;"u8);
        }

        TemplateResult after = Wait(app, r => r.FindByPath("b") is not null, TimeSpan.FromSeconds(2) + TimeSpan.FromSeconds(8));
        Assert.Equal(0x0100L, Long(after, "a"));
        Assert.Equal(2L, Long(after, "b"));
    }

    [Fact]
    public void EditsAreReevaluatedFiveHundredMillisecondsAfterTheLastOne()
    {
        string path = Path.Combine(_dir, "edit.hxt");
        File.WriteAllText(path, "u8 a;");
        var time = new FakeTimerProvider();
        byte[] data = [1];
        using var app = new TemplateApplication(new TemplateApplicationInfo { TemplatePath = path }, () => TemplateDataSource.FromBytes(data),
            timeProvider: time, watchFiles: false);
        app.Start();
        Assert.Equal(1, app.EvaluationCount);
        app.DocumentChanged();
        time.Advance(TimeSpan.FromMilliseconds(300));
        app.DocumentChanged();
        time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(1, app.EvaluationCount);
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, app.EvaluationCount);

        app.AutoReevaluate = false;
        app.DocumentChanged();
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, app.EvaluationCount);
    }

    [Fact]
    public void FollowEvaluatesTheTypeAtTheCursorAndConvertsToFixed()
    {
        string path = Path.Combine(_dir, "follow.hxt");
        File.WriteAllText(path, "struct Pair { u8 a; u8 b; }; Pair p;");
        var time = new FakeTimerProvider();
        byte[] data = Bytes256;
        using var app = new TemplateApplication(
            new TemplateApplicationInfo { TemplatePath = path, Kind = TemplateApplyKind.Follow, TypeName = "Pair", StartOffset = 0x10 },
            () => TemplateDataSource.FromBytes(data), timeProvider: time, watchFiles: false);
        app.Start();
        TemplateResult r = Wait(app, _ => true, TimeSpan.FromSeconds(10));
        Assert.Equal(0x10L, Long(r, "Pair.a"));
        Assert.Equal(TemplateApplication.FollowLimits.TimeSeconds, r.Limits.TimeSeconds);
        Assert.Equal(100_000L, r.Limits.Patterns);

        app.MoveCursor(0x40);
        Assert.Equal(1, app.EvaluationCount);
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, app.EvaluationCount);
        TemplateResult r2 = Wait(app, x => x.FindByPath("Pair.a") is { Offset: 0x40 }, TimeSpan.FromSeconds(10));
        Assert.Equal(0x41L, Long(r2, "Pair.b"));

        TemplateApplicationInfo fixedInfo = app.ToFixed();
        Assert.Equal(TemplateApplyKind.Fixed, fixedInfo.Kind);
        Assert.Equal(0x40L, fixedInfo.StartOffset);

        // 固定の適用は、その型をその位置で評価し、カーソルを動かしても変わらない
        using var fixedApp = new TemplateApplication(fixedInfo, () => TemplateDataSource.FromBytes(data), timeProvider: time, watchFiles: false);
        fixedApp.Start();
        TemplateResult f = Wait(fixedApp, _ => true, TimeSpan.FromSeconds(10));
        Assert.Equal(0x40L, Long(f, "Pair.a"));
        Assert.Null(f.FindByPath("p"));
    }

    [Fact]
    public void HeavyFollowTypeStopsAtTheTimeLimit()
    {
        // TD-TPL-HEAVY の 64 回の検索はこの評価器では 1 秒より短く終わることがあるため、検索の回数を増やした型を使う
        string path = Path.Combine(_dir, "heavy.hxt");
        File.WriteAllText(path, File.ReadAllText(Path.Combine(TemplateFeatureTests.DataFolder, "heavy.hxt")).Replace("i < 64", "i < 64000", StringComparison.Ordinal));
        byte[] data = new byte[16 * 1024 * 1024];
        TestDataCatalog.Random(TestDataCatalog.RandomSeed, 0, data);
        using var app = new TemplateApplication(new TemplateApplicationInfo { TemplatePath = path, Kind = TemplateApplyKind.Follow, TypeName = "Heavy" },
            () => TemplateDataSource.FromBytes(data), watchFiles: false);
        app.Start();
        TemplateResult r = Wait(app, _ => true, TimeSpan.FromSeconds(10));
        Assert.Equal(TemplateRunState.LimitReached, r.State);
        Assert.Equal(TemplateLimitKind.Time, r.LimitReached);
        Assert.NotNull(r.FindByPath("Heavy"));
    }

    [Fact]
    public void ApplicationSetKeepsAtMostThirtyTwoAndRoundTrips()
    {
        var set = new TemplateApplicationSet();
        for (int i = 0; i < 32; i++)
        {
            Assert.True(set.TryAdd(new TemplateApplicationInfo { TemplatePath = $"t{i}.hxt", StartOffset = i * 0x1000, BigEndian = i % 2 == 0 }));
        }

        Assert.False(set.TryAdd(new TemplateApplicationInfo { TemplatePath = "x.hxt" }));
        TemplateApplicationSet back = TemplateApplicationSet.FromJson(set.ToJson());
        Assert.Equal(set.Items, back.Items);
        Assert.Empty(TemplateApplicationSet.FromJson("{broken").Items);
    }
}
