using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HexEditor.Core.Forensics.Images.Ewf;

/// <summary>E01 の書き込みの指定 (FOR-20 の仕様 4)。</summary>
public sealed record EwfWriterOptions
{
    public required long MediaSize { get; init; }

    public int BytesPerSector { get; init; } = 512;

    public EwfCompression Compression { get; init; } = EwfCompression.Fast;

    /// <summary>セグメントの大きさの上限 (既定 2 GiB)。</summary>
    public long SegmentSize { get; init; } = 2L * 1024 * 1024 * 1024;

    public EwfCaseInfo Case { get; init; } = new();

    public DateTimeOffset AcquiredAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset SystemDate { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>取得ツールの版 (header の av)。</summary>
    public string Version { get; init; } = "HexEditor";

    public string OperatingSystem { get; init; } = Environment.OSVersion.VersionString;

    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    /// <summary>入力元が物理ディスク (volume の media flags)。</summary>
    public bool Physical { get; init; }

    public Guid SetIdentifier { get; init; } = Guid.NewGuid();
}

/// <summary>書き込む形にしたチャンク (圧縮した zlib のストリーム、または生のデータと Adler-32)。</summary>
public readonly record struct EwfEncodedChunk(byte[] Data, int Length, bool Compressed);

/// <summary>
/// E01 (EnCase 6 互換) の書き込み (FOR-20 の仕様 4)。チャンクを順に受け取り、セグメント (.E01〜) に分けて書く。セクションの順は
/// 1 つ目のセグメントが header2・header2・header・volume、2 つ目以降が data、続けて sectors・table・table2 の組をくり返し、
/// 最後のセグメントの末尾に error2・digest・hash・done (途中のセグメントは next) を置く。
/// チャンクの圧縮 (<see cref="Encode"/>) は呼び出し側で並列に行える。書き込みは 1 つのスレッドから順に呼ぶ。
/// </summary>
public sealed class EwfWriter : IDisposable
{
    private readonly string _first;
    private readonly EwfWriterOptions _options;
    private readonly long _chunkCount;
    private readonly int _chunkBytes;
    private readonly List<string> _files = [];
    private FileStream? _stream;
    private int _segment;
    private long _chunksWritten;
    private long _chunksInSegment;

    // 書き込み中の sectors セクション。
    private long _sectorsOffset = -1;
    private long _dataStart;
    private readonly List<uint> _entries = [];

    public EwfWriter(string firstSegmentPath, EwfWriterOptions options)
    {
        if (options.BytesPerSector <= 0 || options.MediaSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.SegmentSize < 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The segment size must be at least 1 MiB.");
        }

        _first = Path.GetFullPath(firstSegmentPath);
        _options = options;
        _chunkBytes = EwfFormat.SectorsPerChunk * options.BytesPerSector;
        _chunkCount = (options.MediaSize + _chunkBytes - 1) / _chunkBytes;
        if (_chunkCount > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The image is too large for the E01 format.");
        }
    }

    /// <summary>1 つのチャンクのバイト数 (64 セクタ)。</summary>
    public int ChunkBytes => _chunkBytes;

    public long ChunkCount => _chunkCount;

    /// <summary>書いたセグメントのファイル。</summary>
    public IReadOnlyList<string> Files => _files;

    /// <summary>チャンクを書き込む形にする (圧縮が効かなければ生のデータと Adler-32)。スレッドセーフ。</summary>
    public static EwfEncodedChunk Encode(ReadOnlySpan<byte> data, EwfCompression compression)
    {
        if (compression != EwfCompression.None)
        {
            using var output = new MemoryStream(data.Length / 2 + 64);
            using (var z = new ZLibStream(output, compression == EwfCompression.Best ? CompressionLevel.SmallestSize : CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(data);
            }

            if (output.Length < data.Length)
            {
                return new EwfEncodedChunk(output.GetBuffer(), (int)output.Length, true);
            }
        }

        byte[] raw = new byte[data.Length + 4];
        data.CopyTo(raw);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(data.Length), ImageChecksums.Adler32(data));
        return new EwfEncodedChunk(raw, raw.Length, false);
    }

    /// <summary>次のチャンクを書く (<see cref="Encode"/> した形)。</summary>
    /// <exception cref="IOException">書き込みの失敗・容量不足。呼び出し側は同じチャンクを再び書ける (書き込んだ位置は戻す)。</exception>
    public void WriteChunk(EwfEncodedChunk chunk)
    {
        if (_chunksWritten >= _chunkCount)
        {
            throw new InvalidOperationException("All chunks have been written.");
        }

        EnsureRoomFor(chunk.Length);
        FileStream stream = _stream!;
        long at = stream.Position;
        try
        {
            stream.Write(chunk.Data, 0, chunk.Length);
        }
        catch (IOException)
        {
            // 失敗したチャンクを書き直せるよう、位置を戻す。
            stream.Position = at;
            stream.SetLength(at);
            throw;
        }

        long relative = at - _dataStart;
        _entries.Add((uint)relative | (chunk.Compressed ? 0x80000000u : 0));
        _chunksWritten++;
        _chunksInSegment++;
        if (_entries.Count >= EwfFormat.MaxTableEntries)
        {
            CloseSectors();
        }
    }

    /// <summary>
    /// 終わりのセクションを書いて閉じる。<paramref name="md5"/> と <paramref name="sha1"/> は内容全体のハッシュ (hash / digest)、
    /// <paramref name="errors"/> は読めなかったセクタ (error2)。
    /// </summary>
    public void Finish(byte[] md5, byte[]? sha1, IReadOnlyList<SectorRange> errors)
    {
        if (_chunksWritten != _chunkCount)
        {
            throw new InvalidOperationException($"Only {_chunksWritten} of {_chunkCount} chunks were written.");
        }

        if (_stream is null)
        {
            OpenSegment();
        }

        CloseSectors();
        FileStream stream = _stream!;
        if (errors.Count > 0)
        {
            byte[] header = new byte[520];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)errors.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(516), ImageChecksums.Adler32(header.AsSpan(0, 516)));
            byte[] entries = new byte[errors.Count * 8 + 4];
            for (int i = 0; i < errors.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(i * 8), checked((uint)errors[i].StartSector));
                BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(i * 8 + 4), checked((uint)errors[i].SectorCount));
            }

            BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(errors.Count * 8), ImageChecksums.Adler32(entries.AsSpan(0, errors.Count * 8)));
            WriteSection("error2", [.. header, .. entries]);
        }

        if (sha1 is not null)
        {
            byte[] digest = new byte[80];
            md5.CopyTo(digest, 0);
            sha1.CopyTo(digest, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(76), ImageChecksums.Adler32(digest.AsSpan(0, 76)));
            WriteSection("digest", digest);
        }

        byte[] hash = new byte[36];
        md5.CopyTo(hash, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(hash.AsSpan(32), ImageChecksums.Adler32(hash.AsSpan(0, 32)));
        WriteSection("hash", hash);

        long done = stream.Position;
        stream.Write(EwfFormat.SectionDescriptor("done", done, 0));
        stream.Flush(flushToDisk: true);
        stream.Dispose();
        _stream = null;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }

    /// <summary>チャンクと、この sectors の組の残り (table 2 つ) と終わりのセクションが入る余地を確かめ、なければ次のセグメントへ移る。</summary>
    private void EnsureRoomFor(int chunkLength)
    {
        if (_stream is null)
        {
            OpenSegment();
        }

        long tables = 2 * (EwfFormat.SectionDescriptorSize + EwfFormat.TableHeaderSize + 4L * (_entries.Count + 1) + 4);
        const long Reserve = 64 * 1024; // next / done・hash・digest、error2 の余地
        if (_stream!.Position + chunkLength + tables + Reserve > _options.SegmentSize && _chunksInSegment > 0)
        {
            CloseSectors();
            long next = _stream.Position;
            _stream.Write(EwfFormat.SectionDescriptor("next", next, 0));
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
            _stream = null;
            OpenSegment();
        }

        if (_sectorsOffset < 0)
        {
            _sectorsOffset = _stream!.Position;
            _stream.Write(new byte[EwfFormat.SectionDescriptorSize]); // 後で書き直す
            _dataStart = _stream.Position;
        }
    }

    private void OpenSegment()
    {
        _segment++;
        _chunksInSegment = 0;
        if (_segment > EwfFormat.MaxSegmentNumber)
        {
            throw new IOException("Too many E01 segments; use a larger segment size.");
        }

        string path = EwfFormat.SegmentPath(_first, _segment);
        // 書き込み保護モード (FOR-01): 保護の対象 (開いているデータソース・証拠) には書かない。
        HexEditor.Core.Forensics.WriteBlock.WriteBlockGate.Current.EnsureFileWrite(path);
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
        _files.Add(path);
        _stream.Write(EwfFormat.FileHeader(_segment));
        byte[] volume = EwfFormat.Volume(_chunkCount, EwfFormat.SectorsPerChunk, _options.BytesPerSector, _options.MediaSize / _options.BytesPerSector,
            _options.Compression, _options.SetIdentifier, _options.Physical);
        if (_segment == 1)
        {
            byte[] header2 = Compress(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(EwfHeaderText.Header2(_options.Case,
                _options.AcquiredAt, _options.SystemDate, _options.Version, _options.OperatingSystem, _options.Model, _options.SerialNumber))).ToArray());
            byte[] header = Compress(Encoding.ASCII.GetBytes(EwfHeaderText.Header(_options.Case, _options.AcquiredAt, _options.SystemDate,
                _options.Version, _options.OperatingSystem, _options.Compression)));
            WriteSection("header2", header2);
            WriteSection("header2", header2);
            WriteSection("header", header);
            WriteSection("volume", volume);
        }
        else
        {
            WriteSection("data", volume);
        }
    }

    /// <summary>書き込み中の sectors セクションを閉じ、table と table2 を書く。</summary>
    private void CloseSectors()
    {
        if (_sectorsOffset < 0 || _stream is null)
        {
            return;
        }

        FileStream stream = _stream;
        long end = stream.Position;
        stream.Position = _sectorsOffset;
        stream.Write(EwfFormat.SectionDescriptor("sectors", end, end - _sectorsOffset));
        stream.Position = end;

        byte[] table = new byte[EwfFormat.TableHeaderSize + _entries.Count * 4 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)_entries.Count);
        BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(8), _dataStart);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(20), ImageChecksums.Adler32(table.AsSpan(0, 20)));
        for (int i = 0; i < _entries.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(EwfFormat.TableHeaderSize + i * 4), _entries[i]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(EwfFormat.TableHeaderSize + _entries.Count * 4),
            ImageChecksums.Adler32(table.AsSpan(EwfFormat.TableHeaderSize, _entries.Count * 4)));
        WriteSection("table", table);
        WriteSection("table2", table);
        _entries.Clear();
        _sectorsOffset = -1;
    }

    private void WriteSection(string type, byte[] body)
    {
        FileStream stream = _stream!;
        long at = stream.Position;
        long size = EwfFormat.SectionDescriptorSize + body.Length;
        stream.Write(EwfFormat.SectionDescriptor(type, at + size, size));
        stream.Write(body);
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            z.Write(data);
        }

        return output.ToArray();
    }
}
