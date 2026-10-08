using System.Text.Json.Nodes;
using HexEditor.Core.Commands;

namespace HexEditor.Core.Settings;

/// <summary>
/// 組み込みの設定項目 (UI-22)。各機能の仕様書の「設定の一覧」の項目を、機能を作るときにここに足す。
/// </summary>
/// <remarks>
/// 足すときは、ここに 1 行足し、<c>Strings/en</c> と <c>Strings/ja</c> に名前 (<c>Set_&lt;キーの . を _ に&gt;</c>)、説明
/// (<c>SetDesc_…</c>)、選択肢の表示名 (<c>SetOpt_…_&lt;値&gt;</c>) を書き、<c>settings.schema.json</c> を作り直す
/// (<c>HEXEDITOR_UPDATE_SCHEMA=1 dotnet test --filter SettingsSchema</c>)。
/// </remarks>
public static class BuiltInSettings
{
    /// <summary>表示言語の選択肢 (UI-43、00-overview.md 5.1)。</summary>
    public static readonly string[] Languages =
    [
        "system", "en", "zh-Hans", "zh-Hant", "ja", "ko", "id", "vi", "th", "de", "fr", "es", "pt",
        "it", "ru", "uk", "pl", "cs", "hu", "ro", "el", "ar", "tr", "fa",
    ];

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        // ---- 全般 ----
        new("ui.toolbar.visible", SettingCategories.General, SettingKind.Bool, false) { Order = 10 },
        new(ToolbarItems.ItemsKey, SettingCategories.General, SettingKind.List, ToolbarItems.DefaultJson()) { Order = 11, ShowInPage = false },
        new("ui.statusBar.visible", SettingCategories.General, SettingKind.Bool, true) { Order = 20 },
        new("ui.statusBar.items", SettingCategories.General, SettingKind.String, string.Empty) { Order = 21, ShowInPage = false },

        // ---- 外観 ----
        new("ui.theme", SettingCategories.Appearance, SettingKind.Choice, "system") { Options = ["system", "light", "dark"], Order = 10 },
        new("ui.backdrop", SettingCategories.Appearance, SettingKind.Choice, "mica") { Options = ["mica", "micaAlt", "none"], Order = 20 },

        // ---- 表示 ----
        new("view.cursor.nibbleArrowKeys", SettingCategories.View, SettingKind.Bool, false) { Order = 10 },
        new("view.statusBar.showNibble", SettingCategories.View, SettingKind.Bool, false) { Order = 20 },
        new("view.scroll.cursorMargin", SettingCategories.View, SettingKind.Int, 0) { Min = 0, Max = 10, Order = 30 },
        new("view.jump.position", SettingCategories.View, SettingKind.Choice, "third") { Options = ["top", "third", "center"], Order = 40 },

        // ---- ファイルと保存 ----
        new("storage.tempDirectory", SettingCategories.Files, SettingKind.String, string.Empty) { Exportable = false, RequiresRestart = true, Order = 10 },

        // ---- 言語 ----
        new("ui.language", SettingCategories.Language, SettingKind.Choice, "system") { Options = Languages, RequiresRestart = true, Order = 10 },

        // ---- 詳細 ----
        new("log.level", SettingCategories.Advanced, SettingKind.Choice, "info") { Options = ["info", "debug"], Order = 10 },
        new("diagnostics.writeMiniDump", SettingCategories.Advanced, SettingKind.Bool, false) { Order = 20 },
    ];

    public static JsonNode? DefaultOf(string key) => All.FirstOrDefault(s => s.Key == key)?.Default?.DeepClone();
}
