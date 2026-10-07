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
    public static FileByteSource Save(DocumentSnapshot snapshot, string targetPath, LongRunningOperation? operation = null)
    {
        string target = ResolveTarget(Path.GetFullPath(targetPath));
        string folder = Path.GetDirectoryName(target)!;
        CheckFreeSpace(folder, snapshot.Length);

        string temp = Path.Combine(folder, $".{Path.GetFileName(target)}.~hex{RandomNumberGenerator.GetHexString(8, lowercase: true)}.tmp");
        try
        {
            WriteTemp(snapshot, temp, operation);
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

    /// <summary>保存先のドライブの空き容量を確認する (ENG-25)。</summary>
    public static void CheckFreeSpace(string folder, long length)
    {
        string? root = Path.GetPathRoot(folder);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return; // ネットワーク上の場所は空き容量を正確に取れないため、書き込み時のエラーで扱う。
        }

        var drive = new DriveInfo(root);
        long required = length + FreeSpaceMargin;
        if (drive.AvailableFreeSpace < required)
        {
            throw new InsufficientSpaceException(drive.Name, required, drive.AvailableFreeSpace);
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
