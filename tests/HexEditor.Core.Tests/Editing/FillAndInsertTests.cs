using HexEditor.Core.Editing;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Editing;

/// <summary>塗りつぶし (EDIT-29)・バイトの挿入 (EDIT-14)・ファイルサイズの変更 (EDIT-15)・ファイルの内容の挿入 (EDIT-30) の内容。</summary>
public sealed class FillAndInsertTests
{
    private static Document Doc(int length, byte value) => new(new MemoryByteSource(Enumerable.Repeat(value, length).ToArray()), Options());

    private static Document Seq(int length) => new(new MemoryByteSource(Enumerable.Range(0, length).Select(i => (byte)i).ToArray()), Options());

    private static ContentBuilder Builder(Document doc) => ContentBuilder.For(doc);

    private static byte[] Fill(FillSpec spec, long start, long length, int size = 64)
    {
        using Document doc = Doc(size, 0xAA);
        var editor = new EditorState(doc);
        EditCommands.Overwrite(editor, start, Builder(doc).Build(spec, start, length, doc));
        Assert.Equal(size, doc.Length);
        return ReadAll(doc.Current);
    }

    // ---- TC-EDIT-29-02 ----

    [Fact]
    [Trait(TC, "TC-EDIT-29-02")]
    public void Pattern_from_range_start_repeats_and_cuts()
    {
        byte[] result = Fill(new FillSpec { Kind = FillKind.HexPattern, Pattern = [0xDE, 0xAD] }, 0x10, 5);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xDE, 0xAD, 0xDE, 0xAA }, result[0x10..0x16]);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-29-02")]
    public void Pattern_counted_from_offset_zero()
    {
        byte[] result = Fill(new FillSpec { Kind = FillKind.HexPattern, Pattern = [0x00, 0x11, 0x22, 0x33], Origin = PatternOrigin.OffsetZero }, 0x03, 3);
        Assert.Equal(new byte[] { 0x33, 0x00, 0x11 }, result[0x03..0x06]);
    }

    [Theory]
    [Trait(TC, "TC-EDIT-29-02")]
    [InlineData(CounterOverflow.Wrap, new byte[] { 0xFE, 0xFF, 0x00, 0x01 })]
    [InlineData(CounterOverflow.Saturate, new byte[] { 0xFE, 0xFF, 0xFF, 0xFF })]
    public void Counter_wraps_or_saturates(CounterOverflow overflow, byte[] expected)
    {
        byte[] result = Fill(new FillSpec { Kind = FillKind.Counter, CounterStart = 0xFE, CounterStep = 1, CounterOverflow = overflow }, 0x10, 4);
        Assert.Equal(expected, result[0x10..0x14]);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-29-02")]
    public void Same_seed_gives_same_random_bytes()
    {
        var spec = new FillSpec { Kind = FillKind.Random, Seed = 12345 };
        byte[] first = Fill(spec, 0, 64);
        byte[] second = Fill(spec, 0, 64);
        byte[] other = Fill(spec with { Seed = 12346 }, 0, 64);
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);

        // 範囲付きの乱数 (xoshiro256**) も同じシードなら同じ内容で、範囲に収まる。
        var ranged = new FillSpec { Kind = FillKind.Random, Seed = 12345, RandomMin = 0x30, RandomMax = 0x39 };
        byte[] a = Fill(ranged, 0, 64);
        Assert.Equal(a, Fill(ranged, 0, 64));
        Assert.All(a, b => Assert.InRange(b, (byte)0x30, (byte)0x39));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-29-02")]
    public void Pattern_without_repeat_writes_once()
    {
        byte[] result = Fill(new FillSpec { Kind = FillKind.HexPattern, Pattern = [0xDE, 0xAD], Repeat = false }, 0x10, 5);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xAA, 0xAA, 0xAA }, result[0x10..0x15]);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-29-02")]
    public void Big_endian_two_byte_counter_wraps()
    {
        byte[] result = Fill(new FillSpec
        {
            Kind = FillKind.Counter,
            CounterStart = 0xFFFE,
            CounterStep = 1,
            CounterSize = 2,
            CounterBigEndian = true,
        }, 0x20, 6);
        Assert.Equal(new byte[] { 0xFF, 0xFE, 0xFF, 0xFF, 0x00, 0x00 }, result[0x20..0x26]);
    }

    [Fact]
    public void Text_pattern_with_escapes_and_signed_counter()
    {
        Assert.Equal("a\r\nb\t\\", FillText.Unescape("a\\r\\nb\\t\\\\"));
        byte[] counter = Fill(new FillSpec
        {
            Kind = FillKind.Counter,
            CounterStart = 126,
            CounterStep = 1,
            CounterSigned = true,
            CounterOverflow = CounterOverflow.Saturate,
        }, 0, 3);
        Assert.Equal(new byte[] { 0x7E, 0x7F, 0x7F }, counter[..3]);

        byte[] down = Fill(new FillSpec { Kind = FillKind.Counter, CounterStart = 1, CounterStep = -1 }, 0, 3);
        Assert.Equal(new byte[] { 0x01, 0x00, 0xFF }, down[..3]);
    }

    [Fact]
    public void Short_counters_and_patterns_are_generated_pieces()
    {
        using Document doc = Doc(16, 0);
        ContentBuilder builder = Builder(doc);
        Assert.Equal(EditContentKind.Pattern, builder.Build(new FillSpec { Kind = FillKind.Counter }, 0, 1L << 40).Kind);
        Assert.Equal(EditContentKind.Pattern, builder.Build(new FillSpec { Kind = FillKind.HexPattern, Pattern = [1, 2, 3] }, 0, 1L << 40).Kind);
        Assert.Equal(EditContentKind.Random, builder.Build(new FillSpec { Kind = FillKind.Random, Seed = 1 }, 0, 1L << 40).Kind);
        Assert.False(ContentBuilder.NeedsGeneration(new FillSpec { Kind = FillKind.Counter }, 1L << 40));
        Assert.True(ContentBuilder.NeedsGeneration(new FillSpec { Kind = FillKind.Counter, CounterSize = 4 }, 1L << 40));
        Assert.True(ContentBuilder.NeedsGeneration(new FillSpec { Kind = FillKind.CryptoRandom }, 1L << 30));
    }

    [Fact]
    public void Fill_beyond_the_end_extends_resizable_documents_only()
    {
        using Document doc = Doc(16, 0xAA);
        var editor = new EditorState(doc);
        EditCommands.Overwrite(editor, 12, EditContent.Fill(0x11, 8));
        Assert.Equal(20, doc.Length);
        Assert.Equal(12, editor.SelectionStart);
        Assert.Equal(8, editor.SelectionLength);

        using var fixedDoc = new Document(new MemoryByteSource(new byte[16], capabilities: SourceCapabilities.CanWrite), Options());
        var ex = Assert.Throws<RangeEditException>(() => EditCommands.Overwrite(new EditorState(fixedDoc), 12, EditContent.Fill(0x11, 8)));
        Assert.Equal(RangeEditError.FixedLength, ex.Error);
    }

    [Fact]
    public void Generated_data_over_one_mebibyte_goes_to_a_temp_file_and_cancel_removes_it()
    {
        using Document doc = Doc(16, 0);
        string folder = Path.Combine(doc.Options.TempDirectory, doc.Id.ToString("N"));
        ContentBuilder builder = Builder(doc);
        var spec = new FillSpec { Kind = FillKind.CryptoRandom };
        using (EditContent content = builder.Build(spec, 0, 3 * 1024 * 1024))
        {
            Assert.True(content.HasTemporaryFile);
            Assert.Single(Directory.GetFiles(folder, "fill-*.bin"));
        }

        Assert.Empty(Directory.GetFiles(folder, "fill-*.bin"));

        var op = new LongRunningOperation("fill", OperationKind.ModifiesDocument, doc, null, TimeProvider.System);
        op.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => builder.Build(spec, 0, 3 * 1024 * 1024, doc, op));
        Assert.Empty(Directory.GetFiles(folder, "fill-*.bin"));
        Assert.Equal(0, doc.History.CurrentIndex);
    }

    [Fact]
    public void Temp_space_is_checked_before_generating()
    {
        using Document doc = Doc(16, 0);
        var builder = ContentBuilder.For(doc, new FixedVolume(1024));
        var ex = Assert.Throws<TempSpaceException>(() => builder.Build(new FillSpec { Kind = FillKind.CryptoRandom }, 0, 2 * 1024 * 1024));
        Assert.Equal(2 * 1024 * 1024, ex.Required);
        Assert.Equal(1024, ex.Available);
    }

    private sealed class FixedVolume(long free) : IVolumeInfoProvider
    {
        public VolumeInfo? GetVolume(string folder) => new("T:", "NTFS", free);
    }

    // ---- EDIT-14 ----

    [Fact]
    [Trait(TC, "TC-EDIT-14-02")]
    public void Insert_count_upper_limit()
    {
        using Document doc = Doc(1024, 0x55);
        var editor = new EditorState(doc);
        long max = long.MaxValue - 1024;
        Assert.Equal(max, EditCommands.MaxInsertCount(doc));

        // 1〜2. 上限ちょうど。
        EditCommands.Insert(editor, 0, EditContent.Fill(0, max));
        Assert.Equal(long.MaxValue, doc.Length);
        doc.Undo();
        Assert.Equal(1024, doc.Length);

        // 3. 上限 + 1、4. 0 は入力エラーで、ドキュメントは変わらない。
        Assert.Equal(RangeEditError.CountTooLarge, EditCommands.ValidateInsert(doc, 0, max + 1));
        Assert.Equal(RangeEditError.CountTooSmall, EditCommands.ValidateInsert(doc, 0, 0));
        Assert.Throws<RangeEditException>(() => EditCommands.Insert(editor, 0, EditContent.Fill(0, max + 1)));
        Assert.Equal(1024, doc.Length);
        Assert.False(doc.History.CanRedo && doc.History.CurrentIndex > 0);
    }

    [Fact]
    public void Insert_selects_the_inserted_range_and_undoes_in_one_step()
    {
        using Document doc = Seq(32);
        var editor = new EditorState(doc);
        EditCommands.Insert(editor, 0x10, EditContent.Pattern([0xDE, 0xAD], 5));
        Assert.Equal(37, doc.Length);
        Assert.Equal((0x10L, 5L), (editor.SelectionStart, editor.SelectionLength));
        Assert.Equal(new byte[] { 0x0F, 0xDE, 0xAD, 0xDE, 0xAD, 0xDE, 0x10 }, Read(doc.Current, 0x0F, 7));
        doc.Undo();
        Assert.Equal(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), ReadAll(doc.Current));

        EditCommands.Insert(editor, 4, EditContent.Fill(0, 2), selectInserted: false);
        Assert.False(editor.HasSelection);
        Assert.Equal(6, editor.Cursor);
    }

    // ---- EDIT-15 ----

    [Fact]
    public void Resize_extends_with_content_truncates_and_ignores_same_length()
    {
        using Document doc = Seq(1024);
        var editor = new EditorState(doc);
        Assert.Equal(0, EditCommands.Resize(editor, 4L << 30));
        Assert.Equal(4L << 30, doc.Length);
        Assert.Equal(new byte[] { 0xFF, 0x00 }, Read(doc.Current, 0x3FF, 2));
        Assert.Equal(new byte[] { 0x00 }, Read(doc.Current, 0xFFFFFFFF, 1));
        int entries = doc.History.Count;

        // 同じ長さなら編集履歴に残さない (仕様 5)。
        Assert.Equal(0, EditCommands.Resize(editor, 4L << 30));
        Assert.Equal(entries, doc.History.Count);

        Assert.Equal((4L << 30) - 100, EditCommands.Resize(editor, 100));
        Assert.Equal(100, doc.Length);
        doc.Undo();
        doc.Undo();
        Assert.Equal(1024, doc.Length);

        EditCommands.Resize(editor, 1030, EditContent.Fill(0xEE, 6));
        Assert.Equal(new byte[] { 0xFF, 0xEE }, Read(doc.Current, 1023, 2));
        Assert.Equal(RangeEditError.NegativeLength, EditCommands.ValidateResize(doc, -1));
    }

    [Fact]
    public void Truncate_at_cursor()
    {
        using Document doc = Seq(1024);
        var editor = new EditorState(doc);
        editor.GoTo(0x100);
        Assert.True(EditCommands.CanTruncateAtCursor(editor));
        Assert.Equal(768, EditCommands.TruncateAtCursor(editor));
        Assert.Equal(0x100, doc.Length);
        Assert.False(EditCommands.CanTruncateAtCursor(editor));
        doc.Undo();
        Assert.Equal(1024, doc.Length);
    }

    [Fact]
    public void Fixed_length_documents_reject_resize_and_insert()
    {
        using var doc = new Document(new MemoryByteSource(new byte[16], capabilities: SourceCapabilities.CanWrite), Options());
        var editor = new EditorState(doc);
        Assert.Equal(RangeEditError.FixedLength, EditCommands.ValidateResize(doc, 8));
        Assert.Equal(RangeEditError.FixedLength, EditCommands.ValidateInsert(doc, 0, 1));
        Assert.False(EditCommands.CanTruncateAtCursor(editor));
    }

    // ---- EDIT-30 ----

    [Fact]
    public void File_content_is_copied_so_later_changes_do_not_show()
    {
        string dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "insertfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "src.bin");
        File.WriteAllBytes(src, Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray());
        using Document doc = Doc(64, 0);
        var editor = new EditorState(doc);
        // 1 MiB 以下はメモリにコピーする。
        EditContent content = Builder(doc).Build(new FillSpec { Kind = FillKind.File, FilePath = src, FileOffset = 0x100, FileLength = 0x80, Repeat = false }, 0, 0x80);
        EditCommands.Insert(editor, 0x10, content);
        File.WriteAllBytes(src, new byte[16]);
        Assert.Equal(64 + 0x80, doc.Length);
        Assert.Equal(Enumerable.Range(0, 0x80).Select(i => (byte)i).ToArray(), Read(doc.Current, 0x10, 0x80));
        Assert.Equal((0x10L, 0x80L), (editor.SelectionStart, editor.SelectionLength));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Same_file_refers_to_the_original_data()
    {
        string dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "samefile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "a.bin");
        File.WriteAllBytes(path, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        using (var doc = new Document(FileByteSource.Open(path), Options()))
        {
            var editor = new EditorState(doc);
            doc.Overwrite(0, [0xFF]);
            EditContent content = Builder(doc).Build(new FillSpec { Kind = FillKind.File, FilePath = path, Repeat = false }, 0, 256, doc);
            Assert.Equal(EditContentKind.Original, content.Kind);
            EditCommands.Insert(editor, 256, content);
            // 編集前の元データ (0x00 のまま) を参照する。
            Assert.Equal(0x00, Read(doc.Current, 256, 1)[0]);
            Assert.Equal(0xFF, Read(doc.Current, 0, 1)[0]);
        }

        Directory.Delete(dir, true);
    }

    [Fact]
    public void Io_errors_while_copying_abort_without_changes()
    {
        using Document doc = Doc(64, 0);
        var faulty = new FaultyByteSource(new VirtualByteSource(3 * 1024 * 1024));
        faulty.AddReadError(2 * 1024 * 1024, 4096);
        var builder = ContentBuilder.For(doc, openFile: _ => faulty);
        Assert.Throws<IOException>(() => builder.Build(new FillSpec { Kind = FillKind.File, FilePath = "x.bin", Repeat = false }, 0, faulty.Length));
        Assert.Equal(0, doc.History.CurrentIndex);
        string folder = Path.Combine(doc.Options.TempDirectory, doc.Id.ToString("N"));
        Assert.True(!Directory.Exists(folder) || Directory.GetFiles(folder, "fill-*.bin").Length == 0);
    }

    // ---- EDIT-16 ----

    [Fact]
    public void Read_only_documents_reject_every_change_but_keep_unsaved_edits()
    {
        using Document doc = Seq(32);
        var editor = new EditorState(doc);
        doc.Overwrite(0, [0xFF]);
        editor.ReadOnly = true;
        Assert.Equal(ReadOnlyReason.User, doc.ReadOnlyReason);
        Assert.Equal(ReadOnlyRelease.Immediate, doc.ReadOnlyRelease);
        Assert.True(doc.IsModified);

        Assert.Equal(EditResult.NotEditable, editor.TypeHexDigit('4'));
        Assert.Equal(EditResult.NotEditable, editor.Paste([1, 2], overwrite: false));
        Assert.Throws<DocumentReadOnlyException>(() => doc.Overwrite(1, [1]));
        Assert.Throws<DocumentReadOnlyException>(() => doc.InsertContent(0, EditContent.Fill(0, 4)));
        Assert.Throws<DocumentReadOnlyException>(() => doc.Undo());
        editor.Undo();
        Assert.Equal(0xFF, Read(doc.Current, 0, 1)[0]);
        Assert.Throws<RangeEditException>(() => EditCommands.Resize(editor, 4));

        editor.ReadOnly = false;
        Assert.False(doc.IsReadOnly);
        editor.Undo();
        Assert.Equal(0x00, Read(doc.Current, 0, 1)[0]);
    }

    [Theory]
    [InlineData(ReadOnlyReason.User, ReadOnlyRelease.Immediate)]
    [InlineData(ReadOnlyReason.FileAttribute, ReadOnlyRelease.NeedsConfirmation)]
    [InlineData(ReadOnlyReason.SharingViolation, ReadOnlyRelease.NeedsConfirmation)]
    [InlineData(ReadOnlyReason.AccessDenied, ReadOnlyRelease.NeedsConfirmation)]
    [InlineData(ReadOnlyReason.Device, ReadOnlyRelease.NeedsConfirmation)]
    [InlineData(ReadOnlyReason.NoWriteTarget, ReadOnlyRelease.NotAllowed)]
    [InlineData(ReadOnlyReason.WriteProtectionMode, ReadOnlyRelease.NotAllowed)]
    [InlineData(ReadOnlyReason.ReadOnlyMedia, ReadOnlyRelease.NotAllowed)]
    public void Release_depends_on_the_reason(ReadOnlyReason reason, ReadOnlyRelease expected)
    {
        using Document doc = Seq(4);
        int changed = 0;
        doc.ReadOnlyChanged += (_, _) => changed++;
        doc.SetReadOnly(reason);
        Assert.Equal(expected, doc.ReadOnlyRelease);
        Assert.Equal(1, changed);
        doc.SetReadOnly(reason);
        Assert.Equal(1, changed);
    }
}
