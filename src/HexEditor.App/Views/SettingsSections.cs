using Microsoft.UI.Xaml;

namespace HexEditor.App.Views;

/// <summary>
/// 設定画面の専用の区画 (UI-22): 汎用の項目 (SettingsCatalog の項目) では表せない画面 (キーボードのショートカット、ツールバーの構成、
/// データフォルダの操作など)。カテゴリの項目の後に <see cref="Order"/> の順で並べる。
/// </summary>
/// <param name="Category">置くカテゴリ (<see cref="Core.Settings.SettingCategories"/>)。</param>
/// <param name="Id">区画の ID (検索・テスト用)。</param>
/// <param name="TitleKey">見出しのリソースのキー。</param>
/// <param name="Factory">区画の中身を作る。設定画面を開くたびに呼ぶ。</param>
/// <param name="SearchKeys">検索で一致させる語のリソースのキー (見出しのほか)。</param>
public sealed record SettingsSection(string Category, string Id, string TitleKey, Func<MainWindow, FrameworkElement> Factory, int Order = 0)
{
    public IReadOnlyList<string> SearchKeys { get; init; } = [];
}

/// <summary>専用の区画の一覧。他の機能はアプリの起動時に <see cref="Register"/> する。</summary>
public static class SettingsSections
{
    private static readonly List<SettingsSection> Sections = [];

    public static IReadOnlyList<SettingsSection> All => Sections;

    public static void Register(SettingsSection section)
    {
        if (Sections.Any(s => s.Id == section.Id))
        {
            throw new InvalidOperationException($"設定の区画 ID が重複しています: {section.Id}");
        }

        Sections.Add(section);
    }

    public static IEnumerable<SettingsSection> In(string category) => Sections.Where(s => s.Category == category).OrderBy(s => s.Order);
}
