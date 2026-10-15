using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HexEditor.Sdk.Hosting;

namespace HexEditor.Core.Scripting.CSharp;

/// <summary>
/// C# スクリプト コンポーネントの検証用の一覧 (AUTO-03 の仕様 3。アプリ本体に埋め込む SHA-256 の一覧)。
/// <see cref="Package"/> はダウンロードする zip、<see cref="Files"/> は展開した各ファイル (手動で展開した場合の検証に使う)。
/// </summary>
public sealed record CSharpComponentManifest
{
    /// <summary>コンポーネントのバージョン (アプリのバージョンと対にする)。</summary>
    public required string Version { get; init; }

    /// <summary>zip のファイル名。</summary>
    public required string PackageName { get; init; }

    /// <summary>zip の SHA-256 (小文字の Hex)。</summary>
    public required string PackageSha256 { get; init; }

    /// <summary>zip の大きさ (確認ダイアログに出す。目安 約 20 MB)。</summary>
    public long PackageSize { get; init; }

    /// <summary>展開したファイルの相対パス (区切りは <c>/</c>) から SHA-256。</summary>
    public required IReadOnlyDictionary<string, string> Files { get; init; }

    /// <summary>Microsoft の署名 (Authenticode) を確かめるファイル (Roslyn のアセンブリ)。</summary>
    public IReadOnlyList<string> MicrosoftSigned { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static CSharpComponentManifest Parse(string json) =>
        JsonSerializer.Deserialize<CSharpComponentManifest>(json, Json) ?? throw new JsonException("一覧が空です。");

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>展開したフォルダから一覧を作る (配布物を作るビルドとテストで使う)。</summary>
    public static CSharpComponentManifest Create(string version, string folder, string packagePath)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            files[Path.GetRelativePath(folder, file).Replace('\\', '/')] = CSharpComponentManager.Sha256Of(file);
        }

        return new CSharpComponentManifest
        {
            Version = version,
            PackageName = Path.GetFileName(packagePath),
            PackageSha256 = CSharpComponentManager.Sha256Of(packagePath),
            PackageSize = new FileInfo(packagePath).Length,
            Files = files,
            MicrosoftSigned = [.. files.Keys.Where(f => Path.GetFileName(f).StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)
                && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))],
        };
    }
}

/// <summary>コンポーネントの配布元 (更新と同じ配布元・チャネル。10 の PKG-17〜PKG-21)。テストはローカルのフォルダの偽物を使う。</summary>
public interface ICSharpComponentFeed
{
    /// <summary>通信先の説明 (確認ダイアログに出す。例: <c>github.com</c>)。</summary>
    string Host { get; }

    /// <summary>zip を <paramref name="destination"/> に書く。</summary>
    Task DownloadAsync(CSharpComponentManifest manifest, Stream destination, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Authenticode の署名の確認 (10 の PKG-17 の仕様 7。App は WinVerifyTrust を使う。テストは偽物)。</summary>
public interface IAuthenticodeVerifier
{
    /// <summary>Microsoft の有効な署名があれば true。失敗の理由を <paramref name="error"/> に入れる。</summary>
    bool IsMicrosoftSigned(string path, out string? error);
}

/// <summary>コンポーネントの追加・読み込みの失敗 (AUTO-03 の「エラー」)。<see cref="ManualInstructions"/> は手動で入れる手順を案内するか。</summary>
public sealed class CSharpComponentException(string message, bool manualInstructions = true, Exception? inner = null) : Exception(message, inner)
{
    public bool ManualInstructions { get; } = manualInstructions;
}

/// <summary>
/// C# スクリプト コンポーネント (Roslyn) の追加・検証・読み込み・削除 (AUTO-03 の仕様 3・4)。置き場所はデータフォルダの
/// <c>components\csharp\&lt;バージョン&gt;\</c>。コンポーネントのバージョンはアプリのバージョンと対にする。読み込む前に、埋め込んだ一覧の
/// SHA-256 と Roslyn のアセンブリの Microsoft の署名を確かめる。
/// </summary>
public sealed class CSharpComponentManager
{
    /// <summary>コンポーネントの入口のアセンブリ。</summary>
    public const string EntryAssembly = "HexEditor.Scripting.CSharp.dll";

    private readonly Lock _lock = new();
    private ICSharpScriptBackend? _backend;
    private string? _verifiedFolder;

    /// <param name="componentsRoot">データフォルダの <c>components</c>。</param>
    /// <param name="manifest">アプリに埋め込んだ一覧 (なければ null。その場合は追加できない)。</param>
    /// <param name="downloadsAllowed"><c>network.offline</c> と <c>network.downloads.enabled</c> (09 の UI-58)。</param>
    public CSharpComponentManager(string componentsRoot, CSharpComponentManifest? manifest, ICSharpComponentFeed? feed, IAuthenticodeVerifier verifier,
        Func<bool> downloadsAllowed)
    {
        Folder = Path.Combine(componentsRoot, "csharp");
        Manifest = manifest;
        Feed = feed;
        Verifier = verifier;
        DownloadsAllowed = downloadsAllowed;
    }

    /// <summary><c>components\csharp\</c>。</summary>
    public string Folder { get; }

    public CSharpComponentManifest? Manifest { get; }

    public ICSharpComponentFeed? Feed { get; }

    public IAuthenticodeVerifier Verifier { get; }

    public Func<bool> DownloadsAllowed { get; }

    /// <summary>このアプリが使うバージョンのフォルダ。</summary>
    public string? VersionFolder => Manifest is null ? null : Path.Combine(Folder, Manifest.Version);

    /// <summary>置いてあるバージョン (フォルダの名前)。</summary>
    public IReadOnlyList<string> InstalledVersions =>
        Directory.Exists(Folder) ? [.. Directory.EnumerateDirectories(Folder).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)] : [];

    /// <summary>このアプリと対になるバージョンが置いてあるか (検証はしない。手動で展開したものも認識する)。</summary>
    public bool IsInstalled => VersionFolder is string f && File.Exists(Path.Combine(f, EntryAssembly));

    /// <summary>古いバージョンだけが置いてある (アプリの更新に合わせて更新が必要)。</summary>
    public bool NeedsUpdate => !IsInstalled && InstalledVersions.Count > 0;

    /// <summary>追加の確認ダイアログに出す内容 (サイズ・保存先・通信先。AUTO-03 の仕様 3)。</summary>
    public (long Size, string Destination, string Host) DownloadInfo =>
        (Manifest?.PackageSize ?? 0, VersionFolder ?? Folder, Feed?.Host ?? "");

    /// <summary>
    /// ダウンロードして入れる。オフラインモード・ダウンロードの無効化のときはダウンロードせず、手動で入れる手順を案内する例外にする。
    /// </summary>
    public async Task InstallAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        CSharpComponentManifest manifest = Manifest ?? throw new CSharpComponentException("このアプリにはコンポーネントの検証用の一覧がありません。");
        if (!DownloadsAllowed())
        {
            throw new CSharpComponentException("オフラインモード、またはダウンロードが無効のため、コンポーネントをダウンロードできません。手動で zip を展開してください。");
        }

        ICSharpComponentFeed feed = Feed ?? throw new CSharpComponentException("コンポーネントの配布元が設定されていません。");
        string temp = Path.Combine(Path.GetTempPath(), "HexEditor", "component-" + Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                try
                {
                    await feed.DownloadAsync(manifest, stream, progress, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    throw new CSharpComponentException($"ダウンロードに失敗しました: {ex.Message}", true, ex);
                }
            }

            await InstallFromZipAsync(temp, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>
    /// zip から入れる (手動で入れる場合・ダウンロードした場合)。zip の SHA-256 が一覧と違えば入れない (改ざんの検出)。
    /// 展開したファイルの SHA-256 と署名も確かめてから置き場所に移す。
    /// </summary>
    public Task InstallFromZipAsync(string zipPath, CancellationToken cancellationToken) => Task.Run(() =>
    {
        CSharpComponentManifest manifest = Manifest ?? throw new CSharpComponentException("このアプリにはコンポーネントの検証用の一覧がありません。");
        string hash = Sha256Of(zipPath);
        if (!string.Equals(hash, manifest.PackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CSharpComponentException($"コンポーネントの zip のハッシュが一致しません (改ざんされているか、別のバージョンです)。期待: {manifest.PackageSha256}、実際: {hash}");
        }

        string staging = Path.Combine(Folder, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            try
            {
                ZipFile.ExtractToDirectory(zipPath, staging);
            }
            catch (InvalidDataException ex)
            {
                throw new CSharpComponentException($"コンポーネントの zip を展開できません: {ex.Message}", true, ex);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Verify(staging, manifest);
            string target = Path.Combine(Folder, manifest.Version);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(staging, target);
            lock (_lock)
            {
                _verifiedFolder = null;
            }

            // 古いバージョンは消す (アプリと対のものだけを使う)。
            foreach (string old in Directory.EnumerateDirectories(Folder))
            {
                string name = Path.GetFileName(old);
                if (name != manifest.Version && !name.StartsWith('.'))
                {
                    TryDelete(old);
                }
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                TryDelete(staging);
            }
        }
    }, cancellationToken);

    /// <summary>フォルダのファイルを一覧と署名で確かめる。違いがあれば例外。</summary>
    public void Verify(string folder, CSharpComponentManifest manifest)
    {
        foreach ((string relative, string expected) in manifest.Files)
        {
            string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                throw new CSharpComponentException($"コンポーネントのファイルが足りません: {relative}");
            }

            string actual = Sha256Of(path);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new CSharpComponentException($"コンポーネントのファイルのハッシュが一致しません (改ざんの可能性): {relative}");
            }
        }

        foreach (string relative in manifest.MicrosoftSigned)
        {
            string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!Verifier.IsMicrosoftSigned(path, out string? error))
            {
                throw new CSharpComponentException($"コンポーネントのファイルの署名を確認できません: {relative} ({error})");
            }
        }
    }

    /// <summary>削除する (設定画面「スクリプト > C#」)。</summary>
    public void Remove()
    {
        lock (_lock)
        {
            if (_backend is not null)
            {
                throw new CSharpComponentException("コンポーネントは使用中です。アプリを再起動してから削除してください。", manualInstructions: false);
            }

            _verifiedFolder = null;
        }

        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }
    }

    /// <summary>
    /// コンポーネントを読み込む (アプリの終了まで保つ)。読み込む前にファイルのハッシュと署名を確かめる (手動で展開したものも同じ)。
    /// </summary>
    public ICSharpScriptBackend LoadBackend()
    {
        lock (_lock)
        {
            if (_backend is not null)
            {
                return _backend;
            }

            CSharpComponentManifest manifest = Manifest ?? throw new CSharpComponentException("このアプリにはコンポーネントの検証用の一覧がありません。");
            string folder = VersionFolder!;
            if (!IsInstalled)
            {
                throw new CSharpComponentException("C# スクリプト コンポーネントがありません。");
            }

            if (_verifiedFolder != folder)
            {
                Verify(folder, manifest);
                _verifiedFolder = folder;
            }

            var alc = new ComponentLoadContext(folder);
            Assembly entry = alc.LoadFromAssemblyPath(Path.Combine(folder, EntryAssembly));
            Type type = entry.GetExportedTypes().FirstOrDefault(t => typeof(ICSharpScriptBackend).IsAssignableFrom(t) && !t.IsAbstract)
                ?? throw new CSharpComponentException("コンポーネントに入口がありません。");
            _backend = (ICSharpScriptBackend)Activator.CreateInstance(type)!;
            return _backend;
        }
    }

    /// <summary>ファイルの SHA-256 (小文字の Hex)。</summary>
    public static string Sha256Of(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// コンポーネントの読み込みの文脈。Roslyn とコンポーネント自身はフォルダから読み、SDK と .NET の基本ライブラリはアプリのものを共有する
    /// (同じ型にするため)。
    /// </summary>
    private sealed class ComponentLoadContext(string folder) : AssemblyLoadContext("HexEditor C# component", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string name = assemblyName.Name ?? "";
            if (name.StartsWith("HexEditor.Sdk", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string path = Path.Combine(folder, name + ".dll");
            if (File.Exists(path) && (name.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("HexEditor.Scripting", StringComparison.OrdinalIgnoreCase)))
            {
                return LoadFromAssemblyPath(path);
            }

            // それ以外 (System.Collections.Immutable など) はアプリにあればそれを使い、なければフォルダから読む。
            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch (FileNotFoundException)
            {
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }
        }
    }
}
