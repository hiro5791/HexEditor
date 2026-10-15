using HexEditor.Core.Coloring;
using HexEditor.Core.Settings;

namespace HexEditor.App.Services;

/// <summary>
/// 全体の色付けルール (INSP-33 の仕様 3): すべてのドキュメントに適用し、設定 (<c>coloring.rules</c>) として保存する。アプリ全体で 1 つ。
/// 保存がなければ組み込みのプリセット (INSP-33 の仕様 5。すべて無効) から始める。
/// </summary>
public static class GlobalColoringRules
{
    public const string RulesKey = "coloring.rules";

    /// <summary>設定「ハイコントラストでも色付けルールの色を使う」(INSP-34 の仕様 4。既定オフ)。</summary>
    public const string HighContrastColorsKey = "coloring.highContrastColors";

    private static IReadOnlyList<ColoringRule>? _rules;

    /// <summary>ルールが変わった (どのウィンドウからでも)。</summary>
    public static event EventHandler? Changed;

    public static IReadOnlyList<ColoringRule> Rules => _rules ??= Load(App.Settings);

    private static IReadOnlyList<ColoringRule> _pluginRules = [];

    /// <summary>プラグインが追加した規則 (08 の AUTO-31 の IColorRuleProvider。設定には保存しない。名前にプラグイン名を付ける)。</summary>
    public static IReadOnlyList<ColoringRule> PluginRules => _pluginRules;

    /// <summary>表示に使う全体の規則 (利用者の規則の後にプラグインの規則)。</summary>
    public static IReadOnlyList<ColoringRule> Effective => _pluginRules.Count == 0 ? Rules : [.. Rules, .. _pluginRules];

    /// <summary>プラグインの規則を置き換える (登録簿が変わるたび)。</summary>
    public static void SetPluginRules(IReadOnlyList<ColoringRule> rules)
    {
        if (rules.Count == 0 && _pluginRules.Count == 0)
        {
            return;
        }

        _pluginRules = [.. rules.Take(ColoringRule.MaxRules)];
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>プリセットの名前 (表示の言語)。</summary>
    public static string PresetName(string id) => Loc.Get("Coloring_Preset_" + id);

    private static IReadOnlyList<ColoringRule> Load(SettingsStore settings)
    {
        IReadOnlyList<ColoringRule>? saved = ColoringRule.Parse(settings.GetString(RulesKey, string.Empty));
        if (saved is null)
        {
            return ColoringPresets.All(PresetName);
        }

        return saved;
    }

    public static void Set(IReadOnlyList<ColoringRule> rules)
    {
        _rules = [.. rules.Take(ColoringRule.MaxRules)];
        App.Settings.SetString(RulesKey, ColoringRule.Serialize(_rules), string.Empty);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>設定ファイルが外で変わったら読み直す。</summary>
    public static void Reload()
    {
        IReadOnlyList<ColoringRule> fresh = Load(App.Settings);
        if (_rules is null || ColoringRule.Serialize(fresh) != ColoringRule.Serialize(_rules))
        {
            _rules = fresh;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
