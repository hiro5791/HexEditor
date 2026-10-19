using HexEditor.Core.Operations;
using HexEditor.Core.Tools.Files;

namespace HexEditor.Core.Wiping;

/// <summary>
/// 「OS 経由」の空き領域の消去 (FOR-31 の仕様 1) が使うマウント中のボリュームの操作。テストではメモリ上の偽のボリュームに差し替える
/// (この PC の実際のボリュームでは実行しない)。
/// </summary>
public interface IFreeSpaceVolume
{
    /// <summary>ボリュームのルート (<c>X:\</c>)。</summary>
    string Root { get; }

    /// <summary>ファイルシステムの名前 (NTFS・FAT32・exFAT など)。</summary>
    string FileSystem { get; }

    /// <summary>今の空き容量 (この利用者が使える量)。</summary>
    long FreeBytes();

    /// <summary>1 ファイルの大きさの上限 (FAT32 は 4 GiB - 1)。</summary>
    long MaxFileSize { get; }

    void CreateDirectory(string path);

    /// <summary>新しいファイルを書き込み用に開く (書き込みはディスクまで反映する)。空き容量がなければ <see cref="IOException"/>。</summary>
    Stream CreateFile(string path);

    /// <summary>作ったファイルを書き込み用に開き直す (2 回目以降の回)。</summary>
    Stream OpenFile(string path);

    void DeleteFile(string path);

    void DeleteDirectory(string path);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>空き容量がないことによる失敗か (<c>ERROR_DISK_FULL</c>・<c>ERROR_HANDLE_DISK_FULL</c>)。</summary>
    bool IsDiskFull(IOException exception) => (exception.HResult & 0xFFFF) is 112 or 39;
}

/// <summary>OS 経由の消去の結果 (FOR-31 の仕様 6)。</summary>
public sealed record OsFreeSpaceWipeResult
{
    /// <summary>上書きした容量 (1 回分。最後の回で書いた量)。</summary>
    public long BytesOverwritten { get; init; }

    /// <summary>全回数で書いた量。</summary>
    public long BytesWritten { get; init; }

    public int LargeFiles { get; init; }

    /// <summary>MFT の空きレコードを上書きするために作った小さいファイルの数 (NTFS)。</summary>
    public int SmallFiles { get; init; }

    public TimeSpan Elapsed { get; init; }

    public bool Cancelled { get; init; }

    /// <summary>作った一時フォルダ。</summary>
    public string TemporaryFolder { get; init; } = string.Empty;

    /// <summary>削除できなかった一時ファイル・フォルダ (手動での削除を求める。「エラー」)。</summary>
    public IReadOnlyList<string> Leftovers { get; init; } = [];

    /// <summary>空き容量の不足以外の書き込みエラー (中止した)。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// 「OS 経由」の空き領域の消去 (FOR-31 の仕様 1): ボリュームに一時フォルダを作り、空き容量がなくなるまで指定パターンのファイルを書き、最後に削除する。
/// NTFS では小さいファイルも多数作り、MFT の空きレコードに常駐していたデータも上書きする。キャンセル・エラーでも一時ファイルは必ず削除する。
/// </summary>
public static class OsFreeSpaceWipe
{
    /// <summary>一時フォルダの名前の始まり (ボリュームのルートに作る)。</summary>
    public const string FolderPrefix = "HexEditor-FreeSpaceWipe-";

    /// <summary>大きいファイル 1 つの上限 (ファイルシステムの上限より小さければこちら)。</summary>
    public const long LargeFileLimit = 16L << 30;

    /// <summary>小さいファイルの大きさ (MFT のレコードに常駐する大きさ)。</summary>
    public const int SmallFileSize = 512;

    /// <summary>小さいファイルの数の上限 (MFT がディスクいっぱいで伸びないため、普通はこれより先に作れなくなる)。</summary>
    public const int MaxSmallFiles = 1_000_000;

    private const int Chunk = 1 << 20;

    public static OsFreeSpaceWipeResult Execute(IFreeSpaceVolume volume, ShredMethod method, LongRunningOperation? operation = null,
        Action<WipeProgress>? progress = null, CancellationToken cancellationToken = default, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        long started = time.GetTimestamp();
        CancellationToken token = operation?.CancellationToken ?? cancellationToken;
        string folder = Path.Combine(volume.Root, FolderPrefix + Guid.NewGuid().ToString("N")[..8]);
        var large = new List<(string Path, long Length)>();
        var small = new List<string>();
        var leftovers = new List<string>();
        long written = 0;
        long lastPassBytes = 0;
        bool cancelled = false;
        string? error = null;
        int passes = method.Passes.Count;
        long free = Math.Max(0, volume.FreeBytes());
        operation?.SetTotal(free * passes);
        bool ntfs = string.Equals(volume.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);
        long fileLimit = Math.Min(LargeFileLimit, volume.MaxFileSize);
        byte[] buffer = new byte[Chunk];
        try
        {
            volume.CreateDirectory(folder);
            for (int pass = 0; pass < passes && !cancelled && error is null; pass++)
            {
                ShredPass shred = method.Passes[pass];
                using WipeRandomStream? random = shred.IsRandom ? new WipeRandomStream() : null;
                long passBytes = 0;
                void Fill(Span<byte> data, long position)
                {
                    if (random is not null)
                    {
                        random.Fill(data, position);
                    }
                    else
                    {
                        SecureDelete.Fill(shred, data, position);
                    }
                }

                void Report()
                {
                    operation?.Report(Math.Min(free * passes, (long)pass * free + passBytes));
                    progress?.Invoke(new WipeProgress(pass + 1, passes, Math.Min(passBytes, free), free, false));
                }

                if (pass == 0)
                {
                    // 1 回目: 空き容量がなくなるまで大きいファイルを作る。
                    bool full = false;
                    for (int index = 0; !full && !cancelled && error is null; index++)
                    {
                        string path = Path.Combine(folder, $"fill{index:D5}.tmp");
                        long length = 0;
                        Stream stream;
                        try
                        {
                            stream = volume.CreateFile(path);
                        }
                        catch (IOException ex) when (volume.IsDiskFull(ex))
                        {
                            break;
                        }

                        large.Add((path, 0));
                        using (stream)
                        {
                            int size = Chunk;
                            while (length < fileLimit)
                            {
                                if (token.IsCancellationRequested)
                                {
                                    cancelled = true;
                                    break;
                                }

                                int n = (int)Math.Min(size, fileLimit - length);
                                Fill(buffer.AsSpan(0, n), length);
                                try
                                {
                                    stream.Write(buffer, 0, n);
                                    stream.Flush();
                                }
                                catch (IOException ex) when (volume.IsDiskFull(ex))
                                {
                                    // 残りを小さい単位で埋める (4 KiB まで)。
                                    if (size > 4096)
                                    {
                                        size /= 4;
                                        TryTruncate(stream, length);
                                        continue;
                                    }

                                    TryTruncate(stream, length);
                                    full = true;
                                    break;
                                }
                                catch (IOException ex)
                                {
                                    error = ex.Message;
                                    break;
                                }

                                length += n;
                                passBytes += n;
                                written += n;
                                Report();
                            }
                        }

                        large[^1] = (path, length);
                        if (length == 0)
                        {
                            full = true;
                        }
                    }

                    // NTFS: 小さいファイルを作れなくなるまで作る (MFT の空きレコードに常駐していたデータを上書きする)。
                    if (ntfs && !cancelled && error is null)
                    {
                        for (int index = 0; index < MaxSmallFiles; index++)
                        {
                            if (token.IsCancellationRequested)
                            {
                                cancelled = true;
                                break;
                            }

                            string path = Path.Combine(folder, $"s{index:D7}.tmp");
                            try
                            {
                                using Stream stream = volume.CreateFile(path);
                                small.Add(path);
                                Fill(buffer.AsSpan(0, SmallFileSize), 0);
                                stream.Write(buffer, 0, SmallFileSize);
                                stream.Flush();
                                passBytes += SmallFileSize;
                                written += SmallFileSize;
                            }
                            catch (IOException)
                            {
                                // 作れない (MFT の空きレコードも空き容量もない): ここまで。
                                break;
                            }

                            if ((index & 0xFF) == 0)
                            {
                                Report();
                            }
                        }
                    }
                }
                else
                {
                    // 2 回目以降: 作ったファイルを同じ大きさのまま次のパターンで上書きする。
                    foreach ((string path, long length) in large)
                    {
                        using Stream stream = volume.OpenFile(path);
                        for (long position = 0; position < length;)
                        {
                            if (token.IsCancellationRequested)
                            {
                                cancelled = true;
                                break;
                            }

                            int n = (int)Math.Min(Chunk, length - position);
                            Fill(buffer.AsSpan(0, n), position);
                            stream.Position = position;
                            stream.Write(buffer, 0, n);
                            stream.Flush();
                            position += n;
                            passBytes += n;
                            written += n;
                            Report();
                        }

                        if (cancelled)
                        {
                            break;
                        }
                    }

                    foreach (string path in small)
                    {
                        if (cancelled || token.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }

                        using Stream stream = volume.OpenFile(path);
                        Fill(buffer.AsSpan(0, SmallFileSize), 0);
                        stream.Write(buffer, 0, SmallFileSize);
                        stream.Flush();
                        passBytes += SmallFileSize;
                        written += SmallFileSize;
                    }
                }

                lastPassBytes = passBytes;
            }
        }
        catch (OperationCanceledException)
        {
            // 長時間処理の進捗報告 (LongRunningOperation.Report) はキャンセルされると例外を投げる。これもキャンセルとして扱い、
            // 一時ファイルの削除 (下の finally) と結果の返却・監査ログを必ず行う。
            cancelled = true;
        }
        catch (IOException ex)
        {
            error = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
        }
        finally
        {
            // キャンセル・エラーでも一時ファイルは必ず削除する (「巨大ファイル・長時間処理」)。
            Cleanup();
        }

        return new OsFreeSpaceWipeResult
        {
            BytesOverwritten = lastPassBytes,
            BytesWritten = written,
            LargeFiles = large.Count,
            SmallFiles = small.Count,
            Elapsed = time.GetElapsedTime(started),
            Cancelled = cancelled,
            TemporaryFolder = folder,
            Leftovers = leftovers,
            Error = error,
        };

        void Cleanup()
        {
            foreach (string path in large.Select(l => l.Path).Concat(small))
            {
                TryDelete(path, file: true);
            }

            if (volume.DirectoryExists(folder))
            {
                TryDelete(folder, file: false);
            }
        }

        void TryDelete(string path, bool file)
        {
            try
            {
                if (file)
                {
                    if (volume.FileExists(path))
                    {
                        volume.DeleteFile(path);
                    }
                }
                else
                {
                    volume.DeleteDirectory(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                leftovers.Add(path);
            }
        }
    }

    private static void TryTruncate(Stream stream, long length)
    {
        try
        {
            stream.SetLength(length);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>本物のマウント中のボリューム (<see cref="DriveInfo"/> と、ディスクまで書き込むファイル)。</summary>
public sealed class WindowsFreeSpaceVolume(string root) : IFreeSpaceVolume
{
    private readonly DriveInfo _drive = new(root);

    public string Root { get; } = Path.GetPathRoot(Path.GetFullPath(root))!;

    public string FileSystem => _drive.DriveFormat;

    public long FreeBytes() => _drive.AvailableFreeSpace;

    public long MaxFileSize => FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) || FileSystem.Equals("FAT", StringComparison.OrdinalIgnoreCase)
        ? uint.MaxValue
        : long.MaxValue;

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Stream CreateFile(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);

    public Stream OpenFile(string path) => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);
}
