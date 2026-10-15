using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Runtime;

internal enum PrimKind
{
    UInt,
    SInt,
    Bool,
    Float,
    BFloat16,
    F80,
    F128,
    Char8,
    Char16,
    Char32,
    ULeb,
    SLeb,
    Vlq,
    SqliteVarint,
    Fixed,
    UFixed,
    CString,
    WString,
    PString,
    Time32,
    Time64,
    FileTime,
    DosDate,
    DosTime,
    DosDateTime,
    OleDate,
    HfsDate,
    Guid,
    Uuid,
    Ipv4,
    Ipv6,
    Mac,
    Rgb8,
    Rgba8,
    Bgra8,
    Rgb565,
    Argb1555,
    Padding,

    /// <summary>ビット単位の整数 (<c>bits&lt;N&gt;</c>。.ksy の <c>bN</c> の変換に使う)。</summary>
    Bits,

    /// <summary><c>std::type::magic&lt;"…"&gt;</c> (指定のバイト列であることを確認する型)。</summary>
    Magic,

    /// <summary><c>std::type::Size&lt;T&gt;</c> (バイト数として表示する整数)。</summary>
    SizeValue,

    /// <summary><c>std::type::Bitmask&lt;T&gt;</c> (2 進で表示する整数)。</summary>
    Bitmask,

    /// <summary>生のバイト列 (.ksy の <c>size</c> だけのフィールド)。</summary>
    RawBytes,
}

/// <summary>評価時の型。</summary>
internal abstract class RtType
{
    public required string Name { get; init; }

    /// <summary>型に付けた属性 (型の定義・別名。パターンの属性で上書きできる。TPL-13 の仕様 1)。</summary>
    public List<AttributeNode> Attributes { get; init; } = [];

    public TemplateSourceLocation? Location { get; init; }

    public string? Docs { get; init; }

    public abstract RtType WithAttributes(List<AttributeNode> attributes, string name);
}

internal sealed class PrimType : RtType
{
    public required PrimKind Kind { get; init; }

    /// <summary>サイズ (可変長は 0)。</summary>
    public int Size { get; init; }

    /// <summary>固定小数点数の小数部のビット数。ビット単位の整数のビット数。</summary>
    public int FracBits { get; init; }

    /// <summary><c>pstring&lt;T&gt;</c> の長さの型、<c>Size&lt;T&gt;</c> / <c>Bitmask&lt;T&gt;</c> の中身の型。</summary>
    public PrimType? Inner { get; init; }

    public byte[]? MagicBytes { get; init; }

    public byte[]? MagicMask { get; init; }

    public bool IsInteger => Kind is PrimKind.UInt or PrimKind.SInt or PrimKind.Bool or PrimKind.Char8 or PrimKind.Char16
        or PrimKind.Char32 or PrimKind.ULeb or PrimKind.SLeb or PrimKind.Vlq or PrimKind.SqliteVarint or PrimKind.Bits
        or PrimKind.Time32 or PrimKind.Time64 or PrimKind.FileTime or PrimKind.DosDate or PrimKind.DosTime or PrimKind.DosDateTime
        or PrimKind.HfsDate or PrimKind.SizeValue or PrimKind.Bitmask or PrimKind.Rgb565 or PrimKind.Argb1555;

    public bool IsSigned => Kind is PrimKind.SInt or PrimKind.SLeb || (Kind is PrimKind.SizeValue or PrimKind.Bitmask && Inner!.IsSigned);

    public bool IsChar => Kind is PrimKind.Char8 or PrimKind.Char16 or PrimKind.Char32;

    public bool IsFloat => Kind is PrimKind.Float or PrimKind.BFloat16 or PrimKind.F80 or PrimKind.F128 or PrimKind.Fixed or PrimKind.UFixed
        or PrimKind.OleDate;

    public bool IsVariable => Size == 0 && Kind is not PrimKind.Bits;

    public bool IsDate => Kind is PrimKind.Time32 or PrimKind.Time64 or PrimKind.FileTime or PrimKind.DosDate or PrimKind.DosTime
        or PrimKind.DosDateTime or PrimKind.OleDate or PrimKind.HfsDate;

    public bool IsColor => Kind is PrimKind.Rgb8 or PrimKind.Rgba8 or PrimKind.Bgra8 or PrimKind.Rgb565 or PrimKind.Argb1555;

    /// <summary>エンディアンを反映する型 (TPL-10 の仕様 2)。1 バイトの型と、バイトの並びが決まっている型は除く。</summary>
    public bool UsesEndian => Size > 1 && Kind is not (PrimKind.Guid or PrimKind.Uuid or PrimKind.Ipv4 or PrimKind.Ipv6 or PrimKind.Mac
        or PrimKind.Rgb8 or PrimKind.Rgba8 or PrimKind.Bgra8 or PrimKind.Padding or PrimKind.Magic or PrimKind.RawBytes);

    public override RtType WithAttributes(List<AttributeNode> attributes, string name) => new PrimType
    {
        Name = name,
        Kind = Kind,
        Size = Size,
        FracBits = FracBits,
        Inner = Inner,
        MagicBytes = MagicBytes,
        MagicMask = MagicMask,
        Attributes = [.. attributes, .. Attributes],
        Location = Location,
        Docs = Docs,
    };
}

internal sealed class StructType : RtType
{
    public required StructDecl Decl { get; init; }

    public Dictionary<string, RtType>? TypeArgs { get; init; }

    public Dictionary<string, Val>? ValueArgs { get; init; }

    /// <summary>型の識別 (同じ位置・同じ型のポインタの判定。TPL-08 の仕様 4)。</summary>
    public string Identity => Name;

    public override RtType WithAttributes(List<AttributeNode> attributes, string name) => new StructType
    {
        Name = name,
        Decl = Decl,
        TypeArgs = TypeArgs,
        ValueArgs = ValueArgs,
        Attributes = [.. attributes, .. Attributes],
        Location = Location,
        Docs = Docs,
    };
}

internal sealed class EnumType : RtType
{
    public required EnumDecl Decl { get; init; }

    public required PrimType Base { get; init; }

    public override RtType WithAttributes(List<AttributeNode> attributes, string name) => new EnumType
    {
        Name = name,
        Decl = Decl,
        Base = Base,
        Attributes = [.. attributes, .. Attributes],
        Location = Location,
        Docs = Docs,
    };
}

internal sealed class BitfieldType : RtType
{
    public required BitfieldDecl Decl { get; init; }

    public PrimType? Base { get; init; }

    public override RtType WithAttributes(List<AttributeNode> attributes, string name) => new BitfieldType
    {
        Name = name,
        Decl = Decl,
        Base = Base,
        Attributes = [.. attributes, .. Attributes],
        Location = Location,
        Docs = Docs,
    };
}

/// <summary>列挙型の解決済みの値。</summary>
internal sealed class EnumInfo
{
    public List<(string Name, Int128 Low, Int128 High, string? Doc)> Members { get; } = [];

    public Dictionary<Int128, string> Exact { get; } = [];

    public string? NameOf(Int128 value)
    {
        if (Exact.TryGetValue(value, out string? n))
        {
            return n;
        }

        foreach ((string name, Int128 low, Int128 high, _) in Members)
        {
            if (low != high && value >= low && value <= high)
            {
                return name;
            }
        }

        return null;
    }
}

/// <summary>組み込み型の表 (TPL-03)。</summary>
internal static class BuiltinTypes
{
    private static readonly Dictionary<string, PrimType> Table = Build();

    private static Dictionary<string, PrimType> Build()
    {
        var t = new Dictionary<string, PrimType>(StringComparer.Ordinal);
        void Add(PrimKind kind, int size, params string[] names)
        {
            foreach (string n in names)
            {
                t[n] = new PrimType { Name = n, Kind = kind, Size = size };
            }
        }

        Add(PrimKind.UInt, 1, "u8", "uint8_t", "byte", "uint8");
        Add(PrimKind.UInt, 2, "u16", "uint16_t", "ushort", "uint16");
        Add(PrimKind.UInt, 3, "u24");
        Add(PrimKind.UInt, 4, "u32", "uint32_t", "uint", "uint32");
        Add(PrimKind.UInt, 6, "u48");
        Add(PrimKind.UInt, 8, "u64", "uint64_t", "ulong", "uint64");
        Add(PrimKind.UInt, 16, "u128");
        Add(PrimKind.SInt, 1, "s8", "int8_t", "int8");
        Add(PrimKind.SInt, 2, "s16", "int16_t", "short", "int16");
        Add(PrimKind.SInt, 3, "s24");
        Add(PrimKind.SInt, 4, "s32", "int32_t", "int", "int32");
        Add(PrimKind.SInt, 6, "s48");
        Add(PrimKind.SInt, 8, "s64", "int64_t", "long", "int64");
        Add(PrimKind.SInt, 16, "s128");
        Add(PrimKind.Bool, 1, "bool");
        Add(PrimKind.Float, 2, "f16", "half");
        Add(PrimKind.BFloat16, 2, "bf16");
        Add(PrimKind.Float, 4, "f32", "float");
        Add(PrimKind.Float, 8, "f64", "double");
        Add(PrimKind.F80, 10, "f80", "long_double");
        Add(PrimKind.F128, 16, "f128");
        Add(PrimKind.Char8, 1, "char");
        Add(PrimKind.Char16, 2, "char16", "wchar_t");
        Add(PrimKind.Char32, 4, "char32");
        Add(PrimKind.ULeb, 0, "uleb128");
        Add(PrimKind.SLeb, 0, "sleb128");
        Add(PrimKind.Vlq, 0, "vlq");
        Add(PrimKind.SqliteVarint, 0, "sqlite_varint");
        Add(PrimKind.CString, 0, "string");
        Add(PrimKind.WString, 0, "wstring");
        Add(PrimKind.Time32, 4, "time32_t");
        Add(PrimKind.Time64, 8, "time64_t");
        Add(PrimKind.FileTime, 8, "filetime");
        Add(PrimKind.DosDate, 2, "dosdate");
        Add(PrimKind.DosTime, 2, "dostime");
        Add(PrimKind.DosDateTime, 4, "dosdatetime");
        Add(PrimKind.OleDate, 8, "oledate");
        Add(PrimKind.HfsDate, 4, "hfsdate");
        Add(PrimKind.Guid, 16, "guid");
        Add(PrimKind.Uuid, 16, "uuid");
        Add(PrimKind.Ipv4, 4, "ipv4");
        Add(PrimKind.Ipv6, 16, "ipv6");
        Add(PrimKind.Mac, 6, "mac");
        Add(PrimKind.Rgb8, 3, "rgb8");
        Add(PrimKind.Rgba8, 4, "rgba8");
        Add(PrimKind.Bgra8, 4, "bgra8");
        Add(PrimKind.Rgb565, 2, "rgb565");
        Add(PrimKind.Argb1555, 2, "argb1555");
        Add(PrimKind.Padding, 1, "padding");
        Add(PrimKind.RawBytes, 1, "__bytes");

        // std::type の別名 (TPL-18)
        foreach (string n in new[] { "guid", "uuid", "ipv4", "ipv6", "mac", "filetime", "time32_t", "time64_t", "dosdate", "dostime",
            "dosdatetime", "oledate", "hfsdate", "rgb8", "rgba8", "bgra8", "rgb565", "argb1555" })
        {
            t["std::type::" + n] = t[n];
        }

        return t;
    }

    public static PrimType? Find(string name) => Table.GetValueOrDefault(name);

    public static bool IsBuiltinName(string name) => Table.ContainsKey(name) || name is "pstring" or "fixed" or "ufixed" or "bits"
        or "std::type::magic" or "magic" or "std::type::Size" or "std::type::Bitmask";

    public static IEnumerable<string> Names => Table.Keys;
}
