using System.Text.Json.Nodes;
using HexEditor.Core.Commands;
using HexEditor.Core.Files;
using HexEditor.Core.Saving;
using HexEditor.Core.View;

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
        new("ui.fullScreen.showStatusBar", SettingCategories.General, SettingKind.Bool, true) { Order = 25 },
        new(StartupPlanner.RestoreOnStartupKey, SettingCategories.General, SettingKind.Choice, "always") { Options = ["always", "ask", "never"], Order = 30 },

        // 外部から開いたファイルの開き方 (UI-15 の仕様 2・4)。
        new("window.openExternalIn", SettingCategories.General, SettingKind.Choice, "lastActiveWindow") { Options = ["lastActiveWindow", "newWindow"], Order = 40 },
        new("window.launchWithoutFileOpensNewWindow", SettingCategories.General, SettingKind.Bool, false) { Order = 41 },

        // ---- 外観 ----
        new("ui.theme", SettingCategories.Appearance, SettingKind.Choice, "system") { Options = ["system", "light", "dark"], Order = 10 },
        new("ui.backdrop", SettingCategories.Appearance, SettingKind.Choice, "mica") { Options = ["mica", "micaAlt", "none"], Order = 20 },

        // Hex 表示のフォントと配色 (UI-28、UI-29)。フォントと配色は一覧から選ぶ専用の区画 (App の ViewSections) で変える。
        new("view.font.family", SettingCategories.Appearance, SettingKind.String, string.Empty) { Order = 30, ShowInPage = false },
        new("view.font.size", SettingCategories.Appearance, SettingKind.Number, 10.0) { Min = 6, Max = 72, Order = 31 },
        new("view.font.lineHeight", SettingCategories.Appearance, SettingKind.Number, 1.2) { Min = 1.0, Max = 2.0, Order = 32 },
        new("view.font.fallback", SettingCategories.Appearance, SettingKind.String, string.Empty) { Order = 33 },
        new("view.font.followTextScaling", SettingCategories.Appearance, SettingKind.Bool, true) { Order = 34 },
        new("view.colorScheme", SettingCategories.Appearance, SettingKind.String, "default") { Order = 35, ShowInPage = false },

        // ---- 表示 ----
        new("view.cursor.nibbleArrowKeys", SettingCategories.View, SettingKind.Bool, false) { Order = 10 },
        new("view.statusBar.showNibble", SettingCategories.View, SettingKind.Bool, false) { Order = 20 },
        new("view.scroll.cursorMargin", SettingCategories.View, SettingKind.Int, 0) { Min = 0, Max = 10, Order = 30 },
        new("view.jump.position", SettingCategories.View, SettingKind.Choice, "third") { Options = ["top", "third", "center"], Order = 40 },
        new("view.tooltips", SettingCategories.View, SettingKind.Bool, true) { Order = 50 },
        new("view.text.nonPrintable", SettingCategories.View, SettingKind.Choice, "dot") { Options = ["dot", "space", "controlPictures"], Order = 55 },
        new("view.text.invalidSymbol", SettingCategories.View, SettingKind.Choice, "dot") { Options = ["dot", "replacement"], Order = 56 },
        new("view.modified.keepAfterSave", SettingCategories.View, SettingKind.Bool, false) { Order = 60 },
        new("view.modified.showDeletions", SettingCategories.View, SettingKind.Bool, false) { Order = 61 },

        // ズーム (UI-08 の仕様 2・3)。全タブ共通の Hex 表示の倍率はズームの操作で変わるので、設定画面には出さない。
        new(ZoomSettings.HexScopeKey, SettingCategories.View, SettingKind.Choice, ZoomSettings.ScopeAll) { Options = [ZoomSettings.ScopeAll, ZoomSettings.ScopeTab], Order = 70 },
        new(ZoomSettings.HexKey, SettingCategories.View, SettingKind.Int, View.ZoomLevels.Default) { Min = 50, Max = 400, Order = 71, ShowInPage = false },
        new(ZoomSettings.UiKey, SettingCategories.View, SettingKind.Int, View.ZoomLevels.Default) { Min = 50, Max = 400, Order = 72 },

        // データインスペクタ (INSP-01〜INSP-19)。行の構成とプリセットはパネルの「行の設定」で変える。
        new("inspector.integerBase", SettingCategories.View, SettingKind.Choice, "decimal") { Options = ["decimal", "hex", "octal"], Order = 100 },
        new("inspector.floatFormat", SettingCategories.View, SettingKind.Choice, "shortest") { Options = ["shortest", "exponent", "hex"], Order = 101 },
        new("inspector.timeZone", SettingCategories.View, SettingKind.Choice, "local") { Options = ["local", "utc"], Order = 102 },
        new("inspector.dateFormat", SettingCategories.View, SettingKind.Choice, "regional") { Options = ["regional", "iso"], Order = 103 },
        new("inspector.digitGrouping", SettingCategories.View, SettingKind.Bool, true) { Order = 104 },
        new("inspector.highlightTarget", SettingCategories.View, SettingKind.Bool, true) { Order = 105 },
        new("inspector.useCursorWithSelection", SettingCategories.View, SettingKind.Bool, false) { Order = 106 },
        new("inspector.rows", SettingCategories.View, SettingKind.String, string.Empty) { Order = 107, ShowInPage = false },
        new("inspector.rowPresets", SettingCategories.View, SettingKind.String, "{}") { Order = 108, ShowInPage = false },

        // ---- 編集 (EDIT-23、EDIT-26、EDIT-27) ----
        new("clipboard.compatFormats", SettingCategories.Editing, SettingKind.Bool, true) { Order = 10 },
        new("edit.pasteSpecial.preferLast", SettingCategories.Editing, SettingKind.Bool, true) { Order = 20 },
        new("edit.paste.detectWithoutConfirmation", SettingCategories.Editing, SettingKind.Bool, false) { Order = 30 },

        // ---- 検索 (FIND-02、FIND-20、FIND-28) ----
        new("search.maxMatchLength", SettingCategories.Search, SettingKind.Int, Search.SearchPattern.DefaultMaxMatchLength) { Min = 1, Max = Search.SearchPattern.MaxMaxMatchLength, Order = 10 },
        new("search.findAll.limit", SettingCategories.Search, SettingKind.Int, 1_000_000) { Min = 1_000, Max = 100_000_000, Order = 20 },
        new("search.results.preview", SettingCategories.Search, SettingKind.Bool, true) { Order = 30 },
        new("search.results.f3Repeats", SettingCategories.Search, SettingKind.Bool, false) { Order = 40 },
        new("search.history.limit", SettingCategories.Search, SettingKind.Int, Search.SearchHistory.DefaultLimit) { Min = 0, Max = Search.SearchHistory.MaxLimit, Order = 50 },
        new("search.history.doNotSave", SettingCategories.Search, SettingKind.Bool, false) { Order = 60 },

        // ---- ファイルと保存 ----
        new("recent.maxItems", SettingCategories.Files, SettingKind.Int, RecentFileList.DefaultMaxItems) { Min = 0, Max = RecentFileList.MaxItemsLimit, Order = 1 },
        new("recent.restorePosition", SettingCategories.Files, SettingKind.Bool, true) { Order = 2 },
        new(ExternalChangeRules.AutoReloadKey, SettingCategories.Files, SettingKind.Bool, true) { Order = 3 },
        new(BackupSettings.EnabledKey, SettingCategories.Files, SettingKind.Bool, false) { Order = 4 },
        new(BackupSettings.LocationKey, SettingCategories.Files, SettingKind.Choice, "sameFolder") { Options = ["sameFolder", "folder"], Order = 5 },
        new(BackupSettings.FolderKey, SettingCategories.Files, SettingKind.String, string.Empty) { Exportable = false, Order = 6 },
        new(BackupSettings.GenerationsKey, SettingCategories.Files, SettingKind.Int, 1) { Min = 1, Max = BackupSettings.MaxGenerations, Order = 7 },
        new("save.recoveryIntervalMinutes", SettingCategories.Files, SettingKind.Int, 1) { Min = 0, Max = 60, Order = 8 },
        new("storage.tempDirectory", SettingCategories.Files, SettingKind.String, string.Empty) { Exportable = false, RequiresRestart = true, Order = 10 },

        // ---- 解析 (ハッシュパネル ANA-18。パネルの中で変える項目なので設定画面には出さない) ----
        new("hash.autoRecompute", SettingCategories.Advanced, SettingKind.Bool, true) { Order = 100, ShowInPage = false },
        new("hash.algorithms", SettingCategories.Advanced, SettingKind.String, string.Empty) { Order = 101, ShowInPage = false },
        new("hash.sets", SettingCategories.Advanced, SettingKind.String, string.Empty) { Order = 102, ShowInPage = false },

        // ---- 言語 ----
        new("ui.language", SettingCategories.Language, SettingKind.Choice, "system") { Options = Languages, RequiresRestart = true, Order = 10 },

        // 翻訳者向け (UI-41 の仕様 5)。
        new("i18n.showStringKeys", SettingCategories.Language, SettingKind.Bool, false) { RequiresRestart = true, Order = 20 },

        // ---- 更新 (PKG-17〜PKG-22) ----
        new("update.checkAutomatically", SettingCategories.Update, SettingKind.Bool, true) { Order = 10 },
        new("update.downloadAutomatically", SettingCategories.Update, SettingKind.Bool, true) { Order = 20, Distributions = [SettingDistributions.Installer] },
        new("update.channel", SettingCategories.Update, SettingKind.Choice, "stable") { Options = ["stable", "preview"], Order = 30 },
        new("update.skippedVersion", SettingCategories.Update, SettingKind.String, string.Empty) { Order = 40, ShowInPage = false },

        // ---- プライバシー (UI-58) ----
        new("network.offline", SettingCategories.Privacy, SettingKind.Bool, false) { Order = 10 },
        new("network.downloads.enabled", SettingCategories.Privacy, SettingKind.Bool, true) { Order = 20 },
        new("network.translationReport.enabled", SettingCategories.Privacy, SettingKind.Bool, true) { Order = 30 },

        // ---- Explorer 連携 (UI-35、UI-54、UI-56) ----
        new("shell.jumpList.enabled", SettingCategories.Explorer, SettingKind.Bool, true) { Order = 10 },
        new("shell.openWith.extensions", SettingCategories.Explorer, SettingKind.String, string.Empty) { Order = 20 },

        // ---- アクセシビリティ (UI-51) ----
        new("a11y.announce.verbosity", SettingCategories.Accessibility, SettingKind.Choice, "full") { Options = ["full", "brief", "offsetOnly", "none"], Order = 10 },

        // ---- 詳細 ----
        new("log.level", SettingCategories.Advanced, SettingKind.Choice, "info") { Options = ["info", "debug"], Order = 10 },
        new("diagnostics.writeMiniDump", SettingCategories.Advanced, SettingKind.Bool, false) { Order = 20 },

        // 「コマンドラインから使えるようにする」(10 の PKG-08 の仕様 6)。インストーラ版だけ。値はユーザーの PATH に反映し、他の PC に
        // 持ち出さない (PATH の状態は PC ごと)。
        new("shell.commandLine.enabled", SettingCategories.Advanced, SettingKind.Bool, true) { Order = 30, Distributions = [SettingDistributions.Installer], Exportable = false },
    ];

    public static JsonNode? DefaultOf(string key) => All.FirstOrDefault(s => s.Key == key)?.Default?.DeepClone();
}
