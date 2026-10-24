using System.Globalization;
using System.IO.Compression;
using System.Text;
using HexEditor.Core.Compare;
using HexEditor.Core.Compare.Structure;
using HexEditor.Core.Engine;
using HexEditor.Core.FileMap;
using HexEditor.Core.Sources;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Inference;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Tests.Templates;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Security;

/// <summary>
/// フェーズ 6 の独自機能が信頼できない入力を読む部分のファジング (フェーズ 6 の監査): 構造の比較 (ANA-31。左右のテンプレートの評価と対応付け)、
/// ファイルマップ (ANA-33。シグネチャの探索・埋め込まれたファイルの長さ・DEFLATE の終わり・自己相関)、テンプレートの推定 (TPL-38〜40。
/// 任意のバイト列からの推定と下書き)。壊れた入力でも例外・無限ループ・際限のないメモリにならないこと。
/// </summary>
public sealed class Phase6FuzzTests
{
    private static readonly TemplateCatalog Catalog = TemplateCatalog.CreateDefault(null);

    private static readonly Lazy<TemplateProgram[]> StandardPrograms = new(() =>
        [.. new[] { "png", "pe", "zip" }.Select(n => Catalog.Load(Catalog.Find(n)!))]);

    // ---- 構造の比較 (ANA-31・ANA-32) ----

    private static StructCompareSide Side(byte[] data, string name) => new(TemplateDataSource.FromBytes(data), 0, 0, data.Length, name, null);

    private static void CompareAndExport(TemplateProgram program, byte[] a, byte[] b)
    {
        var options = new StructCompareOptions { MaxFields = StructCompareOptions.MinMaxFields };
        StructCompareResult r = StructComparer.Run(program, new StructTemplateChoice(program.Path, 0, 0, StructTemplateReason.UserChoice),
            Side(a, "a"), Side(b, "b"), options);
        using (var bytes = new CompareResult(CompareMethod.Simple, CompareRange.Whole(CompareData.FromBytes(a)), CompareRange.Whole(CompareData.FromBytes(b))))
        {
            DataComparer.Run(new HexEditor.Core.Compare.CompareOptions(), bytes);
            r.ComputeOutside(bytes.Diffs.Enumerate());
        }

        // 表示 (絞り込み・開閉・次 / 前) とエクスポートも、壊れた結果で落ちないこと。
        var view = new StructDiffView();
        view.Rebuild(r, new StructDiffFilter { IncludePadding = true, ShowSame = true, Text = "a" });
        view.Rebuild(r);
        for (int i = 0; i < Math.Min(view.RowCount, 200); i++)
        {
            _ = view.RowAt(i);
        }

        _ = view.Step(null, next: true);
        _ = view.Step(null, next: false);
        using var sink = new MemoryStream();
        StructDiffExport.WriteJson(r, r.Diffs(), sink);
        StructDiffExport.WriteCsv(r, r.Diffs(), sink, hexOffsets: true);
    }

    [Fact]
    public void Structure_compare_survives_mutated_png_pe_zip_pairs()
    {
        byte[][] seeds = [TestDataCatalog.StructPngA(), TestDataCatalog.StructPngB(), TestDataCatalog.StructPeA(), TestDataCatalog.ZipSample(), TestDataCatalog.Png16()];
        TemplateProgram[] programs = StandardPrograms.Value;
        var random = new Random(6301);
        Fuzz.Run(Fuzz.Cases(6301, seeds, mutations: 400, randoms: 40, maxRandomLength: 2048), data =>
        {
            byte[] other = Fuzz.Mutate(random, data);
            foreach (TemplateProgram p in programs)
            {
                CompareAndExport(p, data, other);
            }
        }, timeoutMs: 60_000);
    }

    [Fact]
    public void Structure_compare_survives_mutated_record_arrays()
    {
        TemplateProgram[] programs =
        [
            TemplateLoader.LoadText(TestDataCatalog.StructItemsHxt, "items.hxt"),
            TemplateLoader.LoadText(TestDataCatalog.StructItemsKeyHxt, "items-key.hxt"),
            TemplateLoader.LoadText(TestDataCatalog.StructMixedHxt, "mixed.hxt"),
            TemplateLoader.LoadText(TestDataCatalog.StructManyHxt, "many.hxt"),
        ];
        byte[][] seeds = [.. new[] { TestDataCatalog.StructArrA(), TestDataCatalog.StructArrB(), TestDataCatalog.StructMixedA() }.Select(s => s.AsSpan(0, Math.Min(s.Length, 4096)).ToArray())];
        var random = new Random(6302);
        Fuzz.Run(Fuzz.Cases(6302, seeds, mutations: 300, randoms: 30, maxRandomLength: 1024), data =>
        {
            byte[] other = Fuzz.Mutate(random, data);
            foreach (TemplateProgram p in programs)
            {
                CompareAndExport(p, data, other);
            }
        }, timeoutMs: 60_000);
    }

    [Fact]
    public void Structure_template_resolution_survives_random_heads()
    {
        Fuzz.Run(Fuzz.Cases(6303, [TestDataCatalog.StructPngA(), TestDataCatalog.StructPeA(), TestDataCatalog.ZipSample()], mutations: 300, randoms: 50), data =>
        {
            var side = new StructResolveSide(TemplateDataSource.FromBytes(data), "x.bin", 0, []);
            _ = StructTemplateResolver.Resolve(null, side, side, Catalog);
        });
    }

    // ---- ファイルマップ (ANA-33) ----

    private static byte[] Zlib(byte[] data, bool gzip)
    {
        using var output = new MemoryStream();
        using (Stream z = gzip ? new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true) : new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>ファイルマップの種: 小さな PE・PNG・zlib・gzip・ZIP・テキスト・レコード・ゼロを並べた 96〜160 KiB のデータ。</summary>
    private static byte[][] MapSeeds()
    {
        byte[] text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i} of some text\n")));
        byte[] records = new byte[32 * 1024];
        for (int i = 0; i < records.Length; i++)
        {
            records[i] = (byte)(i % 64 < 4 ? "REC1"[i % 64] : i % 64 == 8 ? i / 64 : (i * 7) % 13);
        }

        byte[] Mix(params byte[][] parts)
        {
            var data = new List<byte>();
            foreach (byte[] p in parts)
            {
                data.AddRange(p);
                data.AddRange(new byte[4096 - (data.Count % 4096)]);
            }

            return [.. data];
        }

        byte[] noise = new byte[24 * 1024];
        new Random(6304).NextBytes(noise);
        return
        [
            Mix(TestDataCatalog.PeX64(), text, TestDataCatalog.Png16(), Zlib(text, gzip: false), noise, records, TestDataCatalog.ZipSample()),
            Mix(new byte[8192], Zlib(text, gzip: true), TestDataCatalog.Png16(), TestDataCatalog.Png16(), records, TestDataCatalog.Zip3()),
            Mix(TestDataCatalog.Png16(), Zlib(records, gzip: false), Zlib(noise, gzip: true), text),
        ];
    }

    private static void BuildMapAndExport(byte[] data, int chunk)
    {
        using var doc = new Document(new MemoryByteSource(data), Options());
        FileMapResult map = FileMapBuilder.Build(doc.Current, new FileMapRequest { Templates = Catalog, ChunkSize = chunk, PublishInterval = TimeSpan.Zero, FileName = "x.bin" },
            callbacks: new FileMapCallbacks());
        Assert.True(map.Regions.Count <= FileMapBuilder.MaxRegions);
        foreach (int i in map.ContentLane)
        {
            Assert.InRange(map.Regions[i].Offset, 0, data.Length);
            Assert.InRange(map.Regions[i].End, map.Regions[i].Offset, data.Length);
        }

        foreach (FileMapExportFormat format in Enum.GetValues<FileMapExportFormat>())
        {
            using var sink = new MemoryStream();
            FileMapExport.Write(sink, format, map, new FileMapDocumentInfo("x.bin", null, data.Length, null), key => key, CultureInfo.InvariantCulture,
                CultureInfo.InvariantCulture);
        }
    }

    [Fact]
    public void File_map_survives_mutated_mixtures()
    {
        byte[][] seeds = MapSeeds();
        Fuzz.Run(Fuzz.Cases(6305, seeds, mutations: 120, randoms: 20, maxRandomLength: 64 * 1024), data => BuildMapAndExport(data, 64 * 1024),
            timeoutMs: 60_000, describe: d => $"{d.Length} bytes");
    }

    [Fact]
    public void File_map_survives_dense_false_signatures()
    {
        // 埋め込まれた形式のシグネチャ (ZIP・PNG・gzip・zlib・JPEG・PDF の先頭) だけを敷き詰めたデータ: 候補・カービングの長さの探索・展開の確認が
        // 際限なく増えないこと。
        byte[][] heads =
        [
            "PK\x03\x04"u8.ToArray(), [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], [0x1F, 0x8B, 0x08, 0x00], [0x78, 0xDA], [0x78, 0x9C],
            [0xFF, 0xD8, 0xFF, 0xE0], "%PDF-1."u8.ToArray(), "MZ"u8.ToArray(), "Rar!\x1A\x07"u8.ToArray(), "7z\xBC\xAF\x27\x1C"u8.ToArray(),
        ];
        var random = new Random(6306);
        var seeds = new List<byte[]>();
        for (int s = 0; s < 6; s++)
        {
            var data = new List<byte>();
            while (data.Count < 256 * 1024)
            {
                data.AddRange(heads[random.Next(heads.Length)]);
                if (s % 2 == 1)
                {
                    data.AddRange(Enumerable.Range(0, random.Next(0, 24)).Select(_ => (byte)random.Next(256)));
                }
            }

            seeds.Add([.. data]);
        }

        // 候補は最大 100,000 個で、候補ごとの判定 (テンプレートの自動適用の判定・カービングの検証) の一時的な確保があるため、上限は 2 GB にする
        // (持ち続ける量ではない。上限がないと 256 KiB で 4 GB を超えていた)。時間は 1 件 30 秒まで (修正の前は 1 件が数分かかった)。
        Fuzz.Run(Fuzz.Cases(6306, seeds, mutations: 6), data => BuildMapAndExport(data, 1024 * 1024), timeoutMs: 30_000,
            maxAllocatedBytes: 2L << 30, describe: d => $"{d.Length} bytes");
    }

    // ---- テンプレートの推定 (TPL-38〜TPL-40) ----

    private static readonly Func<InferenceMessage, string> English = InferenceTestSupport.Text();

    private static void InferAndDraft(byte[] data, int? recordLength, InferenceEndian endian)
    {
        using var doc = new Document(new MemoryByteSource(data), Options());
        TemplateInferenceResult r = TemplateInference.Run(doc.Current,
            new TemplateInferenceRequest { Start = 0, Length = data.Length, RecordLength = recordLength, Endian = endian });
        if (!r.HasFields)
        {
            return;
        }

        var table = new InferenceTable(r);
        for (int i = 0; i < table.Rows.Count; i++)
        {
            _ = English(table.Rows[i].Reason);
            if (table.Rows[i].NearMiss is { } near)
            {
                _ = English(near);
            }

            _ = table.TypeChoices(i);
        }

        // 結合・分割・取り消しを試し、下書きが文法どおりであること (TPL-40 の仕様 5: 構文エラーは内部エラー)。
        if (table.CanMergeWithPrevious(1))
        {
            table.MergeWithPrevious(1);
        }

        int last = table.Rows.Count - 1;
        if (table.CanSplit(last))
        {
            table.Split(last, 1);
        }

        table.Undo();
        var info = new TemplateDraftInfo("fuzz \"name\"\\.bin", r.Start, r.RecordLength!.Value, r.RecordCount, new DateTime(2026, 10, 25), table.BigEndian);
        string draft = TemplateDraftWriter.Write(table.Rows, info, English);
        TemplateProgram program = TemplateLoader.LoadText(draft, "inferred.hxt");
        Assert.False(program.HasErrors, draft + "\n" + string.Join("\n", program.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    [Fact]
    public void Template_inference_survives_mutated_records()
    {
        byte[][] seeds =
        [
            InferenceTestSupport.Rec48().AsSpan(0, 48 * 200).ToArray(), InferenceTestSupport.Rec48(big: true).AsSpan(0, 48 * 100).ToArray(),
            TestDataCatalog.InferTimes(), TestDataCatalog.InferNums(), TestDataCatalog.InferRandom().AsSpan(0, 8192).ToArray(),
        ];
        var random = new Random(6307);
        Fuzz.Run(Fuzz.Cases(6307, seeds, mutations: 250, randoms: 40, maxRandomLength: 8192), data =>
        {
            InferAndDraft(data, null, InferenceEndian.Auto);
            int k = random.Next(1, 200);
            if (data.Length >= Math.Max(32, k * 4))
            {
                InferAndDraft(data, k, (InferenceEndian)random.Next(3));
            }
        }, timeoutMs: 60_000, describe: d => $"{d.Length} bytes");
    }

    [Fact]
    public void Template_inference_survives_structured_extremes()
    {
        // 極端な値の並び: すべて同じ値、0 と FF の交互、1 ビットずつの値、連番、大きなレコード長。
        var cases = new List<(byte[] Data, int? K)>();
        cases.Add((new byte[4096], null));
        cases.Add(([.. Enumerable.Repeat((byte)0xFF, 4096)], 16));
        cases.Add(([.. Enumerable.Range(0, 8192).Select(i => (byte)(i % 2 == 0 ? 0 : 0xFF))], null));
        cases.Add(([.. Enumerable.Range(0, 8192).Select(i => (byte)(1 << (i % 8)))], 8));
        cases.Add(([.. Enumerable.Range(0, 1 << 16).Select(i => (byte)(i >> 2))], null));
        cases.Add(([.. Enumerable.Range(0, 1 << 18).Select(i => (byte)(i * 31 >> 3))], 65_536));
        cases.Add(([.. Enumerable.Range(0, 1 << 16).SelectMany(i => BitConverter.GetBytes((ulong)i * 0x0101010101010101UL)).Take(1 << 16)], null));
        cases.Add(([.. Enumerable.Range(0, 1 << 14).SelectMany(i => BitConverter.GetBytes(i % 3 == 0 ? double.NaN : i % 3 == 1 ? double.Epsilon : 1e300))], 24));
        cases.Add(([.. Enumerable.Range(0, 1 << 14).SelectMany(i => Encoding.Unicode.GetBytes(i % 5 == 0 ? "\uD800" : "あいう"))], null));
        Fuzz.Run(cases, c => InferAndDraft(c.Data, c.K, InferenceEndian.Auto), timeoutMs: 60_000, describe: c => $"{c.Data.Length} bytes, k = {c.K}");
    }
}
