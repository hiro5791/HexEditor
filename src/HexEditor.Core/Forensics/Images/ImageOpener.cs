using HexEditor.Core.Forensics.Images.Ewf;
using HexEditor.Core.Forensics.Images.VirtualDisks;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Forensics.Images;

/// <summary>イメージを開くときの指定 (FOR-21 の仕様 3)。</summary>
public sealed record ImageOpenOptions
{
    /// <summary>raw・分割 raw のセクタサイズ (E01・VHD などは格納された値を使う)。</summary>
    public int SectorSize { get; init; } = 512;

    /// <summary>足りないセグメントを読めない範囲として開く (「ある分だけ開く」)。</summary>
    public bool AllowMissingSegments { get; init; }
}

/// <summary>1 つのファイルの raw イメージ (読み取り専用。検証・取得の入力に使う)。</summary>
public sealed class RawImageByteSource : ImageSourceBase
{
    private readonly SafeFileHandle _file;

    private RawImageByteSource(string path, ImageInfo info, SafeFileHandle file) : base(path, info) => _file = file;

    public static RawImageByteSource Open(string path, int sectorSize = 512)
    {
        SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var info = new ImageInfo { Format = ImageFormat.Raw, Files = [Path.GetFullPath(path)], MediaSize = RandomAccess.GetLength(file), BytesPerSector = sectorSize };
        return new RawImageByteSource(path, info, file);
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        int read = RandomAccess.Read(_file, buffer[..count], offset);
        if (read < count)
        {
            buffer[read..count].Clear();
            return new ReadResult(count, [new UnreadableRange(offset + read, count - read, UnreadableReason.IoError)]);
        }

        return new ReadResult(count);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
        }
    }
}

/// <summary>イメージの形式の判定と開く処理 (FOR-21 の仕様 1・2)。</summary>
public static class ImageOpener
{
    /// <summary>「ファイル → 開く」で自動でイメージとして開く拡張子 (FOR-21 の仕様 2)。</summary>
    public static bool OpensAsImageAutomatically(string path)
    {
        string ext = Path.GetExtension(path);
        return EwfFormat.IsFirstSegment(path) || SplitRawNaming.IsFirstSegment(path)
            || ext.Equals(".vhd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".vhdx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".vmdk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".avhdx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>拡張子と先頭の署名から形式を判定する。</summary>
    public static ImageFormat Detect(string path)
    {
        if (EwfFormat.IsFirstSegment(path))
        {
            return ImageFormat.E01;
        }

        if (SplitRawNaming.IsFirstSegment(path))
        {
            return ImageFormat.SplitRaw;
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".vhd":
                return ImageFormat.Vhd;
            case ".vhdx":
            case ".avhdx":
                return ImageFormat.Vhdx;
            case ".vmdk":
                return ImageFormat.Vmdk;
        }

        using SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> head = stackalloc byte[8];
        int n = RandomAccess.Read(file, head, 0);
        if (n == 8 && head.SequenceEqual(EwfFormat.Signature))
        {
            return ImageFormat.E01;
        }

        if (VhdxByteSource.LooksLikeVhdx(file))
        {
            return ImageFormat.Vhdx;
        }

        if (VhdByteSource.LooksLikeVhd(file))
        {
            return ImageFormat.Vhd;
        }

        return n >= 4 && head[..4].SequenceEqual("KDMV"u8) ? ImageFormat.Vmdk : ImageFormat.Raw;
    }

    /// <summary>イメージを開く (形式は <see cref="Detect"/>)。</summary>
    /// <exception cref="MissingSegmentsException">セグメントが足りない。</exception>
    /// <exception cref="ImageFormatException">形式が壊れている。</exception>
    public static IImageSource Open(string path, ImageOpenOptions? options = null) => Open(path, Detect(path), options);

    /// <summary>形式を指定してイメージを開く (取得の直後の検証など、形式が分かっているとき)。</summary>
    public static IImageSource Open(string path, ImageFormat format, ImageOpenOptions? options = null)
    {
        options ??= new ImageOpenOptions();
        return format switch
        {
            ImageFormat.E01 => EwfByteSource.Open(path, options.AllowMissingSegments),
            ImageFormat.SplitRaw => SplitRawByteSource.Open(path, options.SectorSize, options.AllowMissingSegments),
            ImageFormat.Vhd => VhdByteSource.Open(path),
            ImageFormat.Vhdx => VhdxByteSource.Open(path),
            ImageFormat.Vmdk => VmdkByteSource.Open(path),
            _ => RawImageByteSource.Open(path, options.SectorSize),
        };
    }

    /// <summary>差分ディスクの親を開く (親の形式は子と違ってよい)。</summary>
    internal static IByteSource OpenParent(string path, int depth) => Detect(path) switch
    {
        ImageFormat.Vhd => VhdByteSource.Open(path, depth),
        ImageFormat.Vhdx => VhdxByteSource.Open(path, depth),
        ImageFormat.Vmdk => VmdkByteSource.Open(path, depth),
        _ => RawImageByteSource.Open(path),
    };
}
