using System.Text.Json.Nodes;

namespace HexEditor.Core.Commands;

/// <summary>ツールバーの構成 (UI-04)。設定 <c>ui.toolbar.items</c> にコマンド ID の配列で保存する。</summary>
public static class ToolbarItems
{
    public const string VisibleKey = "ui.toolbar.visible";
    public const string ItemsKey = "ui.toolbar.items";

    /// <summary>既定のボタン (UI-04 の仕様 1)。</summary>
    public static readonly IReadOnlyList<string> Default =
    [
        "file.new", "file.open", "file.save", "edit.undo", "edit.redo", "edit.cut", "edit.copy", "edit.paste",
        "search.find", "go.goTo", "edit.toggleInsert", "view.panel.inspector",
    ];

    public static JsonArray DefaultJson() => new([.. Default.Select(id => (JsonNode?)id)]);

    /// <summary>設定の値を読む。配列でなければ既定。</summary>
    public static IReadOnlyList<string> Parse(JsonNode? value) =>
        value is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : null).OfType<string>().ToList()
            : Default;

    /// <summary>表示するボタンと、見つからないコマンドの数 (UI-04 の「エラー」)。</summary>
    public static (IReadOnlyList<string> Shown, int Missing) Resolve(IReadOnlyList<string> items, CommandCatalog catalog)
    {
        var shown = items.Where(catalog.Contains).Select(catalog.Canonical).ToList();
        return (shown, items.Count - shown.Count);
    }

    /// <summary>アイコンのないコマンドは表示名の先頭 2 文字をアイコン代わりにする (UI-04 の仕様 2)。</summary>
    public static string FallbackIcon(string displayName)
    {
        var info = new System.Globalization.StringInfo(displayName.Trim());
        return info.LengthInTextElements <= 2 ? info.String : info.SubstringByTextElements(0, 2);
    }
}
