using System.Buffers.Binary;
using System.Text;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Forensics.Images.VirtualDisks;

/// <summary>
/// VHDX ([MS-VHDX] 2.0) の読み込み (FOR-21 の仕様 9)。固定・可変・差分。ヘッダー 2 つのうち CRC-32C が正しくシーケンス番号の大きいもの、
/// 領域表から BAT とメタデータの領域を求める。BAT (1 エントリ 8 バイト) は開くときに読む。ログが残っている (正しく閉じられていない) VHDX は、
/// ログを適用しないと内容が正しくないため開かない (読み取り専用で開くため、ログの適用はしない)。
/// </summary>
public sealed class VhdxByteSource : ImageSourceBase
{
    private static readonly Guid BatRegion = new("2DC27766-F623-4200-9D64-115E9BFD4A08");
    private static readonly Guid MetadataRegion = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
    private static readonly Guid FileParameters = new("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");
    private static readonly Guid VirtualDiskSize = new("2FA54224-CD1B-4876-B211-5DBED83BF4B8");
    private static readonly Guid LogicalSectorSizeItem = new("8141BF1D-A96F-4709-BA47-F233A8FAAB5F");
    private static readonly Guid PhysicalSectorSizeItem = new("CDA348C7-445D-4471-9CC9-E9885251C556");
    private static readonly Guid ParentLocatorItem = new("A8D35F2D-B30B-454D-ABF7-D3D84834AB0C");

    private const long Mib = 1024 * 1024;
    private const int MaxParentDepth = 16;

    private readonly SafeFileHandle _file;
    private readonly long _fileLength;
    private readonly ulong[] _bat;
    private readonly long _blockSize;
    private readonly long _chunkRatio;
    private readonly bool _hasParent;
    private readonly IByteSource? _parent;
    private readonly int _physicalSectorSize;

    private VhdxByteSource(string path, ImageInfo info, SafeFileHandle file, long fileLength, ulong[] bat, long blockSize, long chunkRatio,
        bool hasParent, IByteSource? parent, int physicalSectorSize) : base(path, info)
    {
        _file = file;
        _fileLength = fileLength;
        _bat = bat;
        _blockSize = blockSize;
        _chunkRatio = chunkRatio;
        _hasParent = hasParent;
        _parent = parent;
        _physicalSectorSize = physicalSectorSize;
    }

    public override int PhysicalSectorSize => _physicalSectorSize;

    public static bool LooksLikeVhdx(SafeFileHandle file)
    {
        Span<byte> sig = stackalloc byte[8];
        return RandomAccess.Read(file, sig, 0) == 8 && sig.SequenceEqual("vhdxfile"u8);
    }

    public static VhdxByteSource Open(string path, int depth = 0)
    {
        if (depth > MaxParentDepth)
        {
            throw new ImageFormatException(ImageFormat.Vhdx, "parent", "The chain of differencing disks is too deep.");
        }

        string full = Path.GetFullPath(path);
        SafeFileHandle file = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        IByteSource? parent = null;
        try
        {
            long length = RandomAccess.GetLength(file);
            if (length < 1 * Mib || !LooksLikeVhdx(file))
            {
                throw new ImageFormatException(ImageFormat.Vhdx, "identifier", "The VHDX file type identifier (vhdxfile) was not found.");
            }

            byte[] header = BestHeader(file);
            Guid logGuid = new(header.AsSpan(48, 16));
            if (logGuid != Guid.Empty)
            {
                throw new ImageFormatException(ImageFormat.Vhdx, "log", "The VHDX has a log that has not been applied (it was not closed cleanly).");
            }

            Dictionary<Guid, (long Offset, long Length)> regions = ReadRegions(file, length);
            if (!regions.TryGetValue(BatRegion, out (long Offset, long Length) batRegion) || !regions.TryGetValue(MetadataRegion, out (long Offset, long Length) metaRegion))
            {
                throw new ImageFormatException(ImageFormat.Vhdx, "region", "The VHDX region table has no BAT or metadata region.");
            }

            Dictionary<Guid, byte[]> metadata = ReadMetadata(file, metaRegion);
            byte[] parameters = Item(metadata, FileParameters, 8);
            long blockSize = BinaryPrimitives.ReadUInt32LittleEndian(parameters);
            bool hasParent = (parameters[4] & 0x02) != 0;
            long size = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(Item(metadata, VirtualDiskSize, 8)), 1UL << 56);
            int logical = (int)BinaryPrimitives.ReadUInt32LittleEndian(Item(metadata, LogicalSectorSizeItem, 4));
            int physical = metadata.TryGetValue(PhysicalSectorSizeItem, out byte[]? ps) && ps.Length >= 4 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(ps) : logical;
            if (blockSize is < Mib or > 256 * Mib || (blockSize & (blockSize - 1)) != 0 || logical is not (512 or 4096) || size < 0 || size % logical != 0)
            {
                throw new ImageFormatException(ImageFormat.Vhdx, "metadata", "The VHDX metadata has invalid values.");
            }

            long chunkRatio = (1L << 23) * logical / blockSize;
            long dataBlocks = (size + blockSize - 1) / blockSize;
            long sectorBitmapBlocks = (dataBlocks + chunkRatio - 1) / chunkRatio;
            long totalEntries = hasParent ? sectorBitmapBlocks * (chunkRatio + 1) : dataBlocks + (dataBlocks - 1) / chunkRatio;
            if (totalEntries < 0 || totalEntries * 8 > batRegion.Length || totalEntries > 1 << 27)
            {
                throw new ImageFormatException(ImageFormat.Vhdx, "bat", "The VHDX BAT region is too small for the disk size.");
            }

            byte[] raw = new byte[totalEntries * 8];
            RandomAccess.Read(file, raw, batRegion.Offset);
            ulong[] bat = new ulong[totalEntries];
            for (int i = 0; i < bat.Length; i++)
            {
                bat[i] = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(i * 8));
            }

            string? parentPath = null;
            if (hasParent)
            {
                parentPath = FindParent(full, metadata.GetValueOrDefault(ParentLocatorItem));
                if (parentPath is null)
                {
                    throw new ParentImageNotFoundException(full, "parent_linkage");
                }

                parent = ImageOpener.OpenParent(parentPath, depth + 1);
            }

            string creator = Encoding.Unicode.GetString(ReadAt(file, 8, 512)).TrimEnd('\0');
            var info = new ImageInfo
            {
                Format = ImageFormat.Vhdx,
                Files = parent is IImageSource p ? [full, .. p.Info.Files] : [full],
                MediaSize = size,
                BytesPerSector = logical,
                Subtype = hasParent ? "differencing" : (parameters[4] & 0x01) != 0 ? "fixed" : "dynamic",
                AcquisitionTool = creator.Length > 0 ? creator : null,
                ParentPath = parentPath,
            };
            return new VhdxByteSource(full, info, file, length, bat, blockSize, chunkRatio, hasParent, parent, physical);
        }
        catch
        {
            parent?.Dispose();
            file.Dispose();
            throw;
        }
    }

    private static byte[] ReadAt(SafeFileHandle file, long offset, int length)
    {
        byte[] data = new byte[length];
        int n = RandomAccess.Read(file, data, offset);
        return n == length ? data : data[..n];
    }

    /// <summary>64 KiB と 128 KiB のヘッダーのうち、署名と CRC-32C が正しくシーケンス番号の大きいもの。</summary>
    private static byte[] BestHeader(SafeFileHandle file)
    {
        byte[]? best = null;
        ulong bestSequence = 0;
        foreach (long at in new[] { 64 * 1024L, 128 * 1024L })
        {
            byte[] h = ReadAt(file, at, 4096);
            if (h.Length == 4096 && h.AsSpan(0, 4).SequenceEqual("head"u8) && Crc32COk(h, 4))
            {
                ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(8));
                if (best is null || sequence > bestSequence)
                {
                    best = h;
                    bestSequence = sequence;
                }
            }
        }

        return best ?? throw new ImageFormatException(ImageFormat.Vhdx, "header", "Neither VHDX header is valid.");
    }

    private static bool Crc32COk(byte[] data, int at)
    {
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        byte[] copy = (byte[])data.Clone();
        copy.AsSpan(at, 4).Clear();
        return stored == ImageChecksums.Crc32C(copy);
    }

    private static Dictionary<Guid, (long Offset, long Length)> ReadRegions(SafeFileHandle file, long length)
    {
        foreach (long at in new[] { 192 * 1024L, 256 * 1024L })
        {
            byte[] table = ReadAt(file, at, 64 * 1024);
            if (table.Length != 64 * 1024 || !table.AsSpan(0, 4).SequenceEqual("regi"u8) || !Crc32COk(table, 4))
            {
                continue;
            }

            int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(8)), 2047);
            var regions = new Dictionary<Guid, (long, long)>();
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> e = table.AsSpan(16 + i * 32, 32);
                var guid = new Guid(e[..16]);
                long offset = BinaryPrimitives.ReadInt64LittleEndian(e[16..]);
                long size = BinaryPrimitives.ReadUInt32LittleEndian(e[24..]);
                if (offset >= Mib && offset % Mib == 0 && size > 0 && offset + size <= length)
                {
                    regions.TryAdd(guid, (offset, size));
                }
            }

            return regions;
        }

        throw new ImageFormatException(ImageFormat.Vhdx, "region", "Neither VHDX region table is valid.");
    }

    private static Dictionary<Guid, byte[]> ReadMetadata(SafeFileHandle file, (long Offset, long Length) region)
    {
        byte[] table = ReadAt(file, region.Offset, 64 * 1024);
        if (table.Length < 32 || !table.AsSpan(0, 8).SequenceEqual("metadata"u8))
        {
            throw new ImageFormatException(ImageFormat.Vhdx, "metadata", "The VHDX metadata table was not found.");
        }

        int count = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(10)), 2047);
        var items = new Dictionary<Guid, byte[]>();
        for (int i = 0; i < count && 32 + (i + 1) * 32 <= table.Length; i++)
        {
            ReadOnlySpan<byte> e = table.AsSpan(32 + i * 32, 32);
            var guid = new Guid(e[..16]);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(e[20..]), 1 << 20);
            if (length > 0 && offset >= 64 * 1024 && offset + length <= region.Length)
            {
                items.TryAdd(guid, ReadAt(file, region.Offset + offset, length));
            }
        }

        return items;
    }

    private static byte[] Item(Dictionary<Guid, byte[]> metadata, Guid id, int minLength) =>
        metadata.TryGetValue(id, out byte[]? value) && value.Length >= minLength
            ? value
            : throw new ImageFormatException(ImageFormat.Vhdx, "metadata", $"The VHDX metadata item {id} is missing.");

    /// <summary>親ロケーター (キーと値の表。relative_path・absolute_win32_path・volume_path) から、存在する親を探す。</summary>
    private static string? FindParent(string child, byte[]? locator)
    {
        if (locator is null || locator.Length < 20)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(locator.AsSpan(18));
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count && 20 + (i + 1) * 12 <= locator.Length; i++)
        {
            ReadOnlySpan<byte> e = locator.AsSpan(20 + i * 12, 12);
            int keyOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(e);
            int valueOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(e[8..]);
            int valueLength = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]);
            if (keyOffset < 0 || valueOffset < 0 || keyOffset + keyLength > locator.Length || valueOffset + valueLength > locator.Length)
            {
                continue;
            }

            values.TryAdd(Encoding.Unicode.GetString(locator, keyOffset, keyLength), Encoding.Unicode.GetString(locator, valueOffset, valueLength));
        }

        string dir = Path.GetDirectoryName(child)!;
        foreach (string key in new[] { "relative_path", "absolute_win32_path", "volume_path" })
        {
            if (values.TryGetValue(key, out string? value) && value.Length > 0)
            {
                try
                {
                    string candidate = Path.GetFullPath(key == "relative_path" ? Path.Combine(dir, value) : value);
                    if (File.Exists(candidate) && !string.Equals(candidate, child, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                }
            }
        }

        return null;
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        List<UnreadableRange>? bad = null;
        int done = 0;
        while (done < count)
        {
            long pos = offset + done;
            long block = pos / _blockSize;
            long inBlock = pos % _blockSize;
            int n = (int)Math.Min(count - done, _blockSize - inBlock);
            Span<byte> part = buffer.Slice(done, n);
            long index = block + block / _chunkRatio;
            ulong entry = index < _bat.Length ? _bat[index] : 0;
            int state = (int)(entry & 7);
            long fileOffset = (long)(entry >> 20) * Mib;
            switch (state)
            {
                case 6: // PAYLOAD_BLOCK_FULLY_PRESENT
                    ReadFile(fileOffset + inBlock, part, pos, ref bad);
                    break;
                case 7: // PAYLOAD_BLOCK_PARTIALLY_PRESENT (差分): セクタのビットマップで決める
                    ReadPartial(block, fileOffset, inBlock, pos, part, ref bad);
                    break;
                case 0 when _hasParent: // PAYLOAD_BLOCK_NOT_PRESENT: 親から
                    ReadParent(pos, part, ref bad);
                    break;
                default: // ZERO / UNMAPPED / UNDEFINED / NOT_PRESENT (親なし)
                    part.Clear();
                    break;
            }

            done += n;
        }

        return new ReadResult(count, bad);
    }

    private void ReadFile(long at, Span<byte> part, long pos, ref List<UnreadableRange>? bad)
    {
        if (at < 0 || at + part.Length > _fileLength || RandomAccess.Read(_file, part, at) != part.Length)
        {
            part.Clear();
            (bad ??= []).Add(new UnreadableRange(pos, part.Length, UnreadableReason.IoError));
        }
    }

    private void ReadParent(long pos, Span<byte> part, ref List<UnreadableRange>? bad)
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

    private void ReadPartial(long block, long blockOffset, long inBlock, long pos, Span<byte> part, ref List<UnreadableRange>? bad)
    {
        // セクタのビットマップのエントリ: チャンク (chunkRatio 個のブロック) ごとに 1 つ。1 ビットが 1 セクタ。
        long chunk = block / _chunkRatio;
        long bitmapIndex = (chunk + 1) * (_chunkRatio + 1) - 1;
        ulong sbEntry = bitmapIndex < _bat.Length ? _bat[bitmapIndex] : 0;
        long bitmapAt = (long)(sbEntry >> 20) * Mib;
        int sector = LogicalSectorSize;
        Span<byte> one = stackalloc byte[1];
        int done = 0;
        while (done < part.Length)
        {
            long at = inBlock + done;
            int n = (int)Math.Min(part.Length - done, sector - at % sector);
            long sectorInChunk = (block % _chunkRatio) * (_blockSize / sector) + at / sector;
            bool present = (sbEntry & 7) == 6 && RandomAccess.Read(_file, one, bitmapAt + sectorInChunk / 8) == 1
                && (one[0] & (1 << (int)(sectorInChunk % 8))) != 0;
            Span<byte> piece = part.Slice(done, n);
            if (present)
            {
                ReadFile(blockOffset + at, piece, pos + done, ref bad);
            }
            else
            {
                ReadParent(pos + done, piece, ref bad);
            }

            done += n;
        }
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
