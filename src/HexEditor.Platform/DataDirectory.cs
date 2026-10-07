using Microsoft.Win32.SafeHandles;

namespace HexEditor.Platform;

/// <summary>データフォルダと一時フォルダの準備と後始末 (PKG-05 の仕様 3、PKG-06 の仕様 4・5、PKG-13 の仕様 4)。</summary>
public static class DataDirectory
{
    public const string WriteTestFileName = ".write-test";

    /// <summary>
    /// フォルダを作り (初回起動)、<c>.write-test</c> を作って消せるかで書き込めるかを確かめる (PKG-06 の仕様 5)。
    /// </summary>
    public static bool TryPrepare(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, WriteTestFileName);
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 一時フォルダの中身のうち、実行中の他のインスタンスが使っていないものを消す (PKG-13 の仕様 4)。
    /// 開かれている (ロックされた) ファイルを含むフォルダは丸ごと残す。<paramref name="keep"/> (一時フォルダに置いた
    /// 復旧用データ。PKG-06 の仕様 5 の 3) は消さない。消した項目の数を返す。
    /// </summary>
    public static int CleanTemp(string temp, string? keep = null)
    {
        if (!Directory.Exists(temp))
        {
            return 0;
        }

        int deleted = 0;
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(temp).ToList())
            {
                if (keep is not null && IsSameOrUnder(keep, entry))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    if (!ContainsLockedFile(entry) && TryDeleteDirectory(entry))
                    {
                        deleted++;
                    }
                }
                else if (TryDeleteFile(entry))
                {
                    deleted++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return deleted;
    }

    /// <summary>
    /// 終了時に一時フォルダを消す (ポータブル版の <c>%TEMP%\HexEditor-&lt;ハッシュ&gt;\</c>。PKG-06 の仕様 4)。
    /// 他のインスタンスが使っているものは残し、空になったらフォルダごと消す。
    /// </summary>
    public static void DeleteTempAtExit(string temp)
    {
        CleanTemp(temp);
        try
        {
            if (Directory.Exists(temp) && !Directory.EnumerateFileSystemEntries(temp).Any())
            {
                Directory.Delete(temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>exe のフォルダのパスのハッシュ 8 桁 (SHA-256 の先頭。大文字小文字と末尾の区切りを無視する。PKG-11、PKG-13)。</summary>
    public static string FolderHash(string folder)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)).ToUpperInvariant();
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..8];
    }

    private static bool IsSameOrUnder(string path, string entry)
    {
        string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsLockedFile(string folder)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    using SafeFileHandle handle = File.OpenHandle(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryDeleteDirectory(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
