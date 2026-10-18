using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using HexEditor.Core.Tools.Files;

namespace HexEditor.Core.Saving;

/// <summary>
/// 代替データストリーム (ENG-37) の、長さが変わる保存。ストリームには置き換え (<c>ReplaceFile</c>) が使えないため、旧内容をジャーナル
/// (復旧用フォルダのファイル) に保存してから、ストリームの内容を書き直して長さを合わせる。書き直している間も保存前の版が読めるよう、
/// 新しい内容を先に復旧用フォルダの一時ファイルに作る。書き直しに失敗したらジャーナルから旧内容を戻す。
/// </summary>
public static class StreamRewriteSaver
{
    private const int BufferSize = 4 * 1024 * 1024;

    /// <summary>ジャーナルの大きさ (= 旧内容の長さ)。</summary>
    public static long JournalSize(DocumentSnapshot snapshot) => snapshot.Storage.Source.Length;

    /// <summary>復旧用フォルダに必要な量: 旧内容のジャーナル (保護するとき) と、新しい内容の一時ファイル。</summary>
    public static long RequiredSpace(DocumentSnapshot snapshot, bool protect) => snapshot.Length + (protect ? JournalSize(snapshot) : 0);

    /// <summary>書き直す。成功したら、書き直したストリームを開き直したデータソースを返す。</summary>
    public static FileByteSource Save(DocumentSnapshot snapshot, string streamPath, string journalDirectory, Guid documentId, bool protect,
        LongRunningOperation? operation = null, IVolumeInfoProvider? volumes = null)
    {
        string full = Path.GetFullPath(streamPath);
        (string file, string stream) = AlternateStreams.SplitStreamPath(full) ?? throw new ArgumentException("ストリームのパスではありません。", nameof(streamPath));
        Directory.CreateDirectory(journalDirectory);
        string id = documentId.ToString("N");
        string temp = Path.Combine(journalDirectory, id + ".adsnew");
        string journal = Path.Combine(journalDirectory, id + ".adsold");
        long oldLength = snapshot.Storage.Source.Length;
        long total = snapshot.Length * 2 + (protect ? oldLength : 0);
        operation?.SetTotal(total);
        long done = 0;
        bool started = false;
        try
        {
            // 1. 新しい内容を一時ファイルに作る (保存前の版を読みながら。ここまではストリームを変えない)。
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan))
            {
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

                    output.Write(buffer, 0, n);
                    done += n;
                    operation?.Report(done);
                }

                output.Flush(flushToDisk: true);
            }

            // 2. 旧内容をジャーナルに保存する。
            if (protect)
            {
                CopyStream(file, stream, journal, ref done, operation);
            }

            // 3. ストリームを書き直す。ここから先はキャンセルできない。
            operation?.DisallowCancel();
            started = true;
            using (Microsoft.Win32.SafeHandles.SafeFileHandle handle = AlternateStreams.OpenHandle(file, stream, FileMode.Open, FileAccess.Write))
            {
                using var input = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                byte[] buffer = new byte[BufferSize];
                long position = 0;
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    RandomAccess.Write(handle, buffer.AsSpan(0, n), position);
                    position += n;
                    done += n;
                    operation?.Report(done);
                }

                RandomAccess.SetLength(handle, snapshot.Length);
                RandomAccess.FlushToDisk(handle);
            }
        }
        catch (Exception ex)
        {
            DocumentSaver.TryDelete(temp);
            if (!started)
            {
                DocumentSaver.TryDelete(journal);
                throw;
            }

            // 書き直しの途中で失敗した: ジャーナルから旧内容を戻す。
            if (protect && TryRestore(file, stream, journal, oldLength))
            {
                DocumentSaver.TryDelete(journal);
                if (ex is IOException io && AlternateStreams.IsRefs(file, volumes))
                {
                    throw new StreamException(StreamError.TooLargeOnRefs, io.Message, io);
                }

                throw new InPlaceSaveRolledBackException(ex);
            }

            if (ex is IOException io2 && AlternateStreams.IsRefs(file, volumes) && !protect)
            {
                throw new StreamException(StreamError.TooLargeOnRefs, io2.Message, io2);
            }

            throw new InPlaceSavePartiallyWrittenException(protect ? journal : null, ex);
        }

        DocumentSaver.TryDelete(temp);
        DocumentSaver.TryDelete(journal);
        return FileByteSource.Open(full);
    }

    /// <summary>失敗した保存の後始末: 書き直し前の旧内容をジャーナルから戻す。戻せたら true。</summary>
    private static bool TryRestore(string file, string stream, string journal, long oldLength)
    {
        try
        {
            using var input = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read);
            using Microsoft.Win32.SafeHandles.SafeFileHandle handle = AlternateStreams.OpenHandle(file, stream, FileMode.Open, FileAccess.Write);
            byte[] buffer = new byte[BufferSize];
            long position = 0;
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                RandomAccess.Write(handle, buffer.AsSpan(0, n), position);
                position += n;
            }

            RandomAccess.SetLength(handle, oldLength);
            RandomAccess.FlushToDisk(handle);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CopyStream(string file, string stream, string destination, ref long done, LongRunningOperation? operation)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle source = AlternateStreams.OpenHandle(file, stream, FileMode.Open, FileAccess.Read);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);
        long length = RandomAccess.GetLength(source);
        byte[] buffer = new byte[BufferSize];
        for (long position = 0; position < length;)
        {
            operation?.CancellationToken.ThrowIfCancellationRequested();
            int n = RandomAccess.Read(source, buffer, position);
            if (n <= 0)
            {
                break;
            }

            output.Write(buffer, 0, n);
            position += n;
            done += n;
            operation?.Report(done);
        }

        output.Flush(flushToDisk: true);
    }
}
