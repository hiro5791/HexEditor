using System.Globalization;
using HexEditor.Core.Engine;
using HexEditor.Core.FileTypes;
using HexEditor.Core.Templates.Loading;

namespace HexEditor.Core.Templates;

/// <summary>テンプレートの出所 (TPL-35 の仕様 2)。</summary>
public enum TemplateOrigin
{
    /// <summary>製品に同梱の標準テンプレート (TPL-34。読み取り専用)。</summary>
    Standard,

    /// <summary>利用者のテンプレートのフォルダ (TPL-35 の仕様 1)。</summary>
    User,
}

/// <summary>一覧の 1 項目。<see cref="Name"/> はファイル名から拡張子を除いたもの (コマンドラインの <c>--template pe</c> の名前)。</summary>
public sealed record TemplateCatalogEntry(string Name, string Path, TemplateOrigin Origin, TemplateMetadata Metadata)
{
    public string DisplayName(CultureInfo uiCulture) => Metadata.GetDisplayName(uiCulture, Path);
}

/// <summary>自動適用の候補 (TPL-30 の仕様 2 の点数と <c>priority</c> の順)。</summary>
public sealed record TemplateCandidate(TemplateCatalogEntry Entry, TemplateMatch Match);

/// <summary>
/// 使えるテンプレートの一覧 (TPL-20 の仕様 1、TPL-35)。利用者のフォルダ (先に書いたものが優先。TPL-35 の仕様 4) と標準テンプレート
/// (TPL-34) を集め、名前・パスで探し、自動適用の条件 (TPL-30) で候補を選ぶ。スクリプト (AUTO-13) とコマンドライン (AUTO-42) が使う。
/// </summary>
/// <remarks>
/// 一覧は呼ぶたびにフォルダを調べ直す (ファイルを置けば再起動せずに一覧に出る。TPL-35 の受け入れ基準 1)。メタ情報はファイルの
/// 更新日時と大きさが変わらない限り読み直さない。
/// </remarks>
public sealed class TemplateCatalog
{
    /// <summary>同梱の標準テンプレートの置き場所 (<c>Bundled/templates/</c>)。</summary>
    internal const string StandardFolder = "templates/";

    private static readonly string[] TemplateExtensions = [".hxt", ".ksy"];

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (DateTime Time, long Size, TemplateCatalogEntry Entry)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public TemplateCatalog(IReadOnlyList<string> userFolders, bool includeStandard = true, TemplateLoadOptions? loadOptions = null)
    {
        UserFolders = userFolders;
        IncludeStandard = includeStandard;
        LoadOptions = loadOptions ?? new TemplateLoadOptions { SearchPaths = userFolders };
    }

    /// <summary>標準テンプレートと、<paramref name="userFolder"/> (あれば) の利用者のテンプレート。</summary>
    public static TemplateCatalog CreateDefault(string? userFolder) => new(userFolder is null ? [] : [userFolder]);

    public IReadOnlyList<string> UserFolders { get; }

    public bool IncludeStandard { get; }

    public TemplateLoadOptions LoadOptions { get; }

    /// <summary>一覧 (利用者のフォルダの順、標準テンプレートは最後)。同じ名前は先にあるものだけを残す (TPL-35 の仕様 4)。</summary>
    /// <summary>
    /// 有効な一覧 (無効にしたもの (<see cref="IsEnabled"/>) と、ほかのテンプレートに隠れたものを除く。TPL-35 の仕様 3・4)。
    /// </summary>
    public IReadOnlyList<TemplateCatalogEntry> Entries =>
        [.. AllEntries.Where(e => !e.Shadowed && (IsEnabled?.Invoke(e.Entry) ?? true)).Select(e => e.Entry)];

    /// <summary>テンプレートごとの有効・無効 (TPL-35 の仕様 3。null はすべて有効)。</summary>
    public Func<TemplateCatalogEntry, bool>? IsEnabled { get; set; }

    /// <summary>テンプレートごとの「自動適用の対象にする」(TPL-35 の仕様 3。null はすべて対象)。</summary>
    public Func<TemplateCatalogEntry, bool>? IsAutoApplyEnabled { get; set; }

    /// <summary>
    /// 管理画面の一覧 (無効なもの・隠れたものを含む。TPL-35 の仕様 2・4)。同じ名前 (ファイル名、または表示名) のテンプレートが複数ある場合は、
    /// 検索パスの順で先にあるものを使い、後のものを <c>Shadowed</c> にする。
    /// </summary>
    public IReadOnlyList<(TemplateCatalogEntry Entry, bool Shadowed)> AllEntries
    {
        get
        {
            var result = new List<(TemplateCatalogEntry, bool)>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var displayNames = new HashSet<string>(StringComparer.Ordinal);
            void Add(TemplateCatalogEntry entry)
            {
                bool first = names.Add(entry.Name);
                bool firstDisplay = entry.Metadata.Name is not { Length: > 0 } dn || displayNames.Add(dn);
                result.Add((entry, !(first && firstDisplay)));
            }

            foreach (string folder in UserFolders)
            {
                foreach (string file in EnumerateFolder(folder))
                {
                    if (Describe(file, TemplateOrigin.User) is { } entry)
                    {
                        Add(entry);
                    }
                }
            }

            if (IncludeStandard)
            {
                foreach (string name in BundledTemplates.Names.Where(n => n.StartsWith(StandardFolder, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
                {
                    if (Describe(BundledTemplates.Root + name, TemplateOrigin.Standard) is { } entry)
                    {
                        Add(entry);
                    }
                }
            }

            return result;
        }
    }

    /// <summary>アクセスできない利用者のフォルダ (一覧にその旨を表示する。TPL-35 の「エラー」)。</summary>
    public IReadOnlyList<string> InaccessibleFolders => [.. UserFolders.Where(f => !CanRead(f))];

    private static bool CanRead(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return true; // まだ作っていないフォルダは空の扱い
            }

            _ = Directory.GetFiles(folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 名前 (ファイル名から拡張子を除いたもの、または表示名。大文字・小文字を区別しない) かファイルのパスでテンプレートを探す。
    /// パスは一覧にないファイルでもよい (利用者が選んだもの)。
    /// </summary>
    public TemplateCatalogEntry? Find(string nameOrPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrPath);
        IReadOnlyList<TemplateCatalogEntry> entries = Entries;
        TemplateCatalogEntry? byName = entries.FirstOrDefault(e => string.Equals(e.Name, nameOrPath, StringComparison.OrdinalIgnoreCase))
            ?? entries.FirstOrDefault(e => string.Equals(e.Metadata.Name, nameOrPath, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        if (nameOrPath.StartsWith(BundledTemplates.Root, StringComparison.Ordinal))
        {
            return Describe(nameOrPath, TemplateOrigin.Standard);
        }

        if (LooksLikePath(nameOrPath) && File.Exists(nameOrPath))
        {
            string full = System.IO.Path.GetFullPath(nameOrPath);
            return entries.FirstOrDefault(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase)) ?? Describe(full, TemplateOrigin.User);
        }

        return null;
    }

    /// <summary>テンプレートを読み込む (評価はしない)。読み込みのエラーは <see cref="TemplateProgram.Diagnostics"/>。</summary>
    public TemplateProgram Load(TemplateCatalogEntry entry) => Load(entry.Path);

    /// <summary>標準テンプレート (読み取り専用。TPL-34 の仕様 5) のパスか。</summary>
    public static bool IsStandardPath(string path) => path.StartsWith(BundledTemplates.Root, StringComparison.Ordinal);

    /// <summary>テンプレートのソースの文字列 (標準テンプレートは同梱のもの)。読めなければ例外。</summary>
    public static string ReadSource(string path)
    {
        if (IsStandardPath(path))
        {
            return BundledTemplates.TryGet(path[BundledTemplates.Root.Length..], out byte[] bytes)
                ? System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿')
                : throw new FileNotFoundException(path);
        }

        return File.ReadAllText(path);
    }

    /// <summary>パスのテンプレートを読み込む (標準テンプレートは同梱のものから)。</summary>
    public TemplateProgram Load(string path)
    {
        if (path.StartsWith(BundledTemplates.Root, StringComparison.Ordinal))
        {
            string relative = path[BundledTemplates.Root.Length..];
            if (!BundledTemplates.TryGet(relative, out byte[] bytes))
            {
                throw new FileNotFoundException(path);
            }

            string directory = BundledTemplates.Root + (System.IO.Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty);
            return TemplateLoader.LoadBytes(bytes, System.IO.Path.GetFileName(relative), directory, LoadOptions);
        }

        return TemplateLoader.LoadFile(path, LoadOptions);
    }

    /// <summary>
    /// 自動適用の条件 (TPL-30 の仕様 1・2) に合うテンプレートの候補。点数、<c>priority</c> の順。<c>auto_apply false</c> のものは除く。
    /// 拡張子だけの一致も候補に含める (<see cref="TemplateMatch.CanAutoApply"/> は false)。
    /// </summary>
    public IReadOnlyList<TemplateCandidate> Detect(TemplateMatchInput input) =>
        [.. Entries
            .Where(e => e.Metadata.AutoApply && (IsAutoApplyEnabled?.Invoke(e) ?? true))
            .Select(e => e.Metadata.Match(input) is { } m ? new TemplateCandidate(e, m) : null)
            .OfType<TemplateCandidate>()
            .OrderByDescending(c => c.Match.Score)
            .ThenByDescending(c => c.Entry.Metadata.Priority)];

    /// <summary>ドキュメントの候補。判定に使うのは先頭と末尾それぞれ 64 KB とファイル形式の判定の結果だけ (TPL-30 の仕様 1)。</summary>
    public IReadOnlyList<TemplateCandidate> Detect(DocumentSnapshot snapshot, string? path) => Detect(MatchInputOf(snapshot, path));

    /// <summary>自動適用で選ぶテンプレート (マジックか MIME で一致した最上位の候補。AUTO-42 の <c>--auto</c>)。なければ null。</summary>
    public TemplateCatalogEntry? SelectAuto(DocumentSnapshot snapshot, string? path) =>
        Detect(snapshot, path).FirstOrDefault(c => c.Match.CanAutoApply)?.Entry;

    /// <summary>判定に使うデータ: 先頭と末尾それぞれ 64 KB、拡張子、ファイル形式の判定 (06 の ANA-17) の MIME。</summary>
    public static TemplateMatchInput MatchInputOf(DocumentSnapshot snapshot, string? path)
    {
        long length = snapshot.Length;
        string? extension = path is null ? null : System.IO.Path.GetExtension(path).TrimStart('.');
        int headLength = (int)Math.Min(length, TemplateMatchInput.WindowBytes);
        int tailLength = (int)Math.Min(length, TemplateMatchInput.WindowBytes);
        byte[] head = new byte[headLength];
        byte[] tail = new byte[tailLength];
        string? mime = null;
        try
        {
            // 先頭と末尾は 1 度だけ読み、ファイル形式の判定と自動適用の条件の両方に使う (読むのは合わせて 128 KB まで。TC-TPL-30-05)
            var data = new HeadTailMagicData(snapshot);
            ReadAll(data, 0, head);
            ReadAll(data, length - tailLength, tail);
            mime = new FileTypeDetector(FileTypeDatabase.BuiltIn).Detect(data, extension).Best?.Mime;
        }
        catch (IOException)
        {
            // 読めない部分があれば MIME は使わない (マジックと拡張子だけで判定する)。
            snapshot.Read(0, head);
            snapshot.Read(length - tailLength, tail);
        }

        return new TemplateMatchInput(length, head, tail, string.IsNullOrEmpty(extension) ? null : extension, mime);
    }

    private static void ReadAll(HeadTailMagicData data, long offset, Span<byte> destination)
    {
        int done = 0;
        while (done < destination.Length)
        {
            int n = data.Read(offset + done, destination[done..]);
            if (n <= 0)
            {
                break;
            }

            done += n;
        }
    }

    private static bool LooksLikePath(string text) =>
        text.Contains('\\') || text.Contains('/') || TemplateExtensions.Any(e => text.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateFolder(string folder)
    {
        string[] files;
        try
        {
            files = Directory.Exists(folder) ? Directory.GetFiles(folder) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // アクセスできないフォルダは飛ばし、ほかのフォルダのテンプレートは使えるようにする (TPL-35 の「エラー」)。
            files = [];
        }

        return files.Where(f => TemplateExtensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>メタ情報を読む (更新日時と大きさが同じなら前回のもの)。読めないファイルは null。</summary>
    private TemplateCatalogEntry? Describe(string path, TemplateOrigin origin)
    {
        DateTime time = default;
        long size = 0;
        if (origin == TemplateOrigin.User)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return null;
                }

                time = info.LastWriteTimeUtc;
                size = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var cached) && cached.Time == time && cached.Size == size)
            {
                return cached.Entry;
            }
        }

        TemplateProgram program;
        try
        {
            program = Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string relative = path.StartsWith(BundledTemplates.Root, StringComparison.Ordinal) ? path[BundledTemplates.Root.Length..] : path;
        var entry = new TemplateCatalogEntry(System.IO.Path.GetFileNameWithoutExtension(relative), path, origin, program.Metadata);
        lock (_lock)
        {
            _cache[path] = (time, size, entry);
        }

        return entry;
    }
}
