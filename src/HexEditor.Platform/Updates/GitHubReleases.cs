using System.Text.Json;
using HexEditor.Platform.Network;

namespace HexEditor.Platform.Updates;

/// <summary>リリースのファイル 1 つ。</summary>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

/// <summary>GitHub Releases のリリース 1 つ (版はタグ <c>v&lt;SemVer&gt;</c> から。PKG-28)。</summary>
public sealed record ReleaseInfo(SemanticVersion Version, string Tag, bool IsPrerelease, bool IsDraft, Uri? PageUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>安定版のチャネルで受け取れるか (PKG-21 の仕様 1: 版に -preview.N がなく、Pre-release でない)。</summary>
    public bool IsStable => Version.Channel == ReleaseChannel.Stable && !IsPrerelease;
}

/// <summary>
/// GitHub Releases の API (<c>GET /repos/&lt;owner&gt;/&lt;repo&gt;/releases</c>) でリリースの一覧を取る (10 の PKG-17 の仕様 4、PKG-20)。
/// 認証は使わない (利用者の識別子を送らない。仕様 5)。
/// </summary>
public static class GitHubReleases
{
    /// <summary>API の応答 (JSON の配列) を読む。版として読めないタグ・ローカルのビルドは除く。</summary>
    public static IReadOnlyList<ReleaseInfo> Parse(string json)
    {
        var releases = new List<ReleaseInfo>();
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("The releases API did not return an array.");
        }

        foreach (JsonElement r in doc.RootElement.EnumerateArray())
        {
            string tag = r.TryGetProperty("tag_name", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : string.Empty;
            if (!tag.StartsWith('v') || !SemanticVersion.TryParse(tag[1..], out SemanticVersion version, out _) || version.IsLocal)
            {
                continue;
            }

            Uri? page = r.TryGetProperty("html_url", out JsonElement h) && h.ValueKind == JsonValueKind.String
                && Uri.TryCreate(h.GetString(), UriKind.Absolute, out Uri? u) ? u : null;
            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out JsonElement a) && a.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in a.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out JsonElement n) && asset.TryGetProperty("browser_download_url", out JsonElement d)
                        && Uri.TryCreate(d.GetString(), UriKind.Absolute, out Uri? url))
                    {
                        long size = asset.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long z) ? z : 0;
                        assets.Add(new ReleaseAsset(n.GetString() ?? string.Empty, url, size));
                    }
                }
            }

            releases.Add(new ReleaseInfo(version, tag, Bool(r, "prerelease"), Bool(r, "draft"), page, assets));
        }

        return releases;
    }

    /// <summary>リリースの一覧を取る。失敗は例外 (<see cref="UpdateService"/> が理由に直す)。</summary>
    public static async Task<IReadOnlyList<ReleaseInfo>> FetchAsync(NetworkClient network, UpdateSource source, bool manual, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "application/vnd.github+json",
            ["X-GitHub-Api-Version"] = "2022-11-28",
        };
        string json = await network.GetStringAsync(NetworkFeature.Updates, source.ReleasesApi, manual, headers, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return Parse(json);
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
}
