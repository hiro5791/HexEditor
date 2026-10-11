using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Core.Files;
using HexEditor.Core.Selection;
using HexEditor.Core.Tests.Editing;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.View;

namespace HexEditor.Core.Tests.Files;

/// <summary>
/// 保存を UI スレッドの外で間引いて行う部品と、その時点の内容を写し取った書き込み (ユーザークリップボード (EDIT-28 の仕様 6)・選択セット (EDIT-09))。
/// </summary>
public sealed class DebouncedWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HexEditorTests", "debounce-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Only_the_last_request_is_written_after_the_delay()
    {
        var time = new FakeTimerProvider();
        var written = new List<int>();
        using var writer = new DebouncedWriter(TimeSpan.FromMilliseconds(300), ex => throw ex, time);
        writer.Request(() => written.Add(1));
        time.Advance(TimeSpan.FromMilliseconds(200));
        writer.Request(() => written.Add(2));
        time.Advance(TimeSpan.FromMilliseconds(200));

        // 最後の要求から 300 ms 経つまでは書かない。
        Assert.True(writer.HasPending);
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.False(writer.HasPending);
        await writer.FlushAsync();
        Assert.Equal([2], written);
    }

    [Fact]
    public async Task Flush_writes_the_pending_request_at_once_and_reports_errors()
    {
        var time = new FakeTimerProvider();
        var errors = new List<Exception>();
        var written = new List<int>();
        using var writer = new DebouncedWriter(TimeSpan.FromSeconds(10), errors.Add, time);
        writer.Request(() => throw new IOException("disk"));
        await writer.FlushAsync();
        writer.Request(() => written.Add(3));
        await writer.FlushAsync();

        // 書けなかった書き込みは知らせ、次の書き込みは続ける。
        Assert.IsType<IOException>(Assert.Single(errors));
        Assert.Equal([3], written);
    }

    [Fact]
    public void Captured_user_clipboard_save_writes_the_state_at_capture_time()
    {
        (Document doc, EditorState _) = MultiSelectionTests.Create(0x100);
        using (doc)
        using (var clipboards = new UserClipboards { Persist = true })
        {
            clipboards.Set(1, ClipboardEntry.Capture(doc, 0x10, 4));
            clipboards.SetName(1, "first");
            Action save = clipboards.CaptureSave(_folder);

            // 写した後に変えても、書くのは写した時点の内容。
            clipboards.SetName(1, "changed");
            clipboards.Set(2, ClipboardEntry.Capture(doc, 0x20, 2));
            save();

            using var loaded = new UserClipboards { Persist = true };
            loaded.Load(_folder);
            Assert.Equal("first", loaded.NameOf(1));
            Assert.Equal(new byte[] { 0x10, 0x11, 0x12, 0x13 }, loaded.Get(1)!.Data);
            Assert.Null(loaded.Get(2));
        }
    }

    [Fact]
    public void Captured_selection_set_save_writes_the_sets_at_capture_time()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            var sets = new SelectionSetCollection();
            s.Select(0x10, 4);
            sets.Save("a", s.CaptureSelection());
            var store = new DocumentDataStore(_folder);
            string path = Path.Combine(_folder, "doc.bin");
            Action save = sets.CaptureSave(store, path, null);
            sets.Save("b", s.CaptureSelection());
            save();
            Assert.Equal(["a"], SelectionSetCollection.Load(store, path).Sets.Select(x => x.Name));
        }
    }

    [Fact]
    public void Selection_set_built_in_the_background_matches_set_selections()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            // 末尾を越える要素は切り詰め、重なる要素は結合し、上限で打ち切る。
            ByteRange[] ranges = [new(0x10, 4), new(0x12, 4), new(0x40, 2), new(0xF0, 0x40), new(0x200, 1)];
            (RangeSet set, bool truncated) = EditorState.BuildSelectionSet(ranges, doc.Length, maxElements: 1000);
            Assert.False(truncated);
            Assert.Equal([new ByteRange(0x10, 6), new ByteRange(0x40, 2), new ByteRange(0xF0, 0x10)], set.ToList());
            Assert.Equal(SelectionResult.Done, s.SetSelectionSet(set));
            Assert.Equal(3, s.SelectedRangeCount);

            (RangeSet cut, bool wasCut) = EditorState.BuildSelectionSet(ranges, doc.Length, maxElements: 2);
            Assert.True(wasCut);
            Assert.Equal(2, cut.Count);
        }
    }
}
