using HexEditor.Core.Bookmarks;

namespace HexEditor.Core.Annotations;

/// <summary>
/// ブックマークを注釈の出どころとして見せるもの (INSP-32 の仕様 2 の「ブックマーク」)。ブックマーク自体は層 7 として別に描くので、
/// これはツールチップ・注釈の列・凡例のための問い合わせに使う。非表示のグループ (INSP-27) のものは返さない。
/// </summary>
public sealed class BookmarkAnnotationSource : IAnnotationSource
{
    public const string SourceId = "bookmarks";

    private readonly BookmarkCollection _bookmarks;

    public BookmarkAnnotationSource(BookmarkCollection bookmarks)
    {
        _bookmarks = bookmarks;
        _bookmarks.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Id => SourceId;

    public AnnotationOrigin Origin => AnnotationOrigin.Bookmark;

    public string DisplayName => string.Empty;

    public event EventHandler? Changed;

    public void Query(long start, long end, List<Annotation> output)
    {
        foreach (Bookmark b in _bookmarks.Overlapping(start, end))
        {
            if (_bookmarks.IsVisible(b))
            {
                output.Add(new Annotation(b.Start, b.Length, b.Name, _bookmarks.EffectiveColor(b).ExportRgb, b.Comment) { Tag = b });
            }
        }
    }
}
