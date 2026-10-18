using HexEditor.Core.Engine;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Tools.Files;

/// <summary>
/// ファイルツール (TOOL-13〜15) が入力を読むための共通の形。ファイル・開いているドキュメント (未保存の変更を含む内容) のどちらも同じように
/// ストリームで読む。読めない範囲があれば <see cref="IOException"/> にする。
/// </summary>
public interface IContentReader : IDisposable
{
    long Length { get; }

    /// <summary>読んだバイト数を返す (末尾を越える分は読まない)。</summary>
    int Read(long offset, Span<byte> buffer);
}

/// <summary>ファイルシステムのファイルを読む。</summary>
public sealed class FileContentReader : IContentReader
{
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _handle;

    public FileContentReader(string path)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        Length = RandomAccess.GetLength(_handle);
    }

    public long Length { get; }

    public int Read(long offset, Span<byte> buffer)
    {
        int want = (int)Math.Min(buffer.Length, Math.Max(0, Length - offset));
        int total = 0;
        while (total < want)
        {
            int n = RandomAccess.Read(_handle, buffer.Slice(total, want - total), offset + total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>ドキュメントのある時点の内容 (スナップショット) を読む。未保存の変更を含む。</summary>
public sealed class SnapshotContentReader(DocumentSnapshot snapshot) : IContentReader
{
    public long Length => snapshot.Length;

    public int Read(long offset, Span<byte> buffer)
    {
        int want = (int)Math.Min(buffer.Length, Math.Max(0, Length - offset));
        if (want == 0)
        {
            return 0;
        }

        ReadResult result = snapshot.Read(offset, buffer[..want]);
        if (!result.IsComplete)
        {
            throw new IOException($"読めない範囲があります (オフセット 0x{result.Unreadable[0].Offset:X})。");
        }

        return result.BytesReturned;
    }

    public void Dispose()
    {
    }
}

/// <summary>データソースを読む (テストの仮想・遅いデータソース用)。</summary>
public sealed class SourceContentReader(IByteSource source, bool owns = false) : IContentReader
{
    public long Length => source.Length;

    public int Read(long offset, Span<byte> buffer)
    {
        int want = (int)Math.Min(buffer.Length, Math.Max(0, Length - offset));
        if (want == 0)
        {
            return 0;
        }

        ReadResult result = source.Read(offset, buffer[..want]);
        if (!result.IsComplete)
        {
            throw new IOException($"読めない範囲があります (オフセット 0x{result.Unreadable[0].Offset:X})。");
        }

        return result.BytesReturned;
    }

    public void Dispose()
    {
        if (owns)
        {
            source.Dispose();
        }
    }
}

/// <summary>メモリ上のバイト列を読む。</summary>
public sealed class MemoryContentReader(byte[] data) : IContentReader
{
    public long Length => data.Length;

    public int Read(long offset, Span<byte> buffer)
    {
        int want = (int)Math.Min(buffer.Length, Math.Max(0, data.Length - offset));
        data.AsSpan((int)offset, want).CopyTo(buffer);
        return want;
    }

    public void Dispose()
    {
    }
}

/// <summary>ファイルツールの失敗。それまでに作ったファイルの一覧を持つ (TOOL-13 の「エラー」)。</summary>
public sealed class FileToolException(FileToolError error, string message, IReadOnlyList<string>? created = null, Exception? inner = null)
    : IOException(message, inner)
{
    public FileToolError Error { get; } = error;

    public IReadOnlyList<string> CreatedFiles { get; } = created ?? [];

    /// <summary>空き容量不足のときの不足の内容。</summary>
    public Saving.SpaceShortage? Space { get; init; }
}

public enum FileToolError
{
    /// <summary>入力が空など、作るものがない。</summary>
    NothingToDo,

    /// <summary>指定した値が範囲外。</summary>
    InvalidArgument,

    /// <summary>出力のファイル名が重なる、または使えない。</summary>
    InvalidNames,

    /// <summary>出力先の空き容量が足りない。</summary>
    InsufficientSpace,

    /// <summary>入力の長さが違う (インターリーブの「エラーにする」)。</summary>
    LengthMismatch,

    /// <summary>入力のファイルが読めない。</summary>
    Unreadable,

    /// <summary>書き込みの失敗。</summary>
    WriteFailed,
}
