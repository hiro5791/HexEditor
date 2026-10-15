#if HEX_TEST_HOOKS
using System.Diagnostics;
using System.Text.Json.Nodes;
using HexEditor.App.ViewModels;
using HexEditor.Core.Bookmarks;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の、型付きブックマークと位置の式 (INSP-29) の命令 (テスト方針 7.2)。
/// <list type="bullet">
/// <item><c>bookmarksTyped</c>: 各ブックマークの開始・長さ・型・値の列・「式」の印・警告の理由と、再評価の記録 (最後の編集から再評価までの ms)。</item>
/// <item><c>bookmarkEditorType</c>: 編集のフライアウトの「型」で型を選ぶ (<c>type</c> が null なら型なし)。</item>
/// <item><c>bookmarkValueEdit</c>: 一覧の値の列を入力欄にして文字列を入れ、Enter と同じ確定をする。</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private JsonObject? HandleBookmarkPhase3TestCommand(string cmd, JsonObject request) => cmd switch
    {
        "bookmarksTyped" => TestBookmarksTyped(),
        "bookmarkEditorType" => TestBookmarkEditorType(request),
        "bookmarkValueEdit" => TestBookmarkValueEdit(request),
        _ => HandleVisualizerTestCommand(cmd, request),
    };

    private JsonObject TestBookmarksTyped()
    {
        var items = new JsonArray();
        if (CurrentAnnotations() is { } a)
        {
            foreach (Bookmark b in a.Bookmarks.All)
            {
                BookmarkRowViewModel row = _bookmarksVm.Rows.Row(b);
                items.Add(new JsonObject
                {
                    ["name"] = b.Name,
                    ["start"] = b.Start,
                    ["length"] = b.Length,
                    ["type"] = b.TypeId,
                    ["value"] = row.ValueText,
                    ["toolTip"] = row.RowToolTip,
                    ["expression"] = b.PositionExpression,
                    ["mark"] = row.ExpressionMark,
                    ["warning"] = row.HasExpressionError,
                    ["reason"] = row.ExpressionErrorText,
                    ["errorKind"] = b.ExpressionError?.Kind.ToString(),
                });
            }
        }

        double Ms(long from, long to) => from == 0 || to == 0 ? -1 : Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
        return new JsonObject
        {
            ["bookmarks"] = items,
            ["header"] = new JsonArray([.. (BookmarkListView?.HeaderTexts ?? []).Select(t => (JsonNode?)t)]),
            ["lastEditToRunMs"] = Ms(_lastExpressionEdit, _lastExpressionRun),
            ["runAfterEdit"] = _lastExpressionRun > _lastExpressionEdit && _lastExpressionEdit != 0,
            ["pending"] = _expressionPending.Count > 0,
            ["editorInfo"] = _bookmarkEditor?.ExpressionInfoText,
            ["valueError"] = BookmarkListView?.ValueError,
        };
    }

    private JsonObject TestBookmarkEditorType(JsonObject request)
    {
        if (_bookmarkEditor is null)
        {
            throw new InvalidOperationException("The bookmark editor is not open.");
        }

        _bookmarkEditor.ApplyType(request["type"]?.GetValue<string>());
        return new JsonObject { ["bookmark"] = _bookmarkEditor.Bookmark is { } b ? BookmarkJson(b) : null };
    }

    private JsonObject TestBookmarkValueEdit(JsonObject request)
    {
        Bookmark b = FindBookmark(request);
        var list = BookmarkListView ?? throw new InvalidOperationException("The bookmark list is not shown.");
        BookmarkRowViewModel row = _bookmarksVm.Rows.Row(b);
        bool began = list.BeginValueEdit(row);
        bool committed = began && list.CommitValueEdit(row, request["text"]!.GetValue<string>());
        return new JsonObject { ["began"] = began, ["committed"] = committed, ["error"] = list.ValueError };
    }
}
#endif
