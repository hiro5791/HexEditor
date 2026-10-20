using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using HexEditor.Core.Forensics.Images;

namespace HexEditor.Core.Tests.Forensics.Images;

/// <summary>
/// テスト用の VHD・VHDX・VMDK を作る (各形式の公開仕様のとおりに直接書く。読み込みの実装とは別に書いた最小の書き込み)。
/// 内容は関数 (オフセット → バイト列) で与え、0 だけのブロックは割り当てない。
/// </summary>
internal static class VirtualDiskWriters
{
    public delegate void Content(long offset, Span<byte> destination);

    private static bool IsZero(ReadOnlySpan<byte> data) => !data.ContainsAnyExcept((byte)0);

    // ---- VHD ----

    private static byte[] VhdFooter(long size, int type, long dataOffset, Guid? id = null)
    {
        byte[] f = new byte[512];
        "conectix"u8.CopyTo(f);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(12), 0x00010000);
        BinaryPrimitives.WriteInt64BigEndian(f.AsSpan(16), dataOffset);
        "test"u8.CopyTo(f.AsSpan(28));
        BinaryPrimitives.WriteInt64BigEndian(f.AsSpan(40), size);
        BinaryPrimitives.WriteInt64BigEndian(f.AsSpan(48), size);
        BinaryPrimitives.WriteInt32BigEndian(f.AsSpan(60), type);
        (id ?? Guid.NewGuid()).TryWriteBytes(f.AsSpan(68));
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(64), OnesComplement(f));
        return f;
    }

    private static uint OnesComplement(byte[] data)
    {
        uint sum = 0;
        foreach (byte b in data)
        {
            sum += b;
        }

        return ~sum;
    }

    public static void VhdFixed(string path, long size, Content content)
    {
        using var stream = new FileStream(path, FileMode.Create);
        byte[] buffer = new byte[1 << 20];
        for (long pos = 0; pos < size; pos += buffer.Length)
        {
            int n = (int)Math.Min(buffer.Length, size - pos);
            content(pos, buffer.AsSpan(0, n));
            stream.Write(buffer, 0, n);
        }

        stream.Write(VhdFooter(size, 2, -1));
    }

    /// <summary>可変 (type 3) または差分 (type 4。<paramref name="parent"/> の名前を W2ru で書く) の VHD。差分ではブロックの中で内容のあるセクタだけビットマップを立てる。</summary>
    public static void VhdDynamic(string path, long size, Content content, int blockSize = 2 * 1024 * 1024, string? parent = null,
        Func<long, bool>? sectorPresent = null)
    {
        int type = parent is null ? 3 : 4;
        long blocks = (size + blockSize - 1) / blockSize;
        int bitmap = (blockSize / 512 / 8 + 511) / 512 * 512;
        long tableOffset = 512 + 1024;
        long tableBytes = (blocks * 4 + 511) / 512 * 512;
        long locatorOffset = tableOffset + tableBytes;
        byte[] locatorData = parent is null ? [] : Encoding.Unicode.GetBytes(Path.GetFileName(parent));
        long dataStart = locatorOffset + (locatorData.Length + 511) / 512 * 512;

        using var stream = new FileStream(path, FileMode.Create);
        byte[] footer = VhdFooter(size, type, 512);
        stream.Write(footer);
        byte[] header = new byte[1024];
        "cxsparse"u8.CopyTo(header);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(8), -1);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16), tableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), (uint)blocks);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(32), (uint)blockSize);
        if (parent is not null)
        {
            Encoding.BigEndianUnicode.GetBytes(Path.GetFileName(parent)).CopyTo(header.AsSpan(64));
            "W2ru"u8.CopyTo(header.AsSpan(576));
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(576 + 4), 512);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(576 + 8), locatorData.Length);
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(576 + 16), locatorOffset);
        }

        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(36), OnesComplement(header));
        stream.Write(header);
        uint[] bat = new uint[blocks];
        Array.Fill(bat, uint.MaxValue);
        long at = dataStart;
        byte[] data = new byte[blockSize];
        var blockData = new List<(long At, byte[] Bitmap, byte[] Data)>();
        for (long b = 0; b < blocks; b++)
        {
            int n = (int)Math.Min(blockSize, size - b * blockSize);
            Array.Clear(data);
            content(b * blockSize, data.AsSpan(0, n));
            byte[] bits = new byte[bitmap];
            bool any = false;
            for (int s = 0; s < blockSize / 512; s++)
            {
                bool present = sectorPresent?.Invoke(b * blockSize + s * 512L) ?? !IsZero(data.AsSpan(s * 512, 512));
                if (present)
                {
                    bits[s / 8] |= (byte)(0x80 >> (s % 8));
                    any = true;
                }
            }

            if (any)
            {
                bat[b] = (uint)(at / 512);
                blockData.Add((at, bits, (byte[])data.Clone()));
                at += bitmap + blockSize;
            }
        }

        byte[] table = new byte[tableBytes];
        table.AsSpan().Fill(0xFF);
        for (int i = 0; i < bat.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(i * 4), bat[i]);
        }

        stream.Position = tableOffset;
        stream.Write(table);
        stream.Position = locatorOffset;
        stream.Write(locatorData);
        foreach ((long position, byte[] bits, byte[] blockBytes) in blockData)
        {
            stream.Position = position;
            stream.Write(bits);
            stream.Write(blockBytes);
        }

        stream.Position = at;
        stream.Write(footer);
    }

    // ---- VHDX ----

    private static readonly Guid BatRegion = new("2DC27766-F623-4200-9D64-115E9BFD4A08");
    private static readonly Guid MetadataRegion = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
    private const long Mib = 1024 * 1024;

    /// <summary>
    /// VHDX (可変、または <paramref name="parent"/> を指定した差分)。差分では <paramref name="sectorPresent"/> が真のセクタだけをこのファイルに置き
    /// (PARTIALLY_PRESENT とセクタのビットマップ)、それ以外は親から読む。
    /// </summary>
    public static void Vhdx(string path, long size, Content content, long blockSize = 2 * Mib, int sectorSize = 512, string? parent = null,
        Func<long, bool>? sectorPresent = null, bool fixedDisk = false)
    {
        bool diff = parent is not null;
        long chunkRatio = (1L << 23) * sectorSize / blockSize;
        long dataBlocks = (size + blockSize - 1) / blockSize;
        long sbBlocks = (dataBlocks + chunkRatio - 1) / chunkRatio;
        long entries = diff ? sbBlocks * (chunkRatio + 1) : dataBlocks + (dataBlocks - 1) / chunkRatio;
        long batOffset = 2 * Mib;
        long batLength = Math.Max(Mib, (entries * 8 + Mib - 1) / Mib * Mib);
        long metaOffset = batOffset + batLength;
        long next = metaOffset + Mib;

        using var stream = new FileStream(path, FileMode.Create);
        stream.SetLength(next);
        stream.Position = 0;
        stream.Write("vhdxfile"u8);
        stream.Write(Encoding.Unicode.GetBytes("HexEditor test"));
        foreach ((long at, ulong sequence) in new[] { (64 * 1024L, 1UL), (128 * 1024L, 2UL) })
        {
            byte[] h = new byte[4096];
            "head"u8.CopyTo(h);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(8), sequence);
            Guid.NewGuid().TryWriteBytes(h.AsSpan(16));
            Guid.NewGuid().TryWriteBytes(h.AsSpan(32));
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(66), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(68), (uint)Mib);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), Mib);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), ImageChecksums.Crc32C(h));
            stream.Position = at;
            stream.Write(h);
        }

        byte[] regions = new byte[64 * 1024];
        "regi"u8.CopyTo(regions);
        BinaryPrimitives.WriteUInt32LittleEndian(regions.AsSpan(8), 2);
        BatRegion.TryWriteBytes(regions.AsSpan(16));
        BinaryPrimitives.WriteInt64LittleEndian(regions.AsSpan(32), batOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(regions.AsSpan(40), (uint)batLength);
        BinaryPrimitives.WriteUInt32LittleEndian(regions.AsSpan(44), 1);
        MetadataRegion.TryWriteBytes(regions.AsSpan(48));
        BinaryPrimitives.WriteInt64LittleEndian(regions.AsSpan(64), metaOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(regions.AsSpan(72), (uint)Mib);
        BinaryPrimitives.WriteUInt32LittleEndian(regions.AsSpan(76), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(regions.AsSpan(4), ImageChecksums.Crc32C(regions));
        foreach (long at in new[] { 192 * 1024L, 256 * 1024L })
        {
            stream.Position = at;
            stream.Write(regions);
        }

        // メタデータ。
        var items = new List<(Guid Id, byte[] Data)>
        {
            (new Guid("CAA16737-FA36-4D43-B3B6-33F0AA44E76B"), [.. BitConverter.GetBytes((uint)blockSize), (byte)((fixedDisk ? 1 : 0) | (diff ? 2 : 0)), 0, 0, 0]),
            (new Guid("2FA54224-CD1B-4876-B211-5DBED83BF4B8"), BitConverter.GetBytes((ulong)size)),
            (new Guid("8141BF1D-A96F-4709-BA47-F233A8FAAB5F"), BitConverter.GetBytes((uint)sectorSize)),
            (new Guid("CDA348C7-445D-4471-9CC9-E9885251C556"), BitConverter.GetBytes((uint)4096)),
        };
        if (diff)
        {
            items.Add((new Guid("A8D35F2D-B30B-454D-ABF7-D3D84834AB0C"), ParentLocator(Path.GetFileName(parent!))));
        }

        byte[] meta = new byte[Mib];
        "metadata"u8.CopyTo(meta);
        BinaryPrimitives.WriteUInt16LittleEndian(meta.AsSpan(10), (ushort)items.Count);
        int dataAt = 64 * 1024;
        for (int i = 0; i < items.Count; i++)
        {
            Span<byte> e = meta.AsSpan(32 + i * 32, 32);
            items[i].Id.TryWriteBytes(e);
            BinaryPrimitives.WriteUInt32LittleEndian(e[16..], (uint)dataAt);
            BinaryPrimitives.WriteUInt32LittleEndian(e[20..], (uint)items[i].Data.Length);
            items[i].Data.CopyTo(meta, dataAt);
            dataAt += (items[i].Data.Length + 7) / 8 * 8;
        }

        stream.Position = metaOffset;
        stream.Write(meta);

        ulong[] bat = new ulong[entries];
        byte[] block = new byte[blockSize];
        for (long b = 0; b < dataBlocks; b++)
        {
            int n = (int)Math.Min(blockSize, size - b * blockSize);
            Array.Clear(block);
            content(b * blockSize, block.AsSpan(0, n));
            long index = b + b / chunkRatio;
            if (diff)
            {
                bool any = false;
                byte[] bitmap = SectorBitmap(stream, bat, b, chunkRatio, ref next);
                for (long s = 0; s < blockSize / sectorSize; s++)
                {
                    if (sectorPresent!(b * blockSize + s * sectorSize))
                    {
                        long inChunk = (b % chunkRatio) * (blockSize / sectorSize) + s;
                        bitmap[inChunk / 8] |= (byte)(1 << (int)(inChunk % 8));
                        any = true;
                    }
                }

                WriteBitmap(stream, bat, b, chunkRatio, bitmap);
                if (!any)
                {
                    continue;
                }

                bat[index] = ((ulong)(next / Mib) << 20) | 7;
            }
            else if (!fixedDisk && IsZero(block))
            {
                continue;
            }
            else
            {
                bat[index] = ((ulong)(next / Mib) << 20) | 6;
            }

            stream.Position = next;
            stream.Write(block);
            next += blockSize;
        }

        byte[] rawBat = new byte[batLength];
        for (int i = 0; i < bat.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(rawBat.AsSpan(i * 8), bat[i]);
        }

        stream.Position = batOffset;
        stream.Write(rawBat);
        stream.SetLength(Math.Max(stream.Length, next));
    }

    private static byte[] SectorBitmap(FileStream stream, ulong[] bat, long block, long chunkRatio, ref long next)
    {
        long sbIndex = (block / chunkRatio + 1) * (chunkRatio + 1) - 1;
        if ((bat[sbIndex] & 7) != 6)
        {
            bat[sbIndex] = ((ulong)(next / Mib) << 20) | 6;
            stream.SetLength(Math.Max(stream.Length, next + Mib));
            next += Mib;
        }

        byte[] bitmap = new byte[Mib];
        stream.Position = (long)(bat[sbIndex] >> 20) * Mib;
        stream.ReadExactly(bitmap);
        return bitmap;
    }

    private static void WriteBitmap(FileStream stream, ulong[] bat, long block, long chunkRatio, byte[] bitmap)
    {
        long sbIndex = (block / chunkRatio + 1) * (chunkRatio + 1) - 1;
        stream.Position = (long)(bat[sbIndex] >> 20) * Mib;
        stream.Write(bitmap);
    }

    private static byte[] ParentLocator(string relative)
    {
        string[] keys = ["parent_linkage", "relative_path"];
        string[] values = [Guid.NewGuid().ToString("B"), ".\\" + relative];
        var data = new List<byte>();
        byte[] header = new byte[20 + keys.Length * 12];
        new Guid("B04AEFB7-D19E-4A81-B789-25B8E9445913").TryWriteBytes(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18), (ushort)keys.Length);
        int at = header.Length;
        for (int i = 0; i < keys.Length; i++)
        {
            byte[] k = Encoding.Unicode.GetBytes(keys[i]);
            byte[] v = Encoding.Unicode.GetBytes(values[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20 + i * 12), (uint)at);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24 + i * 12), (uint)(at + k.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28 + i * 12), (ushort)k.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30 + i * 12), (ushort)v.Length);
            data.AddRange(k);
            data.AddRange(v);
            at += k.Length + v.Length;
        }

        return [.. header, .. data];
    }

    // ---- VMDK ----

    /// <summary>hosted sparse (monolithicSparse。記述を埋め込む)、または <paramref name="streamOptimized"/> (圧縮したグレインとマーカー、GD は末尾)。</summary>
    public static void VmdkSparse(string path, long size, Content content, bool streamOptimized = false, int grainSectors = 128, string? parentHint = null)
    {
        const int Gtes = 512;
        long capacity = size / 512;
        long grainBytes = grainSectors * 512L;
        long grains = (capacity + grainSectors - 1) / grainSectors;
        long gdes = (grains + Gtes - 1) / Gtes;
        string descriptor = "# Disk DescriptorFile\nversion=1\nCID=12345678\nparentCID=" + (parentHint is null ? "ffffffff" : "87654321")
            + "\ncreateType=\"" + (streamOptimized ? "streamOptimized" : "monolithicSparse") + "\"\n"
            + (parentHint is null ? string.Empty : "parentFileNameHint=\"" + parentHint + "\"\n")
            + "\n# Extent description\nRW " + capacity + " SPARSE \"" + Path.GetFileName(path) + "\"\n";
        byte[] desc = new byte[20 * 512];
        Encoding.ASCII.GetBytes(descriptor).CopyTo(desc, 0);

        long descriptorSector = 1;
        long gdSector = descriptorSector + 20;
        long gtSector = gdSector + (gdes * 4 + 511) / 512;
        long gtSectors = gdes * (Gtes * 4 / 512);
        long next = gtSector + gtSectors;
        next = (next + grainSectors - 1) / grainSectors * grainSectors;

        uint[] gd = new uint[gdes];
        uint[] gt = new uint[gdes * Gtes];
        for (long i = 0; i < gdes; i++)
        {
            gd[i] = (uint)(gtSector + i * (Gtes * 4 / 512));
        }

        using var stream = new FileStream(path, FileMode.Create);
        byte[] grain = new byte[grainBytes];
        var writes = new List<(long Sector, byte[] Data)>();
        for (long g = 0; g < grains; g++)
        {
            long offset = g * grainBytes;
            int n = (int)Math.Min(grainBytes, size - offset);
            Array.Clear(grain);
            content(offset, grain.AsSpan(0, n));
            if (IsZero(grain))
            {
                continue;
            }

            if (streamOptimized)
            {
                using var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                {
                    z.Write(grain);
                }

                byte[] marker = new byte[(12 + ms.Length + 511) / 512 * 512];
                BinaryPrimitives.WriteInt64LittleEndian(marker, g * grainSectors);
                BinaryPrimitives.WriteUInt32LittleEndian(marker.AsSpan(8), (uint)ms.Length);
                ms.ToArray().CopyTo(marker, 12);
                gt[g] = (uint)next;
                writes.Add((next, marker));
                next += marker.Length / 512;
            }
            else
            {
                gt[g] = (uint)next;
                writes.Add((next, (byte[])grain.Clone()));
                next += grainSectors;
            }
        }

        byte[] Header(long gdOffset)
        {
            byte[] h = new byte[512];
            "KDMV"u8.CopyTo(h);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), streamOptimized ? 3u : 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 1 | 2 | (streamOptimized ? (1u << 16) | (1u << 17) : 0));
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(12), capacity);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(20), grainSectors);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(28), descriptorSector);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(36), 20);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(44), Gtes);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(48), 0);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(56), gdOffset);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(64), next);
            h[73] = (byte)'\n';
            h[74] = (byte)' ';
            h[75] = (byte)'\r';
            h[76] = (byte)'\n';
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(77), (ushort)(streamOptimized ? 1 : 0));
            return h;
        }

        stream.Write(Header(streamOptimized ? -1 : gdSector));
        stream.Write(desc);
        byte[] rawGd = new byte[(gdes * 4 + 511) / 512 * 512];
        for (int i = 0; i < gd.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rawGd.AsSpan(i * 4), gd[i]);
        }

        stream.Position = gdSector * 512;
        stream.Write(rawGd);
        byte[] rawGt = new byte[gtSectors * 512];
        for (int i = 0; i < gt.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rawGt.AsSpan(i * 4), gt[i]);
        }

        stream.Position = gtSector * 512;
        stream.Write(rawGt);
        foreach ((long sector, byte[] data) in writes)
        {
            stream.Position = sector * 512;
            stream.Write(data);
        }

        stream.Position = next * 512;
        if (streamOptimized)
        {
            // フッターのマーカー、フッター (GD の位置を持つヘッダー)、ストリームの終わりのマーカー。
            stream.Write(new byte[512]);
            stream.Write(Header(gdSector));
            stream.Write(new byte[512]);
        }
    }

    /// <summary>記述ファイル (twoGbMaxExtentFlat の形: FLAT のエクステント 2 つと ZERO 1 つ)。</summary>
    public static void VmdkFlat(string descriptorPath, long sizeA, long zero, long sizeB, Content content)
    {
        string dir = Path.GetDirectoryName(descriptorPath)!;
        string name = Path.GetFileNameWithoutExtension(descriptorPath);
        void Flat(string file, long from, long size)
        {
            using var s = new FileStream(Path.Combine(dir, file), FileMode.Create);
            byte[] data = new byte[size];
            content(from, data);
            s.Write(data);
        }

        Flat(name + "-f001.vmdk", 0, sizeA);
        Flat(name + "-f002.vmdk", sizeA + zero, sizeB);
        File.WriteAllText(descriptorPath, "# Disk DescriptorFile\nversion=1\nCID=fffffffe\nparentCID=ffffffff\ncreateType=\"twoGbMaxExtentFlat\"\n\n"
            + $"RW {sizeA / 512} FLAT \"{name}-f001.vmdk\" 0\nRW {zero / 512} ZERO\nRW {sizeB / 512} FLAT \"{name}-f002.vmdk\" 0\n");
    }
}
