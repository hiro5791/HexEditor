using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Inspector;
using HexEditor.Core.View;
using HexEditor.Sdk;
using CoreInspectorValue = HexEditor.Core.Inspector.InspectorValue;
using SdkInspectorOptions = HexEditor.Sdk.InspectorOptions;
using SdkInspectorValue = HexEditor.Sdk.InspectorValue;
using EncodingInfo = HexEditor.Sdk.EncodingInfo;

namespace HexEditor.Core.Scripting.Api;

/// <summary><c>inspector</c> (AUTO-12)。データインスペクタ (05) と同じ型の変換。</summary>
internal sealed class ScriptInspector(ScriptContext ctx) : IInspector
{
    public IReadOnlyList<InspectorTypeInfo> Types =>
        [.. InspectorTypes.All.Select(t => new InspectorTypeInfo(t.Id, ctx.Environment.Localize("Inspector_Row_" + t.Id), t.Size))];

    private static InspectorType TypeOf(string typeId) =>
        InspectorTypes.Find(typeId) ?? throw new Sdk.ArgumentError($"インスペクタの型が不明です: {typeId} (inspector.types で一覧できます)");

    private Endianness EndianOf(SdkInspectorOptions? options) => options?.Endian switch
    {
        null => ctx.Environment.Inspector.BigEndian ? Endianness.Big : Endianness.Little,
        "le" => Endianness.Little,
        "be" => Endianness.Big,
        string e => throw new Sdk.ArgumentError($"エンディアンは \"le\" か \"be\" です: {e}"),
    };

    public SdkInspectorValue Decode(IDocument document, OffsetArg offset, string typeId, SdkInspectorOptions? options = null) => ScriptErrorMap.Guard(() =>
    {
        if (document is not ScriptDocument doc)
        {
            throw new Sdk.ArgumentError("document にはドキュメントを指定してください。");
        }

        InspectorType type = TypeOf(typeId);
        long o = doc.ResolveOffset(offset);
        doc.CheckOffset(o, allowEnd: true);
        DocumentSnapshot snapshot = doc.Snapshot;
        int n = (int)Math.Min(InspectorDecoder.MaxReadLength, snapshot.Length - o);
        byte[] data = new byte[n];
        ScriptDocument.ReadExact(snapshot, o, data);
        Endianness endian = EndianOf(options);
        Core.Inspector.InspectorOptions panel = ctx.Dispatcher.Invoke(() => ctx.Environment.Inspector.Options);
        CoreInspectorValue value = InspectorDecoder.Decode(type, data, [], endian, panel);
        object? raw = value.Status == InspectorStatus.Ok ? RawValue(type, data, endian) ?? value.FullText ?? value.Text : null;
        return new SdkInspectorValue(value.Text, raw);
    });

    /// <summary>整数・浮動小数点の生の値 (それ以外は表示の文字列を使う)。</summary>
    private static object? RawValue(InspectorType type, byte[] data, Endianness endian)
    {
        bool big = endian == Endianness.Big && !type.FixedEndian;
        int size = type.Size;
        if (size <= 0 || size > data.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> bytes = data.AsSpan(0, size);
        switch (type.Id)
        {
            case InspectorTypes.Int8 or InspectorTypes.Int16 or InspectorTypes.Int24 or InspectorTypes.Int32 or InspectorTypes.Int48
                or InspectorTypes.Int64 or InspectorTypes.Int128:
                return Integer(new BigInteger(bytes, isUnsigned: false, isBigEndian: big), size);
            case InspectorTypes.UInt8 or InspectorTypes.UInt16 or InspectorTypes.UInt24 or InspectorTypes.UInt32 or InspectorTypes.UInt48
                or InspectorTypes.UInt64 or InspectorTypes.UInt128:
                return Integer(new BigInteger(bytes, isUnsigned: true, isBigEndian: big), size);
            case InspectorTypes.Float32:
                return (double)(big ? BinaryPrimitives.ReadSingleBigEndian(bytes) : BinaryPrimitives.ReadSingleLittleEndian(bytes));
            case InspectorTypes.Float64:
                return big ? BinaryPrimitives.ReadDoubleBigEndian(bytes) : BinaryPrimitives.ReadDoubleLittleEndian(bytes);
            case InspectorTypes.Half:
                return (double)(big ? BinaryPrimitives.ReadHalfBigEndian(bytes) : BinaryPrimitives.ReadHalfLittleEndian(bytes));
            default:
                return null;
        }
    }

    /// <summary>64 bit 以上は bigint、それ以下は number (AUTO-02 の仕様 7)。</summary>
    private static object Integer(BigInteger value, int size) => size >= 8 ? value : (double)value;

    public byte[] Encode(string typeId, string valueText, SdkInspectorOptions? options = null) => ScriptErrorMap.Guard(() =>
    {
        _ = TypeOf(typeId);
        Core.Inspector.InspectorOptions panel = ctx.Dispatcher.Invoke(() => ctx.Environment.Inspector.Options);
        InspectorEncodeResult result = InspectorEncoder.Encode(typeId, valueText, EndianOf(options), panel, long.MaxValue);
        return result.Bytes ?? throw new Sdk.ArgumentError($"値を変換できません: {valueText} ({result.Error})");
    });

    public void Show(string typeId)
    {
        _ = TypeOf(typeId);
        ctx.Dispatcher.Invoke(() => ctx.Environment.Inspector.SetVisible(typeId, true));
    }

    public void Hide(string typeId)
    {
        _ = TypeOf(typeId);
        ctx.Dispatcher.Invoke(() => ctx.Environment.Inspector.SetVisible(typeId, false));
    }
}

/// <summary><c>text</c> (AUTO-12 の仕様 5・6)。アプリが対応するすべての文字コードを使える。</summary>
public sealed class ScriptText : IText
{
    static ScriptText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>コードページ番号か名前 (<c>shift_jis</c>、<c>utf-16le</c>、<c>cp932</c> など) から文字コードを決める。</summary>
    public static Encoding Resolve(EncodingArg encoding)
    {
        if (encoding.CodePage is int cp)
        {
            return Resolve(cp);
        }

        string name = encoding.Name ?? throw new Sdk.ArgumentError("文字コードを指定してください。");
        Encoding? enc = Search.TextEncodings.FromCatalogId(name);
        if (enc is not null)
        {
            return enc;
        }

        try
        {
            Encoding found = Encoding.GetEncoding(name);
            return found.CodePage switch
            {
                1200 => new UnicodeEncoding(false, false),
                1201 => new UnicodeEncoding(true, false),
                65001 => new UTF8Encoding(false),
                _ => found,
            };
        }
        catch (ArgumentException ex)
        {
            throw new Sdk.ArgumentError($"文字コードが不明です: {name}", ex);
        }
    }

    public static Encoding Resolve(int codePage)
    {
        try
        {
            return codePage switch
            {
                1200 => new UnicodeEncoding(false, false),
                1201 => new UnicodeEncoding(true, false),
                12000 => new UTF32Encoding(false, false),
                12001 => new UTF32Encoding(true, false),
                65001 => new UTF8Encoding(false),
                _ => Encoding.GetEncoding(codePage),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new Sdk.ArgumentError($"文字コードが不明です: {codePage}", ex);
        }
    }

    /// <summary>NUL 文字の単位 (UTF-16 は 2、UTF-32 は 4)。</summary>
    internal static int UnitSize(Encoding encoding) => encoding.CodePage switch
    {
        1200 or 1201 => 2,
        12000 or 12001 => 4,
        _ => 1,
    };

    public byte[] Encode(string text, EncodingArg encoding) => Resolve(encoding).GetBytes(text);

    public string Decode(BytePattern bytes, EncodingArg encoding, DecodeOptions? options = null)
    {
        byte[] data = bytes.Bytes ?? ScriptValues.ParseHex(bytes.Text ?? "");
        if (data.Length > ScriptDocument.SyncLimit)
        {
            throw new LimitError("同期 API で扱えるのは 1 回 16 MB までです。") { Limit = "size" };
        }

        Encoding enc = Resolve(encoding);
        switch (options?.Invalid)
        {
            case null or "replace":
                return enc.GetString(data);
            case "error":
                try
                {
                    return Encoding.GetEncoding(enc.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(data);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new Sdk.ArgumentError($"文字コード {encoding} として解読できないバイトがあります (位置 {ex.Index})。", ex);
                }

            default:
                throw new Sdk.ArgumentError($"invalid は \"replace\" か \"error\" です: {options.Invalid}");
        }
    }

    public IReadOnlyList<EncodingInfo> Encodings =>
        [.. EncodingCatalog.All.Where(e => e.Selectable && e.CodePage > 1).Select(e => new EncodingInfo(e.CodePage, e.Id, e.EnglishName))];
}
