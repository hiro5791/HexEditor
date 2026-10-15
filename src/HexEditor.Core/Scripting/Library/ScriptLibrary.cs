using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Scripting.Library;

/// <summary>一覧の根 (AUTO-19 の仕様 1): 利用者のスクリプト・マクロ・利用者が追加したフォルダ・サンプル。</summary>
public enum ScriptRootKind
{
    Scripts,
    Macros,
    Extra,
    Samples,
}

/// <summary>一覧の項目の種類。</summary>
public enum ScriptNodeType
{
    Root,
    Folder,
    Script,
}

/// <summary>一覧の根 1 つ。<see cref="Path"/> がなければ「フォルダが見つかりません」を表示する (AUTO-19 の「エラー」)。</summary>
public sealed record ScriptRoot(string Id, ScriptRootKind Kind, string Path);

/// <summary>
/// 一覧の木の 1 つの節 (AUTO-19)。<see cref="Name"/> は表示名 (スクリプトはメタデータの <c>@name</c>、なければ拡張子を除いたファイル名。
/// フォルダはフォルダ名)。
/// </summary>
public sealed class ScriptNode
{
    public required string Name { get; init; }

    /// <summary>ファイル・フォルダのパス。</summary>
    public required string Path { get; init; }

    public required ScriptNodeType Type { get; init; }

    public required ScriptRoot Root { get; init; }

    /// <summary>根からの相対パス (区切りは <c>/</c>)。根では空。</summary>
    public string RelativePath { get; init; } = string.Empty;

    public ScriptLanguage Language { get; init; }

    public string? Description { get; init; }

    /// <summary>メタデータの <c>@on</c> (自動実行の候補。AUTO-25 の仕様 4)。</summary>
    public IReadOnlyList<string> HookCandidates { get; init; } = [];

    /// <summary>メタデータの <c>@context</c> (<c>selection</c> / <c>always</c> / null)。</summary>
    public string? ContextMenu { get; init; }

    /// <summary>メタデータの <c>@menu</c>。</summary>
    public string? Menu { get; init; }

    /// <summary>根のフォルダが見つからない。</summary>
    public bool Missing { get; init; }

    public List<ScriptNode> Children { get; } = [];

    /// <summary>ファイル名 (拡張子つき)。</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>コマンドになるスクリプト (サンプルは含めない。仕様 4)。</summary>
    public bool IsCommand => Type == ScriptNodeType.Script && Root.Kind != ScriptRootKind.Samples;

    public bool IsMacro => Root.Kind == ScriptRootKind.Macros;
}

/// <summary>スクリプトのコマンド ID (AUTO-19 の仕様 4、09 の UI-16 の仕様 3: <c>script.&lt;名前&gt;</c>)。</summary>
public static class ScriptCommandIds
{
    public const string Prefix = "script.";

    /// <summary>
    /// 根と相対パスから決まる ID。ショートカットの割り当ては ID に付くので、同じファイルは常に同じ ID にする
    /// (英数字だけの名前にし、パスの違いは短いハッシュで区別する)。
    /// </summary>
    public static string For(ScriptNode node) => For(node.Root.Kind, node.Root.Id, node.RelativePath);

    public static string For(ScriptRootKind kind, string rootId, string relativePath)
    {
        string withoutExtension = relativePath.Contains('.', StringComparison.Ordinal) ? relativePath[..relativePath.LastIndexOf('.')] : relativePath;
        var name = new StringBuilder();
        bool upper = false;
        foreach (char c in withoutExtension)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                name.Append(name.Length == 0 ? char.ToLowerInvariant(c) : upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else if (name.Length > 0)
            {
                upper = true;
            }
        }

        if (name.Length == 0 || char.IsAsciiDigit(name[0]))
        {
            name.Insert(0, 'x');
        }

        if (name.Length > 40)
        {
            name.Length = 40;
        }

        // 根の ID と相対パスから短いハッシュを付けて、別の根の同じ名前や、記号だけが違う名前と区別する。
        string key = (rootId + "|" + relativePath).ToUpperInvariant();
        uint hash = 2166136261;
        foreach (char c in key)
        {
            hash = (hash ^ c) * 16777619;
        }

        string prefix = kind == ScriptRootKind.Macros ? "m" : "s";
        return Prefix + name + prefix + (hash & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// スクリプト一覧の元 (アプリ全体で 1 つ。AUTO-19)。根のフォルダを走査して木にし、ファイルの追加・変更・削除を監視して
/// <see cref="Changed"/> を出す (仕様 5。変更から 1 秒以内に反映する)。
/// </summary>
public sealed class ScriptLibrary : IDisposable
{
    /// <summary>ファイルの変化をまとめて走査し直すまでの待ち。</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);

    /// <summary>メタデータを読むために読むファイルの先頭の大きさ。</summary>
    private const int HeaderBytes = 64 * 1024;

    private static readonly string[] Extensions = [".js", ".mjs", ".csx"];

    private readonly Lock _lock = new();
    private readonly Func<IReadOnlyList<ScriptRoot>> _roots;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, (DateTime Write, long Length, Header Header)> _headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounce;
    private readonly Timer _poll;
    private IReadOnlyList<ScriptNode> _tree = [];
    private string _language = "en";
    private string _watchedSignature = string.Empty;
    private bool _disposed;

    /// <param name="roots">根の一覧 (設定の追加フォルダの変更に追従するため、呼ぶたびに最新を返す)。</param>
    public ScriptLibrary(Func<IReadOnlyList<ScriptRoot>> roots)
    {
        _roots = roots;
        _debounce = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        // 監視できなかった根 (まだ作られていない追加フォルダなど) と、取りこぼしを拾うための確認 (安いので 1 秒おき)。
        _poll = new Timer(_ => PollRoots(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>標準の根 (<c>scripts</c>、<c>macros</c>、追加フォルダ、サンプル)。</summary>
    public static ScriptLibrary Create(ScriptFolders folders, Func<IEnumerable<string>> extraFolders, Func<string?> samples) =>
        new(() => StandardRoots(folders, extraFolders(), samples()));

    public static IReadOnlyList<ScriptRoot> StandardRoots(ScriptFolders folders, IEnumerable<string> extra, string? samples)
    {
        var roots = new List<ScriptRoot>
        {
            new("scripts", ScriptRootKind.Scripts, folders.Scripts),
            new("macros", ScriptRootKind.Macros, Path.Combine(folders.Settings, "macros")),
        };
        int n = 0;
        foreach (string folder in extra.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(new ScriptRoot("extra" + (++n).ToString(CultureInfo.InvariantCulture), ScriptRootKind.Extra, folder));
        }

        if (!string.IsNullOrEmpty(samples))
        {
            roots.Add(new ScriptRoot("samples", ScriptRootKind.Samples, samples));
        }

        return roots;
    }

    /// <summary>一覧が変わった (どのスレッドからでも発生する)。</summary>
    public event EventHandler? Changed;

    /// <summary>表示言語のタグ (<c>@name:en</c> の選択に使う)。変えると一覧を作り直す。</summary>
    public string Language
    {
        get => _language;
        set
        {
            if (_language != value)
            {
                _language = value;
                Refresh();
            }
        }
    }

    /// <summary>根の節の一覧 (それぞれの下に木)。</summary>
    public IReadOnlyList<ScriptNode> Tree
    {
        get
        {
            lock (_lock)
            {
                return _tree;
            }
        }
    }

    /// <summary>スクリプトの節をすべて平らにしたもの。</summary>
    public IReadOnlyList<ScriptNode> Scripts => [.. Flatten(Tree)];

    /// <summary>コマンドになるスクリプト (サンプルを除く)。</summary>
    public IReadOnlyList<ScriptNode> CommandScripts => [.. Flatten(Tree).Where(n => n.IsCommand)];

    private static IEnumerable<ScriptNode> Flatten(IEnumerable<ScriptNode> nodes)
    {
        foreach (ScriptNode n in nodes)
        {
            if (n.Type == ScriptNodeType.Script)
            {
                yield return n;
            }

            foreach (ScriptNode child in Flatten(n.Children))
            {
                yield return child;
            }
        }
    }

    /// <summary>監視を始める (なければフォルダを作る: <c>scripts</c> と <c>macros</c> のみ)。</summary>
    public void Start()
    {
        foreach (ScriptRoot r in _roots().Where(r => r.Kind is ScriptRootKind.Scripts or ScriptRootKind.Macros))
        {
            try
            {
                Directory.CreateDirectory(r.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 作れない場合は「フォルダが見つかりません」と表示する。
            }
        }

        Refresh();
        _poll.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>走査し直す。</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        IReadOnlyList<ScriptRoot> roots = _roots();
        var tree = new List<ScriptNode>();
        foreach (ScriptRoot root in roots)
        {
            tree.Add(Scan(root));
        }

        lock (_lock)
        {
            _tree = tree;
            SyncWatchers(roots);
            PruneHeaders();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void PollRoots()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // 根の存在の変化 (追加フォルダが作られた・消えた) だけを見る。中身の変化は FileSystemWatcher が拾う。
            string signature = string.Join('|', _roots().Select(r => r.Path + (Directory.Exists(r.Path) ? "+" : "-")));
            if (signature != _watchedSignature)
            {
                Refresh();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
        }
    }

    private ScriptNode Scan(ScriptRoot root)
    {
        bool exists = Directory.Exists(root.Path);
        var node = new ScriptNode
        {
            Name = root.Kind switch
            {
                ScriptRootKind.Scripts or ScriptRootKind.Macros or ScriptRootKind.Samples => root.Id,
                _ => System.IO.Path.GetFileName(root.Path.TrimEnd('\\', '/')) is { Length: > 0 } leaf ? leaf : root.Path,
            },
            Path = root.Path,
            Type = ScriptNodeType.Root,
            Root = root,
            Missing = !exists,
        };
        if (exists)
        {
            ScanFolder(node, root, root.Path, string.Empty, 0);
        }

        return node;
    }

    private void ScanFolder(ScriptNode parent, ScriptRoot root, string folder, string relative, int depth)
    {
        if (depth > 12)
        {
            return;
        }

        IEnumerable<string> directories;
        IEnumerable<string> files;
        try
        {
            directories = Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase);
            files = Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string dir in directories)
        {
            string name = System.IO.Path.GetFileName(dir);
            // storage と lib は script.storage とライブラリの置き場なので一覧に出さない (AUTO-17、AUTO-02 の仕様 3)。
            if (root.Kind == ScriptRootKind.Scripts && depth == 0 && name is "storage" or "lib")
            {
                continue;
            }

            var child = new ScriptNode
            {
                Name = name,
                Path = dir,
                Type = ScriptNodeType.Folder,
                Root = root,
                RelativePath = Join(relative, name),
            };
            ScanFolder(child, root, dir, child.RelativePath, depth + 1);
            // 空のフォルダも出す (新しいスクリプトの置き場所として使える)。
            parent.Children.Add(child);
        }

        foreach (string file in files)
        {
            string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            if (!Extensions.Contains(ext))
            {
                continue;
            }

            Header header = ReadHeader(file);
            ScriptMetadata meta = header.Metadata;
            string fallback = System.IO.Path.GetFileNameWithoutExtension(file);
            parent.Children.Add(new ScriptNode
            {
                Name = meta.DisplayName(_language, fallback),
                Path = file,
                Type = ScriptNodeType.Script,
                Root = root,
                RelativePath = Join(relative, System.IO.Path.GetFileName(file)),
                Language = ext == ".csx" ? ScriptLanguage.CSharp : ScriptLanguage.JavaScript,
                Description = meta.DisplayDescription(_language),
                HookCandidates = [.. meta.On],
                ContextMenu = meta.Context,
                Menu = meta.Menu,
            });
        }
    }

    private static string Join(string relative, string name) => relative.Length == 0 ? name : relative + "/" + name;

    private sealed record Header(ScriptMetadata Metadata);

    private Header ReadHeader(string file)
    {
        try
        {
            var info = new FileInfo(file);
            lock (_lock)
            {
                if (_headers.TryGetValue(file, out var cached) && cached.Write == info.LastWriteTimeUtc && cached.Length == info.Length)
                {
                    return cached.Header;
                }
            }

            string text;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] buffer = new byte[(int)Math.Min(HeaderBytes, stream.Length)];
                int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                text = Encoding.UTF8.GetString(buffer, 0, read);
            }

            var header = new Header(ScriptMetadata.Parse(text));
            lock (_lock)
            {
                _headers[file] = (info.LastWriteTimeUtc, info.Length, header);
            }

            return header;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 書き込み中などで読めない: メタデータなしとして扱う (次の変更で読み直す)。
            return new Header(new ScriptMetadata());
        }
    }

    private void PruneHeaders()
    {
        if (_headers.Count > 4096)
        {
            _headers.Clear();
        }
    }

    // ---- 監視 ----

    private void SyncWatchers(IReadOnlyList<ScriptRoot> roots)
    {
        string signature = string.Join('|', roots.Select(r => r.Path + (Directory.Exists(r.Path) ? "+" : "-")));
        if (signature == _watchedSignature && _watchers.Count > 0)
        {
            return;
        }

        _watchedSignature = signature;
        foreach (FileSystemWatcher w in _watchers)
        {
            w.Dispose();
        }

        _watchers.Clear();
        foreach (ScriptRoot root in roots.Where(r => Directory.Exists(r.Path)))
        {
            try
            {
                var watcher = new FileSystemWatcher(root.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += OnFileEvent;
                watcher.Changed += OnFileEvent;
                watcher.Deleted += OnFileEvent;
                watcher.Renamed += OnFileEvent;
                watcher.Error += (_, _) => ScheduleRefresh();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // 監視できない根は 1 秒おきの確認で拾う。
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        if (!_disposed)
        {
            _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    // ---- 操作 (AUTO-19 の仕様 3) ----

    /// <summary>新しいスクリプトの雛形 (メタデータつき)。</summary>
    public static string NewScriptText(ScriptLanguage language, string name) => language == ScriptLanguage.CSharp
        ? $"// ==HexEditorScript==\n// @name  {name}\n// @api   1.0\n// ==/HexEditorScript==\n\nvar doc = App.ActiveDocument;\n"
        : $"// ==HexEditorScript==\n// @name  {name}\n// @api   1.0\n// ==/HexEditorScript==\n\nconst doc = app.activeDocument;\n";

    /// <summary>新しいスクリプトのファイルを作る。同じ名前があれば番号を付ける。作ったパスを返す。</summary>
    public static string CreateScript(string folder, ScriptLanguage language, string baseName)
    {
        Directory.CreateDirectory(folder);
        string ext = language == ScriptLanguage.CSharp ? ".csx" : ".js";
        string path = UniquePath(folder, SafeFileName(baseName), ext);
        File.WriteAllText(path, NewScriptText(language, System.IO.Path.GetFileNameWithoutExtension(path)), new UTF8Encoding(false));
        return path;
    }

    /// <summary>名前を変える (拡張子は変えない)。新しいパスを返す。同じ名前があれば例外。</summary>
    public static string Rename(string path, string newName)
    {
        string name = SafeFileName(newName);
        string target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name + System.IO.Path.GetExtension(path));
        if (File.Exists(target) && !string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(Msg.T($"同じ名前のファイルがあります: {System.IO.Path.GetFileName(target)}", $"A file with the same name exists: {System.IO.Path.GetFileName(target)}"));
        }

        File.Move(path, target);
        return target;
    }

    /// <summary>サンプルを利用者のスクリプトフォルダにコピーする (AUTO-46 の仕様 3。同梱のファイルは変えない)。コピー先のパスを返す。</summary>
    public static string CopySample(string samplePath, string userScriptsFolder)
    {
        Directory.CreateDirectory(userScriptsFolder);
        string ext = System.IO.Path.GetExtension(samplePath);
        string path = UniquePath(userScriptsFolder, System.IO.Path.GetFileNameWithoutExtension(samplePath), ext);
        File.Copy(samplePath, path, overwrite: false);
        // 同梱のファイルが読み取り専用でもコピーは編集できるようにする。
        File.SetAttributes(path, FileAttributes.Normal);
        return path;
    }

    private static string UniquePath(string folder, string baseName, string ext)
    {
        string path = System.IO.Path.Combine(folder, baseName + ext);
        for (int n = 2; File.Exists(path); n++)
        {
            path = System.IO.Path.Combine(folder, $"{baseName} ({n.ToString(CultureInfo.InvariantCulture)}){ext}");
        }

        return path;
    }

    /// <summary>ファイル名に使えない文字を除く。空になれば <c>script</c>。</summary>
    public static string SafeFileName(string name)
    {
        string cleaned = Regex.Replace(name.Trim(), "[" + Regex.Escape(new string(System.IO.Path.GetInvalidFileNameChars())) + "]", "_").TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "script" : cleaned;
    }

    public void Dispose()
    {
        _disposed = true;
        _debounce.Dispose();
        _poll.Dispose();
        lock (_lock)
        {
            foreach (FileSystemWatcher w in _watchers)
            {
                w.Dispose();
            }

            _watchers.Clear();
        }
    }
}
