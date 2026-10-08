using HexEditor.Platform.Localization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace HexEditor.App.Services;

/// <summary>文字列リソース 1 つ: .resw のキー、英語の原文、今の表示言語で表示している文字列 (訳がなければ英語)。</summary>
public sealed record ResourceString(string Key, string English, string Current);

/// <summary>
/// アプリの文字列リソースの一覧 (翻訳の誤りの報告の選択肢。09 の UI-41 の仕様 1)。MRT の ResourceMap から読む。
/// </summary>
public static class ResourceStrings
{
    public static IReadOnlyList<ResourceString> All(string language)
    {
        var result = new List<ResourceString>();
        try
        {
            var manager = new ResourceManager();
            ResourceMap map = manager.MainResourceMap.GetSubtree("Resources");
            ResourceContext english = manager.CreateResourceContext();
            english.QualifierValues["Language"] = "en";
            ResourceContext current = manager.CreateResourceContext();
            current.QualifierValues["Language"] = language;
            for (uint i = 0; i < map.ResourceCount; i++)
            {
                string name = map.GetValueByIndex(i).Key;
                string key = TranslationReport.KeyFromResourceName(name);
                string source = map.TryGetValue(name, english)?.ValueAsString ?? string.Empty;
                string shown = map.TryGetValue(name, current)?.ValueAsString ?? source;
                result.Add(new ResourceString(key, source, shown));
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            AppLog.Warning($"Resource strings could not be listed: {ex.GetType().Name}");
        }

        return [.. result.OrderBy(r => r.Key, StringComparer.Ordinal)];
    }
}
