using System.Globalization;
using System.Text.RegularExpressions;

namespace HexEditor.Platform;

/// <summary>リリースチャネル (PKG-21、PKG-28: 版にプレリリースの部分があればプレビュー版)。</summary>
public enum ReleaseChannel
{
    Stable,
    Preview,
}

/// <summary>
/// アプリの版 (PKG-28)。<c>MAJOR.MINOR.PATCH</c>、<c>MAJOR.MINOR.PATCH-preview.N</c> (N は 1〜998)、
/// タグのないローカルのビルドの <c>MAJOR.MINOR.PATCH-local</c> を扱う。
/// 対応表の計算は Directory.Build.props にも同じものがある (ビルド時に使う)。両方を同時に直すこと。
/// </summary>
public sealed partial record SemanticVersion(int Major, int Minor, int Patch, int? Preview, bool IsLocal, string? Commit)
    : IComparable<SemanticVersion>
{
    /// <summary>PATCH の上限 (MSIX の第 3 の数 PATCH×1000+999 が 65535 を超えないため)。</summary>
    public const int MaxPatch = 64;

    /// <summary>プレビューの番号 N の上限 (999 は安定版に使う)。</summary>
    public const int MaxPreview = 998;

    /// <summary>MAJOR と MINOR の上限 (MSIX の版の各数は 16 bit)。</summary>
    public const int MaxMajorMinor = 65535;

    /// <summary>タグのないローカルのビルドの版。</summary>
    public static SemanticVersion Local { get; } = new(0, 0, 0, null, true, null);

    /// <summary>SemVer の部分 (ビルドのメタデータ「+コミット」は含めない)。Velopack の版にも使う。</summary>
    public string SemVer => $"{Major}.{Minor}.{Patch}" + (Preview is { } n ? $"-preview.{n}" : IsLocal ? "-local" : string.Empty);

    /// <summary>InformationalVersion (バージョン情報の表示): SemVer + <c>+&lt;コミットの短いハッシュ&gt;</c>。</summary>
    public string Informational => Commit is null ? SemVer : $"{SemVer}+{Commit}";

    public ReleaseChannel Channel => Preview is null && !IsLocal ? ReleaseChannel.Stable : ReleaseChannel.Preview;

    /// <summary>
    /// MSIX の <c>Identity Version</c> と <c>FileVersion</c>: <c>MAJOR.MINOR.(PATCH×1000 + P).0</c>。
    /// P はプレビューなら N、安定版なら 999、ローカルのビルドは 0。プレビュー &lt; 安定版 &lt; 次の修正版 の順になる。
    /// </summary>
    public Version MsixVersion => new(Major, Minor, (Patch * 1000) + (Preview ?? (IsLocal ? 0 : 999)), 0);

    /// <summary>AssemblyVersion: <c>MAJOR.MINOR.0.0</c>。</summary>
    public Version AssemblyVersion => new(Major, Minor, 0, 0);

    public override string ToString() => Informational;

    /// <summary>
    /// 版の順序 (更新の判定。PKG-17、PKG-21): MAJOR、MINOR、PATCH の順に比べ、同じなら ローカル &lt; プレビュー (N の順) &lt; 安定版。
    /// コミット (<c>+</c> の後) は比べない (SemVer 2.0 のビルドのメタデータ)。MSIX の版の順序 (<see cref="MsixVersion"/>) と同じ。
    /// </summary>
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int c = Major.CompareTo(other.Major);
        if (c == 0)
        {
            c = Minor.CompareTo(other.Minor);
        }

        if (c == 0)
        {
            c = Patch.CompareTo(other.Patch);
        }

        return c != 0 ? c : Rank(this).CompareTo(Rank(other));

        static int Rank(SemanticVersion v) => v.Preview ?? (v.IsLocal ? 0 : int.MaxValue);
    }

    /// <summary>SemVer の部分 (コミットを除く) が同じか。</summary>
    public bool SameVersion(SemanticVersion other) => CompareTo(other) == 0;

    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;

    /// <summary>
    /// MSIX の版 (<see cref="MsixVersion"/> の形) から SemVer に戻す (Microsoft Store が返す新しい版の表示。PKG-19)。
    /// 第 3 の数の下 3 桁が 999 なら安定版、1〜998 ならプレビュー版、0 ならローカルのビルド。
    /// </summary>
    public static SemanticVersion FromMsixVersion(Version msix)
    {
        int patch = Math.Max(0, msix.Build) / 1000;
        int p = Math.Max(0, msix.Build) % 1000;
        return p switch
        {
            999 => new SemanticVersion(msix.Major, msix.Minor, patch, null, false, null),
            0 => new SemanticVersion(msix.Major, msix.Minor, patch, null, true, null),
            _ => new SemanticVersion(msix.Major, msix.Minor, patch, p, false, null),
        };
    }

    /// <summary>
    /// 版の文字列 (タグの <c>v</c> は除いたもの。<c>+コミット</c> は付いていてもよい) を読む。
    /// 形式が違う、または上限を超える場合は false と理由を返す (リリースのビルドを失敗にする。PKG-28 の「エラー」)。
    /// </summary>
    public static bool TryParse(string? text, out SemanticVersion version, out string error)
    {
        version = Local;
        error = string.Empty;
        Match m = Pattern().Match(text ?? string.Empty);
        if (!m.Success)
        {
            error = $"'{text}' is not MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-preview.N.";
            return false;
        }

        if (!int.TryParse(m.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(m.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
            || !int.TryParse(m.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch)
            || major > MaxMajorMinor || minor > MaxMajorMinor)
        {
            error = $"'{text}': MAJOR and MINOR must be 0-{MaxMajorMinor}.";
            return false;
        }

        if (patch > MaxPatch)
        {
            error = $"'{text}': PATCH must be 0-{MaxPatch}.";
            return false;
        }

        int? preview = null;
        if (m.Groups["preview"].Success)
        {
            if (!int.TryParse(m.Groups["preview"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n is < 1 or > MaxPreview)
            {
                error = $"'{text}': the preview number must be 1-{MaxPreview}.";
                return false;
            }

            preview = n;
        }

        string? commit = m.Groups["commit"].Success ? m.Groups["commit"].Value : null;
        version = new SemanticVersion(major, minor, patch, preview, m.Groups["local"].Success, commit);
        return true;
    }

    public static SemanticVersion Parse(string text) =>
        TryParse(text, out SemanticVersion version, out string error) ? version : throw new FormatException(error);

    /// <summary>InformationalVersion を読む。読めなければ <see cref="Local"/>。</summary>
    public static SemanticVersion FromInformationalVersion(string? informational) =>
        TryParse(informational, out SemanticVersion version, out _) ? version : Local;

    [GeneratedRegex(@"^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-preview\.(?<preview>[0-9]+)|(?<local>-local))?(?:\+(?<commit>[0-9A-Za-z.-]+))?$")]
    private static partial Regex Pattern();
}
