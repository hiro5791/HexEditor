using HexEditor.App.ViewModels;
using HexEditor.Core.Settings;
using HexEditor.Core.View;

namespace HexEditor.App.Services;

/// <summary>
/// カーソル移動とスクロールの設定 (VIEW-26、VIEW-34)。設定画面ができるまでは settings.json (UI-23) で変える。
/// </summary>
public static class EditorSettings
{
    /// <summary>「Hex 列で ← / → をニブル単位で動かす」(VIEW-26 の仕様 5。既定 false)。</summary>
    public const string NibbleArrowKeysKey = "view.cursor.nibbleArrowKeys";

    /// <summary>「ニブルの位置を表示」(VIEW-26 の仕様 8。既定 false)。</summary>
    public const string ShowNibbleKey = "view.statusBar.showNibble";

    /// <summary>「カーソルの上下に残す行数」(VIEW-34 の仕様 1。0〜10、既定 0)。</summary>
    public const string CursorMarginKey = "view.scroll.cursorMargin";

    /// <summary>「ジャンプ先の表示位置」(VIEW-34 の仕様 2。top / third / center、既定 third)。</summary>
    public const string JumpPositionKey = "view.jump.position";

    /// <summary>設定をタブ 1 つに反映する。</summary>
    public static void Apply(SettingsStore settings, DocumentViewModel doc)
    {
        EditorState editor = doc.Editor;
        editor.NibbleArrowKeys = settings.GetBool(NibbleArrowKeysKey, false);
        editor.CursorMargin = settings.GetInt(CursorMarginKey, 0);
        editor.JumpPlacement = settings.GetString(JumpPositionKey, "third") switch
        {
            "top" => JumpPlacement.Top,
            "center" => JumpPlacement.Center,
            _ => JumpPlacement.Third,
        };
        doc.ShowNibble = settings.GetBool(ShowNibbleKey, false);
    }
}
