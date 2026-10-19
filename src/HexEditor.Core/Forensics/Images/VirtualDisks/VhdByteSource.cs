using System.Buffers.Binary;
using System.Text;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Forensics.Images.VirtualDisks;

/// <summary>
/// VHD (Virtual Hard Disk Image Format Specification 1.0) の読み込み (FOR-21 の仕様 9)。固定・可変・差分の 3 種類。値はビッグエンディアン。
/// 可変・差分は BAT (ブロックの割り当て表) を開くときに読み (1 エントリ 4 バイト。2 TB で 4 MiB)、割り当てていないブロックは 0
/// (差分では親) として読む。差分ディスクの親は親ロケーター (W2ku / W2ru) から探す (深さ 16 まで)。
/// </summary>
public sealed class VhdByteSource : ImageSourceBase
{
    private const int FixedType = 2;
    private const int DynamicType = 3;
    private const int DifferencingType = 4;
    private const int MaxParentDepth = 16;

    private readonly SafeFileHandle _file;
    private readonly long _fileLength;
    private readonly int _type;
    private readonly uint[] _bat = [];
    private readonly int _blockSize;
    private readonly int _bitmapBytes;
    private readonly IByteSource? _parent;

    private VhdByteSource(string path, ImageInfo info, SafeFileHandle file, long fileLength, int type, uint[] bat, int blockSize, IByteSource? parent)
        : base(path, info)
    {
        _file = file;
        _fileLength = fileLength;
        _type = type;
        _bat = bat;
        _blockSize = blockSize;
        _bitmapBytes = blockSize <= 0 ? 0 : (int)((blockSize / 512 / 8 + 511) / 512 * 512);
        _parent = parent;
    }

    /// <summary>先頭 8 バイトまたは末尾 512 バイトが "conectix" か。</summary>
    public static bool LooksLikeVhd(SafeFileHandle file)
    {
        long length = RandomAccess.GetLength(file);
        Span<byte> cookie = stackalloc byte[8];
        return length >= 512 && RandomAccess.Read(file, cookie, length - 512) == 8 && cookie.SequenceEqual("conectix"u8);
    }

    public static VhdByteSource Open(string path, int depth = 0)
    {
        if (depth > MaxParentDepth)
        {
            throw new ImageFormatException(ImageFormat.Vhd, "parent", "The chain of differencing disks is too deep.");
        }

        string full = Path.GetFullPath(path);
        SafeFileHandle file = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        IByteSource? parent = null;
        try
        {
            long length = RandomAccess.GetLength(file);
            byte[] footer = new byte[512];
            if (length < 512 || RandomAccess.Read(file, footer, length - 512) != 512 || !footer.AsSpan(0, 8).SequenceEqual("conectix"u8))
            {
                // 古い形 (511 バイトのフッター) と、末尾が壊れていて先頭のコピーだけが正しいものを先頭のコピーで試す。
                if (length < 512 || RandomAccess.Read(file, footer, 0) != 512 || !footer.AsSpan(0, 8).SequenceEqual("conectix"u8))
                {
                    throw new ImageFormatException(ImageFormat.Vhd, "footer", "The VHD footer (conectix) was not found.");
                }
            }

            if (!ChecksumOk(footer, 64))
            {
                throw new ImageFormatException(ImageFormat.Vhd, "footer", "The checksum of the VHD footer does not match.");
            }

            long currentSize = BinaryPrimitives.ReadInt64BigEndian(footer.AsSpan(48));
            int type = BinaryPrimitives.ReadInt32BigEndian(footer.AsSpan(60));
            string creator = Encoding.ASCII.GetString(footer, 28, 4).TrimEnd('\0', ' ');
            if (currentSize < 0 || currentSize > 1L << 50)
            {
                throw new ImageFormatException(ImageFormat.Vhd, "footer", "The VHD size is invalid.");
            }

            uint[] bat = [];
            int blockSize = 0;
            string? parentPath = null;
            if (type == FixedType)
            {
                if (currentSize > length - 512)
                {
                    throw new ImageFormatException(ImageFormat.Vhd, "footer", "The fixed VHD is shorter than its size.");
                }
            }
            else if (type is DynamicType or DifferencingType)
            {
                long headerOffset = BinaryPrimitives.ReadInt64BigEndian(footer.AsSpan(16));
                byte[] header = new byte[1024];
                if (headerOffset < 0 || headerOffset > length - 1024 || RandomAccess.Read(file, header, headerOffset) != 1024
                    || !header.AsSpan(0, 8).SequenceEqual("cxsparse"u8))
                {
                    throw new ImageFormatException(ImageFormat.Vhd, "header", "The VHD dynamic disk header (cxsparse) was not found.");
                }

                if (!ChecksumOk(header, 36))
                {
                    throw new ImageFormatException(ImageFormat.Vhd, "header", "The checksum of the VHD dynamic disk header does not match.");
                }

                long tableOffset = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(16));
                long entries = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));
                blockSize = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(32)), int.MaxValue);
                if (blockSize < 512 || blockSize > 256 * 1024 * 1024 || (blockSize & (blockSize - 1)) != 0
                    || entries < (currentSize + blockSize - 1) / blockSize || entries > 1 << 26
                    || tableOffset < 0 || tableOffset + entries * 4 > length)
                {
                    throw new ImageFormatException(ImageFormat.Vhd, "bat", "The VHD block allocation table is invalid.");
                }

                byte[] raw = new byte[entries * 4];
                RandomAccess.Read(file, raw, tableOffset);
                bat = new uint[entries];
                for (int i = 0; i < bat.Length; i++)
                {
                    bat[i] = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(i * 4));
                }

                if (type == DifferencingType)
                {
                    parentPath = FindParent(full, header);
                    if (parentPath is null)
                    {
                        string name = Encoding.BigEndianUnicode.GetString(header, 64, 512).TrimEnd('\0');
                        throw new ParentImageNotFoundException(full, name);
                    }

                    parent = ImageOpener.OpenParent(parentPath, depth + 1);
                    if (parent.Length < currentSize)
                    {
                        throw new ImageFormatException(ImageFormat.Vhd, "parent", "The parent disk is smaller than the differencing disk.");
                    }
                }
            }
            else
            {
                throw new ImageFormatException(ImageFormat.Vhd, "footer", $"Unsupported VHD disk type {type}.");
            }

            var info = new ImageInfo
            {
                Format = ImageFormat.Vhd,
                Files = parent is IImageSource p ? [full, .. p.Info.Files] : [full],
                MediaSize = currentSize,
                BytesPerSector = 512,
                Subtype = type switch { FixedType => "fixed", DynamicType => "dynamic", _ => "differencing" },
                AcquisitionTool = creator.Length > 0 ? creator : null,
                ParentPath = parentPath,
            };
            return new VhdByteSource(full, info, file, length, type, bat, blockSize, parent);
        }
        catch
        {
            parent?.Dispose();
            file.Dispose();
            throw;
        }
    }

    /// <summary>1 の補数のチェックサム (チェックサムの欄自身を 0 として足す)。</summary>
    private static bool ChecksumOk(byte[] data, int at)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (i < at || i >= at + 4)
            {
                sum += data[i];
            }
        }

        return ~sum == BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
    }

    /// <summary>親ロケーター (W2ru: 相対パス、W2ku: 絶対パス。UTF-16 LE) と親の名前 (UTF-16 BE) から、存在する親を探す。</summary>
    private static string? FindParent(string child, byte[] header)
    {
        string dir = Path.GetDirectoryName(child)!;
        var candidates = new List<string>();
        using SafeFileHandle file = File.OpenHandle(child, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = RandomAccess.GetLength(file);
        for (int i = 0; i < 8; i++)
        {
            ReadOnlySpan<byte> entry = header.AsSpan(576 + i * 24, 24);
            string code = Encoding.ASCII.GetString(entry[..4]);
            int dataLength = BinaryPrimitives.ReadInt32BigEndian(entry[8..]);
            long dataOffset = BinaryPrimitives.ReadInt64BigEndian(entry[16..]);
            if (code is not ("W2ru" or "W2ku") || dataLength is <= 0 or > 2048 || dataOffset < 0 || dataOffset + dataLength > length)
            {
                continue;
            }

            byte[] data = new byte[dataLength];
            RandomAccess.Read(file, data, dataOffset);
            string p = Encoding.Unicode.GetString(data).TrimEnd('\0');
            candidates.Add(code == "W2ru" ? Path.Combine(dir, p) : p);
        }

        string name = Encoding.BigEndianUnicode.GetString(header, 64, 512).TrimEnd('\0');
        if (name.Length > 0)
        {
            candidates.Add(Path.Combine(dir, Path.GetFileName(name)));
        }

        return candidates.Select(c => { try { return Path.GetFullPath(c); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; } })
            .FirstOrDefault(c => c is not null && File.Exists(c) && !string.Equals(c, child, StringComparison.OrdinalIgnoreCase));
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        if (_type == FixedType)
        {
            int read = RandomAccess.Read(_file, buffer[..count], offset);
            return read < count ? Short(buffer, offset, read, count) : new ReadResult(count);
        }

        List<UnreadableRange>? bad = null;
        int done = 0;
        while (done < count)
        {
            long pos = offset + done;
            long block = pos / _blockSize;
            int inBlock = (int)(pos % _blockSize);
            int n = Math.Min(count - done, _blockSize - inBlock);
            Span<byte> part = buffer.Slice(done, n);
            uint sector = block < _bat.Length ? _bat[block] : uint.MaxValue;
            if (sector == uint.MaxValue)
            {
                ReadParentOrZero(pos, part, ref bad);
            }
            else
            {
                long dataStart = (long)sector * 512 + _bitmapBytes;
                if (_type == DifferencingType)
                {
                    ReadDifferencing(sector, inBlock, pos, part, ref bad);
                }
                else if (dataStart + inBlock + n > _fileLength)
                {
                    part.Clear();
                    (bad ??= []).Add(new UnreadableRange(pos, n, UnreadableReason.IoError));
                }
                else
                {
                    RandomAccess.Read(_file, part, dataStart + inBlock);
                }
            }

            done += n;
        }

        return new ReadResult(count, bad);
    }

    /// <summary>差分ディスクの割り当て済みブロック: セクタのビットマップが 1 のセクタはこのファイル、0 のセクタは親から読む。</summary>
    private void ReadDifferencing(uint blockSector, int inBlock, long pos, Span<byte> part, ref List<UnreadableRange>? bad)
    {
        byte[] bitmap = new byte[_bitmapBytes];
        long bitmapAt = (long)blockSector * 512;
        if (bitmapAt + _bitmapBytes > _fileLength || RandomAccess.Read(_file, bitmap, bitmapAt) != _bitmapBytes)
        {
            part.Clear();
            (bad ??= []).Add(new UnreadableRange(pos, part.Length, UnreadableReason.IoError));
            return;
        }

        int done = 0;
        while (done < part.Length)
        {
            int at = inBlock + done;
            int sectorIndex = at / 512;
            int n = Math.Min(part.Length - done, 512 - at % 512);
            bool present = (bitmap[sectorIndex / 8] & (0x80 >> (sectorIndex % 8))) != 0;
            Span<byte> piece = part.Slice(done, n);
            if (present)
            {
                long from = bitmapAt + _bitmapBytes + at;
                if (from + n > _fileLength || RandomAccess.Read(_file, piece, from) != n)
                {
                    piece.Clear();
                    (bad ??= []).Add(new UnreadableRange(pos + done, n, UnreadableReason.IoError));
                }
            }
            else
            {
                ReadParentOrZero(pos + done, piece, ref bad);
            }

            done += n;
        }
    }

    private void ReadParentOrZero(long pos, Span<byte> part, ref List<UnreadableRange>? bad)
    {
        if (_parent is null)
        {
            part.Clear();
            return;
        }

        ReadResult r = _parent.Read(pos, part);
        if (!r.IsComplete)
        {
            (bad ??= []).AddRange(r.Unreadable);
        }
    }

    private static ReadResult Short(Span<byte> buffer, long offset, int read, int count)
    {
        buffer[read..count].Clear();
        return new ReadResult(count, [new UnreadableRange(offset + read, count - read, UnreadableReason.IoError)]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _parent?.Dispose();
            _file.Dispose();
        }
    }
}
