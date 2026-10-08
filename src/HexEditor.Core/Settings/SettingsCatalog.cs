using System.Text.Json.Nodes;
using HexEditor.Core.Commands;

namespace HexEditor.Core.Settings;

/// <summary>設定項目の表示用の文字列 (表示言語で解決したもの)。</summary>
public sealed record SettingTexts(string Name, string Description, string EnglishName);

/// <summary>設定の検索の結果 1 つ (UI-22 の仕様 4)。<see cref="NamePositions"/> は名前の中で一致した文字の位置。</summary>
public sealed record SettingSearchResult(SettingDefinition Setting, IReadOnlyList<int> NamePositions);

/// <summary>
/// 設定項目の登録 (UI-22)。組み込みの項目は <see cref="BuiltInSettings"/> に書く。アプリ全体で 1 つ。
/// </summary>
public sealed class SettingsCatalog
{
    private readonly Dictionary<string, SettingDefinition> _byKey = new(StringComparer.Ordinal);
    private readonly List<SettingDefinition> _ordered = [];

    public SettingsCatalog()
    {
    }

    public SettingsCatalog(IEnumerable<SettingDefinition> settings)
    {
        foreach (SettingDefinition s in settings)
        {
            Register(s);
        }
    }

    public static SettingsCatalog CreateBuiltIn() => new(BuiltInSettings.All);

    public IReadOnlyList<SettingDefinition> All => _ordered;

    /// <summary>
    /// 実行中の配布形態 (<see cref="SettingDistributions"/> の値)。配布形態に固有の項目 (<see cref="SettingDefinition.Distributions"/>)
    /// を設定画面と検索に出すかを決める。null なら区別しない。
    /// </summary>
    public string? Distribution { get; set; }

    /// <summary>設定画面と検索に出す項目か (<see cref="SettingDefinition.ShowInPage"/> と配布形態)。</summary>
    public bool IsShown(SettingDefinition setting) => setting.IsShownIn(Distribution);

    public event EventHandler? Changed;

    public void Register(SettingDefinition setting)
    {
        if (!_byKey.TryAdd(setting.Key, setting))
        {
            throw new InvalidOperationException($"設定のキーが重複しています: {setting.Key}");
        }

        _ordered.Add(setting);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public SettingDefinition? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>カテゴリの項目 (並びの順)。</summary>
    public IReadOnlyList<SettingDefinition> InCategory(string category) =>
        _ordered.Where(s => s.Category == category).OrderBy(s => s.Order).ToList();

    /// <summary>値が正しいか。知らないキーは正しいとみなす (UI-23 の仕様 4)。</summary>
    public bool Validate(string key, JsonNode? value) => Find(key)?.Validate(value) ?? true;

    /// <summary>既定値から変更されている項目 (settings.json にある項目)。</summary>
    public IReadOnlyList<SettingDefinition> Modified(SettingsStore store, string? category = null) =>
        _ordered.Where(s => (category is null || s.Category == category) && store.Contains(s.Key)).ToList();

    /// <summary>
    /// 検索 (UI-22 の仕様 4): 表示言語の名前・説明、英語の名前、設定キーのどれかに含まれる項目を、カテゴリをまたいで返す。
    /// 大文字・小文字・全角半角の違いは無視する。
    /// </summary>
    public List<SettingSearchResult> Search(string query, Func<SettingDefinition, SettingTexts> texts)
    {
        string q = SearchText.Normalize(query.Trim());
        var results = new List<SettingSearchResult>();
        if (q.Length == 0)
        {
            return results;
        }

        foreach (SettingDefinition s in _ordered.Where(IsShown))
        {
            SettingTexts t = texts(s);
            var name = new SearchText(t.Name);
            int at = name.Normalized.IndexOf(q, StringComparison.Ordinal);
            if (at >= 0)
            {
                results.Add(new SettingSearchResult(s, Enumerable.Range(at, q.Length).Select(i => name.Map[i]).Distinct().ToList()));
            }
            else if (new[] { t.Description, t.EnglishName, s.Key }.Any(x => SearchText.Normalize(x).Contains(q, StringComparison.Ordinal)))
            {
                results.Add(new SettingSearchResult(s, []));
            }
        }

        return results;
    }
}
