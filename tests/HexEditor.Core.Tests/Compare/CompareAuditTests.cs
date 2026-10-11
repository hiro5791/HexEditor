using HexEditor.Core.Compare;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Sources;
using HexEditor.Core.Statistics;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.View;

namespace HexEditor.Core.Tests.Compare;

/// <summary>比較 (ANA-04・ANA-09) と解析の対象範囲 (06 の 0.1)・分類 (ANA-16・ANA-17) の監査で足した振る舞い。</summary>
public sealed class CompareAuditTests
{
    private static Document Doc(int length) => new(new MemoryByteSource(new byte[length]), DocumentAssert.Options());

    [Fact]
    public void View_settings_stay_shared_between_the_two_sides()
    {
        // ANA-04 の仕様 2: 1 行のバイト数・グループ化・文字コードは作った後も左右で共通。
        using Document a = Doc(0x1000);
        using Document b = Doc(0x1000);
        var left = new EditorState(a);
        var right = new EditorState(b);
        left.ApplyView(left.View with { BytesPerRow = 32 });
        right.ApplyView(right.View with { BaseAddress = 0x400000 });

        using var link = new CompareViewLink(left, right);
        Assert.Equal(32, right.View.BytesPerRow);
        Assert.Equal(0x400000UL, right.View.BaseAddress);

        right.ApplyView(right.View with { GroupSize = 4, BytesPerRow = 8 });
        Assert.Equal(4, left.View.GroupSize);
        Assert.Equal(8, left.View.BytesPerRow);
        Assert.Equal(0UL, left.View.BaseAddress);

        left.TextEncoding = TextEncoding.FromId("utf-8");
        Assert.Equal("utf-8", right.View.Encoding, ignoreCase: true);

        link.Dispose();
        left.ApplyView(left.View with { BytesPerRow = 16 });
        Assert.Equal(8, right.View.BytesPerRow);
    }

    [Fact]
    public void Unreadable_ranges_are_shown_but_not_counted_as_differences()
    {
        // ANA-09 の仕様 8: 読めなかったページは「読み込み不可」として表示し、差分には数えない。
        var leftData = new FunctionData(0x3000, (o, s) => s.Clear());
        var rightData = new FunctionData(0x3000, (o, s) =>
        {
            s.Clear();
            for (int i = 0; i < s.Length; i++)
            {
                if ((o + i) is 0x100 or 0x2100)
                {
                    s[i] = 1;
                }
            }
        });
        rightData.Bad.Add(new UnreadableRange(0x1000, 0x1000, UnreadableReason.IoError));
        using var result = new CompareResult(CompareMethod.Simple, CompareRange.Whole(leftData), CompareRange.Whole(rightData));
        DataComparer.Run(new CompareOptions(), result);

        Assert.Equal(3, result.DiffCount);
        Assert.Equal(DiffKind.Unreadable, result.Diffs[1].Kind);
        Assert.Equal(2, result.CountedDiffs);
        Assert.Equal(0L, result.CountedOrdinal(0));
        Assert.Null(result.CountedOrdinal(1));
        Assert.Equal(1L, result.CountedOrdinal(2));
    }

    [Fact]
    public void Analysis_target_follows_the_common_rules()
    {
        // 06 の 0.1: 既定は選択範囲・マルチ選択の有無で決まり、「範囲を指定」は開始と長さ (または終了) の入力式。
        using Document doc = Doc(0x2000);
        var editor = new EditorState(doc);
        Assert.Equal(AnalysisTargetKind.WholeDocument, AnalysisTarget.DefaultKind(false, 0));
        Assert.Equal(AnalysisTargetKind.Selection, AnalysisTarget.DefaultKind(true, 1));
        Assert.Equal(AnalysisTargetKind.MultiSelection, AnalysisTarget.DefaultKind(true, 2));

        Assert.Equal([new HashRange(0, 0x2000)], AnalysisTarget.Resolve(AnalysisTargetKind.Selection, editor, null, "", "", false));
        editor.Select(0x10, 0x20);
        Assert.Equal([new HashRange(0x10, 0x20)], AnalysisTarget.Resolve(AnalysisTargetKind.Selection, editor, null, "", "", false));
        Assert.Equal([new HashRange(0x1000, 0x100)], AnalysisTarget.Resolve(AnalysisTargetKind.Custom, editor, null, "0x1000", "0x100", false));
        Assert.Equal([new HashRange(0x1000, 0x100)], AnalysisTarget.Resolve(AnalysisTargetKind.Custom, editor, null, "0x1000", "0x10FF", true));
        Assert.Equal([new HashRange(0x1000, 0x1000)], AnalysisTarget.Resolve(AnalysisTargetKind.Custom, editor, null, "0x1000", "", false));
        Assert.Null(AnalysisTarget.Resolve(AnalysisTargetKind.Custom, editor, null, "0x3000", "", false));
        Assert.Null(AnalysisTarget.Resolve(AnalysisTargetKind.Custom, editor, null, "0x1000", "0x2000", false));
    }

    [Fact]
    public void Minimap_layer_reads_the_dominant_class_of_a_range()
    {
        // ANA-16 の仕様 7: ミニマップの「分類」レイヤは、ピクセル行の範囲で最も多い分類を描く。
        byte[] data = new byte[16 * 1024];
        var random = new Random(1);
        random.NextBytes(data.AsSpan(8 * 1024));
        using Document doc = new(new MemoryByteSource(data), DocumentAssert.Options());
        ClassificationResult r = DataClassifier.Classify(doc.Current, new ClassifyRequest { BlockSize = 1024 });
        Assert.Equal(DataClass.Zero, r.ClassOf(0, 4096));
        Assert.NotEqual(DataClass.Zero, r.ClassOf(12 * 1024, 4096));
        Assert.Equal(DataClass.Zero, r.ClassOf(0, 9 * 1024));
        Assert.Equal(DataClass.None, r.ClassOf(32 * 1024, 100));

        Document? seen = null;
        void OnRemembered(object? sender, Document d) => seen = d;
        DataClassifier.Remembered += OnRemembered;
        try
        {
            DataClassifier.Remember(doc, r);
        }
        finally
        {
            DataClassifier.Remembered -= OnRemembered;
        }

        Assert.Same(doc, seen);
        Assert.Same(r, DataClassifier.LatestFor(doc));
    }

    [Fact]
    public void A_compressed_file_type_turns_random_looking_blocks_into_compressed()
    {
        // ANA-17 の仕様 7: ファイル形式の判定の結果を分類に渡す。ZIP なら高エントロピーのブロックは「圧縮」。
        byte[] data = new byte[64 * 1024];
        new Random(7).NextBytes(data);
        using Document doc = new(new MemoryByteSource(data), DocumentAssert.Options());
        ClassificationResult plain = DataClassifier.Classify(doc.Current, new ClassifyRequest());
        Assert.Contains(plain.Regions, x => x.Class == DataClass.Encrypted);
        Assert.Null(plain.FileTypeName);

        var zip = new FileTypes.FileTypeCandidate("ZIP archive", "application/zip", ["zip"], 90, [], FileTypes.FileTypeDatabase.BuiltInSource);
        ClassificationResult hinted = DataClassifier.Classify(doc.Current, new ClassifyRequest { FileType = zip });
        Assert.DoesNotContain(hinted.Regions, x => x.Class == DataClass.Encrypted);
        Assert.Contains(hinted.Regions, x => x.Class == DataClass.Compressed);
        Assert.Equal("ZIP archive", hinted.FileTypeName);

        var text = zip with { Name = "Text", Mime = "text/plain" };
        Assert.Contains(DataClassifier.Classify(doc.Current, new ClassifyRequest { FileType = text }).Regions, x => x.Class == DataClass.Encrypted);
    }
}
