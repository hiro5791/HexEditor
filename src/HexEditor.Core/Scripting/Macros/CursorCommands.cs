using HexEditor.Core.View;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting.Macros;

/// <summary>
/// カーソル移動のコマンド (<c>cursor.right</c> など。AUTO-27 の仕様 2: 矢印・PageUp/Down・Home/End の記録に使う)。キーでの移動と同じ動き
/// (端でとどまる) を <c>count</c> 回行う。端に達して動けなくなったら、残りの回数は行わない。
/// </summary>
public static class CursorCommands
{
    public const string Prefix = "cursor.";

    /// <summary>コマンドの ID と、マクロのコメントに使う名前。</summary>
    public static IReadOnlyList<(string Id, string Ja, string En)> All { get; } =
    [
        ("cursor.left", "左", "Left"),
        ("cursor.right", "右", "Right"),
        ("cursor.up", "上", "Up"),
        ("cursor.down", "下", "Down"),
        ("cursor.pageUp", "前のページ", "Page up"),
        ("cursor.pageDown", "次のページ", "Page down"),
        ("cursor.lineStart", "行頭", "Line start"),
        ("cursor.lineEnd", "行末", "Line end"),
        ("cursor.fileStart", "ファイルの先頭", "File start"),
        ("cursor.fileEnd", "ファイルの末尾", "File end"),
    ];

    public static bool Exists(string id) => All.Any(c => c.Id == id);

    public static string NameOf(string id) => All.FirstOrDefault(c => c.Id == id) is { Id: not null } c ? Msg.T(c.Ja, c.En) : id;

    /// <summary>移動する。<paramref name="count"/> は 1 以上 (大きな値は端で止まる)。</summary>
    public static void Execute(EditorState editor, string id, int count = 1)
    {
        if (!Exists(id))
        {
            throw new ArgumentError(Msg.T($"カーソル移動のコマンドではありません: {id}", $"Not a cursor command: {id}"));
        }

        count = Math.Max(1, count);
        for (int i = 0; i < count; i++)
        {
            long before = editor.Cursor;
            long topBefore = editor.TopRow;
            Step(editor, id);
            // 端に達して動けない: 残りは行わない (ファイル全体の移動は 1 回で足りる)。
            if (editor.Cursor == before && editor.TopRow == topBefore)
            {
                break;
            }
        }
    }

    private static void Step(EditorState editor, string id)
    {
        switch (id)
        {
            case "cursor.left":
                editor.MoveLeft();
                break;
            case "cursor.right":
                editor.MoveRight();
                break;
            case "cursor.up":
                editor.MoveUp();
                break;
            case "cursor.down":
                editor.MoveDown();
                break;
            case "cursor.pageUp":
                editor.PageUp();
                break;
            case "cursor.pageDown":
                editor.PageDown();
                break;
            case "cursor.lineStart":
                editor.MoveHome();
                break;
            case "cursor.lineEnd":
                editor.MoveEnd();
                break;
            case "cursor.fileStart":
                editor.MoveToStart();
                break;
            default:
                editor.MoveToEnd();
                break;
        }
    }
}
