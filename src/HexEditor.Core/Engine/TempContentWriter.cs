using HexEditor.Core.Operations;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Engine;

/// <summary>
/// 作った内容 (塗りつぶし・挿入の実データ、挿入するファイルのコピー) を一時ファイルに順に書き出す (EDIT-29 の「巨大ファイル」3、
/// EDIT-30 の「巨大ファイル」)。一時ファイルは閉じると OS が削除する (DeleteOnClose)。キャンセル・失敗した場合は
/// <see cref="Dispose"/> で消え、ドキュメントは変わらない。
/// </summary>
public sealed class TempContentWriter : IDisposable
{
    private readonly long _length;
    private SafeFileHandle? _handle;
    private long _written;

    public TempContentWriter(string directory, long length, string prefix = "content")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"{prefix}-{Guid.NewGuid():N}.bin");
        _length = length;
        _handle = File.OpenHandle(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            FileOptions.DeleteOnClose | FileOptions.RandomAccess, preallocationSize: length);
    }

    /// <summary>一時ファイルのパス (閉じると消える)。</summary>
    public string Path { get; }

    public long Written => _written;

    /// <summary>続きを書く。</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        SafeFileHandle handle = _handle ?? throw new ObjectDisposedException(nameof(TempContentWriter));
        if (_written + data.Length > _length)
        {
            throw new InvalidOperationException("予定した長さを超えて書き込もうとしました。");
        }

        RandomAccess.Write(handle, data, _written);
        _written += data.Length;
    }

    /// <summary>
    /// <paramref name="generate"/> で 1 MiB ずつ作りながら書き出す。1 MiB ごとにキャンセルを確かめ、進捗を報告する
    /// (<paramref name="progressBase"/> は進捗に足す量)。
    /// </summary>
    public void WriteAll(Action<Span<byte>> generate, LongRunningOperation? operation, long progressBase = 0)
    {
        byte[] buffer = new byte[(int)Math.Clamp(_length - _written, 1, AddBuffer.ChunkSize)];
        while (_written < _length)
        {
            operation?.CancellationToken.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buffer.Length, _length - _written);
            generate(buffer.AsSpan(0, n));
            Write(buffer.AsSpan(0, n));
            operation?.Report(progressBase + _written);
        }
    }

    /// <summary>書き終えた一時ファイルを内容にする。以後この一時ファイルは内容 (を受け取ったドキュメント) が持つ。</summary>
    public EditContent Complete()
    {
        SafeFileHandle handle = _handle ?? throw new ObjectDisposedException(nameof(TempContentWriter));
        if (_written != _length)
        {
            throw new InvalidOperationException("予定した長さまで書いていません。");
        }

        _handle = null;
        var source = new TempFileSource(handle, _length, []);
        return EditContent.FromSource(source, 0, _length, owns: true);
    }

    /// <summary>書きかけの一時ファイルを消す (キャンセル・失敗)。</summary>
    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
