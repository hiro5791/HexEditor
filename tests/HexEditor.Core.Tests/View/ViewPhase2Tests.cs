using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.View;

/// <summary>
/// フェーズ 2 の表示の部品: 文字表 (VIEW-23)、レコード (VIEW-18)、区切り (VIEW-33)、横の移動 (VIEW-10・VIEW-11)、テキスト列の切り替え
/// (VIEW-24)、ページ単位の表示 (VIEW-33)、ほかのビューの編集への追従 (VIEW-37・VIEW-38)、同期スクロール (VIEW-37・VIEW-39)、ミニマップ (VIEW-35)。
/// </summary>
public sealed class ViewPhase2Tests
{
    private static (Document Doc, EditorState State) Create(int length, int visibleRows = 10)
    {
        byte[] data = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var doc = new Document(new FakeByteSource(data, SourceCapabilities.CanResize | SourceCapabilities.CanWrite), Options());
        return (doc, new EditorState(doc) { VisibleRows = visibleRows });
    }

    // ---- VIEW-23 ----

    private const string TestTable = "# test table\r\n41=A\r\n4142=あ\r\nFF=<END>\r\nXYZ\r\n4G=B\r\n123=C\r\n\r\n/00=<EOS>";

    [Fact]
    public void Table_file_reads_valid_lines_and_lists_invalid_ones()
    {
        TableLoadResult result = TableFile.Parse(Encoding.UTF8.GetBytes(TestTable), "test.tbl");
        Assert.NotNull(result.Table);
        Assert.Equal(4, result.Table!.Count);
        Assert.Equal([(5, TableLineError.NoEquals), (6, TableLineError.NotHex), (7, TableLineError.OddDigits)], result.InvalidLines);

        // 最長一致: 41 42 は「あ」、41 43 は「A」と一致しないバイト。
        Assert.Equal((2, "あ"), result.Table.Match([0x41, 0x42]));
        Assert.Equal((1, "A"), result.Table.Match([0x41, 0x43]));
        Assert.Equal((0, null), result.Table.Match([0x43]));
        Assert.Equal("あA�", result.Table.Decode([0x41, 0x42, 0x41, 0x43]));

        // UTF-16 LE (BOM 付き) も読める。
        byte[] utf16 = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("41=A")];
        Assert.Equal(1, TableFile.Parse(utf16, "u.tbl").Table!.Count);

        // 有効なエントリがなければ読み込まない。
        Assert.Equal(TableLoadFailure.NoEntries, TableFile.Parse("# only comments\nXYZ", "x").Failure);
    }

    [Fact]
    public void Table_encoding_decodes_cells_by_longest_match()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hexeditor-tbl-" + Guid.NewGuid().ToString("N"));
        string? before = TableEncodings.Folder;
        try
        {
            TableEncodings.Folder = folder;
            Directory.CreateDirectory(folder);
            string source = Path.Combine(folder, "..", Path.GetFileName(folder) + "-src.tbl");
            File.WriteAllText(source, TestTable, new UTF8Encoding(false));
            Assert.NotNull(TableEncodings.Import(source).Table);
            File.Delete(source);

            TextEncoding encoding = TextEncoding.FromId(TableEncodings.IdOf(Path.GetFileName(source)));
            Assert.Equal(TextEncodingKind.Table, encoding.Kind);
            Assert.Contains(EncodingCatalog.All, e => e.Group == EncodingGroup.Custom && e.Id == encoding.Id);

            var cells = new TextCell[4];
            TextCellDecoder.Decode(encoding, [0x41, 0x42, 0x41, 0x43], 0, 0, cells);
            Assert.Equal("あ", cells[0].Text);
            Assert.Equal(2, cells[0].Span);
            Assert.Equal(TextCellKind.Continuation, cells[1].Kind);
            Assert.Equal("A", cells[2].Text);
            Assert.Equal(".", cells[3].Text);
            Assert.True(encoding.TryEncode("あA", out byte[] bytes));
            Assert.Equal([0x41, 0x42, 0x41], bytes);
        }
        finally
        {
            TableEncodings.Folder = before;
            TableEncodings.Reset();
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- VIEW-18 ----

    [Fact]
    public void Records_are_counted_from_the_start_offset()
    {
        var records = new RecordLayout(0x40, 100);
        Assert.Null(records.IndexOf(0x3F));
        Assert.Equal(0, records.IndexOf(0x40));
        Assert.Equal(1, records.IndexOf(0xA4));
        Assert.True(records.IsOdd(0xA4));
        Assert.False(records.IsOdd(0x108));
        Assert.Equal(0xA9, records.Next(0x45, 0xFFFFF));
        Assert.Null(records.Previous(0x45));
        Assert.Null(records.Next(0xFFFFF, 0xFFFFF));

        // 1 行を 1 レコードにすると、0x40 から始まる行ができる。
        (Document doc, EditorState state) = Create(0x1000);
        using (doc)
        {
            state.ApplyView(state.View with { RecordView = true, RecordLength = 100, RecordStart = 0x40, RecordPerRow = true });
            Assert.Equal(100, state.BytesPerRow);
            Assert.Equal(0x40, state.Layout.RowStart(1));
            Assert.Equal(0xA4, state.Layout.RowStart(2));

            // 入力式の rec と recno (VIEW-18 の仕様 8)。
            state.GoTo(0x10D);
            var context = new EditorExpressionContext(state);
            Assert.True(ExpressionEvaluator.TryEvaluate("recno", context, out long recno, out _));
            Assert.Equal(2, recno);
            Assert.True(ExpressionEvaluator.TryEvaluate("rec", context, out long rec, out _));
            Assert.Equal(100, rec);
            state.GoTo(0x10);
            Assert.False(ExpressionEvaluator.TryEvaluate("recno", context, out _, out _));
        }
    }

    // ---- VIEW-33 ----

    [Fact]
    public void Sections_are_numbered_from_zero_and_the_end_belongs_to_the_last()
    {
        var pages = new SectionLayout(4096, 1024 * 1024);
        Assert.Equal(256, pages.Count);
        Assert.Equal(3, pages.IndexOf(0x3000));
        Assert.Equal(2, pages.IndexOf(0x2FF0));
        Assert.Equal(255, pages.IndexOf(1024 * 1024));
        Assert.Equal(0x3000, pages.Next(0x2FF0));
        Assert.Equal(0x2000, pages.Previous(0x2FF0));
        Assert.Equal(0x1000, pages.Previous(0x2000));
        Assert.Null(pages.Next(1024 * 1024 - 1));

        Assert.Equal(SeparatorError.NotMultipleOfRow, SectionLayout.Validate(4096, 24, 0));
        Assert.Null(SectionLayout.Validate(4104, 24, 0));
        Assert.Equal(SeparatorError.RowShift, SectionLayout.Validate(4096, 16, 8));
    }

    [Fact]
    public void Page_view_keeps_the_rows_in_one_section()
    {
        (Document doc, EditorState state) = Create(1024 * 1024, visibleRows: 30);
        using (doc)
        {
            state.ApplyView(state.View with { Separator = SeparatorKind.Page, PageView = true });
            long last = -1;
            for (int i = 0; i < 30; i++)
            {
                state.PageDown();
                Assert.True(state.Cursor > last);
                last = state.Cursor;
                (long first, long lastRow) = state.VisibleSectionRows!.Value;
                Assert.InRange(state.TopRow, first, Math.Max(first, lastRow - 29));
                Assert.Equal(state.TopRow / 256, state.Layout.RowOf(state.Cursor) / 256);
            }
        }
    }

    // ---- VIEW-10・VIEW-11・VIEW-24 ----

    [Fact]
    public void Arrow_keys_follow_cells_and_reversed_groups()
    {
        (Document doc, EditorState state) = Create(0x200);
        using (doc)
        {
            // 逆順表示 (グループ化 4): 0x113 (画面上で左端) から → で 0x112、さらに 3 回で 0x117。
            state.ApplyView(state.View with { GroupSize = 4, ReverseGroups = true });
            state.GoTo(0x113);
            state.MoveRight();
            Assert.Equal(0x112, state.Cursor);
            state.MoveRight();
            state.MoveRight();
            state.MoveRight();
            Assert.Equal(0x117, state.Cursor);
            state.MoveLeft();
            Assert.Equal(0x110, state.Cursor);

            // 32 bit の形式: 単位ごとに動き、入力は受け付けない (VIEW-10 の仕様 6・7)。
            state.ApplyView(state.View with { ReverseGroups = false, CellFormat = CellFormat.Int32Hex });
            state.GoTo(0x102);
            state.MoveRight();
            Assert.Equal(0x104, state.Cursor);
            state.MoveLeft();
            Assert.Equal(0x100, state.Cursor);
            Assert.Equal(EditResult.CellFormatNotEditable, state.TypeHexDigit('A'));

            // テキスト列ではバイト単位。
            state.ToggleColumn();
            state.MoveRight();
            Assert.Equal(0x101, state.Cursor);
        }
    }

    [Fact]
    public void Tab_cycles_through_the_text_columns_and_the_encoding_follows()
    {
        (Document doc, EditorState state) = Create(0x100);
        using (doc)
        {
            state.ApplyView(state.View.WithTextColumns([new("ascii"), new("utf-8"), new("cp37")]));
            string[] names = new string[4];
            for (int i = 0; i < 4; i++)
            {
                state.ToggleColumn();
                names[i] = (state.ActiveColumn == ActiveColumn.Hex ? "hex:" : "text" + state.TextColumn + ":") + state.TextEncoding.Name;
            }

            Assert.Equal(["text0:ASCII", "text1:UTF-8", "text2:037", "hex:037"], names);
            state.ToggleColumn(backward: true);
            Assert.Equal(2, state.TextColumn);
        }
    }

    // ---- VIEW-37・VIEW-38 ----

    [Fact]
    public void Other_views_keep_their_data_still_on_insertion_and_ignore_undo_selection()
    {
        (Document doc, EditorState upper) = Create(1024 * 1024);
        using (doc)
        {
            var lower = new EditorState(doc) { VisibleRows = 10, FollowsEdits = true };
            lower.ScrollToRow(0x800);
            lower.Click(0x8005, ActiveColumn.Hex, false, false);

            // 先頭に 16 バイト挿入すると、もう一方の一番上の行の先頭は 0x8010 になる。
            doc.Insert(0, new byte[16]);
            Assert.Equal(0x8010, lower.TopOffset);
            Assert.Equal(0x8015, lower.Cursor);

            // Undo の範囲の選択は、Undo を実行したビューだけ。
            upper.Undo();
            Assert.True(upper.HasSelection || upper.Cursor == 0);
            Assert.False(lower.HasSelection);
            Assert.Equal(0x8005, lower.Cursor);
            Assert.Equal(0x8000, lower.TopOffset);
        }
    }

    [Fact]
    public void Synced_views_follow_by_offset_or_by_the_kept_difference()
    {
        (Document left, EditorState a) = Create(1024 * 1024);
        (Document right, EditorState b) = Create(256);
        using (left)
        using (right)
        {
            b.ApplyView(b.View with { BytesPerRow = 32 });
            using var sync = new ViewSync([a, b]);
            a.GoTo(0x12340);
            Assert.Equal(b.Layout.MaxCursor, b.Cursor);
            a.MoveToStart();
            Assert.Equal(0, b.Cursor);

            // 1 行のバイト数が違っても同じオフセット (行番号ではない)。
            (Document third, EditorState c) = Create(1024 * 1024);
            using (third)
            {
                c.ApplyView(c.View with { BytesPerRow = 32 });
                using var offsets = new ViewSync([a, c]);
                a.GoTo(0x12340);
                Assert.Equal(0x12340, c.Cursor);
                Assert.Equal(a.TopOffset / 32 * 32, c.TopOffset);

                // 位置の差を保つ (どちらも 1 行 16 バイト)。
                offsets.Mode = SyncMode.Off;
                c.ApplyView(c.View with { BytesPerRow = 16 });
                a.GoTo(0x100);
                a.ScrollToRow(0x10);
                c.GoTo(0x300);
                c.ScrollToRow(0x30);
                offsets.Mode = SyncMode.KeepDifference;
                a.MoveDown();
                a.MoveDown();
                Assert.Equal(0x200, c.Cursor - a.Cursor);
                a.ScrollRows(3);
                Assert.Equal(0x200, c.TopOffset - a.TopOffset);
            }
        }
    }

    [Fact]
    public void Split_panes_scroll_by_the_same_number_of_rows()
    {
        (Document doc, EditorState upper) = Create(1024 * 1024);
        using (doc)
        {
            var lower = new EditorState(doc) { VisibleRows = 10 };
            lower.ScrollToRow(100);
            using var sync = new ViewSync([upper, lower], SyncMode.ScrollRows);
            upper.ScrollRows(6);
            Assert.Equal(106, lower.TopRow);
            lower.ScrollRows(-4);
            Assert.Equal(2, upper.TopRow);
        }
    }

    // ---- VIEW-35 ----

    [Fact]
    public void Minimap_entropy_separates_random_and_zero_data()
    {
        byte[] data = new byte[1 << 20];
        new Random(20261007).NextBytes(data.AsSpan(0, data.Length / 2));
        using var doc = new Document(new FakeByteSource(data, SourceCapabilities.CanWrite), Options());
        using var minimap = new MinimapComputer();
        (long first, long rowBytes, int count) = MinimapComputer.Whole(doc.Length, 64, 16);
        Assert.Equal(64, count);
        var done = new ManualResetEventSlim();
        minimap.Progress += (_, _) =>
        {
            if (minimap.Computed == minimap.RowCount)
            {
                done.Set();
            }
        };
        minimap.Start(doc.Current, first, rowBytes, count);
        Assert.True(done.Wait(TimeSpan.FromSeconds(20)));
        for (int i = 0; i < 32; i++)
        {
            Assert.True(minimap.Row(i)!.Entropy >= 7.9, $"row {i}: {minimap.Row(i)!.Entropy}");
            Assert.True(minimap.Row(i)!.BarOf(MinimapContent.Entropy) >= 0.98);
        }

        for (int i = 32; i < 64; i++)
        {
            Assert.Equal(0, minimap.Row(i)!.Entropy);
            Assert.Equal(0, minimap.Row(i)!.BarOf(MinimapContent.Entropy));
        }

        Assert.Equal(48, minimap.RowOf(rowBytes * 48 + 5));
    }
}
