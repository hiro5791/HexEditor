using HexEditor.Core.Operations;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Engine;

/// <summary>
/// 長さが前もって分からない内容 (文字コード変換の結果など。EDIT-38 の「巨大ファイル」) を順に受け取り、<see cref="EditContent"/> にする。
/// <see cref="InMemoryLimit"/> まではメモリに置き、それを超えたら一時ファイル (閉じると OS が削除する) に書き出す。
/// キャンセル・失敗した場合は <see cref="Dispose"/> で一時ファイルが消え、ドキュメントは変わらない。
/// </summary>
public sealed class ContentSink : IDisposable
{
    /// <summary>これ以下の内容は一時ファイルを作らずメモリ (追加バッファ) に置く。</summary>
    public const int InMemoryLimit = 1024 * 1024;

    private readonly string _directory;
    private readonly Action<long>? _checkSpace;
    private byte[] _memory = new byte[4096];
    private SafeFileHandle? _handle;
    private string? _path;
    private bool _completed;

    /// <param name="directory">一時ファイルの置き場所 (ドキュメントの一時フォルダ)。</param>
    /// <param name="checkSpace">一時ファイルを作る前に呼ぶ (見込みのバイト数を渡す。足りなければ例外を投げる)。</param>
    public ContentSink(string directory, Action<long>? checkSpace = null)
    {
        _directory = directory;
        _checkSpace = checkSpace;
    }

    /// <summary>ドキュメントの一時フォルダに置く。</summary>
    public static ContentSink For(Document document, Action<long>? checkSpace = null) =>
        new(Path.Combine(document.Options.TempDirectory, document.Id.ToString("N")), checkSpace);

    /// <summary>これまでに受け取ったバイト数。</summary>
    public long Length { get; private set; }

    /// <summary>一時ファイルに書き出しているか。</summary>
    public bool IsSpilled => _handle is not null;

    /// <summary>
    /// 一時ファイルを作るときに空き容量を確かめる見込みの全体の長さ (分かっていれば設定する。0 なら今の長さの 2 倍)。
    /// </summary>
    public long ExpectedLength { get; set; }

    /// <summary>続きを書く。</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (data.IsEmpty)
        {
            return;
        }

        if (_handle is null && Length + data.Length <= InMemoryLimit)
        {
            if (Length + data.Length > _memory.Length)
            {
                Array.Resize(ref _memory, (int)Math.Min(InMemoryLimit, Math.Max(_memory.Length * 2L, Length + data.Length)));
            }

            data.CopyTo(_memory.AsSpan((int)Length));
            Length += data.Length;
            return;
        }

        if (_handle is null)
        {
            Spill();
        }

        RandomAccess.Write(_handle!, data, Length);
        Length += data.Length;
    }

    /// <summary>1 バイト書く。</summary>
    public void WriteByte(byte value) => Write([value]);

    /// <summary>受け取った内容を <see cref="EditContent"/> にする。以後この一時ファイルは内容 (を受け取ったドキュメント) が持つ。</summary>
    public EditContent Complete()
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        _completed = true;
        if (_handle is null)
        {
            return EditContent.Bytes(_memory.AsSpan(0, (int)Length).ToArray());
        }

        SafeFileHandle handle = _handle;
        _handle = null;
        return EditContent.FromSource(new TempFileSource(handle, Length, []), 0, Length, owns: true);
    }

    /// <summary>書きかけの一時ファイルを消す (キャンセル・失敗。<see cref="Complete"/> の後は何もしない)。</summary>
    public void Dispose()
    {
        _completed = true;
        _handle?.Dispose();
        _handle = null;
    }

    private void Spill()
    {
        _checkSpace?.Invoke(Math.Max(ExpectedLength, Length * 2));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, $"transform-{Guid.NewGuid():N}.bin");
        _handle = File.OpenHandle(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            FileOptions.DeleteOnClose | FileOptions.RandomAccess);
        RandomAccess.Write(_handle, _memory.AsSpan(0, (int)Length), 0);
        _memory = [];
    }

    /// <summary>キャンセルを確かめ、進捗を報告する (変換の処理から 1 MiB ごとに呼ぶ)。</summary>
    public static void Checkpoint(LongRunningOperation? operation, long done)
    {
        operation?.CancellationToken.ThrowIfCancellationRequested();
        operation?.Report(done);
    }
}
