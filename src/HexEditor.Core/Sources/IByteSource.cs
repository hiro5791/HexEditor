namespace HexEditor.Core.Sources;

/// <summary>データソースが申告する能力 (ENG-01 の仕様 3)。</summary>
[Flags]
public enum SourceCapabilities
{
    None = 0,

    /// <summary>書き込める。</summary>
    CanWrite = 1 << 0,

    /// <summary>長さを変えられる (挿入・削除できる)。</summary>
    CanResize = 1 << 1,

    /// <summary>一時ファイルと置き換える保存ができる。</summary>
    CanReplace = 1 << 2,

    /// <summary>データが外部要因で変わりうる。</summary>
    IsVolatile = 1 << 3,

    /// <summary>読めない範囲がある (領域マップを持つ)。</summary>
    HasGaps = 1 << 4,

    /// <summary>読み書きをセクタ境界に揃える必要がある。</summary>
    NeedsAlignment = 1 << 5,
}

/// <summary>読めなかった理由。</summary>
public enum UnreadableReason
{
    Unallocated,
    AccessDenied,
    IoError,
    Disconnected,
}

/// <summary>読めなかった範囲。オフセットはデータソース上の絶対位置。</summary>
public readonly record struct UnreadableRange(long Offset, long Length, UnreadableReason Reason, int ErrorCode = 0)
{
    public long End => Offset + Length;
}

/// <summary>
/// 読み込みの結果 (ENG-01 の仕様 4)。
/// 末尾を越える範囲を指定した場合、<see cref="BytesReturned"/> は指定より短くなる。末尾を越えた部分は
/// <see cref="Unreadable"/> に含めない。読めなかった範囲のバッファの内容は 0 で埋める。
/// </summary>
public readonly struct ReadResult
{
    private static readonly IReadOnlyList<UnreadableRange> NoRanges = [];

    public ReadResult(int bytesReturned, IReadOnlyList<UnreadableRange>? unreadable = null)
    {
        BytesReturned = bytesReturned;
        Unreadable = unreadable ?? NoRanges;
    }

    /// <summary>バッファの先頭から何バイトを返したか (読めなかった範囲を含む)。</summary>
    public int BytesReturned { get; }

    public IReadOnlyList<UnreadableRange> Unreadable { get; }

    public bool IsComplete => Unreadable.Count == 0;
}

public enum SourceChangeKind
{
    /// <summary>外部で内容が変更された。</summary>
    ModifiedExternally,

    /// <summary>データソースが使えなくなった (取り外し、プロセスの終了など)。</summary>
    Disconnected,
}

public sealed class SourceChangedEventArgs(SourceChangeKind kind) : EventArgs
{
    public SourceChangeKind Kind { get; } = kind;
}

/// <summary>
/// ファイル・ディスク・プロセスメモリなど、すべての元データの共通の形 (ENG-01)。
/// 読み込みはスレッドセーフでなければならない。
/// </summary>
public interface IByteSource : IDisposable
{
    /// <summary>タブとタイトルに出す名前。</summary>
    string DisplayName { get; }

    /// <summary>同じ対象を二重に開かないための比較キー。</summary>
    string Identity { get; }

    /// <summary>バイト数。0 以上、最大 2^63 − 1。</summary>
    long Length { get; }

    /// <summary>オフセット 0 に対応するアドレス。</summary>
    long BaseAddress { get; }

    /// <summary>読み書きの境界。ファイルは 1。</summary>
    int LogicalSectorSize { get; }

    int PhysicalSectorSize { get; }

    SourceCapabilities Capabilities { get; }

    /// <summary>位置を指定して読む。読み込みの失敗は例外ではなく結果で返す。</summary>
    ReadResult Read(long offset, Span<byte> buffer);

    ValueTask<ReadResult> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>位置を指定して書く。<see cref="SourceCapabilities.CanWrite"/> のデータソースだけが提供する。</summary>
    void Write(long offset, ReadOnlySpan<byte> data);

    event EventHandler<SourceChangedEventArgs>? Changed;
}

/// <summary>データソースの共通の実装。</summary>
public abstract class ByteSourceBase : IByteSource
{
    public abstract string DisplayName { get; }

    public abstract string Identity { get; }

    public abstract long Length { get; }

    public virtual long BaseAddress => 0;

    public virtual int LogicalSectorSize => 1;

    public virtual int PhysicalSectorSize => LogicalSectorSize;

    public abstract SourceCapabilities Capabilities { get; }

    public event EventHandler<SourceChangedEventArgs>? Changed;

    public abstract ReadResult Read(long offset, Span<byte> buffer);

    public virtual ValueTask<ReadResult> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(offset, buffer.Span));
    }

    public virtual void Write(long offset, ReadOnlySpan<byte> data) =>
        throw new NotSupportedException("このデータソースには書き込めません。");

    protected void OnChanged(SourceChangeKind kind) => Changed?.Invoke(this, new SourceChangedEventArgs(kind));

    /// <summary>末尾を越える要求を切り詰めた長さを返す。</summary>
    protected int ClampToLength(long offset, int requested)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length)
        {
            return 0;
        }

        return (int)Math.Min(requested, Length - offset);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
