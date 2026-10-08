using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Sources;

/// <summary>
/// 開くときに、ファイルに書き込めるかを確かめる (ENG-14 の仕様 1 の 2 つ目、ENG-11 の「エラー」の「書き込みはできないが読み取りはできる」)。
/// 書き込めなければ読み取り専用の理由を返す。ファイルの内容・更新日時は変えない (書き込み用のハンドルを開いてすぐ閉じるだけ)。
/// </summary>
public static class FileWriteProbe
{
    private const int SharingViolationCode = unchecked((int)0x80070020);
    private const int LockViolationCode = unchecked((int)0x80070021);
    private const int WriteProtectCode = unchecked((int)0x80070013);

    /// <summary>
    /// 書き込めない理由。書き込めれば <see cref="ReadOnlyReason.None"/>。確かめる順は、読み取り専用のメディア (解除できない)、読み取り専用属性
    /// (承認すれば保存時に外せる)、書き込みで開けるか (権限がない・他のアプリが書き込みを共有せずに開いている)。
    /// </summary>
    /// <param name="hasReadOnlyAttribute">ファイルに読み取り専用属性がある (<see cref="FileByteSource.HasReadOnlyAttribute"/>)。</param>
    /// <param name="volumes">ボリュームの情報 (テストで差し替える)。</param>
    /// <param name="openForWrite">書き込み用にハンドルを開く (テストで差し替える)。既定は読み書き・削除を共有して開く。</param>
    public static ReadOnlyReason Probe(string path, bool hasReadOnlyAttribute, IVolumeInfoProvider? volumes = null,
        Func<string, SafeFileHandle>? openForWrite = null)
    {
        string full = Path.GetFullPath(path);
        if (IsOnReadOnlyVolume(full, volumes ?? SystemVolumeInfoProvider.Instance))
        {
            return ReadOnlyReason.ReadOnlyMedia;
        }

        if (hasReadOnlyAttribute)
        {
            return ReadOnlyReason.FileAttribute;
        }

        try
        {
            using SafeFileHandle handle = (openForWrite ?? OpenForWrite)(full);
            return ReadOnlyReason.None;
        }
        catch (UnauthorizedAccessException)
        {
            return ReadOnlyReason.AccessDenied;
        }
        catch (IOException ex) when (ex.HResult is SharingViolationCode or LockViolationCode)
        {
            return ReadOnlyReason.SharingViolation;
        }
        catch (IOException ex) when (ex.HResult == WriteProtectCode)
        {
            return ReadOnlyReason.ReadOnlyMedia;
        }
        catch (IOException)
        {
            // 理由が分からない: 編集はできるようにし、保存のときのエラーで知らせる。
            return ReadOnlyReason.None;
        }
    }

    /// <summary>
    /// 書き込み用に開く。他のアプリの読み書き・削除を妨げない共有で開く (自分の読み取りのハンドルとも両立する)。他のアプリが書き込みを
    /// 共有せずに開いていれば共有違反になる。
    /// </summary>
    private static SafeFileHandle OpenForWrite(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    private static bool IsOnReadOnlyVolume(string path, IVolumeInfoProvider volumes)
    {
        try
        {
            return Path.GetDirectoryName(path) is { } folder && volumes.GetVolume(folder) is { IsReadOnly: true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
