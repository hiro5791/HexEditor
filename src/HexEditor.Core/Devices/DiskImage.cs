using HexEditor.Core.Sources;

namespace HexEditor.Core.Devices;

/// <summary>「ディスクイメージとして開く」の指定 (ENG-31)。</summary>
public sealed record DiskImageOptions
{
    /// <summary>セクタサイズ (128〜65,536 の 2 の累乗)。</summary>
    public int SectorSize { get; init; } = 512;

    /// <summary>「長さの変更を許可する」(既定オフ。仕様 3)。</summary>
    public bool AllowResize { get; init; }
}

/// <summary>ディスクイメージの規則 (ENG-31)。</summary>
public static class DiskImage
{
    public const int MinSectorSize = 128;

    public const int MaxSectorSize = 65536;

    /// <summary>選択肢のセクタサイズ (仕様 1)。</summary>
    public static IReadOnlyList<int> CommonSectorSizes { get; } = [512, 2048, 4096];

    /// <summary>ディスクイメージとして開き直すかを提案する拡張子 (仕様 6)。</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".img", ".dd", ".raw", ".iso", ".bin"];

    /// <summary>セクタサイズとして正しいか (128〜65,536 の 2 の累乗)。</summary>
    public static bool IsValidSectorSize(long size) => size is >= MinSectorSize and <= MaxSectorSize && (size & (size - 1)) == 0;

    /// <summary>既定のセクタサイズ: <c>.iso</c> は 2048、それ以外は 512 (仕様 1)。</summary>
    public static int DefaultSectorSize(string path) =>
        string.Equals(Path.GetExtension(path), ".iso", StringComparison.OrdinalIgnoreCase) ? 2048 : 512;

    /// <summary>拡張子からディスクイメージらしいか (仕様 6 の提案)。</summary>
    public static bool LooksLikeImage(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>長さのセクタの端数 (仕様 5。0 なら端数なし)。</summary>
    public static long Remainder(long length, int sectorSize) => length % sectorSize;

    /// <summary>ファイルをディスクイメージとして開く。</summary>
    public static DiskImageByteSource Open(string path, DiskImageOptions options)
    {
        if (!IsValidSectorSize(options.SectorSize))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.SectorSize, "The sector size must be a power of two from 128 to 65,536.");
        }

        return new DiskImageByteSource(FileByteSource.Open(path), options.SectorSize, options.AllowResize);
    }
}

/// <summary>
/// ディスクイメージとして開いたファイル (ENG-31 の仕様 2・3): 論理セクタサイズを指定値にし、既定では長さを固定する
/// (<see cref="SourceCapabilities.CanResize"/> を外す)。読み書きは元のファイルと同じ (仕様 4)。
/// </summary>
public sealed class DiskImageByteSource : ByteSourceBase
{
    private readonly FileByteSource _file;
    private readonly bool _allowResize;

    public DiskImageByteSource(FileByteSource file, int sectorSize, bool allowResize)
    {
        _file = file;
        LogicalSectorSize = sectorSize;
        _allowResize = allowResize;
        file.Changed += (_, e) => OnChanged(e.Kind);
    }

    /// <summary>元のファイル。</summary>
    public FileByteSource File => _file;

    public string Path => _file.Path;

    public override string DisplayName => _file.DisplayName;

    public override string Identity => _file.Identity + "#image:" + LogicalSectorSize;

    public override long Length => _file.Length;

    public override int LogicalSectorSize { get; }

    public override SourceCapabilities Capabilities =>
        _allowResize ? _file.Capabilities : _file.Capabilities & ~SourceCapabilities.CanResize;

    public override ReadResult Read(long offset, Span<byte> buffer) => _file.Read(offset, buffer);

    public override ValueTask<ReadResult> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _file.ReadAsync(offset, buffer, cancellationToken);

    public override void Write(long offset, ReadOnlySpan<byte> data) => _file.Write(offset, data);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
        }
    }
}
