using System.Globalization;
using HexEditor.Core.Annotations;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.View;
using HexEditor.Sdk;
using SdkRange = HexEditor.Sdk.ByteRange;

namespace HexEditor.Core.Scripting.Api;

/// <summary><c>doc.selection</c> (AUTO-10)。変更は利用者の画面に即座に反映する。</summary>
internal sealed class ScriptSelection(ScriptDocument doc) : ISelection
{
    /// <summary>マルチ選択にできる件数 (仕様の「巨大ファイル」)。</summary>
    public const int MaxResults = 1_000_000;

    private T Ui<T>(Func<EditorState, T> work)
    {
        doc.ThrowIfClosed();
        return doc.Context.Dispatcher.Invoke(() => work(doc.Host.Editor));
    }

    private SdkRange Primary => Ui(e => e.PrimaryRange is { } r ? new SdkRange(r.Start, r.Length) : new SdkRange(e.Cursor, 0));

    public long Start => Primary.Start;

    public long End => Primary.End;

    public long Length => Primary.Length;

    public IReadOnlyList<SdkRange> Ranges => Ui(e =>
        e.HasSelection ? (IReadOnlyList<SdkRange>)[.. e.SelectedRanges.Select(r => new SdkRange(r.Start, r.Length))] : []);

    public bool IsEmpty => Ui(e => !e.HasSelection);

    public bool IsRectangular => Ui(e => e.SelectionKind == SelectionKind.Rectangle);

    public void Set(RangeArg range)
    {
        SdkRange r = doc.CheckedRange(range);
        Ui(e =>
        {
            e.Select(r.Start, r.Length);
            return true;
        });
    }

    public void Add(RangeArg range)
    {
        SdkRange r = doc.CheckedRange(range);
        Ui(e => Check(e.AddSelection(r.Start, r.Length)));
    }

    public void Clear() => Ui(e =>
    {
        e.ClearSelection();
        return true;
    });

    public void SetRectangle(OffsetArg startOffset, long rows, long columns)
    {
        long start = doc.ResolveOffset(startOffset);
        if (rows < 1 || columns < 1)
        {
            throw new Sdk.ArgumentError("rows と columns は 1 以上にしてください。");
        }

        doc.CheckOffset(start, allowEnd: false);
        Ui(e =>
        {
            long end = start + (rows - 1) * e.BytesPerRow + (columns - 1);
            if (end >= e.Document.Length)
            {
                throw new Sdk.RangeError("矩形がドキュメントの外に出ます。");
            }

            e.SelectRectangle(start, end);
            return true;
        });
    }

    public void FromResults(IReadOnlyList<SdkRange> results)
    {
        if (results.Count > MaxResults)
        {
            throw new LimitError($"マルチ選択にできるのは {MaxResults:N0} 件までです ({results.Count:N0} 件)。") { Limit = "count" };
        }

        long length = doc.Snapshot.Length;
        foreach (SdkRange r in results)
        {
            if (r.Start < 0 || r.Length < 0 || r.End > length)
            {
                throw new Sdk.RangeError($"範囲がドキュメントの外です: 0x{r.Start:X}+0x{r.Length:X}");
            }
        }

        Ui(e => Check(e.SetSelections(results.Select(r => new Selection.ByteRange(r.Start, r.Length)))));
    }

    private static bool Check(SelectionResult result) => result switch
    {
        SelectionResult.TooManyElements or SelectionResult.Truncated or SelectionResult.TooManyCarets => throw new LimitError("選択できる範囲の数の上限を超えました。") { Limit = "count" },
        _ => true,
    };
}

/// <summary><c>doc.highlights</c> (AUTO-07)。一時的な強調表示。スクリプトの終了で消える。</summary>
internal sealed class ScriptHighlights(ScriptDocument doc) : IHighlights
{
    public void Add(RangeArg range, string? color = null, string? label = null)
    {
        SdkRange r = doc.CheckedRange(range);
        uint? rgb = color is null ? null : ScriptColors.Rgb(color);
        AnnotationSet set = doc.Context.HighlightsFor(doc.Document);
        doc.Context.Dispatcher.Invoke(() => set.Add(new Annotation(r.Start, r.Length, label ?? "", rgb)));
    }

    public void Clear()
    {
        AnnotationSet set = doc.Context.HighlightsFor(doc.Document);
        doc.Context.Dispatcher.Invoke(set.Clear);
    }
}

/// <summary>
/// 名前付きの色 (AUTO-11 の仕様 3)。ブックマークの 8 色の一覧 (05 の INSP-24 の仕様 4) の名前と <c>#RRGGBB</c>。
/// テーマごとの実際の色はアプリが決める。
/// </summary>
public static class ScriptColors
{
    /// <summary>色の一覧の番号 1〜8 の名前。</summary>
    public static IReadOnlyList<string> Names { get; } = ["red", "orange", "yellow", "green", "teal", "blue", "purple", "pink"];

    public static BookmarkColor Bookmark(string name)
    {
        int index = IndexOf(name);
        if (index > 0)
        {
            return BookmarkColor.Palette(index);
        }

        return BookmarkColor.ParseHex(name) ?? throw Unknown(name);
    }

    public static uint Rgb(string name)
    {
        int index = IndexOf(name);
        return index > 0 ? BookmarkColor.Palette(index).ExportRgb : BookmarkColor.ParseHex(name)?.ExportRgb ?? throw Unknown(name);
    }

    /// <summary>ブックマークの色の名前 (一覧の色は名前、任意の色は <c>#RRGGBB</c>)。</summary>
    public static string NameOf(BookmarkColor color) =>
        color.IsCustom ? "#" + color.Rgb.ToString("X6", CultureInfo.InvariantCulture) : Names[color.PaletteIndex - 1];

    private static int IndexOf(string name)
    {
        for (int i = 0; i < Names.Count; i++)
        {
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static Sdk.ArgumentError Unknown(string name) =>
        new($"色の名前が不明です: {name} ({string.Join(", ", Names)}、または #RRGGBB)");
}
