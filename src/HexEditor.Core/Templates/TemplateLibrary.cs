using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Core.Engine;
using HexEditor.Core.Templates.Loading;

namespace HexEditor.Core.Templates;

/// <summary>
/// テンプレートごとの利用者の選択 (TPL-35 の仕様 3、TPL-30 の仕様 5): 無効にしたもの、自動適用の対象から外したもの、
/// 「この形式では今後表示しない」を選んだもの。鍵はテンプレートのパス (標準は <c>bundled:templates/png.hxt</c>)。
/// 設定 (<c>templates.preferences</c>) に JSON で保存する。
/// </summary>
public sealed class TemplatePreferences
{
    public HashSet<string> Disabled { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> NoAutoApply { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>「この形式では今後表示しない」(自動適用の提案を出さない)。</summary>
    public HashSet<string> Suppressed { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled(TemplateCatalogEntry entry) => !Disabled.Contains(entry.Path);

    public bool IsAutoApplyEnabled(TemplateCatalogEntry entry) => !NoAutoApply.Contains(entry.Path);

    /// <summary>カタログの有効・無効と自動適用の対象をこの選択に従わせる。</summary>
    public void ApplyTo(TemplateCatalog catalog)
    {
        catalog.IsEnabled = IsEnabled;
        catalog.IsAutoApplyEnabled = IsAutoApplyEnabled;
    }

    public JsonObject ToJson() => new()
    {
        ["disabled"] = new JsonArray([.. Disabled.Order(StringComparer.OrdinalIgnoreCase).Select(p => (JsonNode?)p)]),
        ["noAutoApply"] = new JsonArray([.. NoAutoApply.Order(StringComparer.OrdinalIgnoreCase).Select(p => (JsonNode?)p)]),
        ["suppressed"] = new JsonArray([.. Suppressed.Order(StringComparer.OrdinalIgnoreCase).Select(p => (JsonNode?)p)]),
    };

    public static TemplatePreferences FromJson(JsonNode? node)
    {
        var p = new TemplatePreferences();
        if (node is not JsonObject o)
        {
            return p;
        }

        static void Read(JsonObject o, string key, HashSet<string> into)
        {
            if (o[key] is JsonArray a)
            {
                foreach (JsonNode? item in a)
                {
                    if (item is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
                    {
                        into.Add(s);
                    }
                }
            }
        }

        Read(o, "disabled", p.Disabled);
        Read(o, "noAutoApply", p.NoAutoApply);
        Read(o, "suppressed", p.Suppressed);
        return p;
    }
}

/// <summary>設定「自動適用」(TPL-30 の仕様 3)。</summary>
public enum TemplateAutoApplyMode
{
    /// <summary>提案する (既定)。</summary>
    Suggest,

    /// <summary>常に適用する。</summary>
    Always,

    /// <summary>何もしない。</summary>
    None,
}

/// <summary>自動適用の判定の結果の動作。</summary>
public enum TemplateAutoApplyAction
{
    None,

    /// <summary>InfoBar で提案する。</summary>
    Suggest,

    /// <summary>最上位の候補を適用する。</summary>
    Apply,
}

/// <summary>自動適用の判定の結果 (TPL-30)。<see cref="Elapsed"/> は判定にかかった時間 (アプリのログの計測値。TC-TPL-30-04)。</summary>
public sealed record TemplateAutoApplyDecision(TemplateAutoApplyAction Action, TemplateCandidate? Top, IReadOnlyList<TemplateCandidate> Candidates,
    TimeSpan Elapsed, long BytesRead);

/// <summary>
/// ドキュメントを開いたときの自動適用の判定 (TPL-30)。使うのは先頭と末尾それぞれ 64 KB とファイル形式の判定の結果だけで、ファイルの
/// 大きさによらない (仕様 1・7)。
/// </summary>
public static class TemplateAutoApply
{
    /// <summary>
    /// 判定する。<paramref name="hasRecordedApplications"/>: ドキュメントに適用の記録がある (提案しない。仕様 6)。
    /// <paramref name="suppressed"/>: 「この形式では今後表示しない」を選んだテンプレート (提案しない。仕様 5)。
    /// 拡張関数を使うテンプレートは「常に適用する」でも提案だけにする (TPL-19 の仕様 3)。
    /// </summary>
    public static TemplateAutoApplyDecision Decide(TemplateCatalog catalog, DocumentSnapshot snapshot, string? path, TemplateAutoApplyMode mode,
        IReadOnlySet<string>? suppressed, bool hasRecordedApplications)
    {
        var watch = Stopwatch.StartNew();
        TemplateMatchInput input = TemplateCatalog.MatchInputOf(snapshot, path);
        long read = input.Head.Length + input.Tail.Length;
        if (mode == TemplateAutoApplyMode.None || hasRecordedApplications)
        {
            return new TemplateAutoApplyDecision(TemplateAutoApplyAction.None, null, [], watch.Elapsed, read);
        }

        IReadOnlyList<TemplateCandidate> candidates = catalog.Detect(input);
        List<TemplateCandidate> shown = [.. candidates.Where(c => suppressed is null || !suppressed.Contains(c.Entry.Path))];
        if (shown.Count == 0)
        {
            return new TemplateAutoApplyDecision(TemplateAutoApplyAction.None, null, candidates, watch.Elapsed, read);
        }

        if (mode == TemplateAutoApplyMode.Always && candidates.FirstOrDefault(c => c.Match.CanAutoApply) is { } best)
        {
            TemplateAutoApplyAction action = best.Entry.Metadata.UsesExtensions ? TemplateAutoApplyAction.Suggest : TemplateAutoApplyAction.Apply;
            return new TemplateAutoApplyDecision(action, best, candidates, watch.Elapsed, read);
        }

        return new TemplateAutoApplyDecision(TemplateAutoApplyAction.Suggest, shown[0], shown, watch.Elapsed, read);
    }

    /// <summary>適用の結果が <c>require</c> の条件を満たさなかった (「常に適用する」で適用を取り消す。TPL-30 の仕様 3)。</summary>
    public static bool FailedRequire(TemplateResult result) => result.Diagnostics.Any(d => d.Code == TemplateMessages.RequireFailed);
}

/// <summary>
/// 利用者のテンプレートの管理 (TPL-35): 「複製して編集」(仕様 5)、インポート (仕様 6)、エクスポート (仕様 7)。
/// </summary>
public static class TemplateLibrary
{
    private static readonly string[] Extensions = [".hxt", ".ksy"];

    /// <summary>
    /// 標準テンプレート (またはオンラインのテンプレート) を利用者のフォルダにコピーし、コピーのパスを返す。<paramref name="text"/> を
    /// 渡すと、その内容で作る (エディタで変えた内容を引き継ぐ。TC-TPL-34-04)。同じ名前があれば番号を付ける。
    /// </summary>
    public static string Duplicate(string sourcePath, string userFolder, string? text = null)
    {
        Directory.CreateDirectory(userFolder);
        string name = Path.GetFileNameWithoutExtension(TemplateCatalog.IsStandardPath(sourcePath) ? sourcePath[(sourcePath.IndexOf(':') + 1)..] : sourcePath);
        string ext = Path.GetExtension(sourcePath);
        string target = Path.Combine(userFolder, name + ext);
        for (int i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(userFolder, $"{name} ({i}){ext}");
        }

        File.WriteAllText(target, text ?? TemplateCatalog.ReadSource(sourcePath), new System.Text.UTF8Encoding(false));
        return target;
    }

    /// <summary>
    /// <c>.hxt</c>、<c>.ksy</c>、またはそれらを含む <c>.zip</c> を利用者のフォルダに取り込み、取り込んだファイルを返す。zip の中のフォルダの
    /// 並びは保つ。フォルダの外を指す名前 (<c>..\</c>・絶対パス) とテンプレート以外のファイルは取り込まない。
    /// </summary>
    public static IReadOnlyList<string> Import(string file, string userFolder)
    {
        Directory.CreateDirectory(userFolder);
        string root = Path.GetFullPath(userFolder);
        var imported = new List<string>();
        if (string.Equals(Path.GetExtension(file), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            using ZipArchive zip = ZipFile.OpenRead(file);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || !Extensions.Contains(Path.GetExtension(entry.FullName), StringComparer.OrdinalIgnoreCase)
                    || entry.Length > TemplateLimits.MaxFileBytes)
                {
                    continue;
                }

                string target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // フォルダの外を指す名前は取り込まない
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                imported.Add(target);
            }

            return imported;
        }

        if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(Path.GetFileName(file));
        }

        string dest = Path.Combine(root, Path.GetFileName(file));
        File.Copy(file, dest, overwrite: true);
        imported.Add(dest);
        return imported;
    }

    /// <summary>
    /// 選んだテンプレートと、それがインポートするファイル (同梱の標準ライブラリを除く) を 1 つの zip にする (仕様 7)。zip の中の名前は
    /// テンプレートのフォルダからの相対パス。書きかけのファイルは残さない。入れたファイルの名前の一覧を返す。
    /// </summary>
    public static IReadOnlyList<string> Export(IEnumerable<string> templatePaths, string zipPath, TemplateLoadOptions? options = null)
    {
        var entries = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string template in templatePaths)
        {
            string main = Path.GetFullPath(template);
            string baseDir = Path.GetDirectoryName(main)!;
            TemplateProgram program = TemplateLoader.LoadFile(main, options ?? new TemplateLoadOptions { SearchPaths = [baseDir] });
            foreach (string file in program.Files.Prepend(main).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (file.StartsWith(BundledTemplates.Root, StringComparison.Ordinal) || !File.Exists(file))
                {
                    continue;
                }

                string full = Path.GetFullPath(file);
                string relative = Path.GetRelativePath(baseDir, full);
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                {
                    relative = Path.GetFileName(full);
                }

                entries.TryAdd(relative.Replace(Path.DirectorySeparatorChar, '/'), full);
            }
        }

        string temp = zipPath + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach ((string name, string source) in entries)
                {
                    zip.CreateEntryFromFile(source, name, CompressionLevel.Optimal);
                }
            }

            File.Move(temp, zipPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return [.. entries.Keys];
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても続ける
        }
    }

    /// <summary>JSON の比較用 (テスト)。</summary>
    internal static string Normalize(string json) => JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
