using System.Text;
using HexEditor.Platform.Updates;

namespace HexEditor.Platform.Localization;

/// <summary>
/// 翻訳の誤りの報告 (00-overview 5.3 の 5、09 の UI-41)。翻訳の報告用の Issue フォーム
/// (<c>.github/ISSUE_TEMPLATE/translation.yml</c>) の作成画面の URL を、クエリで欄を入力済みにして作る。
/// 欄の ID はフォームの <c>id</c> と同じ: <c>language</c>、<c>key</c>、<c>current</c>、<c>version</c>。
/// </summary>
public static class TranslationReport
{
    public const string TemplateFile = "translation.yml";

    /// <summary>URL に入れる訳文の上限 (長すぎる URL を避ける)。</summary>
    public const int MaxTextLength = 500;

    /// <param name="language">表示言語のフォルダ名 (例: <c>de</c>)。</param>
    /// <param name="key">リソースキー (例: <c>Menu_File.Title</c>)。「選ばずに報告」なら null。</param>
    /// <param name="currentText">現在の訳文 (英語表示のときは原文)。「選ばずに報告」なら null。</param>
    public static Uri IssueUrl(string language, string? key, string? currentText, string appVersion, string repository = UpdateSource.ProductRepositoryUrl)
    {
        var query = new StringBuilder();
        Add("template", TemplateFile);
        Add("labels", "translation");
        Add("title", key is null ? $"[Translation] {language}: " : $"[Translation] {language}: {key}");
        Add("language", language);
        if (key is not null)
        {
            Add("key", key);
            Add("current", currentText is { Length: > MaxTextLength } text ? text[..MaxTextLength] : currentText ?? string.Empty);
        }

        Add("version", appVersion);
        return new Uri($"{repository.TrimEnd('/')}/issues/new?{query}");

        void Add(string name, string value)
        {
            if (query.Length > 0)
            {
                query.Append('&');
            }

            query.Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }
    }

    /// <summary>URL のクエリを読む (テスト用)。</summary>
    public static IReadOnlyDictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : string.Empty);

    /// <summary>MRT のリソース名 (<c>Menu_File/Title</c>) を .resw のキー (<c>Menu_File.Title</c>) に直す。</summary>
    public static string KeyFromResourceName(string name) => name.Replace('/', '.');

    /// <summary>
    /// 文字列を選ぶダイアログの検索 (UI-41 の仕様 1): キー・英語の原文・現在の訳文のどれかに、大文字・小文字を区別せずに含むもの。
    /// 地域設定に依存しない比較 (00-overview 5.4)。
    /// </summary>
    public static IEnumerable<T> Filter<T>(IEnumerable<T> items, string query, Func<T, string> key, Func<T, string> source, Func<T, string> current)
    {
        string q = query.Trim();
        return q.Length == 0
            ? items
            : items.Where(i => key(i).Contains(q, StringComparison.OrdinalIgnoreCase)
                || source(i).Contains(q, StringComparison.OrdinalIgnoreCase)
                || current(i).Contains(q, StringComparison.OrdinalIgnoreCase));
    }
}
