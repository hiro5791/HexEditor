using System.Net;
using System.Text;

namespace HexEditor.Core.Commands;

/// <summary>ショートカット一覧の 1 行 (UI-39 の仕様 2)。文字列は表示言語・キーボード配列で解決したもの。</summary>
public sealed record ShortcutRow(string CommandId, string Category, string Command, string Keys, string Scope, KeyScope ScopeValue)
{
    public bool HasKeys => Keys.Length > 0;
}

/// <summary>ショートカット一覧 (UI-39)。コマンド登録と今の割り当てから作る (手で書いた一覧は持たない)。</summary>
public static class ShortcutList
{
    /// <summary>
    /// 一覧の行を作る。キーが複数ある場合は、有効範囲ごとに 1 行にまとめる。割り当てのないコマンドは
    /// <paramref name="includeUnassigned"/> のときだけ出す (仕様 6)。
    /// </summary>
    public static List<ShortcutRow> Build(KeyMap keys, Func<CommandDefinition, string> commandName, Func<string, string> categoryName,
        Func<KeyChord, string> keyText, Func<KeyScope, string> scopeName, bool includeUnassigned)
    {
        var rows = new List<ShortcutRow>();
        foreach (CommandDefinition command in keys.Catalog.All.Where(c => !c.Hidden))
        {
            var bindings = keys.BindingsFor(command.Id);
            if (bindings.Count == 0)
            {
                if (includeUnassigned)
                {
                    rows.Add(new ShortcutRow(command.Id, categoryName(command.Category), commandName(command), string.Empty, string.Empty, KeyScope.Global));
                }

                continue;
            }

            foreach (var group in bindings.GroupBy(b => b.Binding.Scope))
            {
                rows.Add(new ShortcutRow(command.Id, categoryName(command.Category), commandName(command),
                    string.Join(", ", group.Select(b => keyText(b.Binding.Chord))), scopeName(group.Key), group.Key));
            }
        }

        return rows;
    }

    /// <summary>
    /// 絞り込み (UI-18 の仕様 2 と同じ規則): 表示名・英語名・コマンド ID・キーの表記 (例: <c>ctrl+g</c>) のどれかに含まれる行。
    /// </summary>
    public static bool Matches(ShortcutRow row, string query, string englishName, string storedKeys)
    {
        string q = SearchText.Normalize(query.Trim());
        if (q.Length == 0)
        {
            return true;
        }

        return new[] { row.Command, englishName, row.CommandId, row.Keys, storedKeys, row.Category }
            .Any(s => SearchText.Normalize(s).Contains(q, StringComparison.Ordinal));
    }

    /// <summary>「HTML として保存」(仕様 4)。カテゴリごとに見出しと表を書く。</summary>
    public static string ToHtml(IEnumerable<ShortcutRow> rows, string title, string keyHeader, string commandHeader, string scopeHeader, string language)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(Encode(language)).Append("\">\n<head>\n<meta charset=\"utf-8\" />\n<title>")
            .Append(Encode(title)).Append("</title>\n<style>body{font-family:'Segoe UI',sans-serif;margin:24px}table{border-collapse:collapse;margin-bottom:24px}")
            .Append("th,td{border:1px solid #ccc;padding:4px 8px;text-align:start}th{background:#f3f3f3}kbd{font-family:'Cascadia Mono',Consolas,monospace}</style>\n")
            .Append("</head>\n<body>\n<h1>").Append(Encode(title)).Append("</h1>\n");
        foreach (var category in rows.GroupBy(r => r.Category))
        {
            html.Append("<h2>").Append(Encode(category.Key)).Append("</h2>\n<table>\n<tr><th>").Append(Encode(keyHeader)).Append("</th><th>")
                .Append(Encode(commandHeader)).Append("</th><th>").Append(Encode(scopeHeader)).Append("</th></tr>\n");
            foreach (ShortcutRow row in category)
            {
                html.Append("<tr><td><kbd>").Append(Encode(row.Keys)).Append("</kbd></td><td>").Append(Encode(row.Command))
                    .Append("</td><td>").Append(Encode(row.Scope)).Append("</td></tr>\n");
            }

            html.Append("</table>\n");
        }

        return html.Append("</body>\n</html>\n").ToString();
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
