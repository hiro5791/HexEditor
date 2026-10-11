using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Core.Files;
using HexEditor.Core.Selection;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Editing;

/// <summary>選択範囲の保存と読み込み (EDIT-09)、ユーザークリップボード (EDIT-28)、履歴の任意の時点への移動 (EDIT-20)。</summary>
public sealed class SelectionSetTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HexEditorTests", "sel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static List<ByteRange> Three => [new(0x10, 4), new(0x20, 8), new(0x30, 0x10)];

    [Fact]
    public void Sets_are_saved_and_loaded_with_the_document_data()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            s.SetSelections(Three);
            var sets = new SelectionSetCollection();
            Assert.Equal(SelectionSetSaveResult.Saved, sets.Save("set1", s.CaptureSelection()));
            Assert.Equal(SelectionSetSaveResult.Exists, sets.Save("set1", s.CaptureSelection()));
            Assert.Equal(SelectionSetSaveResult.InvalidName, sets.Save(new string('a', 101), s.CaptureSelection()));
            s.SelectRectangle(0x04, 0x37);
            Assert.Equal(SelectionSetSaveResult.Saved, sets.Save("rect", s.CaptureSelection()));

            var store = new DocumentDataStore(_folder);
            string path = Path.Combine(_folder, "doc.bin");
            sets.Save(store, path, null);
            SelectionSetCollection loaded = SelectionSetCollection.Load(store, path);
            Assert.Equal(["set1", "rect"], loaded.Sets.Select(x => x.Name));
            SelectionSet set1 = loaded.Find("set1")!;
            Assert.Equal(3, set1.Count);
            Assert.Equal(28, set1.TotalLength);
            Assert.Equal(Three, set1.Ranges);

            // 読み込むと同じ 3 要素が選ばれる。
            s.ClearSelection();
            (IReadOnlyList<ByteRange> ranges, RectSelection? rect, _, _) = SelectionSetCollection.Resolve(set1, doc.Length, 16, 0);
            Assert.Null(rect);
            s.SetSelections(ranges);
            Assert.Equal(Three, s.SelectedRanges);

            // 矩形は同じ 1 行のバイト数なら矩形のまま、違えば行ごとの要素になる。
            SelectionSet r = loaded.Find("rect")!;
            Assert.Equal(16, r.TotalLength);
            Assert.NotNull(SelectionSetCollection.Resolve(r, doc.Length, 16, 0).Rectangle);
            Assert.Equal(4, SelectionSetCollection.Resolve(r, doc.Length, 8, 0).Ranges.Count);

            // 長さより後ろの要素は切り詰める・除外する (仕様 4)。
            (IReadOnlyList<ByteRange> clipped, _, int truncated, int removed) = SelectionSetCollection.Resolve(set1, 0x24, 16, 0);
            Assert.Equal([new ByteRange(0x10, 4), new ByteRange(0x20, 4)], clipped);
            Assert.Equal((1, 1), (truncated, removed));
        }
    }

    [Fact]
    public void Csv_export_and_import_round_trip()
    {
        string csv = SelectionFile.ToCsv(Three);
        Assert.Equal("start,length\r\n0x10,0x4\r\n0x20,0x8\r\n0x30,0x10\r\n", csv);
        Assert.Equal(Three, SelectionFile.Parse(csv, out _));

        string json = SelectionFile.ToJson("set1", [new ByteRange(256, 16)]);
        Assert.Equal([new ByteRange(256, 16)], SelectionFile.Parse(json, out string? name));
        Assert.Equal("set1", name);

        // テキスト: 「開始 長さ」と「開始-終了」(終了を含む)。10 進も受け付ける。
        Assert.Equal([new ByteRange(16, 4), new ByteRange(0x20, 8)], SelectionFile.Parse("16 4\n0x20-0x27\n", out _));

        // 誤りは見出しの行を 1 行目として数えた行番号で示し、何も読み込まない。
        var error = Assert.Throws<SelectionImportException>(() => SelectionFile.Parse("start,length\nabc,0x4\n", out _));
        Assert.Equal((2, SelectionImportError.StartNotNumber), (error.Line, error.Error));
        error = Assert.Throws<SelectionImportException>(() => SelectionFile.Parse("start,length\n0x10,0x4\n0x20,zz\n", out _));
        Assert.Equal((3, SelectionImportError.LengthNotNumber), (error.Line, error.Error));
    }

    [Fact]
    public void User_clipboards_are_independent_and_persist_up_to_16_MiB()
    {
        (Document doc, EditorState _) = MultiSelectionTests.Create(0x100);
        using (doc)
        using (var clipboards = new UserClipboards())
        {
            clipboards.Set(3, ClipboardEntry.Capture(doc, 0x10, 4));
            Assert.Equal(new byte[] { 0x10, 0x11, 0x12, 0x13 }, clipboards.Get(3)!.Head);
            Assert.Null(clipboards.Get(5));
            Assert.True(clipboards.SetName(3, "header"));
            Assert.False(clipboards.SetName(3, new string('x', 51)));

            // 履歴は新しい順に最大 20 件。
            for (int k = 1; k <= 21; k++)
            {
                clipboards.AddHistory(ClipboardEntry.Capture(doc, k * 0x10 % 0xF0, k));
            }

            Assert.Equal(20, clipboards.History.Count);
            Assert.Equal(21, clipboards.History[0].Length);
            Assert.Equal(2, clipboards.History[^1].Length);

            // 終了後も残す (16 MiB 以下)。履歴は残さない。
            clipboards.Persist = true;
            clipboards.Save(_folder);
            using var loaded = new UserClipboards { Persist = true };
            loaded.Load(_folder);
            Assert.Equal(new byte[] { 0x10, 0x11, 0x12, 0x13 }, loaded.Get(3)!.Data);
            Assert.Equal("header", loaded.NameOf(3));
            Assert.Empty(loaded.History);
        }
    }

    [Fact]
    public void History_moves_to_any_point_at_once()
    {
        var doc = new Document(new FakeByteSource(new byte[0x100], SourceCapabilities.CanResize | SourceCapabilities.CanWrite), Options());
        using (doc)
        {
            for (int i = 1; i <= 5; i++)
            {
                doc.Overwrite(i * 0x10, [(byte)(0xA0 + i)]);
                doc.History.BreakCoalescing();
            }

            Assert.Equal(6, doc.History.Count);
            int changes = 0;
            doc.Changed += (_, _) => changes++;
            doc.MoveToHistory(1);
            Assert.Equal(1, changes);
            Assert.Equal(1, doc.History.CurrentIndex);
            byte[] values = [.. Enumerable.Range(1, 5).Select(i => { byte[] b = new byte[1]; doc.Current.Read(i * 0x10, b); return b[0]; })];
            Assert.Equal(new byte[] { 0xA1, 0, 0, 0, 0 }, values);
            doc.Redo();
            byte[] two = new byte[1];
            doc.Current.Read(0x20, two);
            Assert.Equal(0xA2, two[0]);
            Assert.All(doc.History.Entries.Skip(1), e => Assert.NotEqual(default, e.Time));
        }
    }
}
