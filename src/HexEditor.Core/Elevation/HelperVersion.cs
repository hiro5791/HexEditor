namespace HexEditor.Core.Elevation;

/// <summary>本体と補助プロセスの版の照合 (Hello) に使う版の形。</summary>
public static class HelperVersion
{
    /// <summary>
    /// InformationalVersion から SemVer の部分 (ビルドのメタデータ「+コミット」を除いたもの) を取り出す。本体が送る版
    /// (AppEnvironment.AppVersion) と同じ形。null・空なら空文字列。
    /// </summary>
    public static string SemVerPart(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return string.Empty;
        }

        string text = informationalVersion.Trim();
        int plus = text.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? text[..plus] : text;
    }
}
