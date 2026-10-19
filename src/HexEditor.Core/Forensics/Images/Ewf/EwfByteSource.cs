using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Forensics.Images.Ewf;

/// <summary>
/// E01 (EWF-E01、EnCase 1〜6 の形) を開いたデータソース (FOR-21)。開くときはセグメントのファイルヘッダーとセクションの記述子をたどるだけで、
/// table のエントリは要求されたチャンクの table だけを読む (数 TB のイメージでも 1 秒以内に開く。FOR-21 の「巨大ファイル」)。
/// チャンクは要求された範囲だけを展開し、圧縮していないチャンクは Adler-32 を、圧縮したチャンクは zlib の Adler-32 を確かめる。
/// 不一致のチャンクは読み込みエラーとして 0 で返す (仕様 6)。展開済みのチャンクは小さな LRU に持つ (表示のブロックキャッシュの下)。
/// 入力は信頼できないデータとして扱い、すべての長さ・個数・位置を上限と照らしてから使う。
/// </summary>
public sealed class EwfByteSource : ImageSourceBase
{
    /// <summary>header・error2 などの付随セクションの読み込みの上限。</summary>
    private const int MaxMetadataSection = 16 * 1024 * 1024;

    private const int CrcError = 23; // ERROR_CRC

    private readonly SafeFileHandle[] _segments;
    private readonly long[] _segmentLengths;
    private readonly List<TableRef> _tables;
    private readonly long[] _tableFirstChunk;
    private readonly int _chunkBytes;
    private readonly long _chunkCount;
    private readonly object _lock = new();
    private readonly LinkedList<(long Chunk, byte[]? Data)> _chunkLru = new();
    private readonly Dictionary<long, LinkedListNode<(long Chunk, byte[]? Data)>> _chunkIndex = [];
    private readonly LinkedList<(int Table, uint[] Entries)> _tableLru = new();
    private const int ChunkCacheSize = 64;
    private const int TableCacheSize = 16;

    /// <summary>1 つの table セクション: そのチャンクの範囲、エントリの位置、データの終わり (最後のチャンクの終わり)。</summary>
    private readonly record struct TableRef(int Segment, long EntriesOffset, int Count, long BaseOffset, long DataEnd, long FirstChunk);

    private EwfByteSource(string path, ImageInfo info, SafeFileHandle[] segments, long[] lengths, List<TableRef> tables, int chunkBytes, long chunkCount)
        : base(path, info)
    {
        _segments = segments;
        _segmentLengths = lengths;
        _tables = tables;
        _tableFirstChunk = [.. tables.Select(t => t.FirstChunk)];
        _chunkBytes = chunkBytes;
        _chunkCount = chunkCount;
    }

    /// <summary>1 つのチャンクのバイト数。</summary>
    public int ChunkBytes => _chunkBytes;

    /// <summary>テスト用: <paramref name="chunk"/> のデータの、セグメントファイル上の位置と格納された大きさ (TC-FOR-21-04 の前提)。</summary>
    public (string File, long Offset, int Size, bool Compressed)? ChunkLocation(long chunk)
    {
        if (!TryLocate(chunk, out TableRef table, out long offset, out int size, out bool compressed))
        {
            return null;
        }

        return (Info.Files[table.Segment], offset, size, compressed);
    }

    /// <summary>E01 を開く。<paramref name="allowMissing"/> が偽で足りないセグメントがあれば <see cref="MissingSegmentsException"/>。</summary>
    /// <exception cref="ImageFormatException">形式が壊れている (失敗した部分を示す)。</exception>
    public static EwfByteSource Open(string firstSegment, bool allowMissing = false)
    {
        string first = Path.GetFullPath(firstSegment);
        var handles = new List<SafeFileHandle>();
        var lengths = new List<long>();
        var files = new List<string>();
        try
        {
            var state = new ParseState();
            bool done = false;
            for (int n = 1; n <= EwfFormat.MaxSegmentNumber && !done; n++)
            {
                string path = n == 1 ? first : EwfFormat.SegmentPath(first, n);
                if (!File.Exists(path))
                {
                    if (n == 1)
                    {
                        throw new FileNotFoundException("The E01 file was not found.", path);
                    }

                    state.Missing.Add(path);
                    break;
                }

                SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                handles.Add(handle);
                long length = RandomAccess.GetLength(handle);
                lengths.Add(length);
                files.Add(path);
                done = ParseSegment(handle, length, n, handles.Count - 1, state);
            }

            if (state.ChunkBytes == 0)
            {
                throw new ImageFormatException(ImageFormat.E01, "volume", "The E01 volume section was not found.");
            }

            if (state.Missing.Count > 0 && !allowMissing)
            {
                throw new MissingSegmentsException(ImageFormat.E01, state.Missing);
            }

            long mediaSize = checked(state.SectorCount * state.BytesPerSector);
            long chunkCount = state.ChunkCount > 0 ? state.ChunkCount : (mediaSize + state.ChunkBytes - 1) / state.ChunkBytes;
            var info = new ImageInfo
            {
                Format = ImageFormat.E01,
                Files = files,
                MediaSize = mediaSize,
                BytesPerSector = state.BytesPerSector,
                Case = new EwfCaseInfo
                {
                    CaseNumber = state.Value("c"),
                    EvidenceNumber = state.Value("n"),
                    Examiner = state.Value("e"),
                    Description = state.Value("a"),
                    Notes = state.Value("t"),
                },
                AcquiredAt = EwfHeaderText.ParseDate(state.Header.GetValueOrDefault("m")),
                SystemDate = EwfHeaderText.ParseDate(state.Header.GetValueOrDefault("u")),
                AcquisitionTool = state.Value("av") is { Length: > 0 } av ? av : null,
                AcquisitionOs = state.Value("ov") is { Length: > 0 } ov ? ov : null,
                Model = state.Value("md") is { Length: > 0 } md ? md : null,
                SerialNumber = state.Value("sn") is { Length: > 0 } sn ? sn : null,
                ChunkCount = chunkCount,
                SectorsPerChunk = state.SectorsPerChunk,
                Compressed = state.AnyCompressed || state.CompressionLevel > 0,
                CompressionLevel = state.CompressionLevel switch { 1 => "fast", 2 => "best", 0 => "none", _ => null },
                StoredHashes = state.Hashes,
                AcquisitionErrors = state.Errors,
                MissingSegments = state.Missing,
            };
            return new EwfByteSource(first, info, [.. handles], [.. lengths], state.Tables, state.ChunkBytes, chunkCount);
        }
        catch
        {
            foreach (SafeFileHandle h in handles)
            {
                h.Dispose();
            }

            throw;
        }
    }

    private sealed class ParseState
    {
        public Dictionary<string, string> Header { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Header2 { get; } = new(StringComparer.Ordinal);

        public int BytesPerSector { get; set; }

        public int SectorsPerChunk { get; set; }

        public int ChunkBytes => SectorsPerChunk * BytesPerSector;

        public long SectorCount { get; set; }

        public long ChunkCount { get; set; }

        public int CompressionLevel { get; set; } = -1;

        public bool AnyCompressed { get; set; }

        public List<TableRef> Tables { get; } = [];

        public long NextChunk { get; set; }

        public Dictionary<string, byte[]> Hashes { get; } = new(StringComparer.Ordinal);

        public List<SectorRange> Errors { get; } = [];

        public List<string> Missing { get; } = [];

        /// <summary>header2 (UTF-16) を優先し、なければ header の値。</summary>
        public string Value(string key) => Header2.GetValueOrDefault(key) ?? Header.GetValueOrDefault(key) ?? string.Empty;
    }

    /// <summary>1 つのセグメントのセクションをたどる。done セクションに着いたら true。</summary>
    private static bool ParseSegment(SafeFileHandle handle, long length, int number, int index, ParseState state)
    {
        Span<byte> fileHeader = stackalloc byte[EwfFormat.FileHeaderSize];
        if (length < EwfFormat.FileHeaderSize || RandomAccess.Read(handle, fileHeader, 0) != EwfFormat.FileHeaderSize
            || !fileHeader[..8].SequenceEqual(EwfFormat.Signature))
        {
            throw new ImageFormatException(ImageFormat.E01, "header", $"Segment {number}: the EWF file header was not found.");
        }

        int segment = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader[9..]);
        if (segment != number)
        {
            throw new ImageFormatException(ImageFormat.E01, "header", $"Segment {number}: the file header says segment {segment}.");
        }

        long pos = EwfFormat.FileHeaderSize;
        byte[] descriptor = new byte[EwfFormat.SectionDescriptorSize];
        bool previousTableBad = false;
        long lastSectorsEnd = -1;
        long sections = 0;
        while (pos + EwfFormat.SectionDescriptorSize <= length)
        {
            if (++sections > length / EwfFormat.SectionDescriptorSize + 1)
            {
                throw new ImageFormatException(ImageFormat.E01, "section", $"Segment {number}: too many sections.");
            }

            if (RandomAccess.Read(handle, descriptor, pos) != descriptor.Length)
            {
                break;
            }

            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(72));
            if (stored != ImageChecksums.Adler32(descriptor.AsSpan(0, 72)))
            {
                throw new ImageFormatException(ImageFormat.E01, "section", $"Segment {number}: the section descriptor at 0x{pos:X} is damaged.");
            }

            string type = EwfFormat.SectionType(descriptor);
            long next = BinaryPrimitives.ReadInt64LittleEndian(descriptor.AsSpan(16));
            long size = BinaryPrimitives.ReadInt64LittleEndian(descriptor.AsSpan(24));
            long body = pos + EwfFormat.SectionDescriptorSize;
            long bodySize = size >= EwfFormat.SectionDescriptorSize ? Math.Min(size, length - pos) - EwfFormat.SectionDescriptorSize : 0;

            switch (type)
            {
                case "header":
                case "header2":
                {
                    string? text = ReadHeaderText(handle, body, bodySize, type == "header2");
                    if (text is not null)
                    {
                        Dictionary<string, string> target = type == "header2" ? state.Header2 : state.Header;
                        if (target.Count == 0)
                        {
                            foreach ((string k, string v) in EwfHeaderText.Parse(text))
                            {
                                target[k] = v;
                            }
                        }
                    }

                    break;
                }

                case "volume":
                case "disk":
                case "data":
                    ParseVolume(handle, body, bodySize, number, state);
                    break;

                case "sectors":
                    lastSectorsEnd = body + bodySize;
                    break;

                case "table":
                case "table2":
                {
                    if (type == "table2" && !previousTableBad)
                    {
                        break;
                    }

                    if (state.ChunkBytes == 0)
                    {
                        throw new ImageFormatException(ImageFormat.E01, "table", $"Segment {number}: a table comes before the volume section.");
                    }

                    // データの終わり: 直前の sectors セクションの終わり。sectors がない (EnCase 1〜5 で table の前にデータがある) 場合は table の位置。
                    long dataEnd = lastSectorsEnd >= 0 ? lastSectorsEnd : pos;
                    TableRef? table = ReadTableHeader(handle, body, bodySize, index, dataEnd, state.NextChunk);
                    if (table is null)
                    {
                        if (type == "table")
                        {
                            previousTableBad = true;
                            break;
                        }

                        throw new ImageFormatException(ImageFormat.E01, "table", $"Segment {number}: the table at 0x{pos:X} and its copy are damaged.");
                    }

                    previousTableBad = false;
                    state.Tables.Add(table.Value);
                    state.NextChunk += table.Value.Count;
                    break;
                }

                case "error2":
                    ParseErrors(handle, body, bodySize, state);
                    break;

                case "hash":
                {
                    byte[] hash = new byte[16];
                    if (bodySize >= 16 && RandomAccess.Read(handle, hash, body) == 16 && hash.Any(b => b != 0))
                    {
                        state.Hashes.TryAdd("MD5", hash);
                    }

                    break;
                }

                case "digest":
                {
                    byte[] digest = new byte[36];
                    if (bodySize >= 36 && RandomAccess.Read(handle, digest, body) == 36)
                    {
                        if (digest.AsSpan(0, 16).ContainsAnyExcept((byte)0))
                        {
                            state.Hashes["MD5"] = digest[..16];
                        }

                        if (digest.AsSpan(16, 20).ContainsAnyExcept((byte)0))
                        {
                            state.Hashes["SHA-1"] = digest[16..36];
                        }
                    }

                    break;
                }

                case "done":
                    return true;

                case "next":
                    return false;
            }

            if (next <= pos || next > length)
            {
                // 最後のセクションの次の位置が自分自身・範囲外: このセグメントの終わり。
                return false;
            }

            pos = next;
        }

        return false;
    }

    private static string? ReadHeaderText(SafeFileHandle handle, long offset, long size, bool utf16)
    {
        if (size is <= 0 or > MaxMetadataSection)
        {
            return null;
        }

        byte[] compressed = new byte[size];
        if (RandomAccess.Read(handle, compressed, offset) != size)
        {
            return null;
        }

        try
        {
            using var input = new MemoryStream(compressed);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] buffer = new byte[16 * 1024];
            int n;
            while ((n = z.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, n);
                if (output.Length > MaxMetadataSection)
                {
                    return null;
                }
            }

            byte[] data = output.ToArray();
            if (utf16)
            {
                int skip = data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE ? 2 : 0;
                return Encoding.Unicode.GetString(data, skip, (data.Length - skip) & ~1);
            }

            return Encoding.Latin1.GetString(data);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static void ParseVolume(SafeFileHandle handle, long offset, long size, int number, ParseState state)
    {
        byte[] v = new byte[Math.Min(size, EwfFormat.VolumeSize)];
        if (v.Length < 24 || RandomAccess.Read(handle, v, offset) != v.Length)
        {
            throw new ImageFormatException(ImageFormat.E01, "volume", $"Segment {number}: the volume section is too short.");
        }

        long chunks = BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(4));
        int sectorsPerChunk = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(8)), int.MaxValue);
        int bytesPerSector = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(12)), int.MaxValue);

        // EnCase 5 以降の volume (1052 バイト) はセクタ数が 64 ビット、それ以前 (94 バイト) は 32 ビット。
        long sectors = v.Length >= EwfFormat.VolumeSize ? BinaryPrimitives.ReadInt64LittleEndian(v.AsSpan(16)) : BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(16));
        if (bytesPerSector is < 1 or > 65536 || (bytesPerSector & (bytesPerSector - 1)) != 0 || sectorsPerChunk is < 1 or > 65536
            || (long)sectorsPerChunk * bytesPerSector > 64 * 1024 * 1024 || sectors < 0 || sectors > long.MaxValue / bytesPerSector)
        {
            throw new ImageFormatException(ImageFormat.E01, "volume", $"Segment {number}: the volume section has invalid values.");
        }

        if (state.BytesPerSector == 0)
        {
            state.BytesPerSector = bytesPerSector;
            state.SectorsPerChunk = sectorsPerChunk;
            state.SectorCount = sectors;
            state.ChunkCount = chunks;
            state.CompressionLevel = v.Length > 52 ? v[52] : -1;
        }
        else if (state.BytesPerSector != bytesPerSector || state.SectorsPerChunk != sectorsPerChunk)
        {
            throw new ImageFormatException(ImageFormat.E01, "volume", $"Segment {number} does not belong to the same image (different chunk size).");
        }
    }

    private static TableRef? ReadTableHeader(SafeFileHandle handle, long offset, long size, int segment, long dataEnd, long firstChunk)
    {
        Span<byte> header = stackalloc byte[EwfFormat.TableHeaderSize];
        if (size < EwfFormat.TableHeaderSize || RandomAccess.Read(handle, header, offset) != header.Length)
        {
            return null;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) != ImageChecksums.Adler32(header[..20]))
        {
            return null;
        }

        long count = BinaryPrimitives.ReadUInt32LittleEndian(header);
        long baseOffset = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
        if (count <= 0 || count > (size - EwfFormat.TableHeaderSize) / 4 || count > 1 << 24 || baseOffset < 0)
        {
            return null;
        }

        return new TableRef(segment, offset + EwfFormat.TableHeaderSize, (int)count, baseOffset, dataEnd, firstChunk);
    }

    private static void ParseErrors(SafeFileHandle handle, long offset, long size, ParseState state)
    {
        byte[] header = new byte[520];
        if (size < header.Length || RandomAccess.Read(handle, header, offset) != header.Length)
        {
            return;
        }

        long count = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (count <= 0 || count > (size - header.Length) / 8 || count > 1 << 20)
        {
            return;
        }

        byte[] entries = new byte[count * 8];
        if (RandomAccess.Read(handle, entries, offset + header.Length) != entries.Length)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            long start = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8));
            long sectors = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8 + 4));
            if (sectors > 0)
            {
                state.Errors.Add(new SectorRange(start, sectors));
            }
        }
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        List<UnreadableRange>? bad = null;
        int done = 0;
        while (done < count)
        {
            long pos = offset + done;
            long chunk = pos / _chunkBytes;
            int inChunk = (int)(pos % _chunkBytes);
            long chunkStart = chunk * _chunkBytes;
            int chunkLength = (int)Math.Min(_chunkBytes, Length - chunkStart);
            int n = Math.Min(count - done, chunkLength - inChunk);
            byte[]? data = GetChunk(chunk, chunkLength, out bool corrupt);
            if (data is null)
            {
                buffer.Slice(done, n).Clear();
                (bad ??= []).Add(new UnreadableRange(pos, n, UnreadableReason.IoError, corrupt ? CrcError : 2));
            }
            else
            {
                data.AsSpan(inChunk, n).CopyTo(buffer[done..]);
            }

            done += n;
        }

        return new ReadResult(count, Merge(bad));
    }

    private static List<UnreadableRange>? Merge(List<UnreadableRange>? ranges)
    {
        if (ranges is not { Count: > 1 })
        {
            return ranges;
        }

        var merged = new List<UnreadableRange> { ranges[0] };
        for (int i = 1; i < ranges.Count; i++)
        {
            UnreadableRange last = merged[^1];
            UnreadableRange r = ranges[i];
            if (r.Offset == last.End && r.Reason == last.Reason && r.ErrorCode == last.ErrorCode)
            {
                merged[^1] = last with { Length = last.Length + r.Length };
            }
            else
            {
                merged.Add(r);
            }
        }

        return merged;
    }

    /// <summary>チャンクを展開して返す。読めない・検査値が合わなければ null (<paramref name="corrupt"/> は検査値の不一致)。</summary>
    private byte[]? GetChunk(long chunk, int expected, out bool corrupt)
    {
        corrupt = false;
        lock (_lock)
        {
            if (_chunkIndex.TryGetValue(chunk, out LinkedListNode<(long Chunk, byte[]? Data)>? node))
            {
                _chunkLru.Remove(node);
                _chunkLru.AddFirst(node);
                corrupt = node.Value.Data is null && CorruptRanges.Contains((chunk * _chunkBytes, expected));
                return node.Value.Data;
            }
        }

        byte[]? data = Decode(chunk, expected, out corrupt);
        if (corrupt)
        {
            ReportCorruption(chunk * _chunkBytes, expected);
        }

        lock (_lock)
        {
            if (!_chunkIndex.ContainsKey(chunk))
            {
                _chunkIndex[chunk] = _chunkLru.AddFirst((chunk, data));
                while (_chunkLru.Count > ChunkCacheSize)
                {
                    _chunkIndex.Remove(_chunkLru.Last!.Value.Chunk);
                    _chunkLru.RemoveLast();
                }
            }
        }

        return data;
    }

    private byte[]? Decode(long chunk, int expected, out bool corrupt)
    {
        corrupt = false;
        if (!TryLocate(chunk, out TableRef table, out long offset, out int size, out bool compressed))
        {
            return null;
        }

        SafeFileHandle handle = _segments[table.Segment];
        if (size <= 0 || offset < 0 || offset + size > _segmentLengths[table.Segment])
        {
            corrupt = true;
            return null;
        }

        byte[] stored = new byte[size];
        if (RandomAccess.Read(handle, stored, offset) != size)
        {
            return null;
        }

        if (!compressed)
        {
            if (size < expected + 4)
            {
                corrupt = true;
                return null;
            }

            uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(expected));
            if (checksum != ImageChecksums.Adler32(stored.AsSpan(0, expected)))
            {
                corrupt = true;
                return null;
            }

            return stored.Length == expected ? stored : stored[..expected];
        }

        try
        {
            using var input = new MemoryStream(stored);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            byte[] data = new byte[expected];
            int total = 0;
            int n;
            while (total < expected && (n = z.Read(data, total, expected - total)) > 0)
            {
                total += n;
            }

            // 展開した長さが合わない・zlib の末尾まで読めない (Adler-32 を確かめられない) ものは壊れたチャンク。
            Span<byte> extra = stackalloc byte[1];
            if (total != expected || z.Read(extra) != 0)
            {
                corrupt = true;
                return null;
            }

            return data;
        }
        catch (InvalidDataException)
        {
            corrupt = true;
            return null;
        }
    }

    /// <summary>チャンクの格納位置。table がない (足りないセグメント) なら false。</summary>
    private bool TryLocate(long chunk, out TableRef table, out long offset, out int size, out bool compressed)
    {
        table = default;
        offset = 0;
        size = 0;
        compressed = false;
        if (chunk < 0 || chunk >= _chunkCount || _tables.Count == 0)
        {
            return false;
        }

        int index = Array.BinarySearch(_tableFirstChunk, chunk);
        if (index < 0)
        {
            index = ~index - 1;
        }

        if (index < 0)
        {
            return false;
        }

        table = _tables[index];
        long i = chunk - table.FirstChunk;
        if (i >= table.Count)
        {
            return false;
        }

        uint[]? entries = Entries(index);
        if (entries is null)
        {
            return false;
        }

        uint entry = entries[i];
        compressed = (entry & 0x80000000u) != 0;
        offset = table.BaseOffset + (entry & 0x7FFFFFFF);
        long end = table.DataEnd;
        if (i + 1 < table.Count)
        {
            long nextOffset = table.BaseOffset + (entries[i + 1] & 0x7FFFFFFF);
            if (nextOffset > offset)
            {
                end = nextOffset;
            }
        }

        long length = end - offset;
        size = length is > 0 and <= 128L * 1024 * 1024 ? (int)length : -1;
        return true;
    }

    private uint[]? Entries(int tableIndex)
    {
        lock (_lock)
        {
            for (LinkedListNode<(int Table, uint[] Entries)>? node = _tableLru.First; node is not null; node = node.Next)
            {
                if (node.Value.Table == tableIndex)
                {
                    _tableLru.Remove(node);
                    _tableLru.AddFirst(node);
                    return node.Value.Entries;
                }
            }
        }

        TableRef table = _tables[tableIndex];
        byte[] raw = new byte[table.Count * 4L];
        if (RandomAccess.Read(_segments[table.Segment], raw, table.EntriesOffset) != raw.Length)
        {
            return null;
        }

        uint[] entries = new uint[table.Count];
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4));
        }

        lock (_lock)
        {
            _tableLru.AddFirst((tableIndex, entries));
            while (_tableLru.Count > TableCacheSize)
            {
                _tableLru.RemoveLast();
            }
        }

        return entries;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (SafeFileHandle h in _segments)
            {
                h.Dispose();
            }
        }
    }
}
