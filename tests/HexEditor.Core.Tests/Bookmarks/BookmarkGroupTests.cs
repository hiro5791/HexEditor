using System.Text;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Engine;
using HexEditor.Core.Files;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Bookmarks;

/// <summary>ブックマークのグループ (INSP-27)、選択範囲との相互変換 (INSP-28)、インポート / エクスポート (INSP-30)、位置マネージャの書き出し (INSP-31)。</summary>
public sealed class BookmarkGroupTests
{
    private static (Document Doc, BookmarkCollection Bookmarks) Create(long length = 0x10_0000)
    {
        var doc = new Document(new MemoryByteSource(new byte[length]), Options());
        return (doc, BookmarkCollection.Attach(doc));
    }

    // ---- INSP-27 ----

    [Fact]
    public void Visibility_change_is_announced_as_visibility_only_and_keeps_the_order()
    {
        (Document doc, BookmarkCollection bm) = Create();
        using Document _ = doc;
        bm.SetGroup(bm.Add(0x100, 1, "a"), "G");
        IReadOnlyList<Bookmark> ordered = bm.Ordered;
        var events = new List<BookmarksChangedEventArgs>();
        bm.Changed += (_, e) => events.Add(e);
        BookmarkGroup group = bm.FindGroup("G")!;
        bm.SetGroupVisible(group, false);
        Assert.True(events.Single().VisibilityOnly);
        Assert.Equal(BookmarkChangeKind.Groups, events[0].Kind);
        Assert.Same(ordered, bm.Ordered);

        // 色の変更は表示 / 非表示だけの変更ではない。
        bm.SetGroupColor(group, BookmarkColor.Palette(2));
        Assert.False(events[1].VisibilityOnly);
    }

    [Fact]
    public void Hidden_group_is_skipped_by_f2_and_not_visible()
    {
        (Document doc, BookmarkCollection bm) = Create();
        using Document _ = doc;
        Bookmark a = bm.Add(0x100, 1, "a");
        Bookmark b = bm.Add(0x200, 1, "b");
        Bookmark c = bm.Add(0x300, 1, "c");
        bm.SetGroup(a, "G");
        bm.SetGroup(b, "G");
        bm.SetGroupVisible(bm.FindGroup("G")!, false);
        Assert.False(bm.IsVisible(a));
        Assert.True(bm.IsVisible(c));
        Assert.Equal(c, BookmarkActions.Next(bm, 0).Target);
        BookmarkJump wrapped = BookmarkActions.Next(bm, 0x300);
        Assert.Equal((c, true), (wrapped.Target, wrapped.Wrapped));
        Assert.Equal(c, BookmarkActions.Previous(bm, 0x300).Target);

        // 親が非表示なら子のグループのものも非表示。
        bm.SetGroupVisible(bm.FindGroup("G")!, true);
        bm.SetGroup(a, "P/C");
        bm.SetGroupVisible(bm.FindGroup("P")!, false);
        Assert.False(bm.IsVisible(a));
        Assert.True(bm.IsVisible(b));
    }

    [Fact]
    public void Group_color_is_used_unless_the_bookmark_color_was_set()
    {
        (Document doc, BookmarkCollection bm) = Create();
        using Document _ = doc;
        Bookmark a = bm.Add(0x100, 1, "a");
        Bookmark b = bm.Add(0x200, 1, "b");
        bm.SetGroup(a, "G");
        bm.SetGroup(b, "G");
        bm.SetColor(b, BookmarkColor.Palette(5));
        bm.SetGroupColor(bm.FindGroup("G")!, BookmarkColor.Palette(3));
        Assert.Equal(BookmarkColor.Palette(3), bm.EffectiveColor(a));
        Assert.Equal(BookmarkColor.Palette(5), bm.EffectiveColor(b));

        // 子のグループに色がなければ、最も近い祖先の色。
        bm.SetGroup(a, "G/Sub");
        Assert.Equal(BookmarkColor.Palette(3), bm.EffectiveColor(a));
    }

    [Fact]
    public void Deleting_a_group_moves_the_contents_to_the_parent_and_can_be_undone()
    {
        (Document doc, BookmarkCollection bm) = Create();
        using Document _ = doc;
        Bookmark a = bm.Add(0x100, 1, "a");
        Bookmark b = bm.Add(0x200, 1, "b");
        bm.SetGroup(a, "P/C");
        bm.SetGroup(b, "P/C/D");
        BookmarkGroupDeletion deletion = bm.DeleteGroup(bm.FindGroup("P/C")!, deleteContents: false);
        Assert.Null(bm.FindGroup("P/C"));
        Assert.Equal("P", a.Group);
        Assert.Equal("P/D", b.Group);
        Assert.NotNull(bm.FindGroup("P/D"));

        bm.UndoGroupDeletion(deletion);
        Assert.Equal("P/C", a.Group);
        Assert.Equal("P/C/D", b.Group);
        Assert.NotNull(bm.FindGroup("P/C"));

        // 中のブックマークも削除する。取り消すと戻る。
        BookmarkGroupDeletion all = bm.DeleteGroup(bm.FindGroup("P")!, deleteContents: true);
        Assert.Equal(0, bm.Count);
        Assert.Empty(bm.Groups);
        bm.UndoGroupDeletion(all);
        Assert.Equal(2, bm.Count);
        Assert.Equal(0x200, b.Start);
        Assert.NotNull(bm.FindGroup("P/C/D"));
    }

    [Fact]
    public void Groups_are_limited_to_eight_levels_and_survive_saving()
    {
        (Document doc, BookmarkCollection bm) = Create();
        using Document _ = doc;
        Assert.Throws<BookmarkGroupDepthException>(() => bm.EnsureGroup("1/2/3/4/5/6/7/8/9"));
        bm.EnsureGroup("1/2/3/4/5/6/7/8");
        Assert.Throws<BookmarkGroupDepthException>(() => bm.CreateGroup("1/2/3/4/5/6/7/8", "x"));
        Assert.Equal("G", bm.CreateGroup(null, "G").Path);
        Assert.Equal("G (2)", bm.CreateGroup(null, "G").Path);

        Bookmark a = bm.Add(0x10, 4, "a");
        bm.SetGroup(a, "Header");
        bm.SetGroupVisible(bm.FindGroup("Header")!, false);
        bm.SetGroupColor(bm.FindGroup("Header")!, BookmarkColor.Palette(2));
        LoadedBookmarks saved = BookmarkStore.Capture(bm);
        (Document doc2, BookmarkCollection copy) = Create();
        using Document __ = doc2;
        BookmarkStore.Apply(copy, saved, doc2.Length);
        BookmarkGroup header = copy.FindGroup("Header")!;
        Assert.False(header.Visible);
        Assert.Equal(BookmarkColor.Palette(2), header.Color);
        Assert.False(copy.IsVisible(copy.First!));
    }

    // ---- INSP-28 ----

    [Fact]
    public void Ranges_become_bookmarks_and_bookmarks_become_ranges()
    {
        (Document doc, BookmarkCollection bm) = Create();
        using Document _ = doc;
        SelectionBookmarksResult result = BookmarkConversions.FromRanges(bm,
            [new SelectedRange(0x10, 16), new SelectedRange(0x40, 16), new SelectedRange(0x80, 16)], n => $"Selection {n}", "Selections 2026-10-07");
        Assert.Equal(["Selection 1", "Selection 2", "Selection 3"], result.Added.Select(b => b.Name));
        Assert.Equal("Selections 2026-10-07", result.Group!.Path);
        Assert.All(result.Added, b => Assert.Equal("Selections 2026-10-07", b.Group));

        // 1 つの範囲ならグループを作らない。
        Assert.Null(BookmarkConversions.FromRanges(bm, [new SelectedRange(0x200, 1)], n => $"S {n}", "x").Group);

        Bookmark empty = bm.Add(0x100, 0, "empty");
        BookmarkSelectionResult ranges = BookmarkConversions.ToRanges([.. result.Added, empty], doc.Length);
        Assert.Equal([new SelectedRange(0x10, 16), new SelectedRange(0x40, 16), new SelectedRange(0x80, 16)], ranges.Ranges);
        Assert.Equal(1, ranges.Excluded);
    }

    // ---- INSP-30 ----

    /// <summary>TD-INSP-BM-SET と同じ 5 件。</summary>
    private static void AddSet(BookmarkCollection bm)
    {
        Bookmark magic = bm.Add(0x10, 4, "magic");
        bm.SetColor(magic, BookmarkColor.Palette(1));
        bm.SetGroup(magic, "Header");
        bm.SetNumber(magic, 1);
        bm.SetComment(magic, "**magic** bytes");
        Bookmark size = bm.Add(0x20, 8, "size");
        bm.SetColor(size, BookmarkColor.Palette(2));
        bm.SetGroup(size, "Header");
        bm.SetComment(size, "size field");
        Bookmark table = bm.Add(0x100, 16, "table");
        bm.SetColor(table, BookmarkColor.Palette(3));
        bm.SetGroup(table, "Body/Tables");
        bm.SetNumber(table, 2);
        bm.SetComment(table, "# Table\nentries");
        Bookmark flag = bm.Add(0x400, 1, "flag");
        bm.SetColor(flag, BookmarkColor.Palette(4));
        Bookmark jp = bm.Add(0x800, 32, "日本語の名前");
        bm.SetColor(jp, BookmarkColor.Palette(5));
        bm.SetGroup(jp, "Body");
        bm.SetComment(jp, "コメント, カンマ入り");
    }

    private static List<(long, long, string, string, string?, int, string)> Snapshot(BookmarkCollection bm) =>
        [.. bm.Ordered.Select(b => (b.Start, b.Length, b.Name, bm.EffectiveColor(b).ToString(), b.Group, b.Number, b.Comment))];

    [Fact]
    [Trait(TC, "TC-INSP-30-01")]
    public void Json_export_and_import_keep_every_field()
    {
        (Document doc, BookmarkCollection bm) = Create();
        using Document _ = doc;
        AddSet(bm);
        byte[] file = BookmarkExchange.Export(BookmarkFileFormat.Json, bm, bm.Ordered);
        string text = Encoding.UTF8.GetString(file);
        Assert.StartsWith("{\n  \"version\": 1,", text.Replace("\r\n", "\n", StringComparison.Ordinal));

        (Document other, BookmarkCollection copy) = Create(16 * 1024 * 1024);
        using Document __ = other;
        BookmarkImportReport report = BookmarkExchange.Apply(copy, BookmarkExchange.Parse(BookmarkFileFormat.Json, file), BookmarkImportMode.Append, 0, other.Length);
        Assert.Equal(5, report.Imported);
        Assert.Equal(Snapshot(bm), Snapshot(copy));
        Assert.NotNull(copy.FindGroup("Body/Tables"));

        // プロジェクトファイルにも同じ形で入る。
        byte[] project = HexProject.Write(@"C:\work\proj.hexproj", @"C:\work\seq.bin", bm, [], "big");
        HexProject read = HexProject.Read(@"C:\work\proj.hexproj", project);
        Assert.Equal(@"C:\work\seq.bin", read.FilePath);
        Assert.Equal("big", read.InspectorEndian);
        Assert.Equal(5, read.Bookmarks.Items.Count);
        Assert.Equal(5, BookmarkExchange.Parse(BookmarkFileFormat.Project, project).Items.Count);
    }

    [Fact]
    [Trait(TC, "TC-INSP-30-02")]
    public void Excel_style_csv_is_imported()
    {
        // Excel の「CSV UTF-8 (コンマ区切り)」: BOM 付き、CRLF、カンマ・改行を含む値は " で囲む (TD-INSP-BM-CSV-EXCEL)。
        string csv = "start,length,name,color,group,comment\r\n"
            + "0x10,4,ヘッダ,#FF0000,Header,\"magic, bytes\"\r\n"
            + "0x100,16,table,#00FF00,Body/Tables,\"line1\r\nline2\"\r\n"
            + "0x400,1,flag,#0000FF,,\r\n";
        byte[] file = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)];
        (Document doc, BookmarkCollection bm) = Create();
        using Document _ = doc;
        BookmarkExchange.Apply(bm, BookmarkExchange.Parse(BookmarkFileFormat.Csv, file), BookmarkImportMode.Append, 0, doc.Length);
        Assert.Equal(
            [
                (0x10L, 4L, "ヘッダ", "#FF0000", "Header", 0, "magic, bytes"),
                (0x100L, 16L, "table", "#00FF00", "Body/Tables", 0, "line1\nline2"),
                (0x400L, 1L, "flag", "#0000FF", (string?)null, 0, string.Empty),
            ],
            Snapshot(bm));

        // 書き出した CSV を読み直すと同じになる (Excel で開いて保存しても形は同じ)。
        byte[] exported = BookmarkExchange.Export(BookmarkFileFormat.Csv, bm, bm.Ordered);
        Assert.Equal(Encoding.UTF8.GetPreamble(), exported[..3]);
        (Document doc2, BookmarkCollection again) = Create();
        using Document __ = doc2;
        BookmarkExchange.Apply(again, BookmarkExchange.Parse(BookmarkFileFormat.Csv, exported), BookmarkImportMode.Append, 0, doc2.Length);
        Assert.Equal(Snapshot(bm), Snapshot(again));
    }

    [Fact]
    [Trait(TC, "TC-INSP-30-04")]
    public void Import_with_an_offset_shift_and_out_of_range_items()
    {
        (Document src, BookmarkCollection bm) = Create();
        using Document _ = src;
        AddSet(bm);
        byte[] file = BookmarkExchange.Export(BookmarkFileFormat.Json, bm, bm.Ordered);
        (Document doc, BookmarkCollection copy) = Create();
        using Document __ = doc;
        BookmarkImportReport report = BookmarkExchange.Apply(copy, BookmarkExchange.Parse(BookmarkFileFormat.Json, file), BookmarkImportMode.Append, 0x200, doc.Length);
        Assert.Equal((5, 0), (report.Imported, report.SkippedOutOfRange));
        Assert.Equal(bm.Ordered.Select(b => b.Start + 0x200), copy.Ordered.Select(b => b.Start));

        // 範囲外になるものは読み込まず数える。
        (Document small, BookmarkCollection tiny) = Create(0x500);
        using Document ___ = small;
        BookmarkImportReport partial = BookmarkExchange.Apply(tiny, BookmarkExchange.Parse(BookmarkFileFormat.Json, file), BookmarkImportMode.Append, 0x200, small.Length);
        Assert.Equal((3, 2), (partial.Imported, partial.SkippedOutOfRange));
    }

    [Fact]
    [Trait(TC, "TC-INSP-30-05")]
    public void Invalid_json_reports_the_position_and_changes_nothing()
    {
        byte[] bad = Encoding.UTF8.GetBytes("{\n  \"version\": 1,\n  \"bookmarks\": [ { \"start\": 16,, \"length\": 4 } ]\n}");
        BookmarkFormatException error = Assert.Throws<BookmarkFormatException>(() => BookmarkExchange.Parse(BookmarkFileFormat.Json, bad));
        Assert.Equal(3, error.Line);

        // CSV の誤りは行番号、XML の誤りは位置。
        BookmarkFormatException csv = Assert.Throws<BookmarkFormatException>(() =>
            BookmarkExchange.Parse(BookmarkFileFormat.Csv, Encoding.UTF8.GetBytes("start,length\r\n0x10,4\r\nxyz,1\r\n")));
        Assert.Equal(3, csv.Line);
        Assert.NotNull(Assert.Throws<BookmarkFormatException>(() => BookmarkExchange.Parse(BookmarkFileFormat.WxHexEditor, Encoding.UTF8.GetBytes("<a><b></a>"))).Line);
    }

    [Fact]
    public void Wx_tags_and_number_conflicts()
    {
        (Document doc, BookmarkCollection bm) = Create();
        using Document _ = doc;
        AddSet(bm);
        byte[] wx = BookmarkExchange.Export(BookmarkFileFormat.WxHexEditor, bm, bm.Ordered, fileName: "seq.bin");
        string xml = Encoding.UTF8.GetString(wx);
        Assert.Contains("<start_offset>16</start_offset>", xml);
        Assert.Contains("<end_offset>19</end_offset>", xml);
        BookmarkImportData data = BookmarkExchange.Parse(BookmarkFileFormat.WxHexEditor, wx);
        Assert.Equal((0x10L, 4L, "magic", "**magic** bytes"), (data.Items[0].Start, data.Items[0].Length, data.Items[0].Name, data.Items[0].Comment));

        // 番号が衝突したらインポートする方を優先し、既存のものから外す。1〜9 以外の番号は外す。
        Bookmark existing = bm.Add(0x900, 1, "existing");
        bm.SetNumber(existing, 3);
        var import = new BookmarkImportData(
            [new BookmarkImportItem(0x910, 1, "new3", null, null, 3, string.Empty), new BookmarkImportItem(0x920, 1, "zero", null, null, 12, string.Empty)], []);
        BookmarkImportReport report = BookmarkExchange.Apply(bm, import, BookmarkImportMode.Append, 0, doc.Length);
        Assert.Equal([3], report.NumbersMoved);
        Assert.Equal(1, report.NumbersDropped);
        Assert.Equal(0, existing.Number);
        Assert.Equal("new3", bm.WithNumber(3)!.Name);

        // 置き換える。
        BookmarkExchange.Apply(bm, import, BookmarkImportMode.Replace, 0, doc.Length);
        Assert.Equal(2, bm.Count);
    }

    // ---- INSP-31 ----

    [Fact]
    [Trait(TC, "TC-INSP-31-04")]
    public void Position_notes_are_written_in_offset_order()
    {
        (Document doc, BookmarkCollection bm) = Create();
        using Document _ = doc;
        AddSet(bm);
        string md = PositionNotes.ToMarkdown(bm.Ordered.Reverse(), "Notes", "{0}–{1} ({2} bytes)");
        string[] headings = [.. md.Split('\n').Where(l => l.StartsWith("## ", StringComparison.Ordinal))];
        Assert.Equal(
            ["## magic — 0x10–0x13 (4 bytes)", "## size — 0x20–0x27 (8 bytes)", "## table — 0x100–0x10F (16 bytes)", "## flag — 0x400–0x400 (1 bytes)",
                "## 日本語の名前 — 0x800–0x81F (32 bytes)"],
            headings);
        Assert.Contains("## table — 0x100–0x10F (16 bytes)\n\n# Table\nentries\n", md);

        string html = PositionNotes.ToHtml(bm.Ordered, "Notes", "{0}–{1} ({2} bytes)");
        Assert.Contains("<strong>magic</strong> bytes", html);
        Assert.Contains("<h2>magic — 0x10–0x13 (4 bytes)</h2>", html);
    }

    [Fact]
    [Trait(TC, "TC-INSP-30-05")]
    public void Format_errors_carry_a_kind_and_detail_for_the_app_to_localize()
    {
        BookmarkFormatException Fail(BookmarkFileFormat format, string text) =>
            Assert.Throws<BookmarkFormatException>(() => BookmarkExchange.Parse(format, Encoding.UTF8.GetBytes(text)));

        BookmarkFormatException start = Fail(BookmarkFileFormat.Csv, "start,length\r\nxyz,1\r\n");
        Assert.Equal((BookmarkFormatError.InvalidStart, "xyz", 2), (start.Error, start.Detail, start.Line!.Value));
        Assert.Equal("Invalid start offset \"xyz\"", start.Reason);
        Assert.Equal(BookmarkFormatError.InvalidLength, Fail(BookmarkFileFormat.Csv, "start,length\r\n0x10,-1\r\n").Error);
        Assert.Equal(BookmarkFormatError.InvalidColor, Fail(BookmarkFileFormat.Csv, "start,color\r\n0x10,red\r\n").Error);
        Assert.Equal(BookmarkFormatError.UnterminatedQuote, Fail(BookmarkFileFormat.Csv, "start,name\r\n0x10,\"abc\r\n").Error);
        Assert.Equal(BookmarkFormatError.MissingBookmarks, Fail(BookmarkFileFormat.Json, "{\"version\": 1}").Error);
        BookmarkFormatException version = Fail(BookmarkFileFormat.Json, "{\"version\": 9, \"bookmarks\": []}");
        Assert.Equal((BookmarkFormatError.UnsupportedVersion, "9"), (version.Error, version.Detail));
        BookmarkFormatException missing = Fail(BookmarkFileFormat.Json, "{\"bookmarks\": [{\"start\": 1}, {\"length\": 2}]}");
        Assert.Equal((BookmarkFormatError.MissingStart, "2"), (missing.Error, missing.Detail));
        Assert.Equal(BookmarkFormatError.Syntax, Fail(BookmarkFileFormat.Json, "{,}").Error);
        Assert.Equal(BookmarkFormatError.Syntax, Fail(BookmarkFileFormat.WxHexEditor, "<a><b></a>").Error);
        Assert.Equal(BookmarkFormatError.InvalidTagOffsets,
            Fail(BookmarkFileFormat.WxHexEditor, "<wxHexEditor_XML_TAG><filename><TAG id=\"0\"><start_offset>9</start_offset><end_offset>2</end_offset></TAG></filename></wxHexEditor_XML_TAG>").Error);
    }

    [Fact]
    public void Large_import_is_prepared_off_the_collection_with_progress_and_cancel_then_committed_in_steps()
    {
        (Document doc, BookmarkCollection bm) = Create();
        using Document _ = doc;
        bm.Add(0x10, 1, "existing");
        var items = Enumerable.Range(0, 50_000).Select(i => new BookmarkImportItem(i * 16L, 4, "b" + i, null, i % 2 == 0 ? "Even" : null, i == 7 ? 3 : i == 9 ? 42 : 0, string.Empty)).ToList();
        var data = new BookmarkImportData(items, []);

        // キャンセル: 何も加えない。
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => BookmarkExchange.Prepare(data, BookmarkImportMode.Append, 0x100, doc.Length, bm.Count, cts.Token));
        Assert.Equal(1, bm.Count);

        var progress = new List<long>();
        PreparedBookmarkImport prepared = BookmarkExchange.Prepare(data, BookmarkImportMode.Append, 0x100, doc.Length, bm.Count, default, progress.Add);
        Assert.Equal(1, bm.Count);
        Assert.Equal(data.Items.Count, progress[^1]);
        Assert.True(progress.Count > 2);
        Assert.Equal(1, prepared.NumbersDropped);

        int notices = 0;
        bm.Changed += (_, _) => notices++;
        var steps = prepared.CommitInSteps(bm, 20_000).ToList();
        Assert.Equal([20_000, 40_000, 50_000], steps);
        Assert.Equal(1, notices);
        Assert.Equal((50_000, 0, 0, 1), (prepared.Report!.Imported, prepared.Report.SkippedOutOfRange, prepared.Report.SkippedLimit, prepared.Report.NumbersDropped));
        Assert.Equal(50_001, bm.Count);
        Assert.Equal(0x100 + 7 * 16, bm.WithNumber(3)!.Start);
        Assert.Equal("Even", bm.StartingAt(0x100)!.Group);
    }

    [Fact]
    public void Export_from_captured_values_matches_and_can_be_cancelled()
    {
        (Document doc, BookmarkCollection bm) = Create();
        using Document _ = doc;
        AddSet(bm);
        IReadOnlyList<BookmarkExportItem> captured = BookmarkExchange.Capture(bm, bm.Ordered);
        IReadOnlyList<BookmarkGroupRecord> groups = BookmarkExchange.GroupsOf(bm, bm.Ordered);
        foreach (BookmarkFileFormat format in new[] { BookmarkFileFormat.Json, BookmarkFileFormat.Csv, BookmarkFileFormat.WxHexEditor })
        {
            Assert.Equal(BookmarkExchange.Export(format, bm, bm.Ordered, fileName: "seq.bin"), BookmarkExchange.Export(format, captured, groups, "seq.bin"));
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => BookmarkExchange.Export(BookmarkFileFormat.Csv, captured, groups, null, cts.Token));
    }

    [Fact]
    [Trait(TC, "TC-INSP-28-01")]
    public void Selection_to_bookmarks_is_prepared_then_committed_in_steps()
    {
        (Document doc, BookmarkCollection bm) = Create();
        using Document _ = doc;
        SelectedRange[] ranges = [.. Enumerable.Range(0, 5).Select(i => new SelectedRange(i * 0x100L, 4))];
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => BookmarkConversions.PrepareFromRanges(ranges, n => "s" + n, "G", null, cts.Token));
        Assert.Equal(0, bm.Count);

        PreparedSelectionBookmarks prepared = BookmarkConversions.PrepareFromRanges(ranges, n => "Selection " + n, "Selection group");
        Assert.Equal([2, 4, 5], prepared.CommitInSteps(bm, 2).ToList());
        Assert.Equal(5, prepared.Result!.Added.Count);
        Assert.Equal("Selection group", prepared.Result.Group!.Path);
        Assert.Equal(["Selection 1", "Selection 5"], new[] { bm.Ordered[0].Name, bm.Ordered[4].Name });

        // 範囲 (写したもの) から選択範囲へ: 開始位置の順、長さ 0 を除く。
        BookmarkSelectionResult back = BookmarkConversions.ToRanges([new SelectedRange(0x300, 4), new SelectedRange(0x100, 0), new SelectedRange(0x10, 2)], doc.Length);
        Assert.Equal([new SelectedRange(0x10, 2), new SelectedRange(0x300, 4)], back.Ranges);
        Assert.Equal(1, back.Excluded);
    }
}
