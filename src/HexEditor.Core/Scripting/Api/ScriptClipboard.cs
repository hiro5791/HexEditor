using System.Globalization;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Sdk;
using SdkRange = HexEditor.Sdk.ByteRange;

namespace HexEditor.Core.Scripting.Api;

/// <summary><c>clipboard</c> (AUTO-15)。読み込みには許可 <c>clipboard-read</c> が必要。</summary>
internal sealed class ScriptClipboard(ScriptContext ctx) : IClipboard
{
    /// <summary>Copy As の出力の上限 (03 の EDIT-22 の 4。既定 64 MiB)。</summary>
    public const long OutputLimit = ClipboardPlan.DefaultLimit;

    /// <summary>
    /// Copy As の形式の ID (AUTO-15 の仕様 3)。<see cref="CopyFormat"/> の名前を小文字のハイフン区切りにしたもの
    /// (配列は <c>c-array</c> のように言語名 + <c>-array</c>、Hex は <c>hex</c> (空白区切り)・<c>hex-plain</c> など)。
    /// </summary>
    public static IReadOnlyDictionary<string, CopyFormat> Formats { get; } = new Dictionary<string, CopyFormat>(StringComparer.OrdinalIgnoreCase)
    {
        ["hex"] = CopyFormat.HexSpaced,
        ["hex-plain"] = CopyFormat.HexPlain,
        ["hex-comma"] = CopyFormat.HexCommaPrefixed,
        ["hex-escaped"] = CopyFormat.HexEscaped,
        ["hex-url"] = CopyFormat.HexUrl,
        ["decimal"] = CopyFormat.Decimal,
        ["octal"] = CopyFormat.Octal,
        ["binary"] = CopyFormat.Binary,
        ["c-array"] = CopyFormat.ArrayC,
        ["cpp-array"] = CopyFormat.ArrayCpp,
        ["csharp-array"] = CopyFormat.ArrayCSharp,
        ["java-array"] = CopyFormat.ArrayJava,
        ["javascript-array"] = CopyFormat.ArrayJavaScript,
        ["python-array"] = CopyFormat.ArrayPython,
        ["rust-array"] = CopyFormat.ArrayRust,
        ["go-array"] = CopyFormat.ArrayGo,
        ["pascal-array"] = CopyFormat.ArrayPascal,
        ["vb-array"] = CopyFormat.ArrayVisualBasic,
        ["purebasic-array"] = CopyFormat.ArrayPureBasic,
        ["asm-array"] = CopyFormat.ArrayAssembly,
        ["base64"] = CopyFormat.Base64,
        ["base32"] = CopyFormat.Base32,
        ["ascii85"] = CopyFormat.Ascii85,
        ["uuencode"] = CopyFormat.UUEncode,
        ["xxencode"] = CopyFormat.XXEncode,
        ["quoted-printable"] = CopyFormat.QuotedPrintable,
        ["html"] = CopyFormat.Html,
        ["rtf"] = CopyFormat.Rtf,
        ["markdown"] = CopyFormat.Markdown,
        ["tex"] = CopyFormat.Tex,
        ["screen"] = CopyFormat.ScreenDump,
        ["text"] = CopyFormat.Text,
        ["intel-hex"] = CopyFormat.IntelHex,
        ["srecord"] = CopyFormat.SRecord,
        ["json"] = CopyFormat.Json,
    };

    public void SetText(string text) => ctx.Dispatcher.Invoke(() => ctx.Environment.Clipboard.SetText(text));

    public void SetBytes(BytePattern data)
    {
        byte[] bytes = ScriptDocument.PatternBytes(data, allowRandom: false);
        if (bytes.Length > OutputLimit)
        {
            throw new LimitError("クリップボードに置ける大きさの上限を超えます。") { Limit = "size" };
        }

        ctx.Dispatcher.Invoke(() => ctx.Environment.Clipboard.SetBytes(bytes, HexText.Format(bytes)));
    }

    public string? GetText()
    {
        ctx.RequirePermission(ScriptPermissions.ClipboardRead, "clipboard.getText");
        return ctx.Dispatcher.Invoke(ctx.Environment.Clipboard.GetText);
    }

    public byte[]? GetBytes()
    {
        ctx.RequirePermission(ScriptPermissions.ClipboardRead, "clipboard.getBytes");
        return ctx.Dispatcher.Invoke(ctx.Environment.Clipboard.GetBytes);
    }

    public void CopyAs(IDocument document, RangeArg range, string formatId, CopyAsOptions? options = null) =>
        SetText(FormatAs(document, range, formatId, options));

    public string FormatAs(IDocument document, RangeArg range, string formatId, CopyAsOptions? options = null) => ScriptErrorMap.Guard(() =>
    {
        if (document is not ScriptDocument doc)
        {
            throw new Sdk.ArgumentError("document にはドキュメントを指定してください。");
        }

        if (!Formats.TryGetValue(formatId, out CopyFormat format))
        {
            throw new Sdk.ArgumentError($"形式が不明です: {formatId} ({string.Join(", ", Formats.Keys)})");
        }

        SdkRange r = doc.CheckedRange(range);
        CopyOptions copy = ctx.Dispatcher.Invoke(() => ctx.Environment.CopyOptions);
        if (options?.BytesPerLine is int bpl && bpl > 0)
        {
            copy = copy with { BytesPerLine = bpl };
        }

        if (options?.Uppercase is bool upper)
        {
            copy = copy with { UpperCase = upper };
        }

        if (CopyFormatter.Validate(format, copy, r.Start, r.Length) is CopyOptionError error)
        {
            throw new Sdk.ArgumentError($"Copy As の設定が正しくありません: {error}");
        }

        DocumentSnapshot snapshot = doc.Snapshot;
        void Read(long offset, Span<byte> dest) => ScriptDocument.ReadExact(snapshot, offset, dest);
        if (CopyFormatter.EstimateChars(format, copy, Read, r.Start, r.Length) > OutputLimit)
        {
            throw new LimitError("Copy As の出力の上限 (64 MiB) を超えます。exportRange でファイルに書き出してください。") { Limit = "size" };
        }

        var writer = new StringWriter(CultureInfo.InvariantCulture);
        CopyFormatter.Write(format, copy, Read, r.Start, r.Length, writer, ctx.Token);
        return writer.ToString();
    });
}
