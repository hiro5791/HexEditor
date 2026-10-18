using System.Globalization;
using System.Text;
using HexEditor.Core.Hashing;

namespace HexEditor.Core.Tools.Checksums;

/// <summary>チェックサムファイルの形式 (TOOL-18 の仕様 3)。</summary>
public enum ChecksumFormat
{
    /// <summary>GNU (coreutils): 各行 <c>ハッシュ値  相対パス</c>。</summary>
    Gnu,

    /// <summary>BSD: 各行 <c>SHA256 (相対パス) = ハッシュ値</c>。</summary>
    Bsd,

    /// <summary>ファイルごと (サイドカー): 対象ごとに <c>ファイル名.sha256</c> を作る (中身は GNU 形式の 1 行)。</summary>
    Sidecar,

    /// <summary>SFV: 各行 <c>相対パス ハッシュ値</c> (CRC-32 のみ)。</summary>
    Sfv,
}

/// <summary>出力の改行 (TOOL-18 の仕様 5)。</summary>
public enum ChecksumLineEnding
{
    Lf,
    CrLf,
}

/// <summary>パスの区切り文字 (TOOL-18 の仕様 4)。</summary>
public enum ChecksumSeparator
{
    /// <summary><c>/</c> (既定。他の OS の <c>sha256sum</c> で検証できる)。</summary>
    Slash,

    /// <summary><c>\</c>。</summary>
    Backslash,
}

/// <summary>チェックサムファイルの 1 行ぶんの内容 (書き出し用)。</summary>
public sealed record ChecksumItem(string RelativePath, byte[] Value);

/// <summary>チェックサムファイルの書き出し (TOOL-18)。文字コードは UTF-8 (BOM なし)。</summary>
public static class ChecksumWriter
{
    /// <summary>UTF-8 (BOM なし。仕様 5)。</summary>
    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>SFV が使えるアルゴリズムか (CRC-32 のみ。仕様 3)。</summary>
    public static bool SupportsSfv(string algorithmId) => string.Equals(algorithmId, "crc32", StringComparison.OrdinalIgnoreCase);

    /// <summary>BSD 形式の行頭のアルゴリズム名 (<c>SHA256</c>、<c>MD5</c> など)。</summary>
    public static string BsdTag(HashAlgorithmInfo algorithm) =>
        algorithm.Id.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    /// <summary>
    /// 既定のファイル名 (TOOL-18 の仕様 3 の表)。GNU は <c>SHA256SUMS</c>、BSD は <c>CHECKSUM.SHA256</c>、SFV は <c>フォルダ名.sfv</c>、
    /// サイドカーは <paramref name="targetFileName"/> に拡張子を付けたもの。
    /// </summary>
    public static string DefaultFileName(ChecksumFormat format, HashAlgorithmInfo algorithm, string? folderName = null, string? targetFileName = null)
    {
        string id = algorithm.Id.Replace("-", string.Empty, StringComparison.Ordinal);
        return format switch
        {
            ChecksumFormat.Gnu => id.ToUpperInvariant() + "SUMS",
            ChecksumFormat.Bsd => "CHECKSUM." + id.ToUpperInvariant(),
            ChecksumFormat.Sfv => (string.IsNullOrEmpty(folderName) ? "checksums" : folderName) + ".sfv",
            _ => (targetFileName ?? "file") + SidecarExtension(algorithm),
        };
    }

    /// <summary>サイドカーの拡張子 (<c>.sha256</c>、<c>.md5</c> など。CRC-32 は <c>.sfv</c>)。</summary>
    public static string SidecarExtension(HashAlgorithmInfo algorithm) => HashExport.ChecksumExtension(algorithm);

    /// <summary>パスの区切りを揃える。</summary>
    public static string NormalizePath(string relativePath, ChecksumSeparator separator) =>
        separator == ChecksumSeparator.Slash
            ? relativePath.Replace('\\', '/')
            : relativePath.Replace('/', '\\');

    /// <summary>1 つのチェックサムファイルの内容を作る (サイドカーは 1 行の GNU 形式)。</summary>
    public static string Build(ChecksumFormat format, HashAlgorithmInfo algorithm, IEnumerable<ChecksumItem> items, ChecksumSeparator separator,
        ChecksumLineEnding lineEnding, bool binaryMark)
    {
        string newline = lineEnding == ChecksumLineEnding.CrLf ? "\r\n" : "\n";
        var sb = new StringBuilder();
        foreach (ChecksumItem item in items)
        {
            string path = NormalizePath(item.RelativePath, separator);
            string hex = format == ChecksumFormat.Sfv ? Convert.ToHexString(item.Value) : Convert.ToHexStringLower(item.Value);
            switch (format)
            {
                case ChecksumFormat.Bsd:
                    sb.Append(BsdTag(algorithm)).Append(" (").Append(path).Append(") = ").Append(hex);
                    break;
                case ChecksumFormat.Sfv:
                    sb.Append(path).Append(' ').Append(hex);
                    break;
                default:
                    sb.Append(hex).Append(binaryMark ? " *" : "  ").Append(path);
                    break;
            }

            sb.Append(newline);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 一時ファイルに書いてから置き換える (途中で失敗しても元のファイルを壊さない)。呼び出し側が上書きの確認を済ませていること
    /// (仕様 6)。
    /// </summary>
    public static void WriteFile(string path, string content)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        try
        {
            File.WriteAllText(temp, content, Utf8);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
