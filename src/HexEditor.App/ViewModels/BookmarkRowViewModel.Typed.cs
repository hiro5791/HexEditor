using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Bookmarks;

namespace HexEditor.App.ViewModels;

/// <summary>ブックマーク一覧の行の、型付きブックマークの値と位置の式の表示 (INSP-29 の仕様 2・4・8、「エラー」)。</summary>
public sealed partial class BookmarkRowViewModel
{
    /// <summary>値の列の文字列を作る関数 (ドキュメントのバイトを読んで型で解釈する。一覧の持ち主が付ける)。</summary>
    public Func<Bookmark, string>? ValueSource { get; set; }

    /// <summary>値の列 (型がなければ空)。</summary>
    public string ValueText => Bookmark.TypeId is null ? string.Empty : ValueSource?.Invoke(Bookmark) ?? string.Empty;

    /// <summary>行のツールチップ: コメントの 1 行目と、型付きなら値 (INSP-29 の仕様 2)。</summary>
    public string RowToolTip => Bookmark.TypeId is null ? CommentLine
        : CommentLine.Length == 0 ? Loc.Format("Bookmarks_ValueToolTip", Bookmark.TypeId, ValueText)
        : CommentLine + "\n" + Loc.Format("Bookmarks_ValueToolTip", Bookmark.TypeId, ValueText);

    /// <summary>位置の式を持つ (「式」の印を付ける。INSP-29 の仕様 8)。</summary>
    public bool HasExpression => Bookmark.PositionExpression is not null;

    public string ExpressionMark => HasExpression ? Loc.Get("Bookmarks_ExpressionMark") : string.Empty;

    public bool HasExpressionError => Bookmark.ExpressionError is not null;

    /// <summary>位置の式のエラーの理由 (INSP-29 の「エラー」)。</summary>
    public string ExpressionErrorText => BookmarkExpressionText.Describe(Bookmark.ExpressionError);

    // ---- 値の書き換え (INSP-29 の仕様 4) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotEditingValue))]
    public partial bool IsEditingValue { get; set; }

    public bool IsNotEditingValue => !IsEditingValue;

    [ObservableProperty]
    public partial string EditValueText { get; set; } = string.Empty;
}

/// <summary>位置の式のエラーの説明文。</summary>
public static class BookmarkExpressionText
{
    public static string Describe(BookmarkExpressionError? error) => error switch
    {
        null => string.Empty,
        { Kind: BookmarkExpressionErrorKind.Cycle } => Loc.Get("Bookmarks_ExprCycle"),
        { Kind: BookmarkExpressionErrorKind.TooDeep } => Loc.Format("Bookmarks_ExprTooDeep", BookmarkExpressions.MaxDepth),
        { Kind: BookmarkExpressionErrorKind.OutOfRange } => Loc.Format("Bookmarks_ExprOutOfRange", "0x" + error.Value.ToString("X", System.Globalization.CultureInfo.InvariantCulture)),
        { Detail: { } detail } => Loc.Format("Bookmarks_ExprInvalid", Controls.DialogParts.ExpressionError(detail)),
        _ => Loc.Format("Bookmarks_ExprInvalid", string.Empty),
    };
}
