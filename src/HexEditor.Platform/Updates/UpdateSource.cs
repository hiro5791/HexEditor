namespace HexEditor.Platform.Updates;

/// <summary>
/// 更新の配布元 (GitHub Releases と同じ形のリポジトリ。10 の PKG-17 の仕様 4・8、PKG-26)。
/// 製品の配布元は <see cref="Product"/>。テスト用のビルドは設定 <c>test.update.source</c> で差し替える。
/// <c>github.com</c> 以外のホストは、そのホストの <c>/api/v3/</c> を API の場所とする (GitHub Enterprise と同じ形)。
/// </summary>
public sealed record UpdateSource
{
    /// <summary>製品のリポジトリ。</summary>
    public const string ProductRepositoryUrl = "https://github.com/hiro5791/HexEditor";

    private UpdateSource(Uri repository, string owner, string name)
    {
        Repository = repository;
        Owner = owner;
        Name = name;
    }

    public static UpdateSource Product { get; } = Parse(ProductRepositoryUrl);

    /// <summary>リポジトリの URL (<c>https://github.com/&lt;owner&gt;/&lt;repo&gt;</c>。末尾の / なし)。</summary>
    public Uri Repository { get; }

    public string Owner { get; }

    public string Name { get; }

    public bool IsGitHubCom => Repository.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>API の場所 (末尾は /)。github.com は https://api.github.com/、それ以外は &lt;ホスト&gt;/api/v3/。</summary>
    public Uri ApiBase => IsGitHubCom
        ? new Uri("https://api.github.com/")
        : new Uri(Repository.GetLeftPart(UriPartial.Authority) + "/api/v3/");

    /// <summary>リリースの一覧の API (<c>/repos/&lt;owner&gt;/&lt;repo&gt;/releases</c>。PKG-17 の仕様 4)。</summary>
    public Uri ReleasesApi => new(ApiBase, $"repos/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Name)}/releases?per_page=30");

    /// <summary>その版のリリースのページ (「ダウンロードページを開く」「リリースノート」。PKG-20 の仕様 2、PKG-22 の仕様 2)。</summary>
    public Uri ReleasePage(SemanticVersion version) => new($"{Repository}/releases/tag/v{version.SemVer}");

    /// <summary>リリースの一覧のページ。</summary>
    public Uri ReleasesPage => new($"{Repository}/releases");

    /// <summary>
    /// <c>https://&lt;ホスト&gt;/&lt;owner&gt;/&lt;repo&gt;</c> を読む。http は 127.0.0.1 / localhost (テスト用の偽のサーバー) だけ許す。
    /// </summary>
    public static bool TryParse(string? text, out UpdateSource source)
    {
        source = null!;
        if (!Uri.TryCreate(text?.Trim().TrimEnd('/'), UriKind.Absolute, out Uri? uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            return false;
        }

        string[] parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0))
        {
            return false;
        }

        string name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        source = new UpdateSource(new Uri($"{uri.GetLeftPart(UriPartial.Authority)}/{parts[0]}/{name}"), parts[0], name);
        return true;
    }

    public static UpdateSource Parse(string text) =>
        TryParse(text, out UpdateSource source) ? source : throw new FormatException($"'{text}' is not a repository URL (https://<host>/<owner>/<repo>).");

    /// <summary>設定から使う配布元を決める (テスト用の確認先があり、正しい形ならそれ。なければ製品の配布元)。</summary>
    public static UpdateSource Resolve(UpdatePreferences preferences) =>
        preferences.TestSource is { } test && TryParse(test, out UpdateSource source) ? source : Product;
}
