using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Runtime;

internal sealed partial class Evaluator
{
    private const int MaxStringValue = 16 * 1024 * 1024;

    // ───────────── データの読み込み ─────────────

    private byte ReadByte(Space space, long offset)
    {
        Span<byte> b = stackalloc byte[1];
        space.Read(offset, b, this, "read_u8");
        return b[0];
    }

    private UInt128 ReadUnsigned(Space space, long offset, int size, bool big)
    {
        Span<byte> buf = stackalloc byte[16];
        space.Read(offset, buf[..size], this, "read");
        UInt128 v = 0;
        for (int i = 0; i < size; i++)
        {
            byte b = big ? buf[i] : buf[size - 1 - i];
            v = (v << 8) | b;
        }

        return v;
    }

    private Int128 ReadIntegerValue(Space space, long offset, PrimType p, bool big)
    {
        UInt128 u = ReadUnsigned(space, offset, p.Size, big && p.UsesEndian);
        return p.IsSigned ? Truncate((Int128)u, p.Size * 8, signed: true) : (Int128)u;
    }

    internal static Encoding EncodingByName(string? name)
    {
        string n = (name ?? "latin1").Trim().ToLowerInvariant();
        switch (n)
        {
            case "latin1":
            case "iso-8859-1":
            case "iso8859-1":
            case "cp28591":
                return Encoding.Latin1;
            case "ascii":
            case "us-ascii":
                return Encoding.ASCII;
            case "utf-8":
            case "utf8":
                return new UTF8Encoding(false);
            case "utf-16":
            case "utf-16le":
            case "utf16le":
                return Encoding.Unicode;
            case "utf-16be":
            case "utf16be":
                return Encoding.BigEndianUnicode;
            case "utf-32":
            case "utf-32le":
                return Encoding.UTF32;
            case "utf-32be":
                return new UTF32Encoding(true, false);
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (View.EncodingCatalog.Find(n) is { CodePage: > 1 } entry)
        {
            return Encoding.GetEncoding(entry.CodePage);
        }

        try
        {
            return Encoding.GetEncoding(n);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1;
        }
    }

    private string DecodeString(Space space, long offset, long length, Encoding enc, string what)
    {
        int n = (int)Math.Min(length, MaxStringValue);
        CheckStringMemory(n);
        byte[] buf = new byte[n];
        space.Read(offset, buf, this, what);
        return enc.GetString(buf);
    }

    /// <summary>組み込み型の生の値。</summary>
    private Val ReadPrim(TemplateNode node)
    {
        if (node.ValueCached && node.ValueCache is Val cached)
        {
            return cached;
        }

        Val v = ReadPrimCore(node);
        node.ValueCache = v;
        node.ValueCached = true;
        return v;
    }

    private Val ReadPrimCore(TemplateNode node)
    {
        RtType? type = node.Type;
        if (type is EnumType et)
        {
            type = et.Base;
        }

        if (type is not PrimType p)
        {
            return Val.Null;
        }

        Space space = SpaceOfNode(node);
        long off = node.Offset;
        bool big = node.BigEndian && p.UsesEndian;
        switch (p.Kind)
        {
            case PrimKind.UInt:
            case PrimKind.SInt:
            case PrimKind.Char8:
            case PrimKind.Char16:
            case PrimKind.Char32:
            case PrimKind.Time32:
            case PrimKind.Time64:
            case PrimKind.FileTime:
            case PrimKind.DosDate:
            case PrimKind.DosTime:
            case PrimKind.DosDateTime:
            case PrimKind.HfsDate:
            case PrimKind.Rgb565:
            case PrimKind.Argb1555:
                return Val.Int(ReadIntegerValue(space, off, p, node.BigEndian));
            case PrimKind.SizeValue:
            case PrimKind.Bitmask:
                return Val.Int(ReadIntegerValue(space, off, p.Inner!, node.BigEndian));
            case PrimKind.Bool:
                return Val.Bool(ReadByte(space, off) != 0);
            case PrimKind.Bits:
                return Val.Int(node.BitValue);
            case PrimKind.Float:
            {
                UInt128 raw = ReadUnsigned(space, off, p.Size, big);
                return p.Size switch
                {
                    2 => Val.Float((double)BitConverter.UInt16BitsToHalf((ushort)raw)),
                    4 => Val.Float(BitConverter.UInt32BitsToSingle((uint)raw)),
                    _ => Val.Float(BitConverter.UInt64BitsToDouble((ulong)raw)),
                };
            }

            case PrimKind.BFloat16:
                return Val.Float(BitConverter.UInt32BitsToSingle((uint)ReadUnsigned(space, off, 2, big) << 16));
            case PrimKind.F80:
                return Val.Float(DecodeF80(ReadUnsigned(space, off, 10, big)));
            case PrimKind.F128:
                return Val.Float(DecodeF128(ReadUnsigned(space, off, 16, big)));
            case PrimKind.OleDate:
                return Val.Float(BitConverter.UInt64BitsToDouble((ulong)ReadUnsigned(space, off, 8, big)));
            case PrimKind.Fixed:
            case PrimKind.UFixed:
            {
                UInt128 raw = ReadUnsigned(space, off, p.Size, big);
                Int128 v = p.Kind == PrimKind.Fixed ? Truncate((Int128)raw, p.Size * 8, true) : (Int128)raw;
                return Val.Float((double)v / Math.Pow(2, p.FracBits));
            }

            case PrimKind.ULeb:
            case PrimKind.SLeb:
            {
                Int128 result = 0;
                int shift = 0;
                byte b = 0;
                for (int i = 0; i < node.Size; i++)
                {
                    b = ReadByte(space, off + i);
                    result |= (Int128)(b & 0x7F) << shift;
                    shift += 7;
                }

                if (p.Kind == PrimKind.SLeb && (b & 0x40) != 0 && shift < 128)
                {
                    result |= -(Int128.One << shift);
                }

                return Val.Int(result);
            }

            case PrimKind.Vlq:
            {
                Int128 result = 0;
                for (int i = 0; i < node.Size; i++)
                {
                    result = (result << 7) | (ReadByte(space, off + i) & 0x7F);
                }

                return Val.Int(result);
            }

            case PrimKind.SqliteVarint:
            {
                ulong result = 0;
                for (int i = 0; i < node.Size; i++)
                {
                    byte b = ReadByte(space, off + i);
                    result = i == 8 ? (result << 8) | b : (result << 7) | (uint)(b & 0x7F);
                }

                return Val.Int(result);
            }

            case PrimKind.CString:
            {
                long len = Math.Max(0, node.Size - (TerminatedWithZero(node, space, 1) ? 1 : 0));
                return Val.Str(DecodeString(space, off, len, EncodingByName(node.Attributes.Encoding ?? EncodingOf(node)), node.Name));
            }

            case PrimKind.WString:
            {
                long len = Math.Max(0, node.Size - (TerminatedWithZero(node, space, 2) ? 2 : 0));
                return Val.Str(DecodeString(space, off, len, node.BigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode, node.Name));
            }

            case PrimKind.PString:
            {
                int ls = p.Inner!.Size;
                return Val.Str(DecodeString(space, off + ls, node.Size - ls, EncodingByName(node.Attributes.Encoding ?? EncodingOf(node)), node.Name));
            }

            case PrimKind.Padding:
                return Val.Null;
            default:
            {
                if (node.Size > 64 * 1024 * 1024)
                {
                    return Val.Null;
                }

                byte[] buf = new byte[node.Size];
                space.Read(off, buf, this, node.Name);
                return Val.Bytes(buf);
            }
        }
    }

    private bool TerminatedWithZero(TemplateNode node, Space space, int unit)
    {
        if (node.Size < unit)
        {
            return false;
        }

        Span<byte> b = stackalloc byte[2];
        space.Read(node.End - unit, b[..unit], this, node.Name);
        return b[0] == 0 && (unit == 1 || b[1] == 0);
    }

    private string EncodingOf(TemplateNode node) => node.Context?.Encoding ?? _root.Encoding;

    private static double DecodeF80(UInt128 raw)
    {
        ulong mantissa = (ulong)raw;
        int exp = (int)((raw >> 64) & 0x7FFF);
        bool neg = ((raw >> 79) & 1) != 0;
        if (exp == 0 && mantissa == 0)
        {
            return neg ? -0.0 : 0.0;
        }

        if (exp == 0x7FFF)
        {
            return (mantissa << 1) == 0 ? (neg ? double.NegativeInfinity : double.PositiveInfinity) : double.NaN;
        }

        double v = mantissa / Math.Pow(2, 63) * Math.Pow(2, exp - 16383);
        return neg ? -v : v;
    }

    private static double DecodeF128(UInt128 raw)
    {
        int exp = (int)((raw >> 112) & 0x7FFF);
        bool neg = (raw >> 127) != 0;
        UInt128 frac = raw & ((UInt128.One << 112) - 1);
        if (exp == 0 && frac == 0)
        {
            return neg ? -0.0 : 0.0;
        }

        if (exp == 0x7FFF)
        {
            return frac == 0 ? (neg ? double.NegativeInfinity : double.PositiveInfinity) : double.NaN;
        }

        double m = 1 + ((double)(ulong)(frac >> 60) / Math.Pow(2, 52));
        double v = m * Math.Pow(2, exp - 16383);
        return neg ? -v : v;
    }

    internal byte[] ArrayBytes(TemplateNode n)
    {
        byte[] buf = new byte[n.Size];
        SpaceOfNode(n).Read(n.Offset, buf, this, n.Name);
        return buf;
    }

    internal Int128 RawInt(TemplateNode n)
    {
        Val v = n.ValueCached && n.ValueCache is Val c ? c : ReadPrim(n);
        return v.Kind is VK.Int or VK.Bool ? v.I : 0;
    }

    // ───────────── パターンの値 ─────────────

    private TemplateNode Resolve(TemplateNode n)
    {
        if (_instanceNodes.Contains(n) && n.Children0 is PointerChildren pc)
        {
            return pc.Target ?? n;
        }

        return n;
    }

    internal Val NodeValue(TemplateNode n0, bool converted)
    {
        TemplateNode n = Resolve(n0);
        switch (n.Kind)
        {
            case TemplateNodeKind.Computed:
                return n.ComputedVariable?.Value ?? Val.Null;
            case TemplateNodeKind.Error:
            case TemplateNodeKind.Padding:
                return Val.Null;
            case TemplateNodeKind.Struct:
            case TemplateNodeKind.Union:
            case TemplateNodeKind.Bitfield:
            case TemplateNodeKind.Pointer:
            case TemplateNodeKind.Section:
            case TemplateNodeKind.Application:
                return Val.Node(n);
            case TemplateNodeKind.Array:
                if (n.Type is PrimType { IsChar: true } cp)
                {
                    return Val.Str(CharArrayString(n, cp, n.Attributes.ShowAll));
                }

                return Val.Node(n);
            case TemplateNodeKind.BitfieldMember:
                return Val.Int(n.BitValue);
        }

        if (n.Children0 is null && n.Type is null && n.BitSize is not null)
        {
            return n.Kind == TemplateNodeKind.Bool ? Val.Bool(n.BitValue != 0) : Val.Int(n.BitValue);
        }

        if (n.BitSize is not null && n.Type is EnumType or PrimType { Kind: PrimKind.Bool } && n.Kind != TemplateNodeKind.Integer)
        {
            return n.Kind == TemplateNodeKind.Bool ? Val.Bool(n.BitValue != 0) : Val.Int(n.BitValue);
        }

        Val raw = ReadPrim(n);
        if (!converted || Attr(n.AttributeNodes, "read") is not { Args.Count: > 0 } read)
        {
            return raw;
        }

        if (n.ConvertedCache is Val conv)
        {
            return conv;
        }

        Frame f = n.Context ?? _root;
        Val fn = Eval(read.Args[0], f);
        if (fn.Kind != VK.Func)
        {
            throw ErrorAt(read.Location, TemplateMessages.NotCallable, "read");
        }

        Val result = Invoke((Callable)fn.O!, [raw], null, f);
        n.ConvertedCache = result;
        return result;
    }

    private string CharArrayString(TemplateNode n, PrimType cp, bool all)
    {
        Space space = SpaceOfNode(n);
        long len = Math.Min(n.Size, MaxStringValue);
        byte[] buf = new byte[len];
        space.Read(n.Offset, buf, this, n.Name);
        Encoding enc = cp.Kind switch
        {
            PrimKind.Char16 => n.BigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode,
            PrimKind.Char32 => n.BigEndian ? new UTF32Encoding(true, false) : Encoding.UTF32,
            _ => EncodingByName(n.Attributes.Encoding ?? EncodingOf(n)),
        };
        string s = enc.GetString(buf);
        if (!all)
        {
            int z = s.IndexOf('\0');
            if (z >= 0)
            {
                s = s[..z];
            }
        }

        return s;
    }

    /// <summary>公開の値 (TPL-25 の value)。</summary>
    internal object? PublicValue(TemplateNode node, bool converted)
    {
        try
        {
            Val v = NodeValue(node, converted);
            return ToPublic(v, node);
        }
        catch (TemplateError e) when (!e.IsLimit)
        {
            RecordError(e, node);
            return null;
        }
    }

    private static object? ToPublic(Val v, TemplateNode node) => v.Kind switch
    {
        VK.Int when node.Type is PrimType { IsSigned: false } or EnumType { Base.IsSigned: false } || node.Kind == TemplateNodeKind.BitfieldMember
            => v.I >= 0 && v.I <= ulong.MaxValue ? (v.I <= long.MaxValue ? (object)(long)v.I : (ulong)v.I) : v.I,
        VK.Int => v.I >= long.MinValue && v.I <= long.MaxValue ? (object)(long)v.I : v.I,
        VK.Bool => v.I != 0,
        VK.Float => v.F,
        VK.Str => v.AsString,
        VK.Bytes => (byte[])v.O!,
        VK.DateTime => new DateTime((long)v.I, DateTimeKind.Utc),
        _ => null,
    };

    // ───────────── 表示 (TPL-14) ─────────────

    internal string Display(TemplateNode node0, TemplateDisplayContext ctx)
    {
        TemplateNode node = Resolve(node0);
        try
        {
            if (Attr(node.AttributeNodes, "format") is { Args.Count: > 0 } fmt)
            {
                return CustomFormat(node, fmt, ctx);
            }

            return DefaultDisplay(node, ctx);
        }
        catch (TemplateError e) when (!e.IsLimit)
        {
            RecordDisplayError(e, node);
            return $"<error: {e.Diagnostic.Message}>";
        }
    }

    private readonly HashSet<TemplateNode> _displayErrors = new(ReferenceEqualityComparer.Instance);

    private void RecordDisplayError(TemplateError e, TemplateNode node)
    {
        if (_displayErrors.Add(node))
        {
            var d = new TemplateDiagnostic(TemplateSeverity.Error, TemplateMessages.DisplayFunctionFailed, [e.Diagnostic.Message],
                e.Diagnostic.Location, node.Offset, node.Path, e.Diagnostic.CallStack);
            node.DisplayError = d;
            Report(d, node);
        }
    }

    private string CustomFormat(TemplateNode node, AttributeNode fmt, TemplateDisplayContext ctx)
    {
        Frame f = node.Context ?? _root;
        Expr arg = fmt.Args[0];
        if (arg is LiteralExpr { Value: string name })
        {
            return BuiltinFormat(node, name, ctx);
        }

        long steps = _steps;
        long limit = _stepLimit;
        _stepLimit = _steps + 100_000; // 1 回の評価の上限は 10 万ステップ (TPL-14 の「巨大ファイル」)
        TemplateNode? saveThis = _thisNode;
        _thisNode = node;
        try
        {
            Val fn = Eval(arg, f);
            if (fn.Kind == VK.Str)
            {
                return BuiltinFormat(node, fn.AsString, ctx);
            }

            if (fn.Kind != VK.Func)
            {
                throw ErrorAt(fmt.Location, TemplateMessages.NotCallable, "format");
            }

            Val arg0 = fn.O is UserFunction { Decl.IsExtern: true } ? NodeValue(node, true) : Val.Node(node);
            Val result = Invoke((Callable)fn.O!, [arg0], null, f);
            return ToDisplayString(Deref(result));
        }
        catch (TemplateError e) when (e.IsLimit && e.Diagnostic.Code == TemplateMessages.StepLimit)
        {
            throw new TemplateError(e.Diagnostic);
        }
        finally
        {
            _thisNode = saveThis;
            _stepLimit = limit;
            _ = steps;
        }
    }

    private string BuiltinFormat(TemplateNode node, string name, TemplateDisplayContext ctx)
    {
        Val v = Deref(Val.Node(node));
        switch (name)
        {
            case "hex":
                return v.Kind == VK.Int ? FormatHex(v.I, upperPrefix: false) : DefaultDisplay(node, ctx);
            case "HEX":
                return v.Kind == VK.Int ? FormatHex(v.I, upperPrefix: true) : DefaultDisplay(node, ctx);
            case "dec":
                return v.Kind == VK.Int ? v.I.ToString(CultureInfo.InvariantCulture) : DefaultDisplay(node, ctx);
            case "oct":
                return v.Kind == VK.Int ? "0o" + ToRadix(v.I, 8) : DefaultDisplay(node, ctx);
            case "bin":
                return v.Kind == VK.Int ? "0b" + ToRadix(v.I, 2) : DefaultDisplay(node, ctx);
            case "char":
                return v.Kind == VK.Int ? "'" + CharDisplay(v.I) + "'" : DefaultDisplay(node, ctx);
            case "size":
                return v.Kind == VK.Int ? FormatSize((long)v.I, ctx.Culture) : DefaultDisplay(node, ctx);
            case "offset":
                return v.Kind == VK.Int ? ((long)v.I).ToString("X8", CultureInfo.InvariantCulture) : DefaultDisplay(node, ctx);
            case "datetime":
                return v.Kind == VK.Int ? FormatDate(DateTime.UnixEpoch.AddSeconds((double)v.I), ctx) : DefaultDisplay(node, ctx);
            case "bool":
                return Truthy(v) ? "true" : "false";
        }

        return DefaultDisplay(node, ctx);
    }

    internal static string FormatHex(Int128 v, bool upperPrefix)
    {
        string digits = v < 0 ? ((UInt128)v).ToString("X", CultureInfo.InvariantCulture) : v.ToString("X", CultureInfo.InvariantCulture);
        return (upperPrefix ? "0X" : "0x") + digits;
    }

    private static string ToRadix(Int128 v, int radix)
    {
        UInt128 u = (UInt128)v;
        if (u == 0)
        {
            return "0";
        }

        var sb = new StringBuilder();
        while (u != 0)
        {
            sb.Insert(0, (char)('0' + (int)(u % (UInt128)radix)));
            u /= (UInt128)radix;
        }

        return sb.ToString();
    }

    private static string CharDisplay(Int128 code) =>
        code >= 0x20 && code < 0x7F ? ((char)(int)code).ToString() : code >= 0 && code <= 0x10FFFF && !(code >= 0xD800 && code <= 0xDFFF)
            ? (code < 0x20 || code == 0x7F ? $"\\x{(int)code:X2}" : char.ConvertFromUtf32((int)code))
            : "?";

    internal static string FormatSize(long bytes, CultureInfo culture)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB", "EB"];
        if (Math.Abs(bytes) < 1024)
        {
            return bytes.ToString("N0", culture) + " " + units[0];
        }

        double v = bytes;
        int u = 0;
        while (Math.Abs(v) >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return v.ToString(v >= 100 ? "N0" : "0.#", culture) + " " + units[u];
    }

    private static string FormatDate(DateTime utc, TemplateDisplayContext ctx)
    {
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), ctx.TimeZone);
        return local.ToString("G", ctx.Culture);
    }

    private string DefaultDisplay(TemplateNode node, TemplateDisplayContext ctx)
    {
        switch (node.Kind)
        {
            case TemplateNodeKind.Application:
                return string.Empty;
            case TemplateNodeKind.Error:
                return "<error: " + (node.Diagnostics.FirstOrDefault()?.Message ?? string.Empty) + ">";
            case TemplateNodeKind.Struct:
            case TemplateNodeKind.Union:
            case TemplateNodeKind.Bitfield:
                if (node.Children0 is PointerChildren && _instanceNodes.Contains(node))
                {
                    return string.Empty;
                }

                return $"{node.TypeName} ({ChildCount(node).ToString(CultureInfo.InvariantCulture)})";
            case TemplateNodeKind.Section:
                return FormatSize(node.Size, ctx.Culture);
            case TemplateNodeKind.Padding:
                return $"(padding {node.Size.ToString(CultureInfo.InvariantCulture)} bytes)";
            case TemplateNodeKind.Pointer:
                return PointerDisplay(node);
            case TemplateNodeKind.Array:
                return ArrayDisplay(node);
        }

        Val v = NodeValue(node, converted: true);
        if (node.Kind is TemplateNodeKind.Enum or TemplateNodeKind.Flags && node.Type is EnumType or null)
        {
            EnumType? et = node.Type as EnumType ?? (node.Context is not null ? null : null);
            if (et is not null && v.Kind == VK.Int)
            {
                return et.Decl.IsFlags ? FlagsDisplay(et, v.I) : EnumDisplay(et, v.I);
            }
        }

        if (node.Kind == TemplateNodeKind.BitfieldMember && v.Kind == VK.Int)
        {
            return $"{v.I.ToString(CultureInfo.InvariantCulture)} (0b{ToRadix(v.I, 2)})";
        }

        if (node.Type is PrimType p)
        {
            Val raw = ReadPrim(node);
            if (Attr(node.AttributeNodes, "read") is null)
            {
                switch (p.Kind)
                {
                    case PrimKind.Bitmask:
                        return "0b" + ToRadix(raw.I, 2);
                    case PrimKind.SizeValue:
                        return FormatSize((long)raw.I, ctx.Culture);
                    case PrimKind.Char8:
                    case PrimKind.Char16:
                    case PrimKind.Char32:
                        return CharDisplay(raw.I);
                    case PrimKind.Guid:
                        return new Guid((byte[])raw.O!).ToString("B").ToUpperInvariant();
                    case PrimKind.Uuid:
                        return new Guid((byte[])raw.O!, bigEndian: true).ToString("D");
                    case PrimKind.Ipv4:
                    case PrimKind.Ipv6:
                        return new IPAddress((byte[])raw.O!).ToString();
                    case PrimKind.Mac:
                        return string.Join(":", ((byte[])raw.O!).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
                    case PrimKind.Magic:
                    case PrimKind.RawBytes:
                        return HexPreview((byte[])raw.O!, ((byte[])raw.O!).Length);
                }

                if (p.IsColor)
                {
                    uint argb = ColorOf(node) ?? 0;
                    return p.Kind is PrimKind.Rgba8 or PrimKind.Bgra8
                        ? $"#{argb >> 16 & 0xFF:X2}{argb >> 8 & 0xFF:X2}{argb & 0xFF:X2}{argb >> 24:X2}"
                        : $"#{argb >> 16 & 0xFF:X2}{argb >> 8 & 0xFF:X2}{argb & 0xFF:X2}";
                }

                if (p.IsDate && DateTimeOf(node) is { } dt)
                {
                    return FormatDate(dt, ctx);
                }

                if (p.Kind == PrimKind.Float && p.Size == 4 && v.Kind == VK.Float)
                {
                    return ((float)v.F).ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        if (node.Kind is TemplateNodeKind.Enum or TemplateNodeKind.Flags && node.Type is EnumType et2 && v.Kind == VK.Int)
        {
            return et2.Decl.IsFlags ? FlagsDisplay(et2, v.I) : EnumDisplay(et2, v.I);
        }

        return ValueDisplay(v);
    }

    private string ValueDisplay(Val v) => v.Kind switch
    {
        VK.Bool => v.I != 0 ? "true" : "false",
        VK.Float => FormatDouble(v.F),
        VK.Int => v.I.ToString(CultureInfo.InvariantCulture),
        VK.Str => v.AsString,
        VK.Bytes => HexPreview((byte[])v.O!, 16),
        VK.Null => "null",
        VK.Node => v.AsNode.Path,
        VK.DateTime => new DateTime((long)v.I, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    private static string HexPreview(byte[] bytes, int max)
    {
        int n = Math.Min(max, bytes.Length);
        string s = string.Join(" ", bytes.Take(n).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        return bytes.Length > n ? s + " …" : s;
    }

    private string ArrayDisplay(TemplateNode node)
    {
        if (node.Type is PrimType { IsChar: true } cp)
        {
            return CharArrayString(node, cp, node.Attributes.ShowAll);
        }

        if (node.Type is PrimType { Kind: PrimKind.UInt or PrimKind.SInt, Size: 1 })
        {
            int n = (int)Math.Min(16, node.Size);
            byte[] buf = new byte[n];
            SpaceOfNode(node).Read(node.Offset, buf, this, node.Name);
            string s = string.Join(" ", buf.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            return node.Size > 16 ? s + " …" : s;
        }

        return $"[{ChildCount(node).ToString(CultureInfo.InvariantCulture)}]";
    }

    private string PointerDisplay(TemplateNode node)
    {
        TemplatePointerInfo info = node.Pointer!;
        if (info.IsNull)
        {
            return "null";
        }

        if (node.Children0 is PointerChildren pc)
        {
            _ = pc.Target;
        }

        if (info.Error is not null)
        {
            return "<error: " + info.Error.Message + ">";
        }

        if (info.OutOfRange)
        {
            return $"out of range (0x{info.TargetOffset:X})";
        }

        if (info.DuplicateOf is not null)
        {
            return $"→ {info.DuplicateOf} (seen)";
        }

        return $"→ 0x{info.TargetOffset:X} ({info.TargetType})";
    }

    private string EnumDisplay(EnumType et, Int128 value)
    {
        string? name = EnumInfoOf(et.Decl).NameOf(value);
        string hex = "0x" + (value < 0 ? ((UInt128)Truncate(value, et.Base.Size * 8, false)).ToString("X", CultureInfo.InvariantCulture)
            : value.ToString("X", CultureInfo.InvariantCulture));
        return name is null ? $"({hex})" : $"{name} ({hex})";
    }

    private string FlagsDisplay(EnumType et, Int128 value)
    {
        EnumInfo info = EnumInfoOf(et.Decl);
        if (value == 0)
        {
            return info.Exact.TryGetValue(0, out string? zero) ? zero : "0";
        }

        var parts = new List<string>();
        Int128 rest = value;
        foreach ((string name, Int128 low, Int128 high, _) in info.Members)
        {
            if (low == 0 || low != high)
            {
                continue;
            }

            if ((value & low) == low && (rest & low) != 0)
            {
                parts.Add(name);
                rest &= ~low;
            }
        }

        if (rest != 0)
        {
            parts.Add("0x" + rest.ToString("X", CultureInfo.InvariantCulture));
        }

        return string.Join(" | ", parts);
    }

    internal bool IsUndefinedEnum(TemplateNode node)
    {
        if (node.Type is not EnumType et || et.Decl.IsFlags)
        {
            return false;
        }

        try
        {
            Val v = NodeValue(node, converted: false);
            return v.Kind == VK.Int && EnumInfoOf(et.Decl).NameOf(v.I) is null;
        }
        catch (TemplateError)
        {
            return false;
        }
    }

    internal IReadOnlyList<KeyValuePair<string, Int128>> EnumMembers(TemplateNode node) =>
        node.Type is EnumType et ? [.. EnumInfoOf(et.Decl).Members.Select(m => new KeyValuePair<string, Int128>(m.Name, m.Low))] : [];

    internal DateTime? DateTimeOf(TemplateNode node)
    {
        if (node.Type is not PrimType p)
        {
            return null;
        }

        try
        {
            Val v = ReadPrim(node);
            switch (p.Kind)
            {
                case PrimKind.Time32:
                case PrimKind.Time64:
                    return DateTime.UnixEpoch.AddSeconds((double)v.I);
                case PrimKind.FileTime:
                    return v.I >= 0 && v.I <= 2650467743999999999 ? DateTime.FromFileTimeUtc((long)v.I) : null;
                case PrimKind.HfsDate:
                    return new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((double)v.I);
                case PrimKind.OleDate:
                    return double.IsFinite(v.F) && v.F is > -657435 and < 2958466 ? DateTime.SpecifyKind(DateTime.FromOADate(v.F), DateTimeKind.Utc) : null;
                case PrimKind.DosDate:
                    return DosDate((int)v.I, 0);
                case PrimKind.DosTime:
                    return DosDate(0x21, (int)v.I);
                case PrimKind.DosDateTime:
                    return DosDate((int)((ulong)v.I >> 16), (int)((ulong)v.I & 0xFFFF));
            }
        }
        catch (TemplateError)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }

        return null;
    }

    private static DateTime? DosDate(int date, int time)
    {
        int year = 1980 + ((date >> 9) & 0x7F);
        int month = (date >> 5) & 0xF;
        int day = date & 0x1F;
        int hour = (time >> 11) & 0x1F;
        int minute = (time >> 5) & 0x3F;
        int second = (time & 0x1F) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
    }

    /// <summary>色の型の値 (0xAARRGGBB)。5 bit・6 bit の成分は上位のビットを下位に繰り返して 8 bit に広げる (TPL-03 の仕様 4)。</summary>
    internal uint? ColorOf(TemplateNode node)
    {
        if (node.Type is not PrimType p || !p.IsColor)
        {
            return null;
        }

        Val v = ReadPrim(node);
        static uint E5(uint x) => (x << 3) | (x >> 2);
        static uint E6(uint x) => (x << 2) | (x >> 4);
        switch (p.Kind)
        {
            case PrimKind.Rgb565:
            {
                uint x = (uint)v.I;
                return 0xFF000000 | (E5((x >> 11) & 31) << 16) | (E6((x >> 5) & 63) << 8) | E5(x & 31);
            }

            case PrimKind.Argb1555:
            {
                uint x = (uint)v.I;
                uint a = (x & 0x8000) != 0 ? 0xFFu : 0u;
                return (a << 24) | (E5((x >> 10) & 31) << 16) | (E5((x >> 5) & 31) << 8) | E5(x & 31);
            }
        }

        byte[] b = (byte[])v.O!;
        return p.Kind switch
        {
            PrimKind.Rgb8 => 0xFF000000 | ((uint)b[0] << 16) | ((uint)b[1] << 8) | b[2],
            PrimKind.Rgba8 => ((uint)b[3] << 24) | ((uint)b[0] << 16) | ((uint)b[1] << 8) | b[2],
            _ => ((uint)b[3] << 24) | ((uint)b[2] << 16) | ((uint)b[1] << 8) | b[0],
        };
    }

    // ───────────── コメント ─────────────

    internal string? Comment(TemplateNode node)
    {
        if (Attr(node.AttributeNodes, "comment") is not { Args.Count: > 0 } c)
        {
            return null;
        }

        TemplateNode? saveThis = _thisNode;
        _thisNode = node;
        try
        {
            return ToDisplayString(Deref(Eval(c.Args[0], node.Context ?? _root)));
        }
        catch (TemplateError e) when (!e.IsLimit)
        {
            return $"<error: {e.Diagnostic.Message}>";
        }
        finally
        {
            _thisNode = saveThis;
        }
    }

    // ───────────── 子 ─────────────

    internal long ChildCount(TemplateNode node0)
    {
        TemplateNode node = Resolve(node0);
        try
        {
            switch (node.Children0)
            {
                case LazyChildren lc:
                    Materialize(node);
                    return lc.Items?.Count ?? 0;
                case ListChildren l:
                    return l.Items.Count;
                case OnDemandChildren od:
                    return od.Count;
                case SequenceChildren s:
                    return s.Items.Count;
                case PointerChildren pc:
                    return pc.Target is { } t ? (t.Children0 is null ? 1 : ChildCount(t)) : 0;
            }
        }
        catch (TemplateError e) when (!e.IsLimit)
        {
            RecordError(e, node);
            return node.Children0 is LazyChildren { Items: { } items } ? items.Count : 0;
        }

        return 0;
    }

    internal TemplateNode GetChild(TemplateNode node, long index)
    {
        if (node.Children0 is LazyChildren or ListChildren)
        {
            IReadOnlyList<TemplateNode> list = DisplayChildren(node);
            return list[(int)index];
        }

        return GetChildRaw(node, index);
    }

    internal TemplateNode GetChildRaw(TemplateNode node0, long index)
    {
        TemplateNode node = Resolve(node0);
        switch (node.Children0)
        {
            case LazyChildren lc:
                Materialize(node);
                return lc.Items![(int)index];
            case ListChildren l:
                lock (_result.SyncRoot)
                {
                    return l.Items[(int)index];
                }

            case OnDemandChildren od:
                if (index < 0 || index >= od.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return od.Get(index);
            case SequenceChildren s:
                lock (_result.SyncRoot)
                {
                    return s.Items[(int)index];
                }

            case PointerChildren pc when pc.Target is { } t:
                return t.Children0 is null ? t : GetChildRaw(t, index);
        }

        throw new ArgumentOutOfRangeException(nameof(index));
    }

    /// <summary>結果ツリーに並べる子 (<c>inline</c> の構造体はその子を直接並べる。TPL-13)。</summary>
    internal IReadOnlyList<TemplateNode> DisplayChildren(TemplateNode node0)
    {
        TemplateNode node = Resolve(node0);
        List<TemplateNode> items;
        try
        {
            switch (node.Children0)
            {
                case LazyChildren lc:
                    Materialize(node);
                    items = lc.Items ?? [];
                    break;
                case ListChildren l:
                    lock (_result.SyncRoot)
                    {
                        items = [.. l.Items];
                    }

                    break;
                case PointerChildren pc:
                    if (pc.Target is not { } t)
                    {
                        return [];
                    }

                    return t.Children0 is null ? [t] : DisplayChildren(t);
                case SequenceChildren s:
                    lock (_result.SyncRoot)
                    {
                        return [.. s.Items];
                    }

                default:
                    return [];
            }
        }
        catch (TemplateError e) when (!e.IsLimit)
        {
            RecordError(e, node);
            items = node.Children0 is LazyChildren { Items: { } partial } ? partial : [];
        }

        if (!items.Any(i => i.Attributes.Inline))
        {
            return [.. items];
        }

        var result = new List<TemplateNode>();
        foreach (TemplateNode i in items)
        {
            if (i.Attributes.Inline && i.Children0 is not null)
            {
                result.AddRange(DisplayChildren(i));
            }
            else
            {
                result.Add(i);
            }
        }

        return result;
    }

    internal TemplateNode? FindChild(TemplateNode node, string name)
    {
        try
        {
            return MemberOf(Resolve(node), name) ?? DisplayChildren(node).FirstOrDefault(c => c.Name == name);
        }
        catch (TemplateError e) when (!e.IsLimit)
        {
            RecordError(e, node);
            return null;
        }
    }

    internal TemplateNode? PointerTarget(TemplateNode node) => node.Children0 is PointerChildren pc ? pc.Target : null;

    /// <summary>パス (<c>chunks[3].header.width</c>、<c>@pixels.rows[0]</c>) でノードを探す。</summary>
    internal TemplateNode? FindByPath(string path)
    {
        TemplateNode current = _result.Root;
        int i = 0;
        bool first = true;
        while (i < path.Length)
        {
            if (path[i] == '[')
            {
                int close = path.IndexOf(']', i);
                if (close < 0 || !long.TryParse(path.AsSpan(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out long index))
                {
                    return null;
                }

                TemplateNode? byName = FindChild(current, path[i..(close + 1)]);
                if (byName is not null)
                {
                    current = byName;
                }
                else
                {
                    if (index < 0 || index >= ChildCount(current))
                    {
                        return null;
                    }

                    current = GetChildRaw(current, index);
                }

                i = close + 1;
                continue;
            }

            if (path[i] == '.')
            {
                i++;
                continue;
            }

            int end = i;
            while (end < path.Length && path[end] is not ('.' or '['))
            {
                end++;
            }

            string name = path[i..end];
            TemplateNode? next;
            if (first && name.StartsWith('@'))
            {
                next = DisplayChildren(_result.Root).FirstOrDefault(n => n.Kind == TemplateNodeKind.Section && n.Name == name[1..]);
            }
            else
            {
                next = null;
                if (end < path.Length && path[end] == '[' && path.IndexOf(']', end) is int close and > 0)
                {
                    // ループで宣言した同じ名前のパターン (r[3])
                    string full = path[i..(close + 1)];
                    next = DisplayChildren(current).FirstOrDefault(c => c.Name == full);
                    if (next is not null)
                    {
                        end = close + 1;
                    }
                }

                next ??= FindChild(current, name);
            }

            if (next is null)
            {
                return null;
            }

            current = next;
            i = end;
            first = false;
        }

        return ReferenceEquals(current, _result.Root) ? null : current;
    }

    /// <summary>部分木をすべて評価する (TPL-25 の仕様 5)。</summary>
    internal void EvaluateAll(TemplateNode node, CancellationToken token)
    {
        CancellationToken save = _token;
        _token = token;
        try
        {
            var stack = new Stack<TemplateNode>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                CheckCancel();
                TemplateNode n = stack.Pop();
                if (n.Children0 is OnDemandChildren)
                {
                    continue; // 固定長の要素の配列は必要になったときに作る (1 億要素でもメモリを使わない)
                }

                IReadOnlyList<TemplateNode> children = DisplayChildren(n);
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }
        }
        finally
        {
            _token = save;
        }
    }

    // ───────────── 編集 (TPL-22) ─────────────

    internal TemplateEditBlock EditBlockOf(TemplateNode node)
    {
        if (node.Kind == TemplateNodeKind.Computed)
        {
            return TemplateEditBlock.Computed;
        }

        if (node.Kind == TemplateNodeKind.Error)
        {
            return TemplateEditBlock.Error;
        }

        if (node.Section is not null || node.Kind == TemplateNodeKind.Section)
        {
            return TemplateEditBlock.InSection;
        }

        if (node.Attributes.ReadOnly || Attr(node.Type?.Attributes ?? [], "readonly") is not null)
        {
            return TemplateEditBlock.ReadOnlyAttribute;
        }

        if (node.Kind is TemplateNodeKind.Struct or TemplateNodeKind.Union or TemplateNodeKind.Bitfield or TemplateNodeKind.Application
            or TemplateNodeKind.Padding || (node.Kind == TemplateNodeKind.Array && node.Type is not PrimType { IsChar: true }))
        {
            return TemplateEditBlock.NotAValue;
        }

        return TemplateEditBlock.None;
    }

    internal bool EditsRawOnly(TemplateNode node) =>
        Attr(node.AttributeNodes, "read") is not null && Attr(node.AttributeNodes, "write") is null;

    internal TemplateEditResult EncodeEdit(TemplateNode node, string input, bool raw)
    {
        TemplateEditBlock block = EditBlockOf(node);
        if (block != TemplateEditBlock.None)
        {
            return new TemplateEditResult(null, block.ToString());
        }

        try
        {
            return EncodeEditCore(node, input.Trim(), raw || EditsRawOnly(node));
        }
        catch (TemplateError e)
        {
            return new TemplateEditResult(null, e.Diagnostic.Message);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return new TemplateEditResult(null, ex.Message);
        }
    }

    private TemplateEditResult EncodeEditCore(TemplateNode node, string input, bool raw)
    {
        Frame f = node.Context ?? _root;
        if (node.Type is PrimType { IsChar: true } cp && node.Kind == TemplateNodeKind.Array || node.Kind == TemplateNodeKind.String)
        {
            Encoding enc = node.Type is PrimType { Kind: PrimKind.WString or PrimKind.Char16 } ? (node.BigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode)
                : EncodingByName(node.Attributes.Encoding ?? EncodingOf(node));
            byte[] text = enc.GetBytes(input);
            if (text.Length > node.Size)
            {
                return new TemplateEditResult(null, "too long");
            }

            byte[] buf = new byte[node.Size];
            text.CopyTo(buf, 0);
            return new TemplateEditResult(buf, null) { Offset = node.Offset };
        }

        if (node.Kind == TemplateNodeKind.BitfieldMember || node.BitSize is not null)
        {
            Int128 value = ParseIntegerInput(node, input);
            int w = node.BitSize!.Value;
            if (value < 0 || value >= (Int128.One << w))
            {
                return new TemplateEditResult(null, "out of range");
            }

            return EncodeBits(node, (ulong)value);
        }

        Val rawValue;
        if (!raw && Attr(node.AttributeNodes, "write") is { Args.Count: > 0 } write)
        {
            Val input0 = ParseScalarInput(input);
            Val fn = Eval(write.Args[0], f);
            if (fn.Kind != VK.Func)
            {
                throw ErrorAt(write.Location, TemplateMessages.NotCallable, "write");
            }

            rawValue = Deref(Invoke((Callable)fn.O!, [input0], null, f));
        }
        else
        {
            rawValue = node.Type is EnumType et ? ParseEnumInput(et, input) : ParseTypedInput(node, input);
        }

        return new TemplateEditResult(EncodePrim(node, rawValue), null) { Offset = node.Offset };
    }

    private TemplateEditResult EncodeBits(TemplateNode node, ulong value)
    {
        // ビットを含むまとまりを読み、そのビットだけを置き換える
        TemplateNode owner = node.Owner!;
        long unitOffset;
        int nbytes;
        bool big = node.BigEndian;
        if (owner.Kind == TemplateNodeKind.Bitfield)
        {
            unitOffset = owner.Offset;
            nbytes = (int)owner.Size;
        }
        else
        {
            unitOffset = node.Offset;
            nbytes = (int)node.Size;
        }

        Space space = SpaceOfNode(node);
        UInt128 unit = ReadUnsigned(space, unitOffset, nbytes, big);
        int byteFromStart = (int)(node.Offset - unitOffset);
        int shift = big ? ((nbytes - 1 - byteFromStart - ((int)node.Size - 1)) * 8) + node.BitOffset!.Value : (byteFromStart * 8) + node.BitOffset!.Value;
        UInt128 mask = ((UInt128.One << node.BitSize!.Value) - 1) << shift;
        unit = (unit & ~mask) | (((UInt128)value << shift) & mask);
        byte[] buf = new byte[nbytes];
        for (int i = 0; i < nbytes; i++)
        {
            int k = big ? nbytes - 1 - i : i;
            buf[k] = (byte)(unit >> (8 * i));
        }

        return new TemplateEditResult(buf, null) { Offset = unitOffset };
    }

    private static Val ParseScalarInput(string input)
    {
        if (Lexer.TryParseInteger(input.TrimStart('-'), out Int128 i))
        {
            return Val.Int(input.StartsWith('-') ? -i : i);
        }

        if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            return Val.Float(d);
        }

        return input.ToLowerInvariant() switch
        {
            "nan" => Val.Float(double.NaN),
            "inf" or "+inf" => Val.Float(double.PositiveInfinity),
            "-inf" => Val.Float(double.NegativeInfinity),
            "true" => Val.True,
            "false" => Val.False,
            _ => Val.Str(input),
        };
    }

    private static Int128 ParseIntegerInput(TemplateNode node, string input)
    {
        Val v = ParseScalarInput(input);
        return v.Kind switch
        {
            VK.Int or VK.Bool => v.I,
            _ => throw new FormatException("not an integer: " + input),
        };
    }

    private Val ParseEnumInput(EnumType et, string input)
    {
        EnumInfo info = EnumInfoOf(et.Decl);
        if (et.Decl.IsFlags)
        {
            Int128 acc = 0;
            foreach (string part in input.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                (string Name, Int128 Low, Int128 High, string? Doc) m = info.Members.FirstOrDefault(x => x.Name == part);
                acc |= m.Name is not null ? m.Low : ParseIntegerInput(null!, part);
            }

            return Val.Int(acc);
        }

        foreach ((string name, Int128 low, _, _) in info.Members)
        {
            if (name == input)
            {
                return Val.Int(low);
            }
        }

        return Val.Int(ParseIntegerInput(null!, input));
    }

    private Val ParseTypedInput(TemplateNode node, string input)
    {
        if (node.Type is not PrimType p)
        {
            throw new FormatException("not editable");
        }

        switch (p.Kind)
        {
            case PrimKind.Guid:
                return Val.Bytes(Guid.Parse(input).ToByteArray());
            case PrimKind.Uuid:
                return Val.Bytes(Guid.Parse(input).ToByteArray(bigEndian: true));
            case PrimKind.Ipv4:
            case PrimKind.Ipv6:
                return Val.Bytes(IPAddress.Parse(input).GetAddressBytes());
            case PrimKind.Mac:
                return Val.Bytes([.. input.Split(':', '-').Select(x => Convert.ToByte(x, 16))]);
            case PrimKind.Bool:
                return Val.Bool(input is "true" or "1");
        }

        if (p.IsColor && input.StartsWith('#'))
        {
            uint rgb = Convert.ToUInt32(input[1..], 16);
            uint r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
            return p.Kind switch
            {
                PrimKind.Rgb565 => Val.Int(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)),
                PrimKind.Argb1555 => Val.Int(0x8000 | ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)),
                PrimKind.Rgb8 => Val.Bytes([(byte)r, (byte)g, (byte)b]),
                PrimKind.Rgba8 => Val.Bytes([(byte)r, (byte)g, (byte)b, 0xFF]),
                _ => Val.Bytes([(byte)b, (byte)g, (byte)r, 0xFF]),
            };
        }

        if (p.IsDate && p.Kind != PrimKind.OleDate && DateTime.TryParse(input, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime dt))
        {
            return p.Kind switch
            {
                PrimKind.FileTime => Val.Int(dt.ToFileTimeUtc()),
                PrimKind.HfsDate => Val.Int((long)(dt - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds),
                _ => Val.Int((long)(dt - DateTime.UnixEpoch).TotalSeconds),
            };
        }

        return ParseScalarInput(input);
    }

    private byte[] EncodePrim(TemplateNode node, Val v)
    {
        PrimType p = node.Type as PrimType ?? (node.Type as EnumType)?.Base ?? throw new FormatException("not editable");
        if (v.Kind == VK.Bytes)
        {
            byte[] b = (byte[])v.O!;
            if (b.Length != node.Size)
            {
                throw new FormatException("wrong length");
            }

            return b;
        }

        int size = (int)node.Size;
        bool big = node.BigEndian && p.UsesEndian;
        byte[] buf = new byte[size];
        if (p.IsFloat && p.Kind is PrimKind.Float or PrimKind.OleDate)
        {
            double d = v.Kind == VK.Float ? v.F : (double)v.I;
            ulong bits = size switch
            {
                2 => BitConverter.HalfToUInt16Bits((Half)d),
                4 => BitConverter.SingleToUInt32Bits((float)d),
                _ => BitConverter.DoubleToUInt64Bits(d),
            };
            WriteUnsigned(buf, bits, big);
            return buf;
        }

        if (p.Kind is PrimKind.Fixed or PrimKind.UFixed)
        {
            double d = v.Kind == VK.Float ? v.F : (double)v.I;
            WriteUnsigned(buf, (UInt128)(Int128)Math.Round(d * Math.Pow(2, p.FracBits)), big);
            return buf;
        }

        Int128 i = v.Kind switch
        {
            VK.Int or VK.Bool => v.I,
            VK.Float when double.IsFinite(v.F) => (Int128)Math.Round(v.F),
            _ => throw new FormatException("not a number"),
        };
        int bitsCount = size * 8;
        Int128 min = p.IsSigned ? -(Int128.One << (bitsCount - 1)) : 0;
        Int128 max = p.IsSigned ? (Int128.One << (bitsCount - 1)) - 1 : bitsCount >= 127 ? Int128.MaxValue : (Int128.One << bitsCount) - 1;
        if (i < min || i > max)
        {
            throw new OverflowException($"{i} does not fit in {p.Name}");
        }

        WriteUnsigned(buf, (UInt128)i, big);
        return buf;
    }

    private static void WriteUnsigned(byte[] buf, UInt128 value, bool big)
    {
        for (int k = 0; k < buf.Length; k++)
        {
            byte b = (byte)(value >> (8 * k));
            buf[big ? buf.Length - 1 - k : k] = b;
        }
    }

    internal static ulong ReadU64(ReadOnlySpan<byte> b, bool big) => big ? BinaryPrimitives.ReadUInt64BigEndian(b) : BinaryPrimitives.ReadUInt64LittleEndian(b);
}
