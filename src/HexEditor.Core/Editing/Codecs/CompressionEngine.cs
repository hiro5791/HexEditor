using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Patches;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;
using ZstdSharp.Unsafe;

namespace HexEditor.Core.Editing.Codecs;

/// <summary>圧縮の設定 (EDIT-41 の仕様 2)。</summary>
public sealed record CompressionOptions(CompressionFormat Format, int Level)
{
    /// <summary>gzip のヘッダに入れるファイル名 (null なら入れない。既定は入れない)。</summary>
    public string? GzipFileName { get; init; }

    /// <summary>gzip のヘッダに入れる更新日時 (null なら 0)。</summary>
    public DateTimeOffset? GzipModified { get; init; }
}

/// <summary>
/// 展開の結果 (EDIT-41 の仕様 4)。<see cref="ConsumedInput"/> は範囲の先頭から圧縮データの終わりまでの長さ (残りのデータは変えない)。
/// <see cref="ChecksumMismatch"/> は gzip の CRC-32、zlib の Adler-32、zstd の XXH64、LZ4 の XXH32 が一致しなかった (「エラー」: 警告して結果を使うか選ばせる)。
/// </summary>
public sealed record DecompressionResult(CompressionFormat Format, long ConsumedInput, long OutputLength, EditContent? Content, bool ChecksumMismatch);

/// <summary>
/// 圧縮・展開 (EDIT-41)。入力は 1 MiB 単位で読み、結果は <see cref="ContentSink"/> (1 MiB を超えたら一時ファイル) に順に書く。大きさの上限は
/// 設けない (LZ4 ブロック形式の圧縮だけはメモリ上で行うため <see cref="CompressionFormatInfo.Lz4BlockCompressLimit"/> まで)。
/// キャンセル・失敗で作りかけの一時ファイルは消え、ドキュメントは変わらない。
/// </summary>
public static class CompressionEngine
{
    private const int OutChunk = 1024 * 1024;

    // ---- 圧縮 ----

    /// <summary>範囲を圧縮した内容を作る。</summary>
    public static EditContent Compress(DocumentSnapshot snapshot, long offset, long length, CompressionOptions options, ContentSink sink,
        LongRunningOperation? operation = null)
    {
        try
        {
            var reader = new RangeReader(snapshot, offset, length, operation);
            var output = new CodecOutput(sink);
            CompressTo(reader, options, output, operation?.CancellationToken ?? default);
            return sink.Complete();
        }
        catch
        {
            sink.Dispose();
            throw;
        }
    }

    /// <summary>範囲を圧縮した長さ (書き戻しの前の確かめ、テスト)。</summary>
    internal static void CompressTo(RangeReader reader, CompressionOptions options, CodecOutput output, CancellationToken token)
    {
        CompressionFormatInfo info = CompressionFormats.Info(options.Format);
        int level = Math.Clamp(options.Level, info.MinLevel, info.MaxLevel);
        switch (options.Format)
        {
            case CompressionFormat.Deflate:
                Deflate(reader, level, output, null);
                break;
            case CompressionFormat.Zlib:
                CompressZlib(reader, level, output);
                break;
            case CompressionFormat.Gzip:
                CompressGzip(reader, options, level, output);
                break;
            case CompressionFormat.Brotli:
                using (var stream = new BrotliStream(output.AsStream(), new BrotliCompressionOptions { Quality = level }, leaveOpen: true))
                {
                    Pump(reader, stream, null);
                }

                break;
            case CompressionFormat.Zstd:
                using (var stream = new ZstdSharp.CompressionStream(output.AsStream(), level, leaveOpen: true))
                {
                    // zstd コマンドの既定と同じく、内容のチェックサム (XXH64) を付ける。
                    stream.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
                    Pump(reader, stream, null);
                }

                break;
            case CompressionFormat.Lz4Frame:
                // lz4 コマンドの既定 (4 MiB のブロック、つないだブロック、内容のチェックサム) にそろえる。
                var settings = new LZ4EncoderSettings
                {
                    CompressionLevel = Lz4Level(level),
                    BlockSize = 4 * 1024 * 1024,
                    ChainBlocks = true,
                    ContentChecksum = true,
                };
                if (reader.Length == 0)
                {
                    // K4os は入力が空だと何も書かないため、空のフレーム (ヘッダ、終わりの印、内容のチェックサム) を書く。
                    WriteEmptyLz4Frame(output);
                    break;
                }

                using (Stream stream = LZ4Stream.Encode(output.AsStream(), settings, leaveOpen: true))
                {
                    Pump(reader, stream, null);
                }

                break;
            case CompressionFormat.Lz4Block:
                CompressLz4Block(reader, level, output);
                break;
            case CompressionFormat.Xz:
            case CompressionFormat.Lzma:
                CompressLzma(reader, options.Format, level, output, token);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static void Pump(RangeReader reader, Stream target, SpanObserver? observer)
    {
        byte[] buffer = new byte[RangeReader.ChunkSize];
        int n;
        while ((n = reader.Read(buffer)) > 0)
        {
            observer?.Invoke(buffer.AsSpan(0, n));
            target.Write(buffer, 0, n);
        }
    }

    private static LZ4Level Lz4Level(int level) => level switch
    {
        <= 2 => LZ4Level.L00_FAST,
        >= 12 => LZ4Level.L12_MAX,
        _ => (LZ4Level)level,
    };

    /// <summary>
    /// raw deflate。.NET の DeflateStream は入力が空だと何も書かないため、空のときは最後のブロック (固定ハフマンのブロックの終わりだけ) を書く。
    /// </summary>
    private static void Deflate(RangeReader reader, int level, CodecOutput output, SpanObserver? observer)
    {
        if (reader.Length == 0)
        {
            output.Write([0x03, 0x00]);
            return;
        }

        using var stream = new DeflateStream(output.AsStream(), new ZLibCompressionOptions { CompressionLevel = level }, leaveOpen: true);
        Pump(reader, stream, observer);
    }

    /// <summary>zlib (RFC 1950): ヘッダ、raw deflate、Adler-32 (ビッグエンディアン)。</summary>
    private static void CompressZlib(RangeReader reader, int level, CodecOutput output)
    {
        // FLEVEL は zlib と同じく 0〜1 は 0、2〜5 は 1、6 は 2、7〜9 は 3。
        int flevel = level <= 1 ? 0 : level <= 5 ? 1 : level == 6 ? 2 : 3;
        int header = (0x78 << 8) | (flevel << 6);
        header += (31 - header % 31) % 31;
        output.Write([(byte)(header >> 8), (byte)header]);
        var adler = new Adler32();
        Deflate(reader, level, output, adler.Append);
        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(trailer, adler.Value);
        output.Write(trailer);
    }

    /// <summary>gzip (RFC 1952)。ヘッダのファイル名・更新日時を入れるかを選べる (仕様 2)。</summary>
    private static void CompressGzip(RangeReader reader, CompressionOptions options, int level, CodecOutput output)
    {
        byte flags = options.GzipFileName is { Length: > 0 } ? (byte)0x08 : (byte)0;
        uint mtime = options.GzipModified is { } m ? (uint)Math.Clamp(m.ToUnixTimeSeconds(), 0, uint.MaxValue) : 0;
        Span<byte> header = stackalloc byte[10];
        header[0] = 0x1F;
        header[1] = 0x8B;
        header[2] = 8;
        header[3] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], mtime);
        header[8] = level >= 9 ? (byte)2 : level <= 1 ? (byte)4 : (byte)0;
        header[9] = 0; // FAT (MS-DOS・Windows)
        output.Write(header);
        if (flags != 0)
        {
            // ファイル名は ISO 8859-1 で、NUL で終わる (表せない文字は ?)。
            output.Write(Encoding.Latin1.GetBytes(options.GzipFileName!.Replace('\0', '?')));
            output.Write([0]);
        }

        uint crc = PatchCrc32.Initial;
        Deflate(reader, level, output, data => crc = PatchCrc32.Append(crc, data));

        Span<byte> trailer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, PatchCrc32.Finish(crc));
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], (uint)reader.Length);
        output.Write(trailer);
    }

    private static void WriteEmptyLz4Frame(CodecOutput output)
    {
        byte[] frame = new byte[4 + 3 + 4 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, Lz4Decoder.FrameMagic);
        frame[4] = 0x64; // 版 01、独立したブロック、内容のチェックサム
        frame[5] = 0x70; // 4 MiB のブロック
        frame[6] = (byte)(K4os.Hash.xxHash.XXH32.DigestOf(frame.AsSpan(4, 2)) >> 8);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(11), K4os.Hash.xxHash.XXH32.DigestOf(ReadOnlySpan<byte>.Empty));
        output.Write(frame);
    }

    private static void CompressLz4Block(RangeReader reader, int level, CodecOutput output)
    {
        if (reader.Length > CompressionFormatInfo.Lz4BlockCompressLimit)
        {
            throw new CompressionTooLargeException(CompressionFormatInfo.Lz4BlockCompressLimit);
        }

        byte[] source = new byte[reader.Length];
        int done = 0, n;
        while ((n = reader.Read(source.AsSpan(done))) > 0)
        {
            done += n;
        }

        byte[] target = new byte[LZ4Codec.MaximumOutputSize(source.Length)];
        int written = LZ4Codec.Encode(source, target, Lz4Level(level));
        if (written < 0)
        {
            throw new InvalidOperationException("LZ4 の圧縮に失敗しました。");
        }

        output.Write(target.AsSpan(0, written));
    }

    private static unsafe void CompressLzma(RangeReader reader, CompressionFormat format, int level, CodecOutput output, CancellationToken token)
    {
        LibLzma.Stream stream = default;
        LibLzma.Ret ret;
        if (format == CompressionFormat.Xz)
        {
            ret = LibLzma.EasyEncoder(&stream, (uint)level, LibLzma.CheckCrc64);
        }
        else
        {
            // lzma_options_lzma (配置に依存しないよう、十分な大きさの 0 の領域にプリセットを書かせる)。
            byte* options = stackalloc byte[512];
            new Span<byte>(options, 512).Clear();
            if (LibLzma.LzmaPreset(options, (uint)level) != 0)
            {
                throw new InvalidOperationException("LZMA の設定を作れません。");
            }

            ret = LibLzma.AloneEncoder(&stream, options);
        }

        if (ret != LibLzma.Ret.Ok)
        {
            throw new InvalidOperationException($"liblzma の初期化に失敗しました ({ret})。");
        }

        try
        {
            byte[] input = new byte[64 * 1024];
            byte[] outBuffer = new byte[OutChunk];
            fixed (byte* inPtr = input)
            fixed (byte* outPtr = outBuffer)
            {
                bool finishing = false;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (stream.AvailIn == 0 && !finishing)
                    {
                        int n = reader.Read(input);
                        stream.NextIn = inPtr;
                        stream.AvailIn = (nuint)n;
                        finishing = n == 0;
                    }

                    stream.NextOut = outPtr;
                    stream.AvailOut = (nuint)outBuffer.Length;
                    ret = LibLzma.Code(&stream, finishing ? LibLzma.Action.Finish : LibLzma.Action.Run);
                    output.Write(outBuffer.AsSpan(0, outBuffer.Length - (int)stream.AvailOut));
                    if (ret == LibLzma.Ret.StreamEnd)
                    {
                        break;
                    }

                    if (ret != LibLzma.Ret.Ok)
                    {
                        throw new InvalidOperationException($"liblzma の圧縮に失敗しました ({ret})。");
                    }
                }
            }
        }
        finally
        {
            LibLzma.End(&stream);
        }
    }

    // ---- 展開 ----

    /// <summary>
    /// 範囲を形式 <paramref name="format"/> で展開する。圧縮データの終わりで止まる (仕様 4)。<paramref name="output"/> の書き込み先が null なら
    /// 数えるだけ (「圧縮データの範囲を選択」)。壊れていれば <see cref="CompressedDataException"/> (それまでに展開できた分を
    /// <see cref="CompressedDataException.Partial"/> に入れる)。
    /// </summary>
    public static DecompressionResult Decompress(DocumentSnapshot snapshot, long offset, long length, CompressionFormat format, CodecOutput output,
        LongRunningOperation? operation = null)
    {
        var reader = new RangeReader(snapshot, offset, length, operation);
        var input = new CodecInput(reader);
        CancellationToken token = operation?.CancellationToken ?? default;
        bool mismatch;
        try
        {
            mismatch = DecompressCore(input, format, output, token);
        }
        catch (CompressedDataException e)
        {
            if (output.Sink is { } sink)
            {
                if (output.Length > 0)
                {
                    e.Partial = sink.Complete();
                    e.PartialLength = output.Length;
                }
                else
                {
                    sink.Dispose();
                }
            }

            throw;
        }
        catch
        {
            output.Sink?.Dispose();
            throw;
        }

        EditContent? content = output.Sink?.Complete();
        return new DecompressionResult(format, input.Consumed, output.Length, content, mismatch);
    }

    /// <summary>
    /// 自動判別で展開する (仕様 3)。手がかりの一致する形式を順に試し、最初に展開できた形式の結果を返す。判別できない (手がかりがない、
    /// またはすべて失敗した) 場合は null (形式の選択を求める)。<paramref name="createOutput"/> は試すたびに新しい書き込み先を作る。
    /// </summary>
    public static DecompressionResult? DecompressAuto(DocumentSnapshot snapshot, long offset, long length, Func<CodecOutput> createOutput,
        LongRunningOperation? operation = null)
    {
        byte[] head = RangeReader.Head(snapshot, offset, length, 16);
        foreach (CompressionFormat format in CompressionFormats.Detect(head))
        {
            try
            {
                return Decompress(snapshot, offset, length, format, createOutput(), operation);
            }
            catch (CompressedDataException e)
            {
                e.Partial?.Dispose();
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException && e is not Transforms.DataReadException)
            {
            }
        }

        return null;
    }

    private static bool DecompressCore(CodecInput input, CompressionFormat format, CodecOutput output, CancellationToken token)
    {
        switch (format)
        {
            case CompressionFormat.Deflate:
                new Inflater(input, output, token).Run();
                return false;
            case CompressionFormat.Zlib:
                return InflateZlib(input, output, token);
            case CompressionFormat.Gzip:
                return InflateGzip(input, output, token);
            case CompressionFormat.Brotli:
                DecodeBrotli(input, output, token);
                return false;
            case CompressionFormat.Zstd:
                return DecodeZstd(input, output, token);
            case CompressionFormat.Lz4Frame:
                Lz4Decoder.DecodeFrame(input, output, token, out bool mismatch);
                return mismatch;
            case CompressionFormat.Lz4Block:
                Lz4Decoder.DecodeBlockFormat(input, output, token);
                return false;
            case CompressionFormat.Lzma:
            case CompressionFormat.Xz:
                DecodeLzma(input, format, output, token);
                return false;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static bool InflateZlib(CodecInput input, CodecOutput output, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[2];
        input.ReadExact(header);
        if (!CompressionFormats.IsZlibHeader(header))
        {
            throw new CompressedDataException(CompressedDataError.Corrupt, 0, "zlib のヘッダが正しくありません");
        }

        if ((header[1] & 0x20) != 0)
        {
            throw new CompressedDataException(CompressedDataError.Unsupported, 1, "プリセット辞書を使う zlib データには対応していません");
        }

        var adler = new Adler32();
        var inflater = new Inflater(input, output, token) { Observer = adler.Append };
        inflater.Run();
        Span<byte> trailer = stackalloc byte[4];
        input.ReadExact(trailer);
        return BinaryPrimitives.ReadUInt32BigEndian(trailer) != adler.Value;
    }

    private static bool InflateGzip(CodecInput input, CodecOutput output, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[10];
        input.ReadExact(header);
        if (header[0] != 0x1F || header[1] != 0x8B || header[2] != 8 || (header[3] & 0xE0) != 0)
        {
            throw new CompressedDataException(CompressedDataError.Corrupt, 0, "gzip のヘッダが正しくありません");
        }

        byte flags = header[3];
        if ((flags & 0x04) != 0)
        {
            Span<byte> xlen = stackalloc byte[2];
            input.ReadExact(xlen);
            int skip = BinaryPrimitives.ReadUInt16LittleEndian(xlen);
            byte[] extra = new byte[skip];
            input.ReadExact(extra);
        }

        foreach (byte flag in new byte[] { 0x08, 0x10 })
        {
            if ((flags & flag) != 0)
            {
                int c;
                do
                {
                    c = input.ReadByte();
                    if (c < 0)
                    {
                        throw new CompressedDataException(CompressedDataError.Truncated, input.Consumed);
                    }
                }
                while (c != 0);
            }
        }

        if ((flags & 0x02) != 0)
        {
            Span<byte> hcrc = stackalloc byte[2];
            input.ReadExact(hcrc);
        }

        uint crc = PatchCrc32.Initial;
        var inflater = new Inflater(input, output, token) { Observer = data => crc = PatchCrc32.Append(crc, data) };
        inflater.Run();
        Span<byte> trailer = stackalloc byte[8];
        input.ReadExact(trailer);
        return BinaryPrimitives.ReadUInt32LittleEndian(trailer) != PatchCrc32.Finish(crc)
            || BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) != (uint)inflater.Total;
    }

    private static void DecodeBrotli(CodecInput input, CodecOutput output, CancellationToken token)
    {
        using var decoder = new BrotliDecoder();
        byte[] buffer = new byte[OutChunk];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> available = input.Available;
            System.Buffers.OperationStatus status = decoder.Decompress(available, buffer, out int consumed, out int written);
            input.Advance(consumed);
            output.Write(buffer.AsSpan(0, written));
            switch (status)
            {
                case System.Buffers.OperationStatus.Done:
                    return;
                case System.Buffers.OperationStatus.InvalidData:
                    throw new CompressedDataException(CompressedDataError.Corrupt, input.Consumed, "Brotli のデータが正しくありません");
                case System.Buffers.OperationStatus.NeedMoreData when available.Length == consumed && input.Available.IsEmpty:
                    throw new CompressedDataException(CompressedDataError.Truncated, input.Consumed);
            }
        }
    }

    private static unsafe bool DecodeZstd(CodecInput input, CodecOutput output, CancellationToken token)
    {
        ZSTD_DCtx_s* context = Methods.ZSTD_createDCtx();

        // チェックサムの不一致でも展開した内容を使えるように (EDIT-41 の「エラー」)、ライブラリでは確かめずに XXH64 を自分で計算して比べる。
        Methods.ZSTD_DCtx_setParameter(context, ZSTD_dParameter.ZSTD_d_experimentalParam3, 1);
        var hash = new K4os.Hash.xxHash.XXH64();
        bool? hasChecksum = null;
        try
        {
            byte[] buffer = new byte[OutChunk];
            fixed (byte* outPtr = buffer)
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    ReadOnlySpan<byte> available = input.Available;
                    if (hasChecksum is null && available.Length >= 5)
                    {
                        // フレームヘッダの記述子 (マジックナンバーの次) の Content_Checksum_flag。
                        hasChecksum = (available[4] & 0x04) != 0;
                    }

                    fixed (byte* inPtr = available)
                    {
                        var inBuffer = new ZSTD_inBuffer_s { src = inPtr, size = (nuint)available.Length, pos = 0 };
                        var outBuffer = new ZSTD_outBuffer_s { dst = outPtr, size = (nuint)buffer.Length, pos = 0 };
                        nuint ret = Methods.ZSTD_decompressStream(context, &outBuffer, &inBuffer);
                        input.Advance((int)inBuffer.pos);
                        hash.Update(buffer.AsSpan(0, (int)outBuffer.pos));
                        output.Write(buffer.AsSpan(0, (int)outBuffer.pos));
                        if (Methods.ZSTD_isError(ret))
                        {
                            ZSTD_ErrorCode code = Methods.ZSTD_getErrorCode(ret);
                            throw new CompressedDataException(
                                code == ZSTD_ErrorCode.ZSTD_error_frameParameter_windowTooLarge ? CompressedDataError.MemoryLimit : CompressedDataError.Corrupt,
                                input.Consumed, new string(Methods.ZSTD_getErrorName(ret)));
                        }

                        if (ret == 0)
                        {
                            // フレームの終わり (出力をすべて出し終えた)。チェックサムはフレームの最後の 4 バイト (XXH64 の下位 32 ビット)。
                            if (hasChecksum != true)
                            {
                                return false;
                            }

                            Span<byte> stored = stackalloc byte[4];
                            input.Reader.ReadAt(input.Consumed - 4, stored);
                            return BinaryPrimitives.ReadUInt32LittleEndian(stored) != (uint)hash.Digest();
                        }

                        if (available.IsEmpty && outBuffer.pos == 0)
                        {
                            throw new CompressedDataException(CompressedDataError.Truncated, input.Consumed);
                        }
                    }
                }
            }
        }
        finally
        {
            Methods.ZSTD_freeDCtx(context);
        }
    }

    private static unsafe void DecodeLzma(CodecInput input, CompressionFormat format, CodecOutput output, CancellationToken token)
    {
        LibLzma.Stream stream = default;
        LibLzma.Ret ret = format == CompressionFormat.Xz
            ? LibLzma.StreamDecoder(&stream, LibLzma.DecoderMemoryLimit, 0)
            : LibLzma.AloneDecoder(&stream, LibLzma.DecoderMemoryLimit);
        if (ret != LibLzma.Ret.Ok)
        {
            throw new InvalidOperationException($"liblzma の初期化に失敗しました ({ret})。");
        }

        try
        {
            byte[] buffer = new byte[OutChunk];
            fixed (byte* outPtr = buffer)
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    ReadOnlySpan<byte> available = input.Available;
                    bool atEnd = available.IsEmpty;
                    fixed (byte* inPtr = available)
                    {
                        stream.NextIn = inPtr;
                        stream.AvailIn = (nuint)available.Length;
                        stream.NextOut = outPtr;
                        stream.AvailOut = (nuint)buffer.Length;
                        ret = LibLzma.Code(&stream, atEnd ? LibLzma.Action.Finish : LibLzma.Action.Run);
                        int consumed = available.Length - (int)stream.AvailIn;
                        input.Advance(consumed);
                        int written = buffer.Length - (int)stream.AvailOut;
                        output.Write(buffer.AsSpan(0, written));
                        switch (ret)
                        {
                            case LibLzma.Ret.StreamEnd:
                                return;
                            case LibLzma.Ret.Ok:
                                if (atEnd && written == 0)
                                {
                                    throw new CompressedDataException(CompressedDataError.Truncated, input.Consumed);
                                }

                                break;
                            case LibLzma.Ret.BufError when atEnd:
                                throw new CompressedDataException(CompressedDataError.Truncated, input.Consumed);
                            case LibLzma.Ret.MemLimitError:
                            case LibLzma.Ret.MemError:
                                throw new CompressedDataException(CompressedDataError.MemoryLimit, input.Consumed);
                            case LibLzma.Ret.OptionsError:
                                throw new CompressedDataException(CompressedDataError.Unsupported, input.Consumed, ret.ToString());
                            case LibLzma.Ret.BufError:
                                break;
                            default:
                                throw new CompressedDataException(CompressedDataError.Corrupt, input.Consumed, ret.ToString());
                        }
                    }
                }
            }
        }
        finally
        {
            LibLzma.End(&stream);
        }
    }
}

/// <summary>この形式で圧縮できる大きさを超えている (LZ4 ブロック形式)。</summary>
public sealed class CompressionTooLargeException(long limit) : InvalidOperationException($"圧縮できるのは {limit} バイトまでです。")
{
    public long Limit { get; } = limit;
}

/// <summary>Adler-32 (zlib のチェックサム)。</summary>
internal sealed class Adler32
{
    private uint _a = 1;
    private uint _b;

    public uint Value => (_b << 16) | _a;

    public void Append(ReadOnlySpan<byte> data)
    {
        // 5,552 バイトごとに剰余を取る (32 ビットであふれない最大の長さ)。
        while (!data.IsEmpty)
        {
            int n = Math.Min(5552, data.Length);
            foreach (byte x in data[..n])
            {
                _a += x;
                _b += _a;
            }

            _a %= 65521;
            _b %= 65521;
            data = data[n..];
        }
    }
}
