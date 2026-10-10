using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Editing.Transforms;

/// <summary>大文字・小文字の変換の操作 (EDIT-39 の「呼び出し」)。</summary>
public enum CaseOperation
{
    /// <summary>大文字に。</summary>
    Upper,

    /// <summary>小文字に。</summary>
    Lower,

    /// <summary>大文字と小文字を入れ替える。</summary>
    Swap,
}

/// <summary>大文字・小文字の変換方法 (EDIT-39 の仕様 1)。</summary>
public enum CaseConversionMode
{
    /// <summary>ASCII の英字だけ (既定)。<c>a</c>〜<c>z</c> と <c>A</c>〜<c>Z</c> のバイトだけを変える。長さは変わらない。</summary>
    AsciiOnly,

    /// <summary>文字コードに従う。表示文字コードで解釈し、地域設定に依存しない Unicode の規則で変換して書き戻す (長さが変わりうる)。</summary>
    EncodingAware,
}

/// <summary>
/// 大文字・小文字の変換 (EDIT-39)。範囲を 1 MiB ずつ読んで <see cref="ContentSink"/> に書き、<see cref="RangeReplacement"/> として返す
/// (反映は <see cref="TransformApplier.Apply"/>)。「ASCII の英字だけ」は SIMD で処理する。「文字コードに従う」は文字コード変換 (EDIT-38) と
/// 同じ方式で解読・符号化し、解釈できないバイト列は変えずに残す (仕様 4)。変換は <see cref="Rune.ToUpperInvariant"/> /
/// <see cref="Rune.ToLowerInvariant"/> で行い、地域設定に依存しない (仕様 2。トルコ語でも <c>i</c> は <c>I</c>)。変換後の文字がその文字コードで
/// 表せない場合は、その文字を変えない。
/// </summary>
public static class CaseConversion
{
    /// <summary>変換方法によって長さが変わりうるか (偽なら固定長ドキュメントでも必ず実行できる)。</summary>
    public static bool MayChangeLength(CaseConversionMode mode) => mode == CaseConversionMode.EncodingAware;

    /// <summary>1 つの範囲を変換する。</summary>
    /// <param name="encodingId">「文字コードに従う」で使う表示文字コードの名前 (<see cref="View.TextEncoding.Id"/>)。</param>
    /// <param name="createSink">結果の書き出し先を作る (<see cref="ContentSink.For"/>)。</param>
    /// <param name="progressBase">進捗に足すバイト数 (複数の範囲を続けて変換するとき、それまでの範囲の長さ)。</param>
    /// <exception cref="OperationCanceledException">キャンセルされた。書きかけの一時ファイルは消す。</exception>
    /// <exception cref="IOException">読めない範囲がある。</exception>
    public static RangeReplacement Convert(DocumentSnapshot snapshot, TargetRange range, CaseOperation operation, CaseConversionMode mode,
        string encodingId, Func<ContentSink> createSink, LongRunningOperation? progress = null, long progressBase = 0)
    {
        Transcoder.Setup? setup = mode == CaseConversionMode.EncodingAware ? CreateSetup(operation, encodingId) : null;
        ContentSink sink = createSink();
        try
        {
            if (setup is not null)
            {
                Transcoder.Run(snapshot, range, setup, new SinkOutput(sink), progress, progressBase);
            }
            else
            {
                ConvertAsciiRange(snapshot, range, operation, sink, progress, progressBase);
            }

            return new RangeReplacement(range, sink.Complete());
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// すべての範囲を、それぞれ別に変換する (EDIT-39 の仕様 5)。どれかが失敗・キャンセルされたら、作った内容をすべて捨てて例外を投げる。
    /// </summary>
    public static IReadOnlyList<RangeReplacement> ConvertAll(DocumentSnapshot snapshot, IReadOnlyList<TargetRange> ranges, CaseOperation operation,
        CaseConversionMode mode, string encodingId, Func<ContentSink> createSink, LongRunningOperation? progress = null)
    {
        if (mode == CaseConversionMode.EncodingAware)
        {
            CreateSetup(operation, encodingId);
        }

        progress?.SetTotal(TextTransforms.TotalLength(ranges));
        var parts = new List<RangeReplacement>(ranges.Count);
        long done = 0;
        try
        {
            foreach (TargetRange range in ranges)
            {
                parts.Add(Convert(snapshot, range, operation, mode, encodingId, createSink, progress, done));
                done += range.Length;
            }
        }
        catch
        {
            TransformApplier.DisposeAll(parts);
            throw;
        }

        return parts;
    }

    /// <summary>ドキュメントの今の内容の、選択範囲のすべての要素を変換する (結果はドキュメントの一時フォルダ)。</summary>
    public static IReadOnlyList<RangeReplacement> ConvertAll(Document document, ISelectionRanges ranges, CaseOperation operation,
        CaseConversionMode mode, string encodingId, LongRunningOperation? progress = null, Action<long>? checkSpace = null) =>
        ConvertAll(document.Current, ranges.Ranges, operation, mode, encodingId, () => ContentSink.For(document, checkSpace), progress);

    /// <summary>「ASCII の英字だけ」の変換をその場で行う (<c>a</c>〜<c>z</c>・<c>A</c>〜<c>Z</c> 以外のバイトは変えない)。</summary>
    public static void ConvertAscii(Span<byte> data, CaseOperation operation)
    {
        ref byte start = ref MemoryMarshal.GetReference(data);
        nuint length = (nuint)data.Length;
        nuint i = 0;
        if (Vector256.IsHardwareAccelerated && length >= (nuint)Vector256<byte>.Count)
        {
            for (; i + (nuint)Vector256<byte>.Count <= length; i += (nuint)Vector256<byte>.Count)
            {
                Vector256<byte> v = Vector256.LoadUnsafe(ref start, i);
                (v ^ Flip(v, operation)).StoreUnsafe(ref start, i);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + (nuint)Vector128<byte>.Count <= length; i += (nuint)Vector128<byte>.Count)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref start, i);
                (v ^ Flip(v, operation)).StoreUnsafe(ref start, i);
            }
        }

        for (; i < length; i++)
        {
            ref byte b = ref Unsafe.Add(ref start, i);
            b = Map(b, operation);
        }
    }

    /// <summary>1 バイトの「ASCII の英字だけ」の変換。</summary>
    public static byte Map(byte value, CaseOperation operation)
    {
        bool flip = operation switch
        {
            CaseOperation.Upper => (uint)(value - 'a') < 26,
            CaseOperation.Lower => (uint)(value - 'A') < 26,
            _ => (uint)((value | 0x20) - 'a') < 26,
        };
        return flip ? (byte)(value ^ 0x20) : value;
    }

    /// <summary>1 文字の変換 (地域設定に依存しない規則)。入れ替えは大文字なら小文字に、小文字なら大文字にする (どちらでもなければ変えない)。</summary>
    public static Rune Map(Rune rune, CaseOperation operation) => operation switch
    {
        CaseOperation.Upper => Upper(rune),
        CaseOperation.Lower => Lower(rune),
        _ when Rune.IsUpper(rune) => Lower(rune),
        _ when Rune.IsLower(rune) => Upper(rune),
        _ => rune,
    };

    // .NET の不変の規則は ı (U+0131) と İ (U+0130) を変換しない。Unicode の既定の (トルコ語でない) 単純な対応に従い、
    // ı → I、İ → i にする (EDIT-39 の仕様 3 の例)。i ↔ I はどの地域設定でも変わらない。
    private static Rune Upper(Rune rune) => rune.Value == 0x131 ? new Rune('I') : Rune.ToUpperInvariant(rune);

    private static Rune Lower(Rune rune) => rune.Value == 0x130 ? new Rune('i') : Rune.ToLowerInvariant(rune);

    // 変える英字のバイトの位置に 0x20 を立てたベクトル (大文字と小文字は 0x20 のビットだけが違う)。
    private static Vector256<byte> Flip(Vector256<byte> v, CaseOperation operation)
    {
        Vector256<byte> letters = operation switch
        {
            CaseOperation.Upper => v - Vector256.Create((byte)'a'),
            CaseOperation.Lower => v - Vector256.Create((byte)'A'),
            _ => (v | Vector256.Create((byte)0x20)) - Vector256.Create((byte)'a'),
        };
        return Vector256.LessThan(letters, Vector256.Create((byte)26)) & Vector256.Create((byte)0x20);
    }

    private static Vector128<byte> Flip(Vector128<byte> v, CaseOperation operation)
    {
        Vector128<byte> letters = operation switch
        {
            CaseOperation.Upper => v - Vector128.Create((byte)'a'),
            CaseOperation.Lower => v - Vector128.Create((byte)'A'),
            _ => (v | Vector128.Create((byte)0x20)) - Vector128.Create((byte)'a'),
        };
        return Vector128.LessThan(letters, Vector128.Create((byte)26)) & Vector128.Create((byte)0x20);
    }

    private static void ConvertAsciiRange(DocumentSnapshot snapshot, TargetRange range, CaseOperation operation, ContentSink sink,
        LongRunningOperation? progress, long progressBase)
    {
        sink.ExpectedLength = range.Length;
        int size = (int)Math.Min(Transcoder.ChunkSize, Math.Max(1, range.Length));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            for (long pos = range.Offset; pos < range.End; pos += size)
            {
                ContentSink.Checkpoint(progress, progressBase + (pos - range.Offset));
                Span<byte> chunk = buffer.AsSpan(0, (int)Math.Min(size, range.End - pos));
                Transcoder.ReadChunk(snapshot, pos, chunk);
                ConvertAscii(chunk, operation);
                sink.Write(chunk);
            }

            ContentSink.Checkpoint(progress, progressBase + range.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>「文字コードに従う」の部品 (変換元と変換先は同じ文字コード、解釈できないバイト列はそのまま残す)。</summary>
    private static Transcoder.Setup CreateSetup(CaseOperation operation, string encodingId)
    {
        int codePage = TextTransforms.ResolveCodePage(encodingId);
        Encoding target = TextTransforms.Create(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        return new Transcoder.Setup(codePage, InvalidSourceHandling.KeepBytes, [],
            (output, sizeHint) => new TextPipeline(target, output, stripBom: false, NewlineConversion.Keep, null, operation, sizeHint),
            text => MapString(text, operation), NewlineChanges: false);
    }

    private static string MapString(string text, CaseOperation operation)
    {
        var builder = new StringBuilder(text.Length);
        foreach (Rune rune in text.EnumerateRunes())
        {
            builder.Append(Map(rune, operation).ToString());
        }

        return builder.ToString();
    }
}
