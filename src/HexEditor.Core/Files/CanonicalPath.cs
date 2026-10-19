using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Files;

/// <summary>
/// 同じファイルを指す別の書き方 (<c>\\?\</c> の接頭辞、ジャンクション・シンボリックリンクを通るパス、8.3 の短い名前、<c>..</c>) を
/// 1 つの形にそろえる (12 の FOR-01 の保護の対象・証拠の照合、出力先の検査)。ある部分 (いちばん深い既存のフォルダ・ファイル) は
/// <c>GetFinalPathNameByHandleW</c> でリンクをたどった本当のパスにし、まだない残りの部分はそのまま付ける。
/// ハンドルは属性の読み取りだけの権限 (0) で開き、内容は読まない (証拠の最終アクセス時刻を変えない)。
/// デバイスのパス (<c>\\.\</c>) と UNC のパス (ネットワークの応答を待たないため) はたどらない。
/// </summary>
public static class CanonicalPath
{
    /// <summary>そろえたパス。形として不正なら <paramref name="path"/> を前後の空白を除いて返す。</summary>
    public static string Normalize(string path) => TryNormalize(path, out string result) ? result : path.Trim();

    /// <summary>
    /// そろえたパス。形として不正ならば false。リンクをたどれなかった (アクセスが拒否されたなど) 部分は、
    /// <see cref="Path.GetFullPath(string)"/> の形のまま返す (<paramref name="resolved"/> が false)。
    /// </summary>
    public static bool TryNormalize(string path, out string result) => TryNormalize(path, out result, out _);

    /// <param name="resolved">リンクをたどって確かめられた (いちばん深い既存の部分の本当のパスが分かった)。</param>
    public static bool TryNormalize(string path, out string result, out bool resolved)
    {
        resolved = false;
        result = path.Trim();
        if (result.Length == 0)
        {
            return false;
        }

        // デバイスのパスはそのまま (開かない)。
        if (result.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return true;
        }

        string full;
        try
        {
            full = Path.GetFullPath(StripLongPrefix(result));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        result = full;
        if (!OperatingSystem.IsWindows() || full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        // 代替データストリーム (file:stream) の ':' 以降は、ファイルの部分をたどった後にそのまま付ける。
        string stream = string.Empty;
        string fileName = Path.GetFileName(full);
        if (fileName.IndexOf(':') is int colon and >= 0)
        {
            stream = fileName[colon..];
            full = full[..^stream.Length];
        }

        // いちばん深い既存の部分をたどる。
        string existing = full;
        string rest = string.Empty;
        for (int guard = 0; guard < 4096; guard++)
        {
            if (FinalPath(existing) is { } final)
            {
                result = (rest.Length == 0 ? final : Path.Join(final, rest)) + stream;
                resolved = true;
                return true;
            }

            string? parent = Path.GetDirectoryName(existing);
            if (parent is null)
            {
                return true;
            }

            string name = existing[(parent.Length + (parent.EndsWith('\\') ? 0 : 1))..];
            rest = rest.Length == 0 ? name : Path.Join(name, rest);
            existing = parent;
        }

        return true;
    }

    /// <summary><c>\\?\C:\…</c> → <c>C:\…</c>、<c>\\?\UNC\server\…</c> → <c>\\server\…</c>。それ以外 (<c>\\?\Volume{…}</c> など) はそのまま。</summary>
    public static string StripLongPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        if (path.Length >= 6 && path.StartsWith(@"\\?\", StringComparison.Ordinal) && char.IsAsciiLetter(path[4]) && path[5] == ':')
        {
            return path[4..];
        }

        return path;
    }

    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>既存のファイル・フォルダの本当のパス (DOS の形)。開けない・DOS の形にできなければ null。</summary>
    private static string? FinalPath(string path)
    {
        // 属性の読み取りだけ (アクセス権 0)。フォルダも開けるように FILE_FLAG_BACKUP_SEMANTICS。
        using SafeFileHandle handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        char[] buffer = new char[512];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (n == 0)
            {
                return null;
            }

            if (n < buffer.Length)
            {
                string final = StripLongPrefix(new string(buffer, 0, (int)n));
                return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? null : final;
            }

            if (n > short.MaxValue)
            {
                return null;
            }

            buffer = new char[n + 1];
        }

        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
}
