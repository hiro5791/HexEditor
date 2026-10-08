using System.Globalization;
using HexEditor.Core.Editing;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Editing;

/// <summary>範囲を選択 (EDIT-04) の入力。</summary>
public sealed class RangeSelectionTests
{
    private const long MiB = 1024 * 1024;

    private static (Document Doc, EditorState Editor) Open(long length = MiB)
    {
        var doc = new Document(new VirtualByteSource(length), Options());
        return (doc, new EditorState(doc));
    }

    private static RangeSelectionModel Model(EditorState editor) =>
        new(new EditorExpressionContext(editor), editor.Document.Length,
            editor.HasSelection ? (editor.SelectionStart, editor.SelectionLength) : null, editor.Cursor);

    [Fact]
    public void Start_and_length_compute_the_inclusive_end()
    {
        (Document doc, EditorState editor) = Open();
        using (doc)
        {
            editor.GoTo(0x40);
            RangeSelectionModel model = Model(editor);
            Assert.Equal("0x40", model.TextOf(RangeField.Start));
            Assert.Equal(string.Empty, model.TextOf(RangeField.Length));
            Assert.False(model.CanConfirm);

            model.SetText(RangeField.Start, "0x100");
            model.SetText(RangeField.Length, "0x20");
            Assert.Equal(RangeField.End, model.Computed);
            Assert.Equal("0x11F", model.TextOf(RangeField.End));
            model.SetText(RangeField.Length, "0");
            Assert.False(model.CanConfirm);
            model.SetText(RangeField.Length, "0x20");
            Assert.Equal((0x100L, 0x20L), model.Result());
        }
    }

    [Fact]
    public void Expressions_select_the_last_sixteen_bytes()
    {
        (Document doc, EditorState editor) = Open();
        using (doc)
        {
            RangeSelectionModel model = Model(editor);
            model.SetText(RangeField.Start, "end-0x10");
            model.SetText(RangeField.End, "end-1");
            Assert.Equal(RangeField.Length, model.Computed);
            Assert.Equal(0xFFFF0, model.Start);
            Assert.Equal(0xFFFFF, model.End);
            Assert.Equal(16, model.Length);
            Assert.Equal((0xFFFF0L, 16L), model.Result());
        }
    }

    [Fact]
    public void End_beyond_the_document_is_fixed_by_clamping()
    {
        (Document doc, EditorState editor) = Open();
        using (doc)
        {
            RangeSelectionModel model = Model(editor);
            model.SetText(RangeField.Start, "0xFFF00");
            model.SetText(RangeField.End, "0xFFFFF");
            Assert.Equal(RangeFieldIssue.None, model.IssueOf(RangeField.End));
            Assert.True(model.CanConfirm);

            model.SetText(RangeField.End, "0x100000");
            Assert.Equal(RangeFieldIssue.EndBeyondEnd, model.IssueOf(RangeField.End));
            Assert.False(model.CanConfirm);
            model.ClampEndToDocument();
            Assert.Equal("0xFFFFF", model.TextOf(RangeField.End));
            Assert.True(model.CanConfirm);

            model.SetText(RangeField.Start, "0x100000");
            Assert.Equal(RangeFieldIssue.StartBeyondEnd, model.IssueOf(RangeField.Start));
            Assert.False(model.CanConfirm);
        }
    }

    [Fact]
    public void Errors_and_modes()
    {
        (Document doc, EditorState editor) = Open();
        using (doc)
        {
            editor.Select(0x10, 0x10);
            RangeSelectionModel model = Model(editor);
            Assert.Equal("0x10", model.TextOf(RangeField.Start));
            Assert.Equal("0x1F", model.TextOf(RangeField.End));
            Assert.Equal("0x10", model.TextOf(RangeField.Length));

            model.SetText(RangeField.Start, "0x10");
            model.SetText(RangeField.End, "0x5");
            Assert.Equal(RangeFieldIssue.EndBeforeStart, model.IssueOf(RangeField.Length));
            Assert.Equal(RangeFieldIssue.EndBeforeStart, model.IssueOf(RangeField.End));
            model.SetText(RangeField.End, "0x20 +");
            Assert.Equal(RangeFieldIssue.Expression, model.IssueOf(RangeField.End));
            Assert.NotNull(model.ErrorOf(RangeField.End));

            model.SetText(RangeField.Start, "0x100");
            model.SetText(RangeField.Length, "4");
            model.Mode = RangeSelectionMode.Extend;
            Assert.Equal((0x10L, 0xF4L), model.Result((0x10, 0x10)));
        }
    }

    // ---- TC-EDIT-04-04 ----

    [Theory]
    [Trait(TC, "TC-EDIT-04-04")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    [InlineData("ja-JP")]
    public void Expressions_do_not_depend_on_the_culture(string culture)
    {
        CultureInfo before = CultureInfo.CurrentCulture, beforeUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);
            (Document doc, EditorState editor) = Open();
            using (doc)
            {
                var context = new EditorExpressionContext(editor);
                Assert.Equal(1536, ExpressionEvaluator.Evaluate("1.5K", context));
                Assert.Equal(4096, ExpressionEvaluator.Evaluate("0x1_000", context));
                Assert.Equal(4096, ExpressionEvaluator.Evaluate("4K", context));
                var error = Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate("1,5K", context));
                Assert.Equal(1, error.Position);

                // 範囲を選択の欄でも同じ。
                RangeSelectionModel model = Model(editor);
                model.SetText(RangeField.Length, "1.5K");
                Assert.Equal(1536, model.Length);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
            CultureInfo.CurrentUICulture = beforeUi;
        }
    }
}
