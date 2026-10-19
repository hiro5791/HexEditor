using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Forensics.Images.VirtualDisks;

/// <summary>
/// VMDK (VMware Virtual Disk Format 1.1) の読み込み (FOR-21 の仕様 9)。記述ファイル (テキスト) のエクステント FLAT・VMFS・SPARSE・ZERO と、
/// 記述を埋め込んだ hosted sparse エクステント (monolithicSparse)、圧縮したグレインを持つ streamOptimized を読む。
/// 差分 (parentFileNameHint) は、割り当てていないグレインを親から読む。グレインテーブルは必要なものだけ読む。
/// </summary>
public sealed partial class VmdkByteSource : ImageSourceBase
{
    private const int MaxParentDepth = 16;
    private const int MaxExtents = 4096;

    private readonly List<Extent> _extents;
    private readonly long[] _starts;
    private readonly IByteSource? _parent;

    private VmdkByteSource(string path, ImageInfo info, List<Extent> extents, IByteSource? parent) : base(path, info)
    {
        _extents = extents;
        _starts = [.. extents.Select(e => e.Start)];
        _parent = parent;
    }

    /// <summary>先頭が hosted sparse の "KDMV" か、記述ファイルのテキストか。</summary>
    public static bool LooksLikeVmdk(SafeFileHandle file)
    {
        Span<byte> head = stackalloc byte[64];
        int n = RandomAccess.Read(file, head, 0);
        if (n >= 4 && head[..4].SequenceEqual("KDMV"u8))
        {
            return true;
        }

        string text = Encoding.ASCII.GetString(head[..n]);
        return text.Contains("Disk DescriptorFile", StringComparison.OrdinalIgnoreCase);
    }

    public static VmdkByteSource Open(string path, int depth = 0)
    {
        if (depth > MaxParentDepth)
        {
            throw new ImageFormatException(ImageFormat.Vmdk, "parent", "The chain of delta disks is too deep.");
        }

        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full)!;
        var extents = new List<Extent>();
        IByteSource? parent = null;
        try
        {
            string descriptor;
            SparseExtent? embedded = null;
            using (SafeFileHandle probe = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                Span<byte> magic = stackalloc byte[4];
                bool sparse = RandomAccess.Read(probe, magic, 0) == 4 && magic.SequenceEqual("KDMV"u8);
                if (sparse)
                {
                    embedded = SparseExtent.Open(full);
                    descriptor = embedded.Descriptor ?? string.Empty;
                }
                else
                {
                    long length = RandomAccess.GetLength(probe);
                    if (length > 1024 * 1024)
                    {
                        throw new ImageFormatException(ImageFormat.Vmdk, "descriptor", "The VMDK descriptor file is too large.");
                    }

                    byte[] text = new byte[length];
                    RandomAccess.Read(probe, text, 0);
                    descriptor = Encoding.UTF8.GetString(text);
                }
            }

            string createType = Value(descriptor, "createType") ?? (embedded is not null ? "monolithicSparse" : "unknown");
            long sector = 0;
            var files = new List<string> { full };
            foreach (Match m in ExtentLine().Matches(descriptor))
            {
                if (extents.Count >= MaxExtents)
                {
                    throw new ImageFormatException(ImageFormat.Vmdk, "descriptor", "The VMDK has too many extents.");
                }

                long size = long.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture);
                string type = m.Groups["type"].Value.ToUpperInvariant();
                string? name = m.Groups["name"].Success ? m.Groups["name"].Value : null;
                long offset = m.Groups["offset"].Success ? long.Parse(m.Groups["offset"].Value, CultureInfo.InvariantCulture) : 0;
                if (size <= 0 || size > 1L << 40)
                {
                    throw new ImageFormatException(ImageFormat.Vmdk, "descriptor", "A VMDK extent has an invalid size.");
                }

                Extent extent = type switch
                {
                    "ZERO" => new Extent(sector, size, ExtentKind.Zero, null, null, 0),
                    "FLAT" or "VMFS" or "VMFSRAW" => FlatExtent(dir, name, offset, sector, size, files),
                    "SPARSE" => SparseFor(dir, name, full, embedded, sector, size, files),
                    _ => throw new ImageFormatException(ImageFormat.Vmdk, "descriptor", $"The VMDK extent type {type} is not supported."),
                };
                extents.Add(extent);
                sector += size;
            }

            if (extents.Count == 0 && embedded is not null)
            {
                // 記述のない hosted sparse: ヘッダーの容量で 1 つのエクステント。
                extents.Add(new Extent(0, embedded.CapacitySectors, ExtentKind.Sparse, null, embedded, 0));
                sector = embedded.CapacitySectors;
            }

            if (embedded is not null && !extents.Any(e => ReferenceEquals(e.Sparse, embedded)))
            {
                embedded.Dispose();
            }

            if (extents.Count == 0)
            {
                throw new ImageFormatException(ImageFormat.Vmdk, "descriptor", "The VMDK descriptor has no extents.");
            }

            string? parentHint = Value(descriptor, "parentFileNameHint");
            string? parentCid = Value(descriptor, "parentCID");
            string? parentPath = null;
            if (parentHint is { Length: > 0 } && !string.Equals(parentCid, "ffffffff", StringComparison.OrdinalIgnoreCase))
            {
                parentPath = Path.GetFullPath(Path.Combine(dir, parentHint));
                if (!File.Exists(parentPath))
                {
                    throw new ParentImageNotFoundException(full, parentHint);
                }

                parent = ImageOpener.OpenParent(parentPath, depth + 1);
            }

            var info = new ImageInfo
            {
                Format = ImageFormat.Vmdk,
                Files = parent is IImageSource p ? [.. files.Distinct(StringComparer.OrdinalIgnoreCase), .. p.Info.Files] : [.. files.Distinct(StringComparer.OrdinalIgnoreCase)],
                MediaSize = sector * 512,
                BytesPerSector = 512,
                Subtype = createType,
                ParentPath = parentPath,
            };
            return new VmdkByteSource(full, info, extents, parent);
        }
        catch
        {
            foreach (Extent e in extents)
            {
                e.Dispose();
            }

            parent?.Dispose();
            throw;
        }
    }

    private static Extent FlatExtent(string dir, string? name, long offset, long start, long size, List<string> files)
    {
        string path = ExtentPath(dir, name);
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        files.Add(path);
        return new Extent(start, size, ExtentKind.Flat, handle, null, offset * 512);
    }

    private static Extent SparseFor(string dir, string? name, string self, SparseExtent? embedded, long start, long size, List<string> files)
    {
        // 記述を埋め込んだ sparse (monolithicSparse) のエクステントは自分自身 (ファイル名を変えていても自分自身として読む)。
        if (embedded is not null && (string.IsNullOrEmpty(name) || !File.Exists(Path.Combine(dir, name))
            || string.Equals(Path.GetFullPath(Path.Combine(dir, name)), self, StringComparison.OrdinalIgnoreCase)))
        {
            return new Extent(start, size, ExtentKind.Sparse, null, embedded, 0);
        }

        string path = ExtentPath(dir, name);
        SparseExtent sparse = SparseExtent.Open(path);
        files.Add(path);
        return new Extent(start, size, ExtentKind.Sparse, null, sparse, 0);
    }

    private static string ExtentPath(string dir, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ImageFormatException(ImageFormat.Vmdk, "descriptor", "A VMDK extent has no file name.");
        }

        string path = Path.GetFullPath(Path.Combine(dir, name));
        return File.Exists(path) ? path : throw new MissingSegmentsException(ImageFormat.Vmdk, [path]);
    }

    private static string? Value(string descriptor, string key)
    {
        Match m = Regex.Match(descriptor, "^\\s*" + Regex.Escape(key) + "\\s*=\\s*\"?([^\"\\r\\n]*)\"?", RegexOptions.Multiline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    [GeneratedRegex("^\\s*(?:RW|RDONLY|NOACCESS)\\s+(?<size>\\d{1,15})\\s+(?<type>[A-Za-z]+)(?:\\s+\"(?<name>[^\"]*)\"(?:\\s+(?<offset>\\d{1,15}))?)?", RegexOptions.Multiline, 1000)]
    private static partial Regex ExtentLine();

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        List<UnreadableRange>? bad = null;
        int done = 0;
        while (done < count)
        {
            long pos = offset + done;
            long sector = pos / 512;
            int index = Array.BinarySearch(_starts, sector);
            if (index < 0)
            {
                index = ~index - 1;
            }

            Extent e = _extents[index];
            long inExtent = pos - e.Start * 512;
            int n = (int)Math.Min(count - done, e.Sectors * 512 - inExtent);
            Span<byte> part = buffer.Slice(done, n);
            switch (e.Kind)
            {
                case ExtentKind.Zero:
                    part.Clear();
                    break;
                case ExtentKind.Flat:
                {
                    int read = RandomAccess.Read(e.Handle!, part, e.FileOffset + inExtent);
                    if (read < n)
                    {
                        part[read..].Clear();
                        (bad ??= []).Add(new UnreadableRange(pos + read, n - read, UnreadableReason.IoError));
                    }

                    break;
                }

                default:
                    e.Sparse!.Read(inExtent, part, pos, _parent, ref bad);
                    break;
            }

            done += n;
        }

        return new ReadResult(count, bad);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Extent e in _extents.DistinctBy(e => (object?)e.Sparse ?? e))
            {
                e.Dispose();
            }

            _parent?.Dispose();
        }
    }

    private enum ExtentKind
    {
        Zero,
        Flat,
        Sparse,
    }

    private sealed record Extent(long Start, long Sectors, ExtentKind Kind, SafeFileHandle? Handle, SparseExtent? Sparse, long FileOffset) : IDisposable
    {
        public void Dispose()
        {
            Handle?.Dispose();
            Sparse?.Dispose();
        }
    }

    /// <summary>hosted sparse エクステント (KDMV)。グレインディレクトリは開くときに、グレインテーブルは必要なときに読む。</summary>
    private sealed class SparseExtent : IDisposable
    {
        private const ulong GdAtEnd = ulong.MaxValue;
        private readonly SafeFileHandle _file;
        private readonly long _fileLength;
        private readonly uint[] _directory;
        private readonly int _gtes;
        private readonly long _grainSectors;
        private readonly bool _compressed;
        private readonly object _lock = new();
        private readonly Dictionary<long, uint[]> _tables = [];
        private readonly LinkedList<(long Grain, byte[] Data)> _grains = new();

        private SparseExtent(SafeFileHandle file, long fileLength, uint[] directory, int gtes, long grainSectors, long capacity, bool compressed, string? descriptor)
        {
            _file = file;
            _fileLength = fileLength;
            _directory = directory;
            _gtes = gtes;
            _grainSectors = grainSectors;
            CapacitySectors = capacity;
            _compressed = compressed;
            Descriptor = descriptor;
        }

        public long CapacitySectors { get; }

        public string? Descriptor { get; }

        public static SparseExtent Open(string path)
        {
            SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            try
            {
                long length = RandomAccess.GetLength(file);
                byte[] header = new byte[512];
                if (length < 512 || RandomAccess.Read(file, header, 0) != 512 || !header.AsSpan(0, 4).SequenceEqual("KDMV"u8))
                {
                    throw new ImageFormatException(ImageFormat.Vmdk, "header", "The VMDK sparse extent header (KDMV) was not found.");
                }

                if (BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(56)) == GdAtEnd)
                {
                    // streamOptimized: 本当のヘッダーは末尾の 2 セクタ前 (フッター)。
                    byte[] footer = new byte[512];
                    if (length < 1536 || RandomAccess.Read(file, footer, length - 1024) != 512 || !footer.AsSpan(0, 4).SequenceEqual("KDMV"u8))
                    {
                        throw new ImageFormatException(ImageFormat.Vmdk, "footer", "The VMDK stream-optimized footer was not found.");
                    }

                    header = footer;
                }

                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                long capacity = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12));
                long grain = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(20));
                long descriptorOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(28));
                long descriptorSize = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(36));
                int gtes = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(44)), int.MaxValue);
                long gdOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(56));
                bool compressed = (flags & (1 << 16)) != 0;
                if (capacity < 0 || capacity > 1L << 40 || grain is < 1 or > 1 << 20 || (grain & (grain - 1)) != 0 || gtes is < 1 or > 1 << 16
                    || gdOffset <= 0 || gdOffset * 512 >= length)
                {
                    throw new ImageFormatException(ImageFormat.Vmdk, "header", "The VMDK sparse extent header has invalid values.");
                }

                long gdes = (capacity + grain * gtes - 1) / (grain * gtes);
                if (gdes > 1 << 24 || gdOffset * 512 + gdes * 4 > length)
                {
                    throw new ImageFormatException(ImageFormat.Vmdk, "directory", "The VMDK grain directory is outside the file.");
                }

                byte[] raw = new byte[gdes * 4];
                RandomAccess.Read(file, raw, gdOffset * 512);
                uint[] directory = new uint[gdes];
                for (int i = 0; i < directory.Length; i++)
                {
                    directory[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4));
                }

                string? descriptor = null;
                if (descriptorOffset > 0 && descriptorSize is > 0 and <= 2048 && (descriptorOffset + descriptorSize) * 512 <= length)
                {
                    byte[] text = new byte[descriptorSize * 512];
                    RandomAccess.Read(file, text, descriptorOffset * 512);
                    int end = Array.IndexOf(text, (byte)0);
                    descriptor = Encoding.UTF8.GetString(text, 0, end < 0 ? text.Length : end);
                }

                return new SparseExtent(file, length, directory, gtes, grain, capacity, compressed, descriptor);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public void Read(long offset, Span<byte> part, long pos, IByteSource? parent, ref List<UnreadableRange>? bad)
        {
            long grainBytes = _grainSectors * 512;
            int done = 0;
            while (done < part.Length)
            {
                long at = offset + done;
                long grain = at / grainBytes;
                int inGrain = (int)(at % grainBytes);
                int n = (int)Math.Min(part.Length - done, grainBytes - inGrain);
                Span<byte> piece = part.Slice(done, n);
                uint entry = GrainEntry(grain, out bool tableOk);
                if (!tableOk)
                {
                    piece.Clear();
                    (bad ??= []).Add(new UnreadableRange(pos + done, n, UnreadableReason.IoError));
                }
                else if (entry == 0)
                {
                    if (parent is null)
                    {
                        piece.Clear();
                    }
                    else
                    {
                        ReadResult r = parent.Read(pos + done, piece);
                        if (!r.IsComplete)
                        {
                            (bad ??= []).AddRange(r.Unreadable);
                        }
                    }
                }
                else if (entry == 1)
                {
                    piece.Clear();
                }
                else if (_compressed)
                {
                    byte[]? data = CompressedGrain(grain, entry, (int)grainBytes);
                    if (data is null)
                    {
                        piece.Clear();
                        (bad ??= []).Add(new UnreadableRange(pos + done, n, UnreadableReason.IoError, 23));
                    }
                    else
                    {
                        data.AsSpan(inGrain, n).CopyTo(piece);
                    }
                }
                else
                {
                    long from = (long)entry * 512 + inGrain;
                    if (from + n > _fileLength || RandomAccess.Read(_file, piece, from) != n)
                    {
                        piece.Clear();
                        (bad ??= []).Add(new UnreadableRange(pos + done, n, UnreadableReason.IoError));
                    }
                }

                done += n;
            }
        }

        private uint GrainEntry(long grain, out bool ok)
        {
            ok = true;
            long gde = grain / _gtes;
            if (gde >= _directory.Length)
            {
                return 0;
            }

            uint tableSector = _directory[gde];
            if (tableSector == 0)
            {
                return 0;
            }

            uint[]? table;
            lock (_lock)
            {
                _tables.TryGetValue(gde, out table);
            }

            if (table is null)
            {
                long at = (long)tableSector * 512;
                if (at + _gtes * 4L > _fileLength)
                {
                    ok = false;
                    return 0;
                }

                byte[] raw = new byte[_gtes * 4];
                RandomAccess.Read(_file, raw, at);
                table = new uint[_gtes];
                for (int i = 0; i < table.Length; i++)
                {
                    table[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4));
                }

                lock (_lock)
                {
                    if (_tables.Count > 256)
                    {
                        _tables.Clear();
                    }

                    _tables[gde] = table;
                }
            }

            return table[grain % _gtes];
        }

        /// <summary>圧縮したグレイン (グレインマーカー: LBA 8 バイト、大きさ 4 バイト、zlib のデータ)。</summary>
        private byte[]? CompressedGrain(long grain, uint sector, int grainBytes)
        {
            lock (_lock)
            {
                for (LinkedListNode<(long Grain, byte[] Data)>? node = _grains.First; node is not null; node = node.Next)
                {
                    if (node.Value.Grain == grain)
                    {
                        return node.Value.Data;
                    }
                }
            }

            long at = (long)sector * 512;
            byte[] marker = new byte[12];
            if (at + 12 > _fileLength || RandomAccess.Read(_file, marker, at) != 12)
            {
                return null;
            }

            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(marker.AsSpan(8)), int.MaxValue);
            if (size <= 0 || size > grainBytes * 2 + 1024 || at + 12 + size > _fileLength)
            {
                return null;
            }

            byte[] stored = new byte[size];
            RandomAccess.Read(_file, stored, at + 12);
            byte[] data = new byte[grainBytes];
            try
            {
                using var z = new ZLibStream(new MemoryStream(stored), CompressionMode.Decompress);
                int total = 0;
                int n;
                while (total < grainBytes && (n = z.Read(data, total, grainBytes - total)) > 0)
                {
                    total += n;
                }
            }
            catch (InvalidDataException)
            {
                return null;
            }

            lock (_lock)
            {
                _grains.AddFirst((grain, data));
                while (_grains.Count > 16)
                {
                    _grains.RemoveLast();
                }
            }

            return data;
        }

        public void Dispose() => _file.Dispose();
    }
}
