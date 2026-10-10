using System.Buffers;
using System.Globalization;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using HexEditor.Core.View;

namespace HexEditor.Core.Editing.Transforms;

/// <summary>文字コード変換 (EDIT-38)・大文字小文字の変換 (EDIT-39) に共通する判定。</summary>
public static class TextTransforms
{
    /// <summary>これを超える長さの変換は長時間処理にする (EDIT-38 の「巨大ファイル・長時間処理」)。</summary>
    public const long LongRunningThreshold = 4L * 1024 * 1024;

    /// <summary>対象の合計の長さが長時間処理になるか (4 MiB を超えるか)。</summary>
    public static bool IsLongRunning(long totalLength) => totalLength > LongRunningThreshold;

    /// <summary>対象の範囲の合計の長さ。</summary>
    public static long TotalLength(IEnumerable<TargetRange> ranges) => ranges.Sum(r => r.Length);

    /// <summary>
    /// 変換結果に長さの変わる範囲があるか (EDIT-38 の仕様 7、EDIT-39 の仕様 3)。真で、長さを変えられないドキュメントなら実行しない
    /// (<see cref="TransformApplier.Apply"/> も <see cref="FixedLengthException"/> を投げる)。
    /// </summary>
    public static bool ChangesLength(IEnumerable<RangeReplacement> parts) => parts.Any(p => p.Content.Length != p.Range.Length);

    /// <summary>
    /// 文字コードの名前 (<see cref="EncodingCatalog"/> の <c>ascii</c>、<c>ansi</c>、<c>oem</c>、<c>utf-8</c>、<c>cp932</c> など) から
    /// コードページ番号を得る。一覧にない・この環境で使えない名前は <see cref="ArgumentException"/>。
    /// </summary>
    public static int ResolveCodePage(string encodingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodingId);

        // CodePagesEncodingProvider を登録してから調べる。
        _ = TextEncoding.Ascii;
        if (encodingId.Equals("ansi", StringComparison.OrdinalIgnoreCase) || encodingId.Equals("oem", StringComparison.OrdinalIgnoreCase))
        {
            return TextEncoding.FromId(encodingId).CodePage;
        }

        return EncodingCatalog.Find(encodingId)?.CodePage
            ?? throw new ArgumentException($"文字コード '{encodingId}' は使えません。", nameof(encodingId));
    }

    /// <summary>コードページの文字コードを、指定した代替処理で作る (best fit は使わない)。</summary>
    internal static Encoding Create(int codePage, EncoderFallback encoderFallback, DecoderFallback decoderFallback)
    {
        _ = TextEncoding.Ascii;
        return Encoding.GetEncoding(codePage, encoderFallback, decoderFallback);
    }
}

/// <summary>変換結果の書き出し先 (<see cref="ContentSink"/>、またはプレビュー・長さの見積もり用のメモリ)。</summary>
internal abstract class ByteOutput
{
    public long Length { get; protected set; }

    public abstract void Write(ReadOnlySpan<byte> data);
}

internal sealed class SinkOutput(ContentSink sink) : ByteOutput
{
    public override void Write(ReadOnlySpan<byte> data)
    {
        sink.Write(data);
        Length += data.Length;
    }
}

/// <summary>長さを数え、先頭 <paramref name="captureLimit"/> バイトだけを残す。</summary>
internal sealed class CaptureOutput(int captureLimit) : ByteOutput
{
    private readonly List<byte> _captured = [];

    public byte[] Captured => [.. _captured];

    public override void Write(ReadOnlySpan<byte> data)
    {
        int keep = (int)Math.Clamp(captureLimit - _captured.Count, 0, data.Length);
        for (int i = 0; i < keep; i++)
        {
            _captured.Add(data[i]);
        }

        Length += data.Length;
    }
}

/// <summary>解読した文字列と、そのまま残すバイト列の受け取り手。</summary>
internal interface ITextReceiver
{
    /// <param name="sourceOffset">文字の先頭のおおよその位置 (細かく解読するときは正確)。</param>
    void Text(ReadOnlySpan<char> chars, long sourceOffset);

    /// <summary>解読できず、変えずに残すバイト列 (「そのまま残す」)。</summary>
    void Raw(ReadOnlySpan<byte> bytes, long sourceOffset);
}

/// <summary>変換先で表せない文字 (エラーにする扱いのとき)。位置は後で探す。</summary>
internal sealed class UnmappableTextException(string text) : Exception
{
    public string Text { get; } = text;
}

/// <summary>解読できないバイト列を、文字を出さずに記録する代替処理 (位置を求めて、エラー・そのまま残すに使う)。</summary>
internal sealed class RecordingDecoderFallback : DecoderFallback
{
    public List<(byte[] Bytes, int Index)> Events { get; } = [];

    public override int MaxCharCount => 0;

    public override DecoderFallbackBuffer CreateFallbackBuffer() => new Buffer(this);

    private sealed class Buffer(RecordingDecoderFallback owner) : DecoderFallbackBuffer
    {
        public override int Remaining => 0;

        public override bool Fallback(byte[] bytesUnknown, int index)
        {
            owner.Events.Add(((byte[])bytesUnknown.Clone(), index));
            return false;
        }

        public override char GetNextChar() => '\0';

        public override bool MovePrevious() => false;
    }
}

/// <summary>
/// 変換元のバイト列をチャンクごとに解読する (<see cref="Decoder"/> の状態を引き継ぐので、文字の途中でチャンクが切れても正しく解読する)。
/// 解読できないバイト列の扱いが「エラー」「そのまま残す」のときは、その位置を正確に知るため、先に下見用の解読器でチャンクを解読し、
/// 解読できない箇所の近くだけを本番の解読器で 1 バイトずつ解読する (2 つの解読器には同じバイト列を渡すので状態は同じになる)。
/// </summary>
internal sealed class SourceDecoder
{
    // 下見の解読器が返す位置のずれ (UTF-16 で前のチャンクから続く不正な単位は 0 になる) を見込んで、手前から 1 バイトずつ解読する。
    private const int Margin = 16;

    private readonly ITextReceiver _receiver;
    private readonly InvalidSourceHandling _handling;
    private readonly Decoder _main;
    private readonly Decoder? _probe;
    private readonly RecordingDecoderFallback? _mainEvents;
    private readonly RecordingDecoderFallback? _probeEvents;

    // UTF-16 は 2、UTF-32 は 4 (文字の数から位置を正確に数えられる)。それ以外は 0。
    private readonly int _unitWidth;
    private char[] _chars;
    private char[] _probeChars;
    private long _accounted;
    private long _pendingStart = -1;
    private bool _pendingExact;
    private int _eventsThisPush;

    /// <param name="maxChunk">一度に渡すバイト数の上限 (バッファの大きさ)。</param>
    public SourceDecoder(int codePage, InvalidSourceHandling handling, ITextReceiver receiver, long startOffset, int maxChunk)
    {
        _receiver = receiver;
        _handling = handling;
        _accounted = startOffset;
        _unitWidth = codePage switch
        {
            1200 or 1201 => 2,
            12000 or 12001 => 4,
            _ => 0,
        };
        if (handling == InvalidSourceHandling.ReplaceWithFffd)
        {
            _main = TextTransforms.Create(codePage, EncoderFallback.ExceptionFallback, new DecoderReplacementFallback("�")).GetDecoder();
        }
        else
        {
            _mainEvents = new RecordingDecoderFallback();
            _probeEvents = new RecordingDecoderFallback();
            _main = TextTransforms.Create(codePage, EncoderFallback.ExceptionFallback, _mainEvents).GetDecoder();
            _probe = TextTransforms.Create(codePage, EncoderFallback.ExceptionFallback, _probeEvents).GetDecoder();
        }

        int max = TextTransforms.Create(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback)
            .GetMaxCharCount(maxChunk) + 16;
        _chars = new char[max];
        _probeChars = _probe is null ? [] : new char[max];
    }

    /// <summary>真なら、すべてのバイトを 1 バイトずつ解読して文字の先頭の位置を正確に渡す (表せない文字の位置を探すとき)。</summary>
    public bool FineGrained { get; set; }

    /// <summary>
    /// 続きのバイト列を解読する。<paramref name="final"/> なら最後に解読器を空にする (末尾の不完全な文字も解読できないバイト列にする)。
    /// </summary>
    /// <exception cref="CharsetConversionException">解読できないバイト列があり、扱いが「エラー」。</exception>
    public void Push(ReadOnlySpan<byte> chunk, long offset, bool final)
    {
        _eventsThisPush = 0;
        if (FineGrained)
        {
            for (int i = 0; i < chunk.Length; i++)
            {
                Call(chunk.Slice(i, 1), offset + i, flush: false, single: true);
            }
        }
        else if (_probe is null)
        {
            _pendingStart = -1;
            _pendingExact = false;
            Call(chunk, offset, flush: false, single: false);
        }
        else
        {
            PushWithProbe(chunk, offset, final);
        }

        if (final)
        {
            Call([], offset + chunk.Length, flush: true, single: FineGrained);
        }
    }

    private void PushWithProbe(ReadOnlySpan<byte> chunk, long offset, bool final)
    {
        _probeEvents!.Events.Clear();
        ReadOnlySpan<byte> rest = chunk;
        while (true)
        {
            _probe!.Convert(rest, _probeChars, final, out int used, out _, out bool completed);
            rest = rest[used..];
            if (rest.IsEmpty && completed)
            {
                break;
            }
        }

        // 下見の位置はこのチャンクの先頭からの位置 (負なら前のチャンクから続くバイト列)。
        int[] expected = [.. _probeEvents.Events.Select(e => e.Index)];
        int pos = 0;
        _pendingStart = -1;
        _pendingExact = false;
        for (int j = 0; j < expected.Length; j++)
        {
            if (_eventsThisPush > j)
            {
                continue;
            }

            int start = Math.Clamp(expected[j] - Margin, pos, chunk.Length);
            if (start > pos)
            {
                Call(chunk[pos..start], offset + pos, flush: false, single: false);
                _pendingStart = -1;
                _pendingExact = false;
                pos = start;
            }

            while (_eventsThisPush <= j && pos < chunk.Length)
            {
                Call(chunk.Slice(pos, 1), offset + pos, flush: false, single: true);
                pos++;
            }
        }

        if (pos < chunk.Length)
        {
            Call(chunk[pos..], offset + pos, flush: false, single: false);
            _pendingStart = -1;
            _pendingExact = false;
        }
    }

    private void Call(ReadOnlySpan<byte> bytes, long offset, bool flush, bool single)
    {
        if (single && _pendingStart < 0)
        {
            _pendingStart = offset;
        }

        while (true)
        {
            _mainEvents?.Events.Clear();
            _main.Convert(bytes, _chars, flush, out int used, out int produced, out bool completed);
            bytes = bytes[used..];

            // 1 バイトずつ解読するとき、解読できないバイト列はそのバイトで出る文字より前にある。
            if (_mainEvents is { Events.Count: > 0 })
            {
                foreach ((byte[] unknown, int index) in _mainEvents.Events)
                {
                    long at = EventOffset(unknown.Length, index, offset, single);
                    _eventsThisPush++;
                    if (_handling == InvalidSourceHandling.Error)
                    {
                        throw new CharsetConversionException(CharsetConversionErrorKind.UndecodableSource, at, unknown, null);
                    }

                    _receiver.Raw(unknown, at);
                }
            }

            if (produced > 0)
            {
                ReadOnlySpan<char> chars = _chars.AsSpan(0, produced);
                long start = _unitWidth > 0 ? _accounted : single && _pendingStart >= 0 ? _pendingStart : offset;
                if (_unitWidth == 2)
                {
                    _accounted += 2L * produced;
                }
                else if (_unitWidth == 4)
                {
                    _accounted += 4L * (produced - CountHighSurrogates(chars));
                }

                if (single)
                {
                    _pendingStart = -1;
                    _pendingExact = true;
                }

                _receiver.Text(chars, start);
            }

            if (bytes.IsEmpty && completed)
            {
                return;
            }
        }
    }

    private long EventOffset(int length, int index, long callOffset, bool single)
    {
        long at;
        if (_unitWidth > 0)
        {
            at = _accounted;
        }
        else if (single && _pendingExact && _pendingStart >= 0)
        {
            at = _pendingStart;
        }
        else
        {
            at = callOffset + index;
        }

        _accounted += length;
        if (single && _pendingStart >= 0)
        {
            _pendingStart = at + length;
            _pendingExact = true;
        }

        return at;
    }

    private static int CountHighSurrogates(ReadOnlySpan<char> chars)
    {
        int n = 0;
        foreach (char c in chars)
        {
            if (char.IsHighSurrogate(c))
            {
                n++;
            }
        }

        return n;
    }
}

/// <summary>
/// 解読した文字列を、BOM の除去 → 改行の変換 → 大文字・小文字の変換 → Unicode の正規化 → 符号化の順に処理して書き出す。
/// チャンクの境界をまたぐ CR LF と、正規化で結合しうる文字の並びは次のチャンクまで持ち越す。
/// </summary>
internal sealed class TextPipeline : ITextReceiver
{
    // 符号化に一度に渡す文字数の上限。
    private const int MaxEncodeBlock = 64 * 1024;

    // 正規化の持ち越しがこれを超えたら、安全な境界がなくても正規化して書き出す (メモリを抑えるため。通常のテキストでは起きない)。
    private const int MaxNormalizationCarry = 64 * 1024;

    private readonly ByteOutput _output;
    private readonly Encoder _encoder;
    private readonly bool _stripBom;
    private readonly NewlineConversion _newline;
    private readonly NormalizationForm? _form;
    private readonly CaseOperation? _case;
    private readonly Encoding? _caseCheck;
    private readonly Dictionary<int, bool> _encodable = [];
    private readonly byte[] _bytes;
    private readonly int _encodeBlock;
    private char[] _newlineBuffer = [];
    private char[] _caseBuffer = [];
    private string _carry = string.Empty;
    private char? _caseHigh;
    private bool _started;
    private bool _pendingCr;

    public TextPipeline(Encoding target, ByteOutput output, bool stripBom, NewlineConversion newline, NormalizationForm? form,
        CaseOperation? caseOperation, int sizeHint)
    {
        _output = output;
        _encoder = target.GetEncoder();
        _stripBom = stripBom;
        _newline = newline;
        _form = form;
        _case = caseOperation;
        _caseCheck = caseOperation is not null && target.CodePage is not (65001 or 1200 or 1201 or 12000 or 12001)
            ? TextTransforms.Create(target.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            : null;
        _encodeBlock = (int)Math.Clamp(sizeHint, 256, MaxEncodeBlock);
        _bytes = new byte[target.GetMaxByteCount(_encodeBlock) + 64];
    }

    public void Text(ReadOnlySpan<char> chars, long sourceOffset)
    {
        if (chars.IsEmpty)
        {
            return;
        }

        if (!_started)
        {
            _started = true;
            if (_stripBom && chars[0] == '﻿')
            {
                chars = chars[1..];
            }
        }

        NewlineStage(chars);
    }

    public void Raw(ReadOnlySpan<byte> bytes, long sourceOffset)
    {
        _started = true;
        FlushText();
        Encode([], flush: true);
        _output.Write(bytes);
    }

    /// <summary>持ち越した文字を書き出し、符号化器を空にする (範囲の終わり)。</summary>
    public void Complete()
    {
        FlushText();
        Encode([], flush: true);
    }

    private void FlushText()
    {
        if (_pendingCr)
        {
            _pendingCr = false;
            CaseStage(NewlineText());
        }

        if (_caseHigh is char high)
        {
            _caseHigh = null;
            NormalizeStage([high], final: false);
        }

        NormalizeStage([], final: true);
    }

    private ReadOnlySpan<char> NewlineText() => _newline switch
    {
        NewlineConversion.CrLf => "\r\n",
        NewlineConversion.Lf => "\n",
        _ => "\r",
    };

    private void NewlineStage(ReadOnlySpan<char> chars)
    {
        if (_newline == NewlineConversion.Keep)
        {
            CaseStage(chars);
            return;
        }

        if (_newlineBuffer.Length < chars.Length * 2 + 2)
        {
            _newlineBuffer = new char[chars.Length * 2 + 2];
        }

        ReadOnlySpan<char> nl = NewlineText();
        int n = 0;
        foreach (char c in chars)
        {
            if (_pendingCr)
            {
                _pendingCr = false;
                nl.CopyTo(_newlineBuffer.AsSpan(n));
                n += nl.Length;
                if (c == '\n')
                {
                    continue;
                }
            }

            if (c == '\r')
            {
                // 次の文字が LF かどうか (次のチャンクかもしれない) を見てから書き出す。
                _pendingCr = true;
            }
            else if (c == '\n')
            {
                nl.CopyTo(_newlineBuffer.AsSpan(n));
                n += nl.Length;
            }
            else
            {
                _newlineBuffer[n++] = c;
            }
        }

        CaseStage(_newlineBuffer.AsSpan(0, n));
    }

    private void CaseStage(ReadOnlySpan<char> chars)
    {
        if (_case is not CaseOperation operation || chars.IsEmpty)
        {
            NormalizeStage(chars, final: false);
            return;
        }

        if (_caseBuffer.Length < chars.Length + 2)
        {
            _caseBuffer = new char[chars.Length + 2];
        }

        int n = 0;
        if (_caseHigh is char high)
        {
            _caseHigh = null;
            _caseBuffer[n++] = high;
        }

        chars.CopyTo(_caseBuffer.AsSpan(n));
        n += chars.Length;

        // 末尾のサロゲートの上位は、次の文字と組にしてから変換する。
        if (char.IsHighSurrogate(_caseBuffer[n - 1]))
        {
            _caseHigh = _caseBuffer[--n];
        }

        Span<char> text = _caseBuffer.AsSpan(0, n);
        int i = 0;
        while (i < text.Length)
        {
            if (Rune.DecodeFromUtf16(text[i..], out Rune rune, out int consumed) != OperationStatus.Done)
            {
                i += consumed;
                continue;
            }

            Rune mapped = CaseConversion.Map(rune, operation);
            if (mapped != rune && mapped.Utf16SequenceLength == consumed && IsEncodable(mapped))
            {
                mapped.EncodeToUtf16(text[i..]);
            }

            i += consumed;
        }

        NormalizeStage(text, final: false);
    }

    private bool IsEncodable(Rune rune)
    {
        if (_caseCheck is null)
        {
            return true;
        }

        if (!_encodable.TryGetValue(rune.Value, out bool ok))
        {
            Span<char> buffer = stackalloc char[2];
            int length = rune.EncodeToUtf16(buffer);
            try
            {
                _caseCheck.GetByteCount(buffer[..length]);
                ok = true;
            }
            catch (EncoderFallbackException)
            {
                ok = false;
            }

            _encodable[rune.Value] = ok;
        }

        return ok;
    }

    private void NormalizeStage(ReadOnlySpan<char> chars, bool final)
    {
        if (_form is not NormalizationForm form)
        {
            Encode(chars, flush: false);
            return;
        }

        string text = _carry.Length == 0 ? new string(chars) : string.Concat(_carry, chars);
        int boundary = final ? text.Length : Normalization.LastSafeBoundary(text);
        if (!final && boundary == 0 && text.Length > MaxNormalizationCarry)
        {
            boundary = char.IsHighSurrogate(text[^1]) ? text.Length - 1 : text.Length;
        }

        _carry = text[boundary..];
        if (boundary > 0)
        {
            Encode(text[..boundary].Normalize(form), flush: false);
        }
    }

    private void Encode(ReadOnlySpan<char> chars, bool flush)
    {
        try
        {
            do
            {
                ReadOnlySpan<char> block = chars[..Math.Min(chars.Length, _encodeBlock)];
                bool last = block.Length == chars.Length;
                _encoder.Convert(block, _bytes, flush && last, out int used, out int produced, out bool completed);
                _output.Write(_bytes.AsSpan(0, produced));
                chars = chars[used..];
                if (chars.IsEmpty && (completed || !flush))
                {
                    break;
                }
            }
            while (true);
        }
        catch (EncoderFallbackException ex)
        {
            string text = ex.CharUnknown != '\0' ? ex.CharUnknown.ToString() : new string([ex.CharUnknownHigh, ex.CharUnknownLow]);
            throw new UnmappableTextException(text);
        }
    }
}

/// <summary>正規化の境界の判定。</summary>
internal static class Normalization
{
    /// <summary>
    /// <paramref name="text"/> の中で最後の、前後を別々に正規化してよい位置 (その位置の文字が、前の文字と結合・並べ替えしない
    /// 安全な開始文字)。見つからなければ 0。
    /// </summary>
    public static int LastSafeBoundary(string text)
    {
        for (int i = text.Length - 1; i > 0; i--)
        {
            char c = text[i];
            if (char.IsLowSurrogate(c))
            {
                continue;
            }

            if (Rune.DecodeFromUtf16(text.AsSpan(i), out Rune rune, out _) != OperationStatus.Done)
            {
                continue;
            }

            if (IsSafeStarter(rune))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// 前の文字と結合せず、分解しても結合文字で始まらない文字 (正規化の境界にしてよい文字)。結合文字、ハングルの字母
    /// (合成されて音節になる)、半角カナの濁点 (NFKC で結合文字になる)、未割り当ての文字は安全としない。
    /// </summary>
    public static bool IsSafeStarter(Rune rune)
    {
        int v = rune.Value;
        if (v < 0x80)
        {
            return true;
        }

        if (v is >= 0x1100 and <= 0x11FF or >= 0xA960 and <= 0xA97F or >= 0xD7B0 and <= 0xD7FF or >= 0x3130 and <= 0x318F
            or >= 0xFFA0 and <= 0xFFDC or 0xFF9E or 0xFF9F or >= 0x3099 and <= 0x309C)
        {
            return false;
        }

        return Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark or UnicodeCategory.OtherNotAssigned);
    }
}

/// <summary>範囲をチャンクごとに読み、解読・変換・符号化して書き出す。</summary>
internal static class Transcoder
{
    /// <summary>1 回に読むバイト数 (キャンセルと進捗の確認もこの単位)。</summary>
    public const int ChunkSize = 1024 * 1024;

    /// <summary>変換の設定。</summary>
    internal sealed record Setup(int SourceCodePage, InvalidSourceHandling Invalid, byte[] Preamble, Func<ByteOutput, int, TextPipeline> CreatePipeline,
        Func<string, string> Approximate, bool NewlineChanges);

    /// <summary>範囲を変換して <paramref name="output"/> に書く。</summary>
    /// <param name="decodeAll">偽なら範囲の末尾の不完全な文字を解読しない (プレビュー)。</param>
    public static void Run(DocumentSnapshot snapshot, TargetRange range, Setup setup, ByteOutput output, LongRunningOperation? operation,
        long progressBase, bool decodeAll = true)
    {
        output.Write(setup.Preamble);
        int maxChunk = (int)Math.Min(ChunkSize, Math.Max(1, range.Length));
        TextPipeline pipeline = setup.CreatePipeline(output, maxChunk);
        var decoder = new SourceDecoder(setup.SourceCodePage, setup.Invalid, pipeline, range.Offset, maxChunk);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(maxChunk);
        long chunkStart = range.Offset;
        try
        {
            long pos = range.Offset;
            do
            {
                ContentSink.Checkpoint(operation, progressBase + (pos - range.Offset));
                int n = (int)Math.Min(ChunkSize, range.End - pos);
                ReadChunk(snapshot, pos, buffer.AsSpan(0, n));
                chunkStart = pos;
                bool last = pos + n == range.End;
                decoder.Push(buffer.AsSpan(0, n), pos, last && decodeAll);
                if (last)
                {
                    pipeline.Complete();
                }

                pos += n;
            }
            while (pos < range.End);

            ContentSink.Checkpoint(operation, progressBase + range.Length);
        }
        catch (UnmappableTextException ex)
        {
            long at = Locate(snapshot, range, setup, chunkStart, ex.Text, operation);
            throw new CharsetConversionException(CharsetConversionErrorKind.UnmappableTarget, at, [], ex.Text);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 表せない文字 <paramref name="text"/> の変換元での位置を探す。範囲の先頭から解読し直し、表せなかったチャンクとその前のチャンクだけを
    /// 1 バイトずつ解読して、その文字 (改行の変換・正規化・大文字小文字の変換をおおよそ当てはめたもの) を含む最初の文字の位置を返す。
    /// 見つからなければ (正規化で前後の文字が合成された場合など) 表せなかったチャンクの先頭。
    /// </summary>
    private static long Locate(DocumentSnapshot snapshot, TargetRange range, Setup setup, long failingChunk, string text,
        LongRunningOperation? operation)
    {
        var finder = new LocateReceiver(text, setup.Approximate, setup.NewlineChanges);
        int maxChunk = (int)Math.Min(ChunkSize, Math.Max(1, range.Length));
        var decoder = new SourceDecoder(setup.SourceCodePage, setup.Invalid, finder, range.Offset, maxChunk);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(maxChunk);
        try
        {
            for (long pos = range.Offset; pos <= failingChunk && pos < range.End; pos += ChunkSize)
            {
                operation?.CancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(ChunkSize, range.End - pos);
                ReadChunk(snapshot, pos, buffer.AsSpan(0, n));
                bool fine = pos >= failingChunk - ChunkSize;
                decoder.FineGrained = fine;
                finder.Enabled = fine;
                decoder.Push(buffer.AsSpan(0, n), pos, pos + n == range.End);
            }
        }
        catch (LocatedException found)
        {
            return found.Offset;
        }
        catch (CharsetConversionException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return failingChunk;
    }

    /// <summary>読めない範囲があれば中止する (オフセットを示す)。</summary>
    internal static void ReadChunk(DocumentSnapshot snapshot, long offset, Span<byte> destination)
    {
        ReadResult result = snapshot.Read(offset, destination);
        if (!result.IsComplete)
        {
            throw new IOException($"オフセット 0x{result.Unreadable[0].Offset:X} からを読めません。");
        }

        if (result.BytesReturned < destination.Length)
        {
            throw new IOException($"オフセット 0x{offset + result.BytesReturned:X} からを読めません。");
        }
    }

    private sealed class LocatedException(long offset) : Exception
    {
        public long Offset { get; } = offset;
    }

    private sealed class LocateReceiver(string text, Func<string, string> approximate, bool newlineChanges) : ITextReceiver
    {
        public bool Enabled { get; set; }

        public void Text(ReadOnlySpan<char> chars, long sourceOffset)
        {
            if (!Enabled || chars.IsEmpty)
            {
                return;
            }

            if (chars.IndexOf(text) >= 0 || approximate(new string(chars)).Contains(text, StringComparison.Ordinal)
                || (newlineChanges && text.AsSpan().IndexOfAny('\r', '\n') >= 0 && chars.IndexOfAny('\r', '\n') >= 0))
            {
                throw new LocatedException(sourceOffset);
            }
        }

        public void Raw(ReadOnlySpan<byte> bytes, long sourceOffset)
        {
        }
    }
}
