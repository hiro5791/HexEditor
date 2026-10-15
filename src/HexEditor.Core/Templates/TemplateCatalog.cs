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
    public IReadOnlyList<TemplateCatalogEntry> Entries
    {
        get
        {
            var result = new List<TemplateCatalogEntry>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in UserFolders)
            {
                foreach (string file in EnumerateFolder(folder))
                {
                    if (Describe(file, TemplateOrigin.User) is { } entry && names.Add(entry.Name))
                    {
                        result.Add(entry);
                    }
                }
            }

            if (IncludeStandard)
            {
                foreach (string name in BundledTemplates.Names.Where(n => n.StartsWith(StandardFolder, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
                {
                    if (Describe(BundledTemplates.Root + name, TemplateOrigin.Standard) is { } entry && names.Add(entry.Name))
                    {
                        result.Add(entry);
                    }
                }
            }

            return result;
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

    private TemplateProgram Load(string path)
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
            .Where(e => e.Metadata.AutoApply)
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
        int headLength = (int)Math.Min(length, TemplateMatchInput.WindowBytes);
        byte[] head = new byte[headLength];
        snapshot.Read(0, head);
        int tailLength = (int)Math.Min(length, TemplateMatchInput.WindowBytes);
        byte[] tail = new byte[tailLength];
        snapshot.Read(length - tailLength, tail);
        string? extension = path is null ? null : System.IO.Path.GetExtension(path).TrimStart('.');
        string? mime = null;
        try
        {
            mime = new FileTypeDetector(FileTypeDatabase.BuiltIn).Detect(new HeadTailMagicData(snapshot), extension).Best?.Mime;
        }
        catch (IOException)
        {
            // 読めない部分があれば MIME は使わない (マジックと拡張子だけで判定する)。
        }

        return new TemplateMatchInput(length, head, tail, string.IsNullOrEmpty(extension) ? null : extension, mime);
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
