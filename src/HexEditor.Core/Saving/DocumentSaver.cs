using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Saving;

/// <summary>保存先のドライブの空き容量が足りない (ENG-25)。</summary>
public sealed class InsufficientSpaceException(string drive, long required, long available)
    : IOException($"保存先のドライブ ({drive}) の空き容量が足りません。必要: {required:N0} バイト、空き: {available:N0} バイト")
{
    public string Drive { get; } = drive;

    public long Required { get; } = required;

    public long Available { get; } = available;
}

/// <summary>
/// 保存先のファイルシステムの 1 ファイルの上限を超える (ENG-20 の「エラー」、ENG-25 の仕様 5)。UI は「このドライブのファイルシステム
/// (FAT32) では 4 GiB 以上のファイルを保存できません」と示す。
/// </summary>
public sealed class FileSizeLimitException(string drive, string fileSystem, long maxFileSize, long length)
    : IOException($"このドライブ ({drive}) のファイルシステム ({fileSystem}) では {maxFileSize:N0} バイトを超えるファイルを保存できません。サイズ: {length:N0} バイト")
{
    public string Drive { get; } = drive;

    public string FileSystem { get; } = fileSystem;

    public long MaxFileSize { get; } = maxFileSize;

    public long Length { get; } = length;
}

/// <summary>読めない元データを含むため保存できない (ENG-20 の仕様 3)。</summary>
public sealed class UnreadableDataException(IReadOnlyList<UnreadableRange> ranges)
    : IOException($"読めないデータが {ranges.Count} か所あるため保存できません。")
{
    public IReadOnlyList<UnreadableRange> Ranges { get; } = ranges;
}

/// <summary>
/// 安全な保存 (ENG-22): 保存先と同じフォルダの一時ファイルに書き出してから置き換える。途中で失敗・キャンセルしても
/// 保存先のファイルは保存前のまま残る。
/// </summary>
public static class DocumentSaver
{
    /// <summary>保存時に確保しておく空き容量の余裕 (ENG-25)。</summary>
    public const long FreeSpaceMargin = 16L * 1024 * 1024;

    private const int BufferSize = 4 * 1024 * 1024;

    /// <summary>
    /// <paramref name="snapshot"/> を <paramref name="targetPath"/> に書き出す。成功したら保存したファイルを開いて返す
    /// (呼び出し側は UI スレッドで <see cref="Document.CompleteSave"/> を呼ぶ)。
    /// </summary>
    public static FileByteSource Save(DocumentSnapshot snapshot, string targetPath, LongRunningOperation? operation = null,
        IVolumeInfoProvider? volumes = null)
    {
        string target = ResolveTarget(Path.GetFullPath(targetPath));
        string folder = Path.GetDirectoryName(target)!;
        VolumeInfo? volume = (volumes ?? SystemVolumeInfoProvider.Instance).GetVolume(folder);
        CheckFileSizeLimit(volume, snapshot.Length);
        CheckFreeSpace(volume, snapshot.Length);

        string temp = Path.Combine(folder, $".{Path.GetFileName(target)}.~hex{RandomNumberGenerator.GetHexString(8, lowercase: true)}.tmp");
        try
        {
            WriteTemp(snapshot, temp, operation);

            // 手順 7: 自分の書き込み禁止のハンドル (ENG-15) を閉じる。保存の完了で元の方針に戻す。
            snapshot.Storage.Owner.SuspendLock();
            if (File.Exists(target))
            {
                File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, target);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return FileByteSource.Open(target);
    }

    /// <summary>
    /// 保存先のドライブの空き容量を確認する (ENG-25)。ネットワーク上の場所も確認し、空き容量を取得できない場合だけ省略する
    /// (書き込み時のエラーで扱う)。
    /// </summary>
    public static void CheckFreeSpace(string folder, long length, IVolumeInfoProvider? volumes = null) =>
        CheckFreeSpace((volumes ?? SystemVolumeInfoProvider.Instance).GetVolume(folder), length);

    /// <summary>必要な容量 (書き出すサイズ + 16 MiB) が足りなければ <see cref="InsufficientSpaceException"/>。</summary>
    public static void CheckFreeSpace(VolumeInfo? volume, long length)
    {
        if (volume?.AvailableFreeSpace is not long available)
        {
            return;
        }

        long required = length > long.MaxValue - FreeSpaceMargin ? long.MaxValue : length + FreeSpaceMargin;
        if (available < required)
        {
            throw new InsufficientSpaceException(volume.Name, required, available);
        }
    }

    /// <summary>ファイルシステムのファイルサイズの上限を超えれば <see cref="FileSizeLimitException"/> (空き容量より先に確認する)。</summary>
    public static void CheckFileSizeLimit(VolumeInfo? volume, long length)
    {
        if (volume?.MaxFileSize is long max && length > max)
        {
            throw new FileSizeLimitException(volume.Name, volume.FileSystem!, max, length);
        }
    }

    private static void WriteTemp(DocumentSnapshot snapshot, string temp, LongRunningOperation? operation)
    {
        using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0, FileOptions.SequentialScan);
        File.SetAttributes(temp, File.GetAttributes(temp) | FileAttributes.Hidden);

        // 最終的な長さを先に確保し、容量不足を書き込み前に検出する。
        stream.SetLength(snapshot.Length);
        byte[] buffer = new byte[BufferSize];
        for (long offset = 0; offset < snapshot.Length; offset += BufferSize)
        {
            operation?.CancellationToken.ThrowIfCancellationRequested();
            int n = (int)Math.Min(BufferSize, snapshot.Length - offset);
            ReadResult read = snapshot.Read(offset, buffer.AsSpan(0, n));
            if (!read.IsComplete)
            {
                throw new UnreadableDataException(read.Unreadable);
            }

            stream.Write(buffer, 0, n);
            operation?.Report(offset + n);
        }

        stream.Flush(flushToDisk: true);
        stream.Close();
        File.SetAttributes(temp, File.GetAttributes(temp) & ~FileAttributes.Hidden);
    }

    /// <summary>シンボリックリンクの場合はリンク先のファイルを置き換える (ENG-22 の手順 1)。</summary>
    private static string ResolveTarget(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.LinkTarget is not null)
        {
            FileSystemInfo? resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not null)
            {
                return resolved.FullName;
            }
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 残った一時ファイルは次回起動時の復旧処理 (ENG-27) で片付ける。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
