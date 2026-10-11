using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Editing.Transforms;

/// <summary>変換元で解釈できないバイト列の扱い (EDIT-38 の仕様 2)。</summary>
public enum InvalidSourceHandling
{
    /// <summary>最初の箇所でエラーにして中止する (既定)。</summary>
    Error,

    /// <summary>U+FFFD に置き換える。</summary>
    ReplaceWithFffd,

    /// <summary>そのまま残す (そのバイト列は変換せず、前後の文字だけ変換する)。</summary>
    KeepBytes,
}

/// <summary>変換先で表せない文字の扱い (EDIT-38 の仕様 3)。最善の近似文字 (best fit) には置き換えない。</summary>
public enum UnmappableHandling
{
    /// <summary>最初の箇所でエラーにして中止する (既定)。</summary>
    Error,

    /// <summary><c>?</c> に置き換える。</summary>
    Question,

    /// <summary>指定した文字 (<see cref="CharsetConversionOptions.CustomReplacement"/>) に置き換える。</summary>
    Custom,
}

/// <summary>改行の変換 (EDIT-38 の仕様 5)。</summary>
public enum NewlineConversion
{
    /// <summary>そのまま (既定)。</summary>
    Keep,
    CrLf,
    Lf,
    Cr,
}

/// <summary>Unicode の正規化 (EDIT-38 の仕様 6)。</summary>
public enum UnicodeNormalization
{
    /// <summary>しない (既定)。</summary>
    None,
    Nfc,
    Nfd,
    Nfkc,
    Nfkd,
}

/// <summary>
/// 文字コード変換の設定 (EDIT-38 の仕様 1〜6)。文字コードは <see cref="EncodingCatalog"/> の名前 (<c>utf-8</c>、<c>cp932</c>、<c>cp37</c>、
/// <c>ascii</c>、<c>ansi</c> など。表示文字コードと同じ一覧) で指定する。
/// </summary>
/// <param name="SourceEncodingId">変換元 (既定は現在の表示文字コード。呼び出し側が渡す)。</param>
/// <param name="TargetEncodingId">変換先 (既定 UTF-8)。</param>
public sealed record CharsetConversionOptions(string SourceEncodingId, string TargetEncodingId = "utf-8")
{
    public InvalidSourceHandling InvalidSource { get; init; } = InvalidSourceHandling.Error;

    public UnmappableHandling Unmappable { get; init; } = UnmappableHandling.Error;

    /// <summary><see cref="UnmappableHandling.Custom"/> で置き換える文字列 (変換先で表せること)。</summary>
    public string CustomReplacement { get; init; } = "?";

    /// <summary>変換元の先頭の BOM (U+FEFF) を取り除く (既定 真)。</summary>
    public bool StripSourceBom { get; init; } = true;

    /// <summary>変換先に BOM を付ける (既定 偽。BOM のある Unicode の文字コード・GB18030 だけ)。</summary>
    public bool AddTargetBom { get; init; }

    public NewlineConversion Newline { get; init; } = NewlineConversion.Keep;

    public UnicodeNormalization Normalization { get; init; } = UnicodeNormalization.None;
}

/// <summary>文字コード変換のエラーの種類。</summary>
public enum CharsetConversionErrorKind
{
    /// <summary>変換元で解釈できないバイト列。</summary>
    UndecodableSource,

    /// <summary>変換先で表せない文字。</summary>
    UnmappableTarget,
}

/// <summary>
/// 解釈できない・表せない箇所があり、扱いが「エラー」だったため中止した (EDIT-38 の「エラー」)。<see cref="Offset"/> は最初の箇所の
/// ドキュメント上の位置 (「そのオフセットへ移動」に使う)。ドキュメントは変わらない。
/// </summary>
public sealed class CharsetConversionException(CharsetConversionErrorKind kind, long offset, byte[] bytes, string? text)
    : Exception(kind == CharsetConversionErrorKind.UndecodableSource
        ? $"オフセット 0x{offset:X} のバイト列 {Convert.ToHexString(bytes)} を解釈できません。"
        : $"オフセット 0x{offset:X} の文字 '{text}' を変換先で表せません。")
{
    public CharsetConversionErrorKind Kind { get; } = kind;

    /// <summary>最初の箇所のドキュメント上の位置。</summary>
    public long Offset { get; } = offset;

    /// <summary>解釈できないバイト列 (<see cref="CharsetConversionErrorKind.UndecodableSource"/>)。表せない文字のときは空。</summary>
    public byte[] Bytes { get; } = bytes;

    /// <summary>表せない文字 (<see cref="CharsetConversionErrorKind.UnmappableTarget"/>)。解釈できないバイト列のときは null。</summary>
    public string? Text { get; } = text;
}

/// <summary>文字コード変換の設定の誤りの種類 (ダイアログの入力欄のエラー。文言は UI がリソースから作る)。</summary>
public enum CharsetSettingsError
{
    /// <summary>文字コードが一覧にない・この環境で使えない。</summary>
    UnknownEncoding,

    /// <summary>「指定した文字に置き換える」の文字列が変換先で表せない (または空)。</summary>
    InvalidReplacement,
}

/// <summary>
/// 文字コード変換・大文字小文字の変換の設定の誤り (EDIT-38 の「エラー」)。<see cref="Value"/> は問題の文字コードの名前または置き換えの文字列。
/// </summary>
public sealed class CharsetSettingsException(CharsetSettingsError error, string value, string paramName, Exception? inner = null)
    : ArgumentException(error == CharsetSettingsError.UnknownEncoding
        ? $"Encoding '{value}' is not available."
        : $"The replacement '{value}' cannot be represented in the target encoding.", paramName, inner)
{
    public CharsetSettingsError Error { get; } = error;

    public string Value { get; } = value;
}

/// <summary>プレビュー (EDIT-38 の仕様 8)。</summary>
/// <param name="Before">変換前の先頭 256 バイト。</param>
/// <param name="After">変換後の先頭 (変換前の先頭 256 バイトを変換したもののうち、文字の境界で切った 256 バイトまで。BOM を付ける場合は含む)。</param>
/// <param name="EstimatedLength">変換後の長さ。<paramref name="LengthIsExact"/> でなければ先頭を変換した比率からの推定。</param>
/// <param name="LengthIsExact">範囲が 4 MiB 以下で、全体を変換して長さを求めた。</param>
/// <param name="Error">変換するとエラーになる箇所 (分かった範囲で)。なければ null。</param>
public sealed record CharsetConversionPreview(byte[] Before, byte[] After, long EstimatedLength, bool LengthIsExact,
    CharsetConversionException? Error);

/// <summary>
/// 文字コード変換 (EDIT-38)。範囲を 1 MiB ずつ読み、<see cref="Decoder"/> / <see cref="Encoder"/> の状態を引き継いで変換する。結果は
/// <see cref="ContentSink"/> に書き (1 MiB を超える分は一時ファイル)、<see cref="RangeReplacement"/> として返す。反映は
/// <see cref="TransformApplier.Apply"/>。キャンセル・エラーでは書きかけの一時ファイルを消し、何も返さない。
/// </summary>
public static class CharsetConverter
{
    /// <summary>プレビューのバイト数。</summary>
    public const int PreviewLength = 256;

    // 4 MiB を超える範囲で、長さを推定するために変換する先頭のバイト数。
    private const int SampleLength = 64 * 1024;

    /// <summary>1 つの範囲を変換する。</summary>
    /// <param name="createSink">結果の書き出し先を作る (<see cref="ContentSink.For"/>)。</param>
    /// <param name="progressBase">進捗に足すバイト数 (複数の範囲を続けて変換するとき、それまでの範囲の長さ)。</param>
    /// <exception cref="CharsetConversionException">解釈できない・表せない箇所があり、扱いが「エラー」。</exception>
    /// <exception cref="OperationCanceledException">キャンセルされた。</exception>
    /// <exception cref="IOException">読めない範囲がある。</exception>
    public static RangeReplacement Convert(DocumentSnapshot snapshot, TargetRange range, CharsetConversionOptions options,
        Func<ContentSink> createSink, LongRunningOperation? operation = null, long progressBase = 0)
    {
        Transcoder.Setup setup = CreateSetup(options);
        ContentSink sink = createSink();
        try
        {
            Transcoder.Run(snapshot, range, setup, new SinkOutput(sink), operation, progressBase);
            return new RangeReplacement(range, sink.Complete());
        }
        finally
        {
            sink.Dispose();
        }
    }

    /// <summary>
    /// すべての範囲を、それぞれ別に変換する (EDIT-38 の仕様 9)。どれかが失敗・キャンセルされたら、作った内容をすべて捨てて例外を投げる。
    /// 進捗の全体は範囲の長さの合計。
    /// </summary>
    public static IReadOnlyList<RangeReplacement> ConvertAll(DocumentSnapshot snapshot, IReadOnlyList<TargetRange> ranges,
        CharsetConversionOptions options, Func<ContentSink> createSink, LongRunningOperation? operation = null)
    {
        CreateSetup(options);
        operation?.SetTotal(TextTransforms.TotalLength(ranges));
        var parts = new List<RangeReplacement>(ranges.Count);
        long done = 0;
        try
        {
            foreach (TargetRange range in ranges)
            {
                parts.Add(Convert(snapshot, range, options, createSink, operation, done));
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
    public static IReadOnlyList<RangeReplacement> ConvertAll(Document document, ISelectionRanges ranges, CharsetConversionOptions options,
        LongRunningOperation? operation = null, Action<long>? checkSpace = null) =>
        ConvertAll(document.Current, ranges.Ranges, options, () => ContentSink.For(document, checkSpace), operation);

    /// <summary>
    /// 変換前と変換後の先頭 256 バイトと、変換後の推定の長さ (EDIT-38 の仕様 8)。範囲が 4 MiB 以下なら全体を変換して正確な長さを求める
    /// (その場合、エラーになる最初の箇所も分かる)。
    /// </summary>
    /// <exception cref="IOException">読めない範囲がある。</exception>
    public static CharsetConversionPreview Preview(DocumentSnapshot snapshot, TargetRange range, CharsetConversionOptions options)
    {
        Transcoder.Setup setup = CreateSetup(options);
        int targetCodePage = TextTransforms.ResolveCodePage(options.TargetEncodingId);
        int n = (int)Math.Min(PreviewLength, range.Length);
        byte[] before = new byte[n];
        Transcoder.ReadChunk(snapshot, range.Offset, before);

        CharsetConversionException? error = null;
        byte[] after = [];
        try
        {
            var capture = new CaptureOutput(PreviewLength * 16);
            Transcoder.Run(snapshot, new TargetRange(range.Offset, n), setup, capture, null, 0, decodeAll: n == range.Length);
            after = CutAtCharBoundary(capture.Captured, targetCodePage, PreviewLength);
        }
        catch (CharsetConversionException ex)
        {
            error = ex;
        }

        if (range.Length <= TextTransforms.LongRunningThreshold)
        {
            try
            {
                var count = new CaptureOutput(0);
                Transcoder.Run(snapshot, range, setup, count, null, 0);
                return new CharsetConversionPreview(before, after, count.Length, true, error);
            }
            catch (CharsetConversionException ex)
            {
                error ??= ex;
            }
        }

        return new CharsetConversionPreview(before, after, Estimate(snapshot, range, setup), false, error);
    }

    /// <summary>先頭を変換した長さの比率から、全体の変換後の長さを推定する。</summary>
    private static long Estimate(DocumentSnapshot snapshot, TargetRange range, Transcoder.Setup setup)
    {
        int n = (int)Math.Min(SampleLength, range.Length);
        if (n == 0)
        {
            return setup.Preamble.Length;
        }

        try
        {
            var count = new CaptureOutput(0);
            Transcoder.Run(snapshot, new TargetRange(range.Offset, n), setup, count, null, 0, decodeAll: n == range.Length);
            double ratio = (double)(count.Length - setup.Preamble.Length) / n;
            return setup.Preamble.Length + (long)Math.Round(ratio * range.Length);
        }
        catch (CharsetConversionException)
        {
            return setup.Preamble.Length + range.Length;
        }
    }

    /// <summary>変換後のバイト列を、文字の途中で切らないよう <paramref name="limit"/> バイト以内にする。</summary>
    private static byte[] CutAtCharBoundary(byte[] bytes, int codePage, int limit)
    {
        if (bytes.Length <= limit)
        {
            return bytes;
        }

        int end = limit;
        switch (codePage)
        {
            case 65001:
                while (end > 0 && (bytes[end] & 0xC0) == 0x80)
                {
                    end--;
                }

                break;
            case 1200 or 1201:
                end &= ~1;
                int high = codePage == 1200 ? end - 1 : end - 2;
                if (end >= 2 && bytes[high] is >= 0xD8 and <= 0xDB)
                {
                    end -= 2;
                }

                break;
            case 12000 or 12001:
                end &= ~3;
                break;
            default:
                // 多バイトの文字コードは、切った末尾が解釈できるところまで戻す。
                Encoding strict = TextTransforms.Create(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                for (int back = 0; back < 4 && end - back > 0; back++)
                {
                    try
                    {
                        strict.GetCharCount(bytes, 0, end - back);
                        end -= back;
                        break;
                    }
                    catch (DecoderFallbackException)
                    {
                    }
                }

                break;
        }

        return bytes[..end];
    }

    /// <summary>設定を確かめ、変換の部品を用意する。</summary>
    /// <exception cref="ArgumentException">使えない文字コード、または変換先で表せない置き換えの文字列。</exception>
    private static Transcoder.Setup CreateSetup(CharsetConversionOptions options)
    {
        int source = TextTransforms.ResolveCodePage(options.SourceEncodingId);
        int target = TextTransforms.ResolveCodePage(options.TargetEncodingId);
        EncoderFallback fallback = options.Unmappable switch
        {
            UnmappableHandling.Question => new EncoderReplacementFallback("?"),
            UnmappableHandling.Custom => new EncoderReplacementFallback(ValidReplacement(options.CustomReplacement, target)),
            _ => EncoderFallback.ExceptionFallback,
        };
        Encoding targetEncoding = TextTransforms.Create(target, fallback, DecoderFallback.ExceptionFallback);
        NormalizationForm? form = options.Normalization switch
        {
            UnicodeNormalization.Nfc => NormalizationForm.FormC,
            UnicodeNormalization.Nfd => NormalizationForm.FormD,
            UnicodeNormalization.Nfkc => NormalizationForm.FormKC,
            UnicodeNormalization.Nfkd => NormalizationForm.FormKD,
            _ => null,
        };
        byte[] preamble = options.AddTargetBom ? Bom(target) : [];
        return new Transcoder.Setup(source, options.InvalidSource, preamble,
            (output, sizeHint) => new TextPipeline(targetEncoding, output, options.StripSourceBom, options.Newline, form, null, sizeHint),
            text => Approximate(text, form), options.Newline != NewlineConversion.Keep);
    }

    private static string Approximate(string text, NormalizationForm? form)
    {
        if (form is not NormalizationForm f)
        {
            return text;
        }

        try
        {
            return text.Normalize(f);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    private static string ValidReplacement(string replacement, int target)
    {
        Encoding strict = TextTransforms.Create(target, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        try
        {
            strict.GetByteCount(replacement);
            _ = new EncoderReplacementFallback(replacement);
            return replacement;
        }
        catch (Exception ex) when (ex is EncoderFallbackException or ArgumentException)
        {
            throw new CharsetSettingsException(CharsetSettingsError.InvalidReplacement, replacement, nameof(replacement), ex);
        }
    }

    /// <summary>変換先の BOM (Unicode の文字コードと GB18030 だけ。ほかは付けない)。</summary>
    public static byte[] Bom(int codePage) => codePage switch
    {
        65001 => [0xEF, 0xBB, 0xBF],
        1200 => [0xFF, 0xFE],
        1201 => [0xFE, 0xFF],
        12000 => [0xFF, 0xFE, 0x00, 0x00],
        12001 => [0x00, 0x00, 0xFE, 0xFF],
        54936 => [0x84, 0x31, 0x95, 0x33],
        _ => [],
    };
}
