using System.Globalization;
using System.Numerics;
using System.Text;
using HexEditor.Core.Hashing;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Runtime;

internal sealed partial class Evaluator
{
    /// <summary>
    /// 標準ライブラリの組み込み関数 (TPL-18 の仕様 2)。データの読み込みと計算だけを行い、ファイル・ネットワーク・時刻・乱数・
    /// 環境変数を使わない (仕様 3、TPL-37)。
    /// </summary>
    private static readonly HashSet<string> Builtins =
    [
        "std::core::print", "std::core::assert", "std::core::warn", "std::core::eof", "std::core::size", "std::core::base_offset",
        "std::core::set_endian", "std::core::get_endian", "std::core::raw", "std::core::len", "std::core::array_index",
        "std::core::template_version",
        "std::mem::read_u8", "std::mem::read_u16", "std::mem::read_u24", "std::mem::read_u32", "std::mem::read_u64",
        "std::mem::read_s8", "std::mem::read_s16", "std::mem::read_s24", "std::mem::read_s32", "std::mem::read_s64",
        "std::mem::read_f32", "std::mem::read_f64", "std::mem::read_string", "std::mem::read_bytes", "std::mem::find",
        "std::mem::find_all", "std::mem::compare",
        "std::bits::read", "std::bits::offset", "std::bits::align", "std::bits::reverse", "std::bits::popcount",
        "std::string::length", "std::string::substr", "std::string::find", "std::string::starts_with", "std::string::ends_with",
        "std::string::to_upper", "std::string::to_lower", "std::string::trim", "std::string::split", "std::string::parse_int",
        "std::string::parse_float", "std::string::from_bytes", "std::string::to_string",
        "std::format::format", "std::format::hex", "std::format::size",
        "std::math::min", "std::math::max", "std::math::abs", "std::math::clamp", "std::math::align_up", "std::math::align_down",
        "std::math::log2", "std::math::pow", "std::math::sqrt", "std::math::floor", "std::math::ceil", "std::math::is_nan",
        "std::time::from_unix", "std::time::from_filetime", "std::time::from_dos", "std::time::to_string",
        "std::hash::crc32", "std::hash::crc16", "std::hash::crc", "std::hash::adler32", "std::hash::md5", "std::hash::sha1",
        "std::hash::sha256",
        "std::compress::inflate", "std::compress::zlib", "std::compress::gzip", "std::compress::brotli", "std::compress::zstd",
        "std::compress::lzma", "std::compress::xz", "std::compress::lz4", "std::compress::bzip2", "std::compress::lzss",
        "std::encoding::base64", "std::encoding::hex", "std::encoding::xor", "std::encoding::rol", "std::encoding::ror",
        "std::encoding::convert",
        "std::section::create", "std::section::copy", "std::section::append", "std::section::size", "std::section::open_in_tab",
        "std::ptr::relative_to", "std::ptr::null_if",
        "std::sys::demangle", "std::sys::file_name", "std::sys::file_extension", "std::sys::detected_mime",
        "std::ksy::io_size", "std::ksy::io_pos", "std::ksy::io_base", "std::ksy::io_eof", "std::ksy::bytes_terminate", "std::ksy::bytes_strip_right",
        "std::ksy::to_s", "std::ksy::to_i", "std::ksy::reverse", "std::ksy::substring", "std::ksy::index",
    ];

    private string? CanonicalBuiltinOrNull(string name)
    {
        if (Builtins.Contains(name))
        {
            return name;
        }

        if (name == "std::format")
        {
            return "std::format::format";
        }

        if (name.StartsWith("std::", StringComparison.Ordinal) && name.IndexOf("::", 5, StringComparison.Ordinal) < 0)
        {
            string core = "std::core::" + name[5..];
            if (Builtins.Contains(core))
            {
                return core;
            }
        }

        if (!name.Contains("::", StringComparison.Ordinal))
        {
            foreach (string[] u in _program.Usings)
            {
                string candidate = string.Join("::", u) + "::" + name;
                if (Builtins.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private bool IsBuiltinFunction(string name) => CanonicalBuiltinOrNull(name) is not null;

    private string CanonicalBuiltin(string name) => CanonicalBuiltinOrNull(name) ?? name;

    private Val Arg(CallExpr c, Frame f, int i)
    {
        if (i >= c.Args.Count)
        {
            throw ErrorAt(c.Location, TemplateMessages.ArgumentCount, ((NameExpr)c.Callee).Text, (i + 1).ToString(CultureInfo.InvariantCulture));
        }

        return Eval(c.Args[i], f);
    }

    private Int128 IntArg(CallExpr c, Frame f, int i) => ToInt(Arg(c, f, i), c.Args[i].Location);

    private long LongArg(CallExpr c, Frame f, int i)
    {
        Int128 v = IntArg(c, f, i);
        return v > long.MaxValue ? long.MaxValue : v < long.MinValue ? long.MinValue : (long)v;
    }

    private string StrArg(CallExpr c, Frame f, int i)
    {
        Val v = Deref(Arg(c, f, i));
        if (v.Kind != VK.Str)
        {
            throw ErrorAt(c.Args[i].Location, TemplateMessages.TypeMismatch, KindName(v) + " → string");
        }

        return v.AsString;
    }

    private bool OptEndian(CallExpr c, Frame f, int i)
    {
        if (i >= c.Args.Count)
        {
            return f.BigEndian;
        }

        Val v = Arg(c, f, i);
        return v.Kind == VK.Endian ? v.I != 0 : v.Kind == VK.Str ? v.AsString == "big"
            : throw ErrorAt(c.Args[i].Location, TemplateMessages.TypeMismatch, "endian");
    }

    private TemplateSection SectionArg(CallExpr c, Frame f, int i)
    {
        Val v = Arg(c, f, i);
        return v.Kind == VK.Section ? (TemplateSection)v.O! : throw ErrorAt(c.Args[i].Location, TemplateMessages.TypeMismatch, KindName(v) + " → section");
    }

    private Val CallBuiltin(string name, CallExpr c, Frame f)
    {
        Step();
        switch (name)
        {
            // ── std.core
            case "std::core::print":
                Print(string.Join(" ", c.Args.Select((_, i) => ToDisplayString(Deref(Arg(c, f, i))))));
                return Val.Void;
            case "std::core::assert":
                if (!Truthy(Arg(c, f, 0)))
                {
                    string message = c.Args.Count > 1 ? ToDisplayString(Deref(Arg(c, f, 1))) : "assert";
                    throw ErrorAt(c.Location, TemplateMessages.AssertFailed, message);
                }

                return Val.Void;
            case "std::core::warn":
            {
                string message = ToDisplayString(Deref(Arg(c, f, 0)));
                Report(new TemplateDiagnostic(TemplateSeverity.Warning, TemplateMessages.UserWarning, [message], c.Location, _pos, null,
                    [.. _callStack]), null);
                return Val.Void;
            }

            case "std::core::eof":
                return Val.Bool(_pos >= Math.Min(_space.Length, f.WindowEnd));
            case "std::core::size":
                return Val.Int(Math.Min(_space.Length, f.WindowEnd));
            case "std::core::base_offset":
                return Val.Int(_options.StartOffset);
            case "std::core::set_endian":
            {
                Val v = Arg(c, f, 0);
                f.BigEndian = v.Kind == VK.Endian ? v.I != 0 : v.Kind == VK.Str && v.AsString == "big";
                return Val.Void;
            }

            case "std::core::get_endian":
                return Val.Endian(f.BigEndian);
            case "std::core::raw":
            {
                Val v = Arg(c, f, 0);
                return v.Kind == VK.Node ? NodeValue(v.AsNode, converted: false) : v;
            }

            case "std::core::len":
            {
                Val v = Arg(c, f, 0);
                return v.Kind switch
                {
                    VK.Node => Val.Int(ChildCount(v.AsNode)),
                    VK.Str => Val.Int(v.AsString.Length),
                    VK.Bytes => Val.Int(((byte[])v.O!).Length),
                    VK.List => Val.Int(((List<Val>)v.O!).Count),
                    _ => throw ErrorAt(c.Location, TemplateMessages.TypeMismatch, KindName(v)),
                };
            }

            case "std::core::array_index":
                for (Frame? fr = f; fr is not null; fr = fr.Parent)
                {
                    if (fr.ArrayIndex >= 0)
                    {
                        return Val.Int(fr.ArrayIndex);
                    }
                }

                return Val.Int(-1);
            case "std::core::template_version":
                return Val.Str(_program.Metadata.Version ?? string.Empty);

            // ── std.mem
            case "std::mem::read_u8": return MemRead(c, f, 1, false, false);
            case "std::mem::read_u16": return MemRead(c, f, 2, false, false);
            case "std::mem::read_u24": return MemRead(c, f, 3, false, false);
            case "std::mem::read_u32": return MemRead(c, f, 4, false, false);
            case "std::mem::read_u64": return MemRead(c, f, 8, false, false);
            case "std::mem::read_s8": return MemRead(c, f, 1, true, false);
            case "std::mem::read_s16": return MemRead(c, f, 2, true, false);
            case "std::mem::read_s24": return MemRead(c, f, 3, true, false);
            case "std::mem::read_s32": return MemRead(c, f, 4, true, false);
            case "std::mem::read_s64": return MemRead(c, f, 8, true, false);
            case "std::mem::read_f32": return MemRead(c, f, 4, false, true);
            case "std::mem::read_f64": return MemRead(c, f, 8, false, true);
            case "std::mem::read_string":
            {
                long off = LongArg(c, f, 0);
                long max = LongArg(c, f, 1);
                Encoding enc = EncodingByName(c.Args.Count > 2 ? StrArg(c, f, 2) : f.Encoding);
                long end = Math.Min(_space.Length, off + Math.Max(0, max));
                if (off < 0 || off > _space.Length)
                {
                    throw OutOfRange("read_string", off);
                }

                long len = 0;
                byte[] buf = new byte[4096];
                while (off + len < end)
                {
                    int n = (int)Math.Min(buf.Length, end - off - len);
                    _space.Read(off + len, buf.AsSpan(0, n), this, "read_string");
                    int z = buf.AsSpan(0, n).IndexOf((byte)0);
                    if (z >= 0)
                    {
                        len += z;
                        break;
                    }

                    len += n;
                }

                return Val.Str(DecodeString(_space, off, len, enc, "read_string"));
            }

            case "std::mem::read_bytes":
            {
                long off = LongArg(c, f, 0);
                long len = LongArg(c, f, 1);
                if (len < 0 || len > 64 * 1024 * 1024)
                {
                    throw ErrorAt(c.Location, TemplateMessages.InvalidArgument, "read_bytes", len.ToString(CultureInfo.InvariantCulture));
                }

                CheckStringMemory(len / 2);
                byte[] buf = new byte[len];
                _space.Read(off, buf, this, "read_bytes");
                return Val.Bytes(buf);
            }

            case "std::mem::find":
            {
                (byte[] pattern, byte[]? mask) = PatternArg(c, f, 0);
                long start = c.Args.Count > 1 ? LongArg(c, f, 1) : 0;
                long end = c.Args.Count > 2 ? LongArg(c, f, 2) : _space.Length;
                return Val.Int(Find(pattern, mask, start, end));
            }

            case "std::mem::find_all":
            {
                (byte[] pattern, byte[]? mask) = PatternArg(c, f, 0);
                long start = c.Args.Count > 1 ? LongArg(c, f, 1) : 0;
                long end = c.Args.Count > 2 ? LongArg(c, f, 2) : _space.Length;
                long max = c.Args.Count > 3 ? LongArg(c, f, 3) : 10_000;
                var list = new List<Val>();
                long at = start;
                while (list.Count < max)
                {
                    long hit = Find(pattern, mask, at, end);
                    if (hit < 0)
                    {
                        break;
                    }

                    list.Add(Val.Int(hit));
                    MemoryCounter += 32;
                    at = hit + 1;
                }

                return Val.List(list);
            }

            case "std::mem::compare":
            {
                long off = LongArg(c, f, 0);
                (byte[] pattern, byte[]? mask) = PatternArg(c, f, 1);
                if (off < 0 || off + pattern.Length > _space.Length)
                {
                    return Val.False;
                }

                byte[] buf = new byte[pattern.Length];
                _space.Read(off, buf, this, "compare");
                return Val.Bool(mask is null ? buf.AsSpan().SequenceEqual(pattern) : HexPattern.Matches(buf, pattern, mask));
            }

            // ── std.bits
            case "std::bits::read":
            {
                int n = (int)LongArg(c, f, 0);
                if (n is < 1 or > 64)
                {
                    throw ErrorAt(c.Location, TemplateMessages.BitfieldTooWide, n.ToString(CultureInfo.InvariantCulture));
                }

                if (f.BitPos < 0)
                {
                    f.BitPos = _pos * 8;
                }

                ulong v = ReadBits(_space, f.BitPos, n, f.MsbFirst);
                f.BitPos += n;
                _pos = f.BitPos / 8;
                long end = (f.BitPos + 7) / 8;
                f.MaxEnd = f.HasPattern ? Math.Max(f.MaxEnd, end) : end;
                f.HasPattern = true;
                return Val.Int(v);
            }

            case "std::bits::offset":
                return Val.Int(f.BitPos < 0 ? 0 : f.BitPos % 8);
            case "std::bits::align":
                AlignToByte(f);
                return Val.Void;
            case "std::bits::reverse":
            {
                ulong v = (ulong)IntArg(c, f, 0);
                int n = (int)LongArg(c, f, 1);
                ulong r = 0;
                for (int i = 0; i < n; i++)
                {
                    r = (r << 1) | ((v >> i) & 1);
                }

                return Val.Int(r);
            }

            case "std::bits::popcount":
                return Val.Int(BitOperations.PopCount((ulong)IntArg(c, f, 0)));

            // ── std.string
            case "std::string::length":
                return Val.Int(StrArg(c, f, 0).Length);
            case "std::string::substr":
            {
                string s = StrArg(c, f, 0);
                int start = (int)Math.Clamp(LongArg(c, f, 1), 0, s.Length);
                int len = c.Args.Count > 2 ? (int)Math.Clamp(LongArg(c, f, 2), 0, s.Length - start) : s.Length - start;
                return Val.Str(s.Substring(start, len));
            }

            case "std::string::find":
                return Val.Int(StrArg(c, f, 0).IndexOf(StrArg(c, f, 1), StringComparison.Ordinal));
            case "std::string::starts_with":
                return Val.Bool(StrArg(c, f, 0).StartsWith(StrArg(c, f, 1), StringComparison.Ordinal));
            case "std::string::ends_with":
                return Val.Bool(StrArg(c, f, 0).EndsWith(StrArg(c, f, 1), StringComparison.Ordinal));
            case "std::string::to_upper":
                return Val.Str(StrArg(c, f, 0).ToUpperInvariant());
            case "std::string::to_lower":
                return Val.Str(StrArg(c, f, 0).ToLowerInvariant());
            case "std::string::trim":
                return Val.Str(StrArg(c, f, 0).Trim());
            case "std::string::split":
                return Val.List([.. StrArg(c, f, 0).Split(StrArg(c, f, 1)).Select(Val.Str)]);
            case "std::string::parse_int":
            {
                string s = StrArg(c, f, 0).Trim();
                int radix = c.Args.Count > 1 ? (int)LongArg(c, f, 1) : 10;
                bool neg = s.StartsWith('-');
                if (neg)
                {
                    s = s[1..];
                }

                Int128 acc = 0;
                foreach (char ch in s)
                {
                    int d = char.IsAsciiDigit(ch) ? ch - '0' : char.IsAsciiLetter(ch) ? char.ToLowerInvariant(ch) - 'a' + 10 : 99;
                    if (d >= radix)
                    {
                        throw ErrorAt(c.Location, TemplateMessages.InvalidArgument, "parse_int", s);
                    }

                    acc = (acc * radix) + d;
                }

                return Val.Int(neg ? -acc : acc);
            }

            case "std::string::parse_float":
                return double.TryParse(StrArg(c, f, 0), NumberStyles.Float, CultureInfo.InvariantCulture, out double pf)
                    ? Val.Float(pf) : throw ErrorAt(c.Location, TemplateMessages.InvalidArgument, "parse_float", StrArg(c, f, 0));
            case "std::string::from_bytes":
            {
                byte[] b = BytesOf(Arg(c, f, 0), c.Location);
                return Val.Str(EncodingByName(c.Args.Count > 1 ? StrArg(c, f, 1) : f.Encoding).GetString(b));
            }

            case "std::string::to_string":
                return Val.Str(ToDisplayString(Deref(Arg(c, f, 0))));

            // ── std.format
            case "std::format::format":
            {
                string fmt = StrArg(c, f, 0);
                var args = new Val[c.Args.Count - 1];
                for (int i = 1; i < c.Args.Count; i++)
                {
                    args[i - 1] = Deref(Arg(c, f, i));
                }

                return Val.Str(FormatString(fmt, args, c.Location));
            }

            case "std::format::hex":
            {
                Int128 v = IntArg(c, f, 0);
                int digits = c.Args.Count > 1 ? (int)LongArg(c, f, 1) : 0;
                string s = (v < 0 ? (UInt128)v : (UInt128)v).ToString("X", CultureInfo.InvariantCulture).PadLeft(digits, '0');
                return Val.Str("0x" + s);
            }

            case "std::format::size":
                return Val.Str(FormatSize((long)IntArg(c, f, 0), CultureInfo.InvariantCulture));

            // ── std.math
            case "std::math::min":
            case "std::math::max":
            {
                Val a = Deref(Arg(c, f, 0));
                Val b = Deref(Arg(c, f, 1));
                bool less = Compare(a, b, c) <= 0;
                return name.EndsWith("min", StringComparison.Ordinal) ? (less ? a : b) : (less ? b : a);
            }

            case "std::math::abs":
            {
                Val v = Deref(Arg(c, f, 0));
                return v.Kind == VK.Float ? Val.Float(Math.Abs(v.F)) : Val.Int(Int128.Abs(ToInt(v, c.Location)));
            }

            case "std::math::clamp":
            {
                Val v = Deref(Arg(c, f, 0));
                Val lo = Deref(Arg(c, f, 1));
                Val hi = Deref(Arg(c, f, 2));
                return Compare(v, lo, c) < 0 ? lo : Compare(v, hi, c) > 0 ? hi : v;
            }

            case "std::math::align_up":
            {
                Int128 v = IntArg(c, f, 0);
                Int128 a = IntArg(c, f, 1);
                return a <= 0 ? Val.Int(v) : Val.Int((v + a - 1) / a * a);
            }

            case "std::math::align_down":
            {
                Int128 v = IntArg(c, f, 0);
                Int128 a = IntArg(c, f, 1);
                return a <= 0 ? Val.Int(v) : Val.Int(v / a * a);
            }

            case "std::math::log2":
            {
                Val v = Deref(Arg(c, f, 0));
                return v.Kind == VK.Float ? Val.Float(Math.Log2(v.F)) : Val.Int(v.I <= 0 ? -1 : 127 - Int128.LeadingZeroCount(v.I));
            }

            case "std::math::pow":
            {
                Val a = Deref(Arg(c, f, 0));
                Val b = Deref(Arg(c, f, 1));
                if (a.Kind == VK.Int && b.Kind == VK.Int && b.I >= 0 && b.I < 128)
                {
                    Int128 r = 1;
                    for (int i = 0; i < (int)b.I; i++)
                    {
                        r *= a.I;
                    }

                    return Val.Int(r);
                }

                return Val.Float(Math.Pow(ToDouble(a, c.Location), ToDouble(b, c.Location)));
            }

            case "std::math::sqrt":
                return Val.Float(Math.Sqrt(ToDouble(Arg(c, f, 0), c.Location)));
            case "std::math::floor":
                return Val.Float(Math.Floor(ToDouble(Arg(c, f, 0), c.Location)));
            case "std::math::ceil":
                return Val.Float(Math.Ceiling(ToDouble(Arg(c, f, 0), c.Location)));
            case "std::math::is_nan":
                return Val.Bool(double.IsNaN(ToDouble(Arg(c, f, 0), c.Location)));

            // ── std.time (UTC 固定)
            case "std::time::from_unix":
                return Val.DateTime(DateTime.UnixEpoch.AddSeconds((double)IntArg(c, f, 0)).Ticks);
            case "std::time::from_filetime":
                return Val.DateTime(DateTime.FromFileTimeUtc((long)IntArg(c, f, 0)).Ticks);
            case "std::time::from_dos":
                return DosDate((int)IntArg(c, f, 0), (int)IntArg(c, f, 1)) is { } dd ? Val.DateTime(dd.Ticks) : Val.Null;
            case "std::time::to_string":
            {
                Val v = Deref(Arg(c, f, 0));
                string fmt = c.Args.Count > 1 ? StrArg(c, f, 1) : "yyyy-MM-dd HH:mm:ss";
                return Val.Str(new DateTime((long)v.I, DateTimeKind.Utc).ToString(fmt, CultureInfo.InvariantCulture));
            }

            // ── std.hash (06 の ANA-19 の実装を使う)
            case "std::hash::crc32": return Hash(c, f, "crc32", 2, integer: true);
            case "std::hash::crc16": return Hash(c, f, "crc16-arc", 2, integer: true);
            case "std::hash::adler32": return Hash(c, f, "adler32", 2, integer: true);
            case "std::hash::md5": return Hash(c, f, "md5", 2, integer: false);
            case "std::hash::sha1": return Hash(c, f, "sha1", 2, integer: false);
            case "std::hash::sha256": return Hash(c, f, "sha256", 2, integer: false);
            case "std::hash::crc": return Hash(c, f, StrArg(c, f, 2), 2, integer: true);

            // ── std.compress / std.encoding / std.section (TPL-15)
            case "std::compress::inflate":
            case "std::compress::zlib":
            case "std::compress::gzip":
            case "std::compress::brotli":
                return Decompress(name[(name.LastIndexOf(':') + 1)..], c, f);
            case "std::compress::zstd":
            case "std::compress::lzma":
            case "std::compress::xz":
            case "std::compress::lz4":
            case "std::compress::bzip2":
            case "std::compress::lzss":
                throw ErrorAt(c.Location, TemplateMessages.UnsupportedCompression, name[(name.LastIndexOf(':') + 1)..]);
            case "std::encoding::base64":
            case "std::encoding::hex":
            case "std::encoding::xor":
            case "std::encoding::rol":
            case "std::encoding::ror":
            case "std::encoding::convert":
                return Transform(name[(name.LastIndexOf(':') + 1)..], c, f);
            case "std::section::create":
            {
                string sname = c.Args.Count > 0 ? StrArg(c, f, 0) : "section";
                var section = new TemplateSection(sname, _options.TempDirectory) { Index = Sections.Count };
                lock (_result.SyncRoot)
                {
                    Sections.Add(section);
                }

                return Val.Section(section);
            }

            case "std::section::copy":
            {
                TemplateSection s = SectionArg(c, f, 0);
                long off = LongArg(c, f, 1);
                long len = LongArg(c, f, 2);
                Space src = _space;
                if (off < 0 || len < 0 || off > src.Length - len)
                {
                    throw OutOfRange("std::section::copy", off);
                }

                s.PendingProducers.Add(() => CopyInto(s, src, off, len));
                return Val.Void;
            }

            case "std::section::append":
            {
                TemplateSection s = SectionArg(c, f, 0);
                byte[] data = BytesOf(Deref(Arg(c, f, 1)), c.Location);
                s.PendingProducers.Add(() => AppendLimited(s, data, "append"));
                return Val.Void;
            }

            case "std::section::size":
            {
                TemplateSection s = SectionArg(c, f, 0);
                ProduceSection(s);
                return Val.Int(s.Length);
            }

            case "std::section::open_in_tab":
                return Val.Void; // 結果ツリーからの操作を促すだけ

            // ── std.ptr
            case "std::ptr::relative_to":
            {
                Int128 b = IntArg(c, f, 0);
                return Val.Func(new NativeFunction("relative_to", a => Val.Int(ToInt(a[0], c.Location) + b)));
            }

            case "std::ptr::null_if":
            {
                Int128 x = IntArg(c, f, 0);
                return Val.Func(new NativeFunction("null_if", a => ToInt(a[0], c.Location) == x ? Val.Null : Val.Int(ToInt(a[0], c.Location))));
            }

            // ── std.sys (ファイル名だけ。パスは返さない)
            case "std::sys::demangle":
                return Val.Str(StrArg(c, f, 0));
            case "std::sys::file_name":
                return Val.Str(Path.GetFileName(_result.Data.FileName ?? string.Empty));
            case "std::sys::file_extension":
                return Val.Str(Path.GetExtension(_result.Data.FileName ?? string.Empty).TrimStart('.'));
            case "std::sys::detected_mime":
                return Val.Str(_result.Data.DetectedMime ?? string.Empty);

            // ── .ksy の式言語の補助 (TPL-33)
            case "std::ksy::io_size":
                return Val.Int(Math.Min(_space.Length, f.WindowEnd) - f.IoBase);
            case "std::ksy::io_base":
                return Val.Int(f.IoBase);
            case "std::ksy::io_pos":
                return Val.Int(_pos - f.IoBase);
            case "std::ksy::io_eof":
                return Val.Bool(_pos >= Math.Min(_space.Length, f.WindowEnd));
            case "std::ksy::to_s":
            {
                Val v = Arg(c, f, 0);
                if (v.Kind == VK.Node && v.AsNode.Kind == TemplateNodeKind.Array || v.Kind == VK.Bytes)
                {
                    byte[] b = BytesOf(v, c.Location);
                    return Val.Str(EncodingByName(c.Args.Count > 1 ? StrArg(c, f, 1) : "ascii").GetString(b));
                }

                return Val.Str(ToDisplayString(Deref(v)));
            }

            case "std::ksy::to_i":
            {
                Val v = Deref(Arg(c, f, 0));
                if (v.Kind == VK.Str)
                {
                    int radix = c.Args.Count > 1 ? (int)LongArg(c, f, 1) : 10;
                    return Val.Int(Convert.ToInt64(v.AsString, radix));
                }

                return Val.Int(ToInt(v, c.Location));
            }

            case "std::ksy::reverse":
            {
                Val v = Deref(Arg(c, f, 0));
                return Val.Str(new string(v.AsString.Reverse().ToArray()));
            }

            case "std::ksy::substring":
            {
                string s = StrArg(c, f, 0);
                int a = (int)Math.Clamp(LongArg(c, f, 1), 0, s.Length);
                int b = (int)Math.Clamp(LongArg(c, f, 2), a, s.Length);
                return Val.Str(s[a..b]);
            }

            case "std::ksy::index":
                return Val.Int(f.ArrayIndex);
            case "std::ksy::bytes_terminate":
            case "std::ksy::bytes_strip_right":
            {
                byte[] b = BytesOf(Arg(c, f, 0), c.Location);
                byte t = (byte)IntArg(c, f, 1);
                if (name.EndsWith("terminate", StringComparison.Ordinal))
                {
                    int i = Array.IndexOf(b, t);
                    bool include = c.Args.Count > 2 && Truthy(Arg(c, f, 2));
                    return Val.Bytes(i < 0 ? b : b[..(include ? i + 1 : i)]);
                }

                int end = b.Length;
                while (end > 0 && b[end - 1] == t)
                {
                    end--;
                }

                return Val.Bytes(b[..end]);
            }
        }

        throw ErrorAt(c.Location, TemplateMessages.Undefined, name, string.Empty);
    }

    private Val MemRead(CallExpr c, Frame f, int size, bool signed, bool isFloat)
    {
        long off = LongArg(c, f, 0);
        bool big = OptEndian(c, f, 1);
        string fname = ((NameExpr)c.Callee).Last;
        if (off < 0 || off > _space.Length - size)
        {
            throw OutOfRange(fname, off);
        }

        _touched = off;
        UInt128 raw = ReadUnsigned(_space, off, size, big);
        if (isFloat)
        {
            return size == 4 ? Val.Float(BitConverter.UInt32BitsToSingle((uint)raw)) : Val.Float(BitConverter.UInt64BitsToDouble((ulong)raw));
        }

        return Val.Int(signed ? Truncate((Int128)raw, size * 8, true) : (Int128)raw);
    }

    private (byte[] Pattern, byte[]? Mask) PatternArg(CallExpr c, Frame f, int i)
    {
        Val v = Arg(c, f, i);
        if (v.Kind == VK.Mask)
        {
            (byte[] b, byte[] m) = ((byte[], byte[]))v.O!;
            return (b, m.All(x => x == 0xFF) ? null : m);
        }

        return (BytesOf(Deref(v), c.Location), null);
    }

    /// <summary>
    /// 範囲の中のバイト列を探す。読んだバイト数 / 1,024 のステップを消費したとみなし、キャンセルを確認する (TPL-18 の「巨大ファイル」)。
    /// </summary>
    private long Find(byte[] pattern, byte[]? mask, long start, long end)
    {
        end = Math.Min(end, _space.Length);
        start = Math.Max(0, start);
        if (pattern.Length == 0 || end - start < pattern.Length)
        {
            return -1;
        }

        const int Chunk = 1024 * 1024;
        byte[] buf = new byte[Chunk + pattern.Length];
        long pos = start;
        while (pos <= end - pattern.Length)
        {
            int n = (int)Math.Min(buf.Length, end - pos);
            _space.ReadDirect(pos, buf.AsSpan(0, n), this, "find");
            ConsumeSteps(n / 1024);
            Span<byte> window = buf.AsSpan(0, n);
            int hit;
            if (mask is null)
            {
                hit = window.IndexOf(pattern);
            }
            else
            {
                hit = -1;
                for (int k = 0; k + pattern.Length <= n; k++)
                {
                    if (HexPattern.Matches(window[k..], pattern, mask))
                    {
                        hit = k;
                        break;
                    }
                }
            }

            if (hit >= 0)
            {
                return pos + hit;
            }

            if (n < pattern.Length)
            {
                break;
            }

            pos += n - pattern.Length + 1;
        }

        return -1;
    }

    private Val Hash(CallExpr c, Frame f, string algorithm, int argCount, bool integer)
    {
        long off = LongArg(c, f, 0);
        long len = LongArg(c, f, 1);
        const long Limit = 256L * 1024 * 1024;
        if (len > Limit)
        {
            throw ErrorAt(c.Location, TemplateMessages.HashTooLong, len.ToString(CultureInfo.InvariantCulture));
        }

        if (off < 0 || len < 0 || off > _space.Length - len)
        {
            throw OutOfRange("std::hash", off);
        }

        HashAlgorithmInfo info = HashCatalog.Find(algorithm)
            ?? throw ErrorAt(c.Location, TemplateMessages.InvalidArgument, "std::hash::crc", algorithm);
        IHasher hasher = info.CreateHasher();
        byte[] buf = new byte[Math.Min(len, 1024 * 1024)];
        for (long p = 0; p < len; p += buf.Length)
        {
            int n = (int)Math.Min(buf.Length, len - p);
            _space.ReadDirect(off + p, buf.AsSpan(0, n), this, "std::hash");
            hasher.Append(buf.AsSpan(0, n));
            ConsumeSteps(n / 1024);
        }

        byte[] result = hasher.Finish();
        if (!integer)
        {
            return Val.Bytes(result);
        }

        UInt128 v = 0;
        foreach (byte b in result)
        {
            v = (v << 8) | b;
        }

        return Val.Int((Int128)v);
    }

    /// <summary><c>{}</c>、<c>{:x}</c>、<c>{:08X}</c>、<c>{:.3f}</c> の書式 (TPL-14 の仕様 3。地域設定に依存しない)。</summary>
    private string FormatString(string fmt, Val[] args, TemplateSourceLocation at)
    {
        var sb = new StringBuilder();
        int next = 0;
        for (int i = 0; i < fmt.Length; i++)
        {
            char ch = fmt[i];
            if (ch == '{' && i + 1 < fmt.Length && fmt[i + 1] == '{')
            {
                sb.Append('{');
                i++;
                continue;
            }

            if (ch == '}' && i + 1 < fmt.Length && fmt[i + 1] == '}')
            {
                sb.Append('}');
                i++;
                continue;
            }

            if (ch != '{')
            {
                sb.Append(ch);
                continue;
            }

            int close = fmt.IndexOf('}', i);
            if (close < 0)
            {
                throw ErrorAt(at, TemplateMessages.InvalidArgument, "format", fmt);
            }

            string spec = fmt[(i + 1)..close];
            i = close;
            int colon = spec.IndexOf(':');
            string indexPart = colon < 0 ? spec : spec[..colon];
            string format = colon < 0 ? string.Empty : spec[(colon + 1)..];
            int index = indexPart.Length > 0 ? int.Parse(indexPart, CultureInfo.InvariantCulture) : next++;
            if (index < 0 || index >= args.Length)
            {
                throw ErrorAt(at, TemplateMessages.InvalidArgument, "format", "missing argument " + index.ToString(CultureInfo.InvariantCulture));
            }

            string text = FormatOne(args[index], format, at);
            CheckStringMemory(sb.Length + text.Length);
            sb.Append(text);
        }

        return sb.ToString();
    }

    private string FormatOne(Val v, string spec, TemplateSourceLocation at)
    {
        // [[fill]align][0][width][.precision][type]
        char fill = ' ';
        char align = '\0';
        int p = 0;
        if (spec.Length >= 2 && spec[1] is '<' or '>' or '^')
        {
            fill = spec[0];
            align = spec[1];
            p = 2;
        }
        else if (spec.Length >= 1 && spec[0] is '<' or '>' or '^')
        {
            align = spec[0];
            p = 1;
        }

        bool zero = false;
        if (p < spec.Length && spec[p] == '0')
        {
            zero = true;
            p++;
        }

        int width = 0;
        while (p < spec.Length && char.IsAsciiDigit(spec[p]))
        {
            width = (width * 10) + (spec[p] - '0');
            p++;
        }

        int precision = -1;
        if (p < spec.Length && spec[p] == '.')
        {
            p++;
            precision = 0;
            while (p < spec.Length && char.IsAsciiDigit(spec[p]))
            {
                precision = (precision * 10) + (spec[p] - '0');
                p++;
            }
        }

        char type = p < spec.Length ? spec[p] : '\0';
        string body;
        bool negative = false;
        switch (type)
        {
            case 'x':
            case 'X':
            case 'o':
            case 'b':
            case 'd':
            {
                Int128 i = ToInt(v, at);
                negative = i < 0 && type == 'd';
                UInt128 u = type == 'd' ? (UInt128)Int128.Abs(i) : (UInt128)i;
                body = type switch
                {
                    'x' => u.ToString("x", CultureInfo.InvariantCulture),
                    'X' => u.ToString("X", CultureInfo.InvariantCulture),
                    'o' => ToRadix((Int128)u, 8),
                    'b' => ToRadix((Int128)u, 2),
                    _ => u.ToString(CultureInfo.InvariantCulture),
                };
                break;
            }

            case 'f':
            case 'e':
            case 'g':
            {
                double d = ToDouble(v, at);
                negative = d < 0;
                body = Math.Abs(d).ToString(type switch
                {
                    'f' => "F" + (precision < 0 ? 6 : precision).ToString(CultureInfo.InvariantCulture),
                    'e' => "e" + (precision < 0 ? 6 : precision).ToString(CultureInfo.InvariantCulture),
                    _ => precision < 0 ? "R" : "G" + precision.ToString(CultureInfo.InvariantCulture),
                }, CultureInfo.InvariantCulture);
                break;
            }

            case 'c':
                body = CharDisplay(ToInt(v, at));
                break;
            default:
                if (precision >= 0 && v.Kind == VK.Float)
                {
                    negative = v.F < 0;
                    body = Math.Abs(v.F).ToString("F" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                }
                else
                {
                    body = v.Kind switch
                    {
                        VK.Float => FormatDouble(v.F),
                        VK.Bool => v.I != 0 ? "true" : "false",
                        _ => ToDisplayString(v),
                    };
                    if (precision >= 0 && v.Kind == VK.Str && body.Length > precision)
                    {
                        body = body[..precision];
                    }
                }

                break;
        }

        string sign = negative ? "-" : string.Empty;
        if (zero && align == '\0')
        {
            return sign + body.PadLeft(Math.Max(0, width - sign.Length), '0');
        }

        string s = sign + body;
        if (s.Length >= width)
        {
            return s;
        }

        int pad = width - s.Length;
        bool numeric = v.Kind is VK.Int or VK.Float;
        return (align == '\0' ? (numeric ? '>' : '<') : align) switch
        {
            '<' => s + new string(fill, pad),
            '^' => new string(fill, pad / 2) + s + new string(fill, pad - (pad / 2)),
            _ => new string(fill, pad) + s,
        };
    }
}
