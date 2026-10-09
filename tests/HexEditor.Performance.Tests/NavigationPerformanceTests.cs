using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>
/// 開く・ジャンプ・スクロールとメモリ使用量の性能テスト (ENG-06、ENG-08、ENG-11)。Hex ビューと同じ経路で表示用に読み
/// (<see cref="DocumentSnapshot.ReadForDisplay"/>)、「読み込み中」のバイトがなくなるまでを表示の完了とする。
/// </summary>
[Trait("Category", "Performance")]
public sealed class NavigationPerformanceTests(ITestOutputHelper output)
{
    private const string TC = "TC";

    [Fact]
    [Trait(TC, "TC-ENG-06-01")]
    public void JumpingInHundredGigabytesShowsDataWithinHundredMilliseconds()
    {
        string path = TestDataCatalog.Get("TD-SPARSE-100G");
        long length = new FileInfo(path).Length;
        long[] targets = [(1L << 31) - 0x10, 1L << 32, 50 * GiB, length - 0x100];

        // 前提: ジャンプ先の範囲を一度読んで OS のキャッシュに載せる。アプリのブロックキャッシュは新しく開いたドキュメントなので空。
        foreach (long target in targets)
        {
            long from = Math.Max(0, target - 64 * 1024);
            ReadFile(path, from, (int)Math.Min(MiB, length - from));
        }

        using Document doc = Open("TD-SPARSE-100G");
        EditorState editor = Editor(doc);
        var times = new List<string>();
        foreach (long target in targets)
        {
            byte[] shown = [];
            TimeSpan time = Time(() =>
            {
                editor.GoTo(target);
                shown = ReadVisible(editor);
            });
            times.Add($"0x{target:X}: {Ms(time)}");

            // 表示されている内容が各位置の目印 (または 00) と一致し、読み込み中のバイトが残っていない (ReadVisible が確かめる)。
            long top = editor.TopRow * editor.BytesPerRow;
            Assert.Equal(ReadFile(path, top, shown.Length), shown);
            Assert.True(editor.Cursor >= top && editor.Cursor < top + shown.Length);
            TimeLimit(time <= TimeSpan.FromMilliseconds(100), $"0x{target:X}: {Ms(time)}");
        }

        output.Report("ジャンプから表示まで: " + string.Join("、", times));
    }

    [Fact]
    [Trait(TC, "TC-ENG-08-01")]
    public void ScrollingThroughHundredGigabytesKeepsEngineMemoryBounded()
    {
        using Document doc = Open("TD-SPARSE-100G");
        var memory = new EngineMemory();
        memory.Register(doc);
        using var monitor = new MemoryMonitor(memory);
        long max = 0;
        void Sample() => max = Math.Max(max, memory.TotalUsage.TotalInMemory);
        using var sampler = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // 手順 1: スクロールバーの位置を 0% から 100% まで 0.1% 刻みで動かし、各位置で表示の完了を待つ。
        EditorState editor = Editor(doc);
        long maxTop = editor.Layout.MaxTopRow(editor.VisibleRows);
        for (int i = 0; i <= 1000; i++)
        {
            editor.ScrollToRow((long)(maxTop * (i / 1000.0)));
            ReadVisible(editor);
            Sample();
        }

        // 手順 2: Ctrl+End を押し、末尾から PageUp を 10,000 回押す。
        editor.MoveToEnd();
        ReadVisible(editor);
        for (int i = 0; i < 10_000; i++)
        {
            editor.PageUp();
            ReadVisible(editor);
            Sample();
        }

        output.Report($"エンジンのメモリ使用量の最大: {max / (double)MiB:F1} MiB (キャッシュ {memory.TotalUsage.Cache / (double)MiB:F1} MiB)");
        Assert.True(max <= 300 * MiB, $"最大 {max / MiB} MiB");
    }

    [Fact]
    [Trait(TC, "TC-ENG-08-02")]
    public async Task SearchingTenFilesOfTenGigabytesAtOnceStaysUnderTheLimit()
    {
        // 前提: TD-ENG-SPARSE-10G を 10 個の別名でテスト用フォルダに作り (スパース)、すべて開く。
        using var folder = new TempFolder();
        var memory = new EngineMemory();
        var docs = new List<Document>();
        try
        {
            for (int i = 0; i < 10; i++)
            {
                string path = TestDataCatalog.Generate("TD-ENG-SPARSE-10G", Path.Combine(folder.Path, $"copy{i}"));
                var doc = new Document(FileByteSource.Open(path), Options());
                memory.Register(doc);
                docs.Add(doc);
            }

            using var monitor = new MemoryMonitor(memory);
            long max = 0;
            void Sample() => max = Math.Max(max, memory.TotalUsage.TotalInMemory);
            using var sampler = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

            // 手順 1: 見つからない FE ED FA CE の全体の検索を同時に始める (開始時のスナップショットを読む)。
            SearchPattern pattern = SearchPattern.FromHex("FE ED FA CE");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Task<SearchResults>[] searches = [.. docs.Select(d => d.Current).Select(s => Task.Run(() => SearchEngine.FindAll(s, pattern)))];

            // 手順 2: 各ドキュメントのオフセット 0x100 に 1 MiB を挿入貼り付けする。
            byte[] paste = new byte[MiB];
            new Random(802).NextBytes(paste);
            foreach (Document doc in docs)
            {
                doc.Insert(0x100, paste, "貼り付け");
                Sample();
            }

            // 手順 3: 検索がすべて終わるまで記録する。
            SearchResults[] results = await Task.WhenAll(searches);
            Sample();
            output.Report($"10 × 10 GiB の同時検索: {watch.Elapsed.TotalSeconds:F1} 秒、エンジンのメモリ使用量の最大 {max / (double)MiB:F1} MiB");
            Assert.All(results, r =>
            {
                Assert.Equal(SearchResultsState.Completed, r.State);
                Assert.Equal(0, r.Count);
            });
            Assert.True(max <= EngineMemory.DefaultLimit, $"最大 {max / MiB} MiB");
            Assert.All(docs, d => Assert.Equal(10 * GiB + MiB, d.Length));
        }
        finally
        {
            foreach (Document doc in docs)
            {
                memory.Unregister(doc);
                doc.Dispose();
            }
        }
    }

    /// <summary>
    /// TC-ENG-11-01 のエンジンの部分: 開く要求から先頭行の表示の完了までの時間と、その間にファイルから読んだ量。
    /// OS のファイルキャッシュの消去 (管理者権限が要る) は CI の性能テスト用のランナーだけで行う。アプリの起動から開く部分 (コマンドラインの
    /// 引数) は UI の性能テスト (HexEditor.UITests の PerformanceTests) で計る。
    /// </summary>
    [Theory]
    [Trait(TC, "TC-ENG-11-01")]
    [InlineData("TD-SPARSE-100G")]
    [InlineData("TD-EMPTY")]
    [InlineData("TD-SEQ-1M")]
    public void OpeningIsFastAndReadsLittle(string id)
    {
        string path = TestDataCatalog.Get(id);
        (long readBefore, _) = IoCounters();
        Document? doc = null;
        TimeSpan time = Time(() =>
        {
            doc = new Document(FileByteSource.Open(path), Options());
            ReadVisible(Editor(doc));
        });
        (long readAfter, _) = IoCounters();
        doc!.Dispose();
        long read = readAfter - readBefore;
        output.Report($"{id}: 開く要求から先頭行の表示まで {Ms(time)}、読んだ量 {read:N0} バイト");
        TimeLimit(time <= TimeSpan.FromSeconds(1), Ms(time));
        Assert.True(read <= MiB, $"{read:N0} バイト");
    }
}
