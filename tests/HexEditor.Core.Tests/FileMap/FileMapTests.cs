using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HexEditor.Core.Editing.TransformView;
using HexEditor.Core.Engine;
using HexEditor.Core.FileMap;
using HexEditor.Core.Sources;
using HexEditor.Core.Statistics;
using HexEditor.Core.Templates;
using HexEditor.Core.Tests.I18n;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Statistics.StatisticsTestSupport;

namespace HexEditor.Core.Tests.FileMap;

/// <summary>ANA-33 ファイルマップの作成 (領域の判定) と ANA-34 のエクスポート。</summary>
public sealed class FileMapTests
{
    private const string TC = "TC";

    private static readonly Lazy<Dictionary<string, string>> English = new(() => Resw("en"));
    private static readonly Lazy<Dictionary<string, string>> Japanese = new(() => Resw("ja"));

    private static Dictionary<string, string> Resw(string language) => ResourceChecker.LoadResw(Path.Combine(
        Path.GetDirectoryName(Engine.SourceTests.FindRepoFile("src/HexEditor.App/HexEditor.App.csproj"))!, "Strings", language, "Resources.resw"));

    private static string En(string key) => English.Value[key];

    private static TemplateCatalog Templates() => TemplateCatalog.CreateDefault(null);

    private static FileMapResult Build(Document doc, FileMapCallbacks? callbacks = null, CancellationToken token = default) =>
        FileMapBuilder.Build(doc.Current, new FileMapRequest { Templates = Templates() }, callbacks: callbacks, cancellationToken: token);

    private static TestDataCatalog.MapPart Part(string name) => TestDataCatalog.MapMixParts.Single(p => p.Name == name);

    private static IEnumerable<int> Content(FileMapResult map, FileMapKind kind) => map.ContentLane.Where(i => map.Regions[i].Kind == kind);

    private static string Label(FileMapResult map, int i) => FileMapText.Format(map.LabelOf(i), En, CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en"));

    /// <summary>内容のレーンで、[start, end) を覆う種類の領域がある (境目はブロック 1 つ以内の差)。</summary>
    private static int AssertContent(FileMapResult map, FileMapKind kind, long start, long end)
    {
        const long Block = 4096;
        int found = Content(map, kind).FirstOrDefault(i => Math.Abs(map.Regions[i].Offset - start) <= Block && Math.Abs(map.Regions[i].End - end) <= Block, -1);
        Assert.True(found >= 0, $"内容のレーンに {kind} の 0x{start:X}〜0x{end:X} がありません: " + string.Join("; ",
            map.ContentLane.Select(i => $"{map.Regions[i].Kind} 0x{map.Regions[i].Offset:X}+0x{map.Regions[i].Length:X}")));
        return found;
    }

    private static void AssertMixRegions(FileMapResult map)
    {
        Assert.True(map.Complete);

        // 構造のレーン: 0x0 から始まる PE の構造 (header、nt、sections)、0x50000 の PNG、0x360000 の ZIP。
        string[] pe = [.. map.StructureLane.Where(i => map.Regions[i].Kind == FileMapKind.Structure && map.Regions[i].Parent < 0).Select(i => Label(map, i))];
        Assert.Contains("header", pe);
        Assert.Contains("nt", pe);
        Assert.Contains(pe, l => l.StartsWith("sections", StringComparison.Ordinal));
        Assert.Equal(0, map.Regions[map.StructureLane[0]].Offset);
        int png = Assert.Single(map.StructureLane, i => map.Regions[i].Kind == FileMapKind.Embedded && map.Regions[i].Offset == Part("png").Offset);
        Assert.Equal("PNG image", Label(map, png));
        int zip = Assert.Single(map.StructureLane, i => map.Regions[i].Kind == FileMapKind.Embedded && map.Regions[i].Offset == Part("zip").Offset);
        Assert.Contains("ZIP", Label(map, zip), StringComparison.Ordinal);

        // 内容のレーン: ゼロ (TD-PE-X64 の後半の 00 の埋め草から 0x10000 まで)、テキスト、zlib、乱数、64 バイトのレコード。
        int zero = Content(map, FileMapKind.Zero).First();
        Assert.InRange(map.Regions[zero].Offset, 0x100, Part("pe").End + 4096);
        Assert.InRange(map.Regions[zero].End, Part("text").Offset - 4096, Part("text").Offset + 4096);
        AssertContent(map, FileMapKind.Text, Part("text").Offset, Part("text").End);
        int zlib = Assert.Single(Content(map, FileMapKind.Compressed));
        Assert.Equal(Part("zlib").Offset, map.Regions[zlib].Offset);
        Assert.Equal(Part("zlib").Length, map.Regions[zlib].Length);
        Assert.Equal("zlib", Label(map, zlib));
        AssertContent(map, FileMapKind.Encrypted, Part("random").Offset, Part("random").End);
        int records = AssertContent(map, FileMapKind.Records, Part("records").Offset, Part("records").End);
        Assert.Contains("64", Label(map, records), StringComparison.Ordinal);

        // 内容のレーンは重ならず、対象の全体を覆う。
        long at = 0;
        foreach (int i in map.ContentLane)
        {
            Assert.Equal(at, map.Regions[i].Offset);
            at = map.Regions[i].End;
        }

        Assert.Equal(TestDataCatalog.MapMixLength, at);
    }

    [Fact]
    [Trait(TC, "TC-ANA-33-01")]
    public void MixedDataIsMappedByKindAndPosition()
    {
        using Document doc = Doc(TestDataCatalog.MapMix());
        AssertMixRegions(Build(doc));
    }

    [Fact]
    [Trait(TC, "TC-ANA-33-02")]
    public void EmbeddedPngLengthComesFromTheTemplate()
    {
        using Document doc = Doc(TestDataCatalog.MapMix());
        FileMapResult map = Build(doc);
        int png = map.StructureLane.Single(i => map.Regions[i].Kind == FileMapKind.Embedded && map.Regions[i].Offset == 0x50000);
        FileMapRegion r = map.Regions[png];
        Assert.Equal(TestDataCatalog.Png16().Length, r.Length);
        Assert.Equal(Confidence.High, r.Confidence);
        Assert.Equal(FileMapStage.Structure, r.Stage);
        FileMapDetail detail = map.Details[r.Detail];
        Assert.True(detail.TemplateParsed);
        Assert.Equal("png", detail.Template!.Name);
        Assert.Equal(TemplateOrigin.Standard, detail.Template.Origin);
    }

    [Fact]
    [Trait(TC, "TC-ANA-33-05")]
    public void CancellingDuringTheScanKeepsScannedAndSampledResults()
    {
        var source = new FaultyByteSource(new MemoryByteSource(TestDataCatalog.MapMix()));
        using var doc = new Document(source, DocumentAssert.Options(cacheCapacity: 0));
        using var cts = new CancellationTokenSource();
        var scanning = new ManualResetEventSlim();
        var callbacks = new FileMapCallbacks
        {
            StageStarted = stage =>
            {
                if (stage == FileMapStage.Scan)
                {
                    // 概観が終わったら、0x280000 より後ろの読み込みを止める (走査が 0x100000 を過ぎ、0x200000 からの読み込みで待たせる)。
                    source.HoldFromOffset = 0x280000;
                    source.Hold.Reset();
                }
            },
            Partial = map =>
            {
                if (map.Stages.Scan == FileMapStageState.Running && map.ScannedEnd > 0x100000)
                {
                    scanning.Set();
                }
            },
        };
        FileMapResult? result = null;
        long stopped = 0;
        var thread = new Thread(() =>
        {
            result = FileMapBuilder.Build(doc.Current, new FileMapRequest { Templates = Templates(), PublishInterval = TimeSpan.Zero }, callbacks: callbacks,
                cancellationToken: cts.Token);
            stopped = Stopwatch.GetTimestamp();
        });
        thread.Start();
        try
        {
            Assert.True(scanning.Wait(TimeSpan.FromSeconds(60)), "走査が 0x100000 を過ぎません。");
            long requested = Stopwatch.GetTimestamp();
            cts.Cancel();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "キャンセルしても止まりません。");
            TimeSpan took = Stopwatch.GetElapsedTime(requested, stopped);
            TimeBudget.Limit(took <= TimeSpan.FromMilliseconds(100), $"キャンセルから止まるまで {took.TotalMilliseconds:F0} ms");
        }
        finally
        {
            source.Hold.Set();
            thread.Join();
        }

        FileMapResult map = result!;
        Assert.Equal(FileMapStageState.Cancelled, map.Stages.Scan);
        Assert.Equal(FileMapStageState.NotStarted, map.Stages.Structure);
        Assert.False(map.Complete);
        Assert.InRange(map.ScannedEnd, 0x100001, 0x200000);
        foreach (int i in map.ContentLane)
        {
            FileMapRegion r = map.Regions[i];
            if (r.End <= map.ScannedEnd)
            {
                Assert.Equal(FileMapStage.Scan, r.Stage);
            }
            else
            {
                Assert.Equal(FileMapStage.Sample, r.Stage);
                Assert.True(r.Confidence <= Confidence.Medium);
                Assert.Contains(map.EvidenceOf(i), m => m.Key == "FileMap_Ev_Sample");
            }
        }

        // 構造の解析の段階の領域 (埋め込まれたファイルなど) はない。
        Assert.Empty(map.StructureLane);
        Assert.DoesNotContain(map.Regions, r => r.Stage == FileMapStage.Structure);
    }

    [Fact]
    [Trait(TC, "TC-ANA-33-06")]
    public void TransformViewDataGivesTheSameMap()
    {
        using Document plain = Doc(TestDataCatalog.MapMix());
        FileMapResult a = Build(plain);
        byte[] xored = [.. TestDataCatalog.MapMix().Select(b => (byte)(b ^ 0x5A))];
        using Document doc = Doc(xored);
        doc.SetViewTransform(TransformDefinition.Xor([0x5A]).Create());
        FileMapResult b = Build(doc);
        Assert.Equal(a.Regions.Count, b.Regions.Count);
        for (int i = 0; i < a.Regions.Count; i++)
        {
            Assert.Equal((a.Regions[i].Offset, a.Regions[i].Length, a.Regions[i].Kind), (b.Regions[i].Offset, b.Regions[i].Length, b.Regions[i].Kind));
            Assert.Equal(Label(a, i), Label(b, i));
        }

        AssertMixRegions(b);
    }

    [Fact]
    [Trait(TC, "TC-ANA-33-07")]
    public void StatisticsReuseTheMapClassificationWithoutReading()
    {
        var source = Virtual(TestDataCatalog.MapMixLength, (o, s) => TestDataCatalog.MapMix().AsSpan((int)o, s.Length).CopyTo(s));
        using Document doc = Doc(source);
        FileMapResult map = Build(doc);
        ClassificationResult shared = Assert.IsType<ClassificationResult>(map.Classification);

        // ファイルマップを作ったウィンドウが記録する (App の処理と同じ)。
        DataClassifier.Remember(doc, shared);
        source.Reads.Clear();

        // 統計パネルの「分類」タブ (対象はドキュメント全体、ブロック 4 KB) とミニマップの分類の印は、記録した結果を使う。
        var request = new ClassifyRequest { Ranges = [new(0, doc.Length)], BlockSize = 4096 };
        ClassificationResult? reused = DataClassifier.Reusable(doc, doc.Current, request);
        Assert.Same(shared, reused);
        Assert.Same(shared, DataClassifier.LatestFor(doc));
        Assert.Equal(DataClass.Encrypted, reused!.ClassOf(0x200000, 0x1000));
        Assert.Empty(source.Reads);

        // 分類の区間がファイルマップの内容のレーンの ANA-16 の分類と一致する。
        foreach (ClassRegion region in reused.Regions)
        {
            int at = map.ContentAt(region.Offset + (region.Length / 2));
            FileMapKind kind = map.Regions[at].Kind;
            Assert.True(kind == KindOf(region.Class) || kind is FileMapKind.Records or FileMapKind.Compressed or FileMapKind.Zero,
                $"0x{region.Offset:X}: {region.Class} と {kind}");
        }

        // 条件が違う (ブロックの大きさ・データ) と使わない。
        Assert.Null(DataClassifier.Reusable(doc, doc.Current, request with { BlockSize = 8192 }));
    }

    private static FileMapKind KindOf(DataClass c) => c switch
    {
        DataClass.Zero => FileMapKind.Zero,
        DataClass.Constant => FileMapKind.Constant,
        DataClass.Text => FileMapKind.Text,
        DataClass.Encrypted => FileMapKind.Encrypted,
        DataClass.Compressed => FileMapKind.Compressed,
        DataClass.Unreadable => FileMapKind.Unreadable,
        _ => FileMapKind.Binary,
    };

    [Fact]
    [Trait(TC, "TC-ANA-33-09")]
    public void EveryRegionHasConfidenceAndNumericEvidence()
    {
        using Document doc = Doc(TestDataCatalog.MapMix());
        FileMapResult map = Build(doc);
        var en = CultureInfo.GetCultureInfo("en-US");
        var ja = CultureInfo.GetCultureInfo("ja-JP");
        for (int i = 0; i < map.Regions.Count; i++)
        {
            Assert.True(Enum.IsDefined(map.Regions[i].Confidence));
            IReadOnlyList<FileMapMessage> evidence = map.EvidenceOf(i);
            Assert.NotEmpty(evidence);
            Assert.All(evidence, m => Assert.True(m.Key.Length > 0 && English.Value.ContainsKey(m.Key) && Japanese.Value.ContainsKey(m.Key), m.Key));
            string english = FileMapText.Join(evidence, k => English.Value[k], en, en);
            string japanese = FileMapText.Join(evidence, k => Japanese.Value[k], ja, ja);
            Assert.Matches("[0-9]", english);
            Assert.Matches("[0-9]", japanese);
            Assert.NotEqual(english, japanese);
            Assert.DoesNotMatch("[\u3040-\u30FF\u4E00-\u9FFF]", english);
            Assert.Matches("[\u3040-\u30FF\u4E00-\u9FFF]", japanese);
        }

        int random = Content(map, FileMapKind.Encrypted).Single();
        Assert.Contains(map.EvidenceOf(random), m => m.Key == "FileMap_Ev_EntropyP");
        string text = FileMapText.Join(map.EvidenceOf(random), k => English.Value[k], en, en);
        Assert.Contains("Entropy", text, StringComparison.Ordinal);
        Assert.Contains("p-value", text, StringComparison.Ordinal);
        int records = Content(map, FileMapKind.Records).Single();
        string rec = FileMapText.Join(map.EvidenceOf(records), k => Japanese.Value[k], ja, ja);
        Assert.Contains("ずらし 64 バイト", rec, StringComparison.Ordinal);
        Assert.Contains("%", rec, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TC, "TC-ANA-33-10")]
    public void UnreadableRangeIsMarkedAndTheMapIsCompleted()
    {
        var source = new FaultyByteSource(new MemoryByteSource(TestDataCatalog.MapMix()));
        source.AddReadError(0x170000, 0x10000);
        using var doc = new Document(source, DocumentAssert.Options(cacheCapacity: 0));
        FileMapResult map = Build(doc);
        Assert.True(map.Complete);
        int bad = Assert.Single(Content(map, FileMapKind.Unreadable));
        Assert.Equal(0x170000, map.Regions[bad].Offset);
        Assert.Equal(0x10000, map.Regions[bad].Length);
        AssertContent(map, FileMapKind.Records, Part("records").Offset, Part("records").End);
        Assert.Single(map.StructureLane, i => map.Regions[i].Kind == FileMapKind.Embedded && map.Regions[i].Offset == Part("zip").Offset);
    }

    [Fact]
    public void TheBlockSizeGrowsForTargetsOverSixtyFourGigabytes()
    {
        Assert.Equal(4096, FileMapBuilder.BlockSizeFor(64L << 30));
        Assert.Equal(8192, FileMapBuilder.BlockSizeFor(100L << 30));
        Assert.Equal(4096, FileMapBuilder.SampleIntervalFor(TestDataCatalog.MapMixLength));
        Assert.Equal(32L << 20, FileMapBuilder.SampleIntervalFor(100L << 30));
    }

    [Fact]
    public void TheOverviewReadsAtMostSixteenMegabytesAndCapsConfidence()
    {
        var source = Virtual(100L << 30, (_, s) => s.Clear());
        using Document doc = Doc(source);
        FileMapResult? overview = null;
        using var cts = new CancellationTokenSource();
        var callbacks = new FileMapCallbacks
        {
            Partial = m =>
            {
                if (m.Stages.Sample == FileMapStageState.Done && overview is null)
                {
                    overview = m;
                    cts.Cancel();
                }
            },
        };
        FileMapBuilder.Build(doc.Current, new FileMapRequest(), callbacks: callbacks, cancellationToken: cts.Token);
        Assert.NotNull(overview);
        long read = source.Reads.Sum(r => (long)r.Length);
        Assert.True(read <= (16L << 20) + (128L << 10) + (2L << 20), $"{read} バイト");
        Assert.All(overview!.Regions, r => Assert.Equal(FileMapStage.Sample, r.Stage));
        Assert.All(overview.Regions, r => Assert.True(r.Confidence <= Confidence.Medium));
        Assert.Equal(FileMapKind.Zero, Assert.Single(overview.Regions).Kind);
    }

    [Fact]
    public void CompressedStreamsWithWrongChecksumsAreNotPainted()
    {
        // zlib のストリームの Adler-32 を壊すと、範囲の確定 (圧縮の領域) に使わない。
        byte[] data = [.. TestDataCatalog.MapMix()];
        TestDataCatalog.MapPart zlib = Part("zlib");
        data[zlib.End - 1] ^= 0xFF;
        using Document doc = Doc(data);
        FileMapResult map = Build(doc);
        Assert.DoesNotContain(Content(map, FileMapKind.Compressed), i => map.Regions[i].Offset == zlib.Offset && map.Regions[i].Length == zlib.Length);
    }

    [Fact]
    public void EmbeddedFilesAreFoundAtEveryPosition()
    {
        using Document doc = Doc(TestDataCatalog.MapPng40());
        FileMapResult map = Build(doc);
        int[] pngs = [.. map.StructureLane.Where(i => map.Regions[i].Kind == FileMapKind.Embedded)];

        // 先頭 (0x0) の PNG は先頭の形式 (形式の構造) で、残りの 39 個が埋め込まれたファイル。
        Assert.Equal(39, pngs.Length);
        Assert.All(pngs, i => Assert.Equal(0, map.Regions[i].Offset % 0x1000));
        Assert.Equal(39, map.EmbeddedWithTemplate.Count());
        Assert.Contains(map.StructureLane, i => map.Regions[i].Kind == FileMapKind.Structure && map.Regions[i].Offset == 0);
    }

    [Fact]
    public void ConstantRunsAreTrackedAcrossReads()
    {
        var runs = new RunTracker();
        byte[] a = new byte[4096];
        a.AsSpan(4096 - 300).Fill(0xFF);
        byte[] b = new byte[4096];
        b.AsSpan(0, 300).Fill(0xFF);
        Random.Shared.NextBytes(b.AsSpan(300, 100));
        b[300] = 0x11;
        runs.Feed(0, a);
        runs.Feed(4096, b);
        runs.Finish();
        Assert.Contains(runs.Runs, r => r.Value == 0xFF && r.Start == 4096 - 300 && r.Length == 600);
        Assert.Contains(runs.Runs, r => r.Value == 0 && r.Start == 0 && r.Length == 4096 - 300);
    }

    [Fact]
    [Trait(TC, "TC-ANA-34-08")]
    public void TheMapIsExportedAsJsonCsvAndText()
    {
        using Document doc = Doc(TestDataCatalog.MapMix());
        FileMapResult map = Build(doc);
        var info = new FileMapDocumentInfo("mix.bin", @"C:\data\mix.bin", doc.Length, null);
        var en = CultureInfo.GetCultureInfo("en-US");
        byte[] Export(FileMapExportFormat format)
        {
            using var stream = new MemoryStream();
            FileMapExport.Write(stream, format, map, info, En, en, en);
            return stream.ToArray();
        }

        // JSON の regions が一覧 (領域の並び) と一致し、入れ子の領域の parent が親の番号を指す。
        using JsonDocument json = JsonDocument.Parse(Export(FileMapExportFormat.Json));
        JsonElement root = json.RootElement;
        Assert.True(root.GetProperty("complete").GetBoolean());
        JsonElement[] regions = [.. root.GetProperty("regions").EnumerateArray()];
        Assert.Equal(map.Regions.Count, regions.Length);
        for (int i = 0; i < regions.Length; i++)
        {
            FileMapRegion r = map.Regions[i];
            JsonElement e = regions[i];
            Assert.Equal(r.Offset, e.GetProperty("offset").GetInt64());
            Assert.Equal(r.Length, e.GetProperty("length").GetInt64());
            Assert.Equal(FileMapWords.Kind(r.Kind), e.GetProperty("kind").GetString());
            Assert.Equal(FileMapWords.Lane(r.Lane), e.GetProperty("lane").GetString());
            Assert.Equal(FileMapWords.Confidence(r.Confidence), e.GetProperty("confidence").GetString());
            Assert.Equal(FileMapWords.Stage(r.Stage), e.GetProperty("stage").GetString());
            if (r.Parent < 0)
            {
                Assert.Equal(JsonValueKind.Null, e.GetProperty("parent").ValueKind);
            }
            else
            {
                int parent = e.GetProperty("parent").GetInt32();
                Assert.Equal(r.Parent, parent);
                Assert.True(regions[parent].GetProperty("offset").GetInt64() <= r.Offset);
            }
        }

        Assert.Contains(regions, e => e.GetProperty("parent").ValueKind == JsonValueKind.Number);
        long contentBytes = Enum.GetValues<FileMapKind>().Where(k => k is not (FileMapKind.Structure or FileMapKind.Embedded))
            .Sum(k => root.GetProperty("legend").GetProperty(FileMapWords.Kind(k)).GetInt64());
        Assert.Equal(doc.Length, contentBytes);
        Assert.Equal("mix.bin", root.GetProperty("document").GetProperty("name").GetString());

        // CSV は BOM 付きの UTF-8・CRLF で、1 行目が列の名前。
        byte[] csv = Export(FileMapExportFormat.Csv);
        Assert.Equal([0xEF, 0xBB, 0xBF], csv[..3]);
        string csvText = Encoding.UTF8.GetString(csv, 3, csv.Length - 3);
        Assert.StartsWith("offset,length,kind,lane,label,confidence,stage,template,evidence\r\n", csvText, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", csvText.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(map.Regions.Count + 1, csvText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);

        // テキストのレポートの先頭に推定である旨 (BOM なし)。
        byte[] text = Export(FileMapExportFormat.Text);
        Assert.NotEqual(0xEF, text[0]);
        Assert.StartsWith(En("FileMap_EstimateNote"), Encoding.UTF8.GetString(text), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedExportRemovesThePartialFile()
    {
        using Document doc = Doc(TestDataCatalog.MapPng40());
        FileMapResult map = Build(doc);
        string path = Path.Combine(Path.GetTempPath(), "HexEditorTests", $"filemap-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => FileMapExport.ToFile(path, FileMapExportFormat.Json, map,
            new FileMapDocumentInfo("a", null, doc.Length, null), En, CultureInfo.InvariantCulture, CultureInfo.InvariantCulture, cts.Token));
        Assert.False(System.IO.File.Exists(path));
    }
}
