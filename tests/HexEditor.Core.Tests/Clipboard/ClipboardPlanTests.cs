using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Clipboard;

/// <summary>
/// EDIT-22 の仕様 4〜6: システムのクリップボードに入れる形式と大きさの上限。システムのクリップボードには触らず、
/// アプリがコピーで使う判定 (<see cref="ClipboardPlan"/>) を、実際のドキュメントの選択範囲で確かめる。
/// </summary>
public sealed class ClipboardPlanTests
{
    private const long MiB = TestDataCatalog.MiB;

    /// <summary>Hex 列で選択範囲をコピーしたときの形式 (アプリの ClipboardService.CopyAsync と同じ判定)。</summary>
    private static ClipboardPlan CopyFromHexColumn(EditorState editor)
    {
        Assert.Equal(ActiveColumn.Hex, editor.ActiveColumn);
        return ClipboardPlan.For(editor.SelectionLength, () => ClipboardPlan.HexTextLength(editor.SelectionLength));
    }

    [Fact]
    [Trait("TC", "TC-EDIT-22-04")]
    public void ClipboardSizeLimitBoundaries()
    {
        using var doc = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-MARKERS-1G")), Options());
        var editor = new EditorState(doc) { VisibleRows = 32 };

        // 手順 1: 64 MiB ちょうど。バイナリ形式はあり、テキスト形式 (64 MiB × 3 文字) は上限を超えるので入れず、InfoBar で知らせる。
        editor.Select(0, 64 * MiB);
        ClipboardPlan plan = CopyFromHexColumn(editor);
        Assert.True(plan.Binary);
        Assert.Equal(ClipboardTextKind.None, plan.Text);
        Assert.True(plan.TextOmitted);
        Assert.False(plan.InAppOnly);

        // 手順 2: 64 MiB + 1。バイナリ形式はなく、Meta と 1 行のテキストだけ (HexEditor 内でだけ貼り付けられる)。
        editor.Select(0, 64 * MiB + 1);
        plan = CopyFromHexColumn(editor);
        Assert.False(plan.Binary);
        Assert.Equal(ClipboardTextKind.TooLargeLine, plan.Text);
        Assert.True(plan.InAppOnly);

        // 手順 3: 11,184,811 バイト。Hex 文字列は 33,554,432 文字 = 67,108,864 バイトでちょうど上限。両方の形式を入れる。
        editor.Select(0, 11_184_811);
        plan = CopyFromHexColumn(editor);
        Assert.True(plan.Binary);
        Assert.Equal(ClipboardTextKind.Data, plan.Text);
        byte[] bytes = new byte[editor.SelectionLength];
        doc.Current.Read(0, bytes);
        string text = editor.FormatForClipboard(bytes);
        Assert.Equal(33_554_432, text.Length);
        Assert.Equal(ClipboardPlan.HexTextLength(bytes.Length), text.Length);
        Assert.StartsWith("40 30 30 30", text, StringComparison.Ordinal);

        // 手順 4: 11,184,812 バイト。Hex 文字列は 67,108,870 バイトで上限を超えるので、テキスト形式は入れない。
        editor.Select(0, 11_184_812);
        plan = CopyFromHexColumn(editor);
        Assert.True(plan.Binary);
        Assert.Equal(ClipboardTextKind.None, plan.Text);
        Assert.True(plan.TextOmitted);
    }

    [Fact]
    public void TextColumnCountsTheDecodedCharacters()
    {
        // テキスト列では、文字コードで変換した後の文字数で判定する (1 バイト 1 文字なら 32 MiB ちょうどまで)。
        Assert.Equal(ClipboardTextKind.Data, ClipboardPlan.For(32 * MiB, () => 32 * MiB).Text);
        Assert.Equal(ClipboardTextKind.None, ClipboardPlan.For(32 * MiB + 1, () => 32 * MiB + 1).Text);
        Assert.Equal(0, ClipboardPlan.HexTextLength(0));
        Assert.Equal(2, ClipboardPlan.HexTextLength(1));
    }
}
