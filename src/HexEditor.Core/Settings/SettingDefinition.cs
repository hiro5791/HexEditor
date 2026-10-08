using System.Text.Json.Nodes;
using HexEditor.Core.Commands;

namespace HexEditor.Core.Settings;

/// <summary>設定の値の種類。</summary>
public enum SettingKind
{
    Bool,
    Int,
    Number,
    String,

    /// <summary><see cref="SettingDefinition.Options"/> の中から選ぶ文字列。</summary>
    Choice,

    /// <summary>文字列の配列 (例: ツールバーのコマンド ID)。</summary>
    List,
}

/// <summary>
/// 設定項目の定義 (UI-22)。設定画面の項目・検索・既定値への戻し・エクスポート・JSON スキーマはここから作る。
/// 名前・説明・選択肢の表示名はリソース (<see cref="NameKey"/>、<see cref="DescriptionKey"/>、<see cref="OptionKey"/>)。
/// </summary>
public sealed record SettingDefinition(string Key, string Category, SettingKind Kind, JsonNode? Default)
{
    /// <summary>Choice の選択肢。</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>再起動後に反映する項目 (表示言語など。UI-22 の仕様 6)。</summary>
    public bool RequiresRestart { get; init; }

    /// <summary>配布形態に固有の項目 (データフォルダの場所など) は false。エクスポートしない (UI-25 の仕様 4)。</summary>
    public bool Exportable { get; init; } = true;

    /// <summary>設定画面に汎用の操作部品で出すか。専用の画面 (キーボード・ツールバー) で扱う項目は false。</summary>
    public bool ShowInPage { get; init; } = true;

    /// <summary>カテゴリの中の並び。</summary>
    public int Order { get; init; }

    /// <summary>
    /// 設定画面に出す配布形態 (<see cref="SettingDistributions"/> の値)。空ならすべての配布形態で出す。
    /// 例: 更新の自動ダウンロードはインストーラ版だけ (10 の PKG-17 の仕様 2)。
    /// </summary>
    public IReadOnlyList<string> Distributions { get; init; } = [];

    /// <summary>配布形態 <paramref name="distribution"/> の設定画面に出すか (null は配布形態を区別しない)。</summary>
    public bool IsShownIn(string? distribution) =>
        ShowInPage && (Distributions.Count == 0 || distribution is null || Distributions.Contains(distribution, StringComparer.OrdinalIgnoreCase));

    public string NameKey => "Set_" + CommandDefinition.KeyPart(Key);

    public string DescriptionKey => "SetDesc_" + CommandDefinition.KeyPart(Key);

    public string OptionKey(string value) => "SetOpt_" + CommandDefinition.KeyPart(Key) + "_" + value;

    /// <summary>値が正しいか (種類・選択肢・範囲)。null (未設定) は常に正しい。</summary>
    public bool Validate(JsonNode? value)
    {
        if (value is null)
        {
            return true;
        }

        switch (Kind)
        {
            case SettingKind.Bool:
                return value is JsonValue b && b.TryGetValue(out bool _);
            case SettingKind.Int:
                return value is JsonValue i && i.TryGetValue(out int n) && InRange(n);
            case SettingKind.Number:
                return TryGetNumber(value, out double x) && double.IsFinite(x) && InRange(x);
            case SettingKind.String:
                return value is JsonValue s && s.TryGetValue(out string? _);
            case SettingKind.Choice:
                return value is JsonValue c && c.TryGetValue(out string? choice) && Options.Contains(choice, StringComparer.Ordinal);
            case SettingKind.List:
                return value is JsonArray a && a.All(e => e is JsonValue v && v.TryGetValue(out string? _));
            default:
                return false;
        }
    }

    /// <summary>JSON の数値を double で読む (整数で作った値・ファイルから読んだ値のどちらでも)。</summary>
    public static bool TryGetNumber(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private bool InRange(double v) => (Min is not { } min || v >= min) && (Max is not { } max || v <= max);
}

/// <summary>配布形態の名前 (<see cref="SettingDefinition.Distributions"/>。Platform の Distribution の名前の小文字)。</summary>
public static class SettingDistributions
{
    public const string Msix = "msix";
    public const string Installer = "installer";
    public const string Portable = "portable";
    public const string Development = "development";
}

/// <summary>設定画面のカテゴリ (UI-22 の仕様 2)。表示名はリソース <c>SetCategory_&lt;ID&gt;</c>。</summary>
public static class SettingCategories
{
    public const string General = "general";
    public const string Appearance = "appearance";
    public const string View = "view";
    public const string Editing = "editing";
    public const string Search = "search";
    public const string Files = "files";
    public const string Keyboard = "keyboard";
    public const string Language = "language";
    public const string Accessibility = "accessibility";
    public const string Automation = "automation";
    public const string Update = "update";
    public const string Privacy = "privacy";
    public const string Explorer = "explorer";
    public const string Advanced = "advanced";

    public static readonly string[] All =
        [General, Appearance, View, Editing, Search, Files, Keyboard, Language, Accessibility, Automation, Update, Privacy, Explorer, Advanced];
}
