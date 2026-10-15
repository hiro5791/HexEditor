using System.Globalization;
using System.Text;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Templates.Runtime;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Kaitai;

/// <summary>
/// Kaitai Struct (.ksy) を HXT のテキストに変換する (TPL-33)。変換したテキストを HXT と同じ構文解析・評価で実行するため、
/// 結果ツリー・自動適用・エクスポートは HXT と同じに扱える。「.ksy を HXT に変換」(仕様 4) はこのテキストを保存する。
/// 変換できない部分は <c>// HXT: …</c> のコメントで印を付け、評価時のエラー (その型・そのフィールドだけ) にする。
/// </summary>
internal static class KsyConverter
{
    private static readonly HashSet<string> HxtReserved =
    [
        "struct", "union", "enum", "flags", "bitfield", "fn", "return", "if", "else", "switch", "case", "default", "for", "while", "do",
        "break", "continue", "local", "const", "import", "namespace", "using", "le", "be", "in", "section", "true", "false", "null",
        "this", "parent", "last", "sizeof", "addressof", "endof", "typeof", "until", "eof", "extern", "padding", "auto", "ref",
    ];

    public static string? Convert(byte[] bytes, string path, DiagnosticBag diags, out string? rootType)
    {
        rootType = null;
        string? text = TemplateLoader.DecodeUtf8(bytes, path, diags);
        if (text is null)
        {
            return null;
        }

        YamlNode root;
        try
        {
            root = YamlParser.Parse(text);
        }
        catch (YamlException ex)
        {
            diags.Error(ex.Code, new TemplateSourceLocation(path, ex.Line, ex.Column), ex.Args);
            return null;
        }

        if (root is not YamlMapping spec)
        {
            diags.Error(TemplateMessages.KsyInvalid, new TemplateSourceLocation(path, root.Line, root.Column), "the top level must be a mapping");
            return null;
        }

        try
        {
            var conv = new Converter(path, diags);
            string hxt = conv.Run(spec);
            rootType = conv.RootName;
            return hxt;
        }
        catch (KsyError ex)
        {
            diags.Error(TemplateMessages.KsyInvalid, new TemplateSourceLocation(path, ex.Line, ex.Column), ex.Message);
            return null;
        }
    }

    private sealed class KsyError(string message, YamlNode? at) : Exception(message)
    {
        public int Line { get; } = at?.Line ?? 1;

        public int Column { get; } = at?.Column ?? 1;
    }

    /// <summary>型・列挙型の名前の範囲 (.ksy の types の入れ子)。</summary>
    private sealed class TypeScope
    {
        public required string HxtName { get; init; }

        public TypeScope? Parent { get; init; }

        public Dictionary<string, TypeScope> Types { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Enums { get; } = new(StringComparer.Ordinal);

        public YamlMapping? Spec { get; init; }
    }

    private sealed class Converter(string path, DiagnosticBag diags)
    {
        private readonly StringBuilder _out = new();
        private string _encoding = "ascii";
        private YamlNode? _endianSwitch;

        public string RootName { get; private set; } = "ksy";

        public string Run(YamlMapping spec)
        {
            var meta = spec["meta"] as YamlMapping;
            string id = Scalar(meta?["id"]) ?? Path.GetFileNameWithoutExtension(path);
            RootName = TypeName(id);
            if (Scalar(meta?["title"]) is { } title)
            {
                Line($"#pragma name \"{Esc(title)}\"");
            }
            else
            {
                Line($"#pragma name \"{Esc(id)}\"");
            }

            switch (meta?["endian"])
            {
                case YamlScalar { Value: "le" }:
                    Line("#pragma endian little");
                    break;
                case YamlScalar { Value: "be" }:
                    Line("#pragma endian big");
                    break;
                case YamlMapping sw:
                    _endianSwitch = sw;
                    break;
            }

            Line(Scalar(meta?["bit-endian"]) == "le" ? "#pragma bitfield_order lsb_first" : "#pragma bitfield_order msb_first");
            if (Scalar(meta?["encoding"]) is { } enc)
            {
                _encoding = enc;
                Line($"#pragma encoding \"{Esc(enc)}\"");
            }

            switch (meta?["file-extension"])
            {
                case YamlScalar s:
                    Line($"#pragma extensions \"{Esc(s.Value)}\"");
                    break;
                case YamlSequence seq:
                    Line($"#pragma extensions \"{Esc(string.Join(";", seq.Items.OfType<YamlScalar>().Select(i => i.Value)))}\"");
                    break;
            }

            if (Scalar(meta?["license"]) is { } license)
            {
                Line($"#pragma license \"{Esc(license)}\"");
            }

            if (meta?["imports"] is YamlSequence imports)
            {
                foreach (YamlScalar imp in imports.Items.OfType<YamlScalar>())
                {
                    string v = imp.Value;
                    _imports.Add(TypeName(v.Split('/')[^1]));
                    if (v.StartsWith('/'))
                    {
                        string dotted = string.Join('.', v.Trim('/').Split('/'));
                        Line($"import {dotted};");
                    }
                    else
                    {
                        Line($"import \"{Esc(v)}.ksy\";");
                    }
                }
            }

            // ks-opaque-types: 定義のない型を使うフィールドだけをエラーにする (TPL-33 の仕様 2 の表: opaque-types は対応しない)
            _opaque = Scalar(meta?["ks-opaque-types"]) == "true" || Scalar(meta?["opaque-types"]) == "true";

            var rootScope = new TypeScope { HxtName = RootName, Spec = spec };
            Collect(rootScope, spec);
            Line(string.Empty);
            EmitEnums(rootScope);
            EmitType(rootScope, spec, isRoot: true);
            Line(string.Empty);
            Line($"{RootName} __root [[inline]];");
            return _out.ToString();
        }

        private void Line(string s) => _out.Append(s).Append('\n');

        private static string Esc(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal);

        private static string? Scalar(YamlNode? n) => n is YamlScalar s ? s.Value : null;

        private static string TypeName(string id)
        {
            string n = Ident(id);
            return BuiltinTypes.IsBuiltinName(n) || HxtReserved.Contains(n) ? "ksy_" + n : n;
        }

        private static string Ident(string id)
        {
            var sb = new StringBuilder();
            foreach (char c in id)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            }

            if (sb.Length == 0 || char.IsDigit(sb[0]))
            {
                sb.Insert(0, '_');
            }

            return sb.ToString();
        }

        /// <summary>フィールドの名前 (HXT の予約語は末尾に _ を付ける)。</summary>
        private static string FieldName(string id) => HxtReserved.Contains(id) ? id + "_" : Ident(id);

        private void Collect(TypeScope scope, YamlMapping spec)
        {
            if (spec["enums"] is YamlMapping enums)
            {
                foreach ((YamlScalar key, _) in enums.Entries)
                {
                    scope.Enums[key.Value] = scope.HxtName + "__" + Ident(key.Value);
                }
            }

            if (spec["types"] is YamlMapping types)
            {
                foreach ((YamlScalar key, YamlNode value) in types.Entries)
                {
                    if (value is not YamlMapping tm)
                    {
                        throw new KsyError($"type '{key.Value}' must be a mapping", value);
                    }

                    var child = new TypeScope { HxtName = scope.HxtName + "__" + Ident(key.Value), Parent = scope, Spec = tm };
                    scope.Types[key.Value] = child;
                    Collect(child, tm);
                }
            }
        }

        private void EmitEnums(TypeScope scope)
        {
            if (scope.Spec?["enums"] is YamlMapping enums)
            {
                foreach ((YamlScalar key, YamlNode value) in enums.Entries)
                {
                    if (value is not YamlMapping members)
                    {
                        throw new KsyError($"enum '{key.Value}' must be a mapping", value);
                    }

                    Line($"enum {scope.Enums[key.Value]} : s64 {{");
                    foreach ((YamlScalar k, YamlNode v) in members.Entries)
                    {
                        string? name = v is YamlMapping vm ? Scalar(vm["id"]) : Scalar(v);
                        if (name is null || !TryInteger(k.Value, out Int128 n))
                        {
                            throw new KsyError($"invalid enum value '{k.Value}'", k);
                        }

                        if (v is YamlMapping vm2 && Scalar(vm2["doc"]) is { } doc)
                        {
                            Line($"    /// {doc.Split('\n')[0]}");
                        }

                        Line($"    {FieldName(name)} = {n.ToString(CultureInfo.InvariantCulture)},");
                    }

                    Line("};");
                }
            }

            foreach (TypeScope child in scope.Types.Values)
            {
                EmitEnums(child);
            }
        }

        private static bool TryInteger(string s, out Int128 value)
        {
            string t = s.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
            bool neg = t.StartsWith('-');
            if (neg)
            {
                t = t[1..];
            }

            if (Lexer.TryParseInteger(t, out value))
            {
                if (neg)
                {
                    value = -value;
                }

                return true;
            }

            return false;
        }

        private void EmitType(TypeScope scope, YamlMapping spec, bool isRoot)
        {
            foreach ((string _, TypeScope child) in scope.Types)
            {
                EmitType(child, child.Spec!, isRoot: false);
            }

            if (Scalar(spec["doc"]) is { } doc)
            {
                foreach (string l in doc.TrimEnd('\n').Split('\n'))
                {
                    Line("/// " + l);
                }
            }

            string parameters = string.Empty;
            if (spec["params"] is YamlSequence ps)
            {
                var list = new List<string>();
                foreach (YamlMapping p in ps.Items.OfType<YamlMapping>())
                {
                    string pt = Scalar(p["type"]) is { } t && MapPrimitive(t, out string prim, out _, out _) ? prim : "auto";
                    list.Add($"{(pt == "auto" ? "ref auto" : pt)} {FieldName(Scalar(p["id"]) ?? "p")}");
                }

                parameters = "(" + string.Join(", ", list) + ")";
            }

            Line($"struct {scope.HxtName}{parameters} {{");
            var meta = spec["meta"] as YamlMapping;
            bool needsEndianSwitch = isRoot && _endianSwitch is not null;
            if (!isRoot && meta?["endian"] is YamlScalar te)
            {
                Line($"    std::core::set_endian({(te.Value == "be" ? "big" : "little")});");
            }
            else if (!isRoot && meta?["endian"] is YamlMapping tsw)
            {
                EmitEndianSwitch(tsw, scope);
            }

            if (spec["seq"] is YamlSequence seq)
            {
                int i = 0;
                foreach (YamlNode item in seq.Items)
                {
                    if (item is not YamlMapping attr)
                    {
                        throw new KsyError("seq entries must be mappings", item);
                    }

                    if (needsEndianSwitch && NeedsEndian(attr))
                    {
                        EmitEndianSwitch((YamlMapping)_endianSwitch!, scope);
                        needsEndianSwitch = false;
                    }

                    EmitAttribute(attr, scope, instance: false, index: i++);
                }
            }

            if (needsEndianSwitch)
            {
                EmitEndianSwitch((YamlMapping)_endianSwitch!, scope);
            }

            if (spec["instances"] is YamlMapping instances)
            {
                foreach ((YamlScalar key, YamlNode value) in instances.Entries)
                {
                    if (value is not YamlMapping attr)
                    {
                        throw new KsyError("instances must be mappings", value);
                    }

                    EmitAttribute(attr, scope, instance: true, index: 0, name: key.Value);
                }
            }

            Line("};");
            Line(string.Empty);
        }

        private static bool NeedsEndian(YamlMapping attr)
        {
            string? t = Scalar(attr["type"]);
            if (attr["type"] is YamlMapping)
            {
                return true;
            }

            if (t is null)
            {
                return false;
            }

            return t is not ("u1" or "s1" or "str" or "strz") && !(t.StartsWith('b') && t.Length > 1 && char.IsDigit(t[1]))
                && !t.EndsWith("le", StringComparison.Ordinal) && !t.EndsWith("be", StringComparison.Ordinal);
        }

        private void EmitEndianSwitch(YamlMapping sw, TypeScope scope)
        {
            string on = Expr(Scalar(sw["switch-on"]) ?? "0", scope, sw);
            Line($"    switch ({on}) {{");
            if (sw["cases"] is YamlMapping cases)
            {
                foreach ((YamlScalar key, YamlNode value) in cases.Entries)
                {
                    string label = key.Value == "_" ? "default" : "case " + Expr(key.Value, scope, key);
                    Line($"        {label}: std::core::set_endian({(Scalar(value) == "be" ? "big" : "little")});");
                }
            }

            Line("    }");
        }

        /// <summary>seq の 1 項目または instances の 1 項目。</summary>
        private void EmitAttribute(YamlMapping attr, TypeScope scope, bool instance, int index, string? name = null)
        {
            string id = FieldName(name ?? Scalar(attr["id"]) ?? $"_unnamed{index}");
            foreach (string ignored in new[] { "consume", "include", "pad-right", "eos-error", "io" })
            {
                if (attr[ignored] is { } node && !(ignored == "io" && instance))
                {
                    // 値の切り出し方だけに影響する指定は近似する (位置とサイズは変わらない)
                    diags.Add(new TemplateDiagnostic(TemplateSeverity.Info, TemplateMessages.UnsupportedKaitai, [ignored],
                        new TemplateSourceLocation(path, node.Line, node.Column)));
                }
            }

            string indent = "    ";
            if (Scalar(attr["doc"]) is { } doc)
            {
                Line($"{indent}/// {doc.TrimEnd('\n').Split('\n')[0]}");
            }

            string? cond = Scalar(attr["if"]);
            if (cond is not null)
            {
                Line($"{indent}if ({Expr(cond, scope, attr["if"])}) {{");
                indent += "    ";
            }

            if (instance && attr["value"] is { } valueNode)
            {
                Line($"{indent}local auto {id} = {Expr(Scalar(valueNode) ?? "0", scope, valueNode)} [[instance, show]];");
            }
            else
            {
                foreach (string line in Declaration(attr, scope, id, instance))
                {
                    Line(indent + line);
                }
            }

            if (cond is not null)
            {
                Line("    }");
            }
        }

        private IEnumerable<string> Declaration(YamlMapping attr, TypeScope scope, string id, bool instance)
        {
            var attrs = new List<string>();
            string placement = string.Empty;
            if (instance)
            {
                attrs.Add("instance");
                if (attr["pos"] is { } pos)
                {
                    string io = Scalar(attr["io"]) is "_root._io" ? "0" : "std::ksy::io_base()";
                    placement = $" @ ({io} + {Expr(Scalar(pos) ?? "0", scope, pos)})";
                }
                else
                {
                    placement = " @ $";
                }
            }

            string suffix = Repeat(attr, scope);
            if (Scalar(attr["enum"]) is { } enumRef)
            {
                attrs.Add($"ksy_enum(\"{ResolveEnum(enumRef, scope, attr["enum"]!)}\")");
            }

            if (attr["valid"] is { } valid)
            {
                attrs.AddRange(Valid(valid, id, scope));
            }

            if (Scalar(attr["-webide-representation"]) is { } rep)
            {
                attrs.Add($"comment(\"{Esc(rep)}\")");
            }

            string attrText() => attrs.Count == 0 ? string.Empty : " [[" + string.Join(", ", attrs) + "]]";

            // process: セクションに展開してから型を置く (TPL-15)
            if (Scalar(attr["process"]) is { } process)
            {
                string size = SizeExpr(attr, scope) ?? throw new KsyError("process requires size", attr);
                string raw = id + "__raw";
                string sec = id + "__s";
                yield return $"u8 {raw}[{size}] [[hidden]];";
                yield return $"section {sec};";
                yield return ProcessCall(process, sec, $"addressof({raw})", $"sizeof({raw})", scope, attr["process"]!);
                string type = attr["type"] is YamlScalar pt ? MapUserOrPrim(pt.Value, scope, attr, out _) : string.Empty;
                if (type.Length == 0 || attr["type"] is null)
                {
                    yield return $"u8 {id}[std::section::size({sec})] @ 0 in {sec}{attrText()};";
                }
                else
                {
                    attrs.Add($"substream(std::section::size({sec}))");
                    yield return $"{type} {id}{suffix} @ 0 in {sec}{attrText()};";
                }

                yield break;
            }

            if (Scalar(attr["contents"]) is not null || attr["contents"] is YamlSequence)
            {
                byte[] contents = Contents(attr["contents"]!);
                attrs.Add($"expect({id} == x\"{System.Convert.ToHexString(contents)}\", \"contents\")");
                yield return $"u8 {id}[{contents.Length}]{placement}{attrText()};";
                yield break;
            }

            if (attr["type"] is YamlMapping sw)
            {
                // type の switch-on / cases
                string on = Expr(Scalar(sw["switch-on"]) ?? "0", scope, sw);
                yield return $"switch ({on}) {{";
                bool hasDefault = false;
                if (sw["cases"] is YamlMapping cases)
                {
                    foreach ((YamlScalar key, YamlNode value) in cases.Entries)
                    {
                        string label = key.Value == "_" ? "default" : "case " + Expr(key.Value, scope, key);
                        hasDefault |= key.Value == "_";
                        foreach (string line in Single(attr, scope, id, Scalar(value) ?? "u1", suffix, placement, attrs, instance))
                        {
                            yield return $"    {label}: {line}";
                        }
                    }
                }

                if (!hasDefault && SizeExpr(attr, scope) is { } size)
                {
                    yield return $"    default: u8 {id}[{size}]{placement}{attrText()};";
                }

                yield return "}";
                yield break;
            }

            if (attr["type"] is YamlScalar ts)
            {
                foreach (string line in Single(attr, scope, id, ts.Value, suffix, placement, attrs, instance))
                {
                    yield return line;
                }

                yield break;
            }

            // 型のないバイト列
            if (SizeExpr(attr, scope) is { } bsize)
            {
                yield return $"u8 {id}[{bsize}]{placement}{attrText()};";
            }
            else if (Truthy(attr["size-eos"]))
            {
                yield return $"u8 {id}[eof]{placement}{attrText()};";
            }
            else if (Scalar(attr["terminator"]) is { } term)
            {
                yield return $"u8 {id}[until(last == {Expr(term, scope, attr["terminator"])})]{placement}{attrText()};";
            }
            else
            {
                yield return $"u8 {id}{suffix}{placement}{attrText()};";
            }
        }

        private IEnumerable<string> Single(YamlMapping attr, TypeScope scope, string id, string type, string suffix, string placement,
            List<string> attrs0, bool instance)
        {
            var attrs = new List<string>(attrs0);
            string attrText() => attrs.Count == 0 ? string.Empty : " [[" + string.Join(", ", attrs) + "]]";
            if (type is "str" or "strz")
            {
                string enc = Scalar(attr["encoding"]) ?? _encoding;
                attrs.Add($"encoding(\"{Esc(enc)}\")");
                if (SizeExpr(attr, scope) is { } size)
                {
                    yield return $"char {id}[{size}]{suffix}{placement}{attrText()};";
                }
                else if (Truthy(attr["size-eos"]))
                {
                    yield return $"char {id}[eof]{placement}{attrText()};";
                }
                else if (type == "strz" || Scalar(attr["terminator"]) is not null)
                {
                    string term = Scalar(attr["terminator"]) is { } t ? Expr(t, scope, attr["terminator"]) : "0";
                    yield return term == "0" && suffix.Length == 0 ? $"string {id}{placement}{attrText()};"
                        : $"char {id}[until(last == {term})]{placement}{attrText()};";
                }
                else
                {
                    yield return $"// HXT: str needs size, size-eos or terminator ({id})";
                    yield return $"char {id}[]{placement}{attrText()};";
                }

                yield break;
            }

            string mapped = MapUserOrPrim(type, scope, attr, out bool isUser);
            if (isUser && SizeExpr(attr, scope) is { } usize)
            {
                attrs.Add($"substream({usize})");
            }
            else if (isUser && Truthy(attr["size-eos"]))
            {
                attrs.Add("substream(std::ksy::io_size() - std::ksy::io_pos())");
            }

            if (mapped.StartsWith("__unsupported", StringComparison.Ordinal))
            {
                // 対応していない機能: そのフィールドだけをエラーにして続ける (TPL-33 の「エラー」)
                yield return $"// HXT: unsupported Kaitai Struct feature ({type})";
                yield return $"{mapped} {id} @ $;";
                yield break;
            }

            _ = instance;
            yield return $"{mapped} {id}{suffix}{placement}{attrText()};";
        }

        private string Repeat(YamlMapping attr, TypeScope scope) => Scalar(attr["repeat"]) switch
        {
            "eos" => "[eof]",
            "expr" => $"[{Expr(Scalar(attr["repeat-expr"]) ?? "0", scope, attr["repeat-expr"])}]",
            "until" => $"[until({Expr(Scalar(attr["repeat-until"]) ?? "true", scope, attr["repeat-until"])})]",
            null => string.Empty,
            var other => throw new KsyError($"unknown repeat '{other}'", attr["repeat"]),
        };

        private string? SizeExpr(YamlMapping attr, TypeScope scope) =>
            attr["size"] is YamlScalar s ? Expr(s.Value, scope, s) : null;

        private static bool Truthy(YamlNode? n) => n is YamlScalar { Value: "true" };

        private IEnumerable<string> Valid(YamlNode valid, string id, TypeScope scope)
        {
            if (valid is YamlScalar s)
            {
                yield return $"require({id} == {Expr(s.Value, scope, s)}, \"valid\")";
                yield break;
            }

            if (valid is not YamlMapping m)
            {
                yield break;
            }

            if (m["eq"] is YamlScalar eq)
            {
                yield return $"require({id} == {Expr(eq.Value, scope, eq)}, \"valid: eq\")";
            }

            if (m["min"] is YamlScalar min)
            {
                yield return $"require({id} >= {Expr(min.Value, scope, min)}, \"valid: min\")";
            }

            if (m["max"] is YamlScalar max)
            {
                yield return $"require({id} <= {Expr(max.Value, scope, max)}, \"valid: max\")";
            }

            if (m["any-of"] is YamlSequence any)
            {
                yield return $"require({string.Join(" || ", any.Items.OfType<YamlScalar>().Select(x => $"{id} == {Expr(x.Value, scope, x)}"))}, \"valid: any-of\")";
            }

            if (m["expr"] is YamlScalar ex)
            {
                yield return $"require({Expr(ex.Value, scope, ex, self: id)}, \"valid: expr\")";
            }
        }

        private static byte[] Contents(YamlNode n)
        {
            var bytes = new List<byte>();
            void Add(YamlNode item)
            {
                switch (item)
                {
                    case YamlScalar { Quoted: true } q:
                        bytes.AddRange(Encoding.UTF8.GetBytes(q.Value));
                        break;
                    case YamlScalar s when TryInteger(s.Value, out Int128 v):
                        bytes.Add((byte)v);
                        break;
                    case YamlScalar s:
                        bytes.AddRange(Encoding.UTF8.GetBytes(s.Value));
                        break;
                    case YamlSequence seq:
                        foreach (YamlNode i in seq.Items)
                        {
                            Add(i);
                        }

                        break;
                }
            }

            Add(n);
            return [.. bytes];
        }

        private string ProcessCall(string process, string sec, string offset, string length, TypeScope scope, YamlNode at)
        {
            int paren = process.IndexOf('(');
            string name = paren < 0 ? process.Trim() : process[..paren].Trim();
            string args = paren < 0 ? string.Empty : process[(paren + 1)..process.LastIndexOf(')')];
            switch (name)
            {
                case "zlib":
                    return $"std::compress::zlib({sec}, {offset}, {length});";
                case "xor":
                    return $"std::encoding::xor({sec}, {offset}, {length}, {Expr(args, scope, at)});";
                case "rol":
                case "ror":
                    return $"std::encoding::{name}({sec}, {offset}, {length}, {Expr(args, scope, at)});";
                default:
                {
                    // 独自の処理: 拡張関数 (TPL-19) として登録されていれば使う
                    string fn = Ident(name);
                    string extra = args.Length > 0 ? ", " + Expr(args, scope, at) : string.Empty;
                    return $"extern fn {fn}(); std::section::append({sec}, {fn}(std::mem::read_bytes({offset}, {length}){extra}));";
                }
            }
        }

        private string MapUserOrPrim(string type, TypeScope scope, YamlNode at, out bool isUser)
        {
            isUser = false;
            if (MapPrimitive(type, out string prim, out string? endianKeyword, out _))
            {
                return endianKeyword is null ? prim : endianKeyword + " " + prim;
            }

            // ユーザー定義の型 (引数付き: foo(a, b))
            int paren = type.IndexOf('(');
            string name = paren < 0 ? type : type[..paren];
            string args = paren < 0 ? string.Empty : "(" + string.Join(", ", SplitArgs(type[(paren + 1)..type.LastIndexOf(')')])
                .Select(a => Expr(a, scope, at))) + ")";
            isUser = true;
            string? resolved = ResolveType(name, scope);
            if (resolved is null)
            {
                return $"__unsupported<\"opaque-types\">";
            }

            return resolved + args;
        }

        private static IEnumerable<string> SplitArgs(string s)
        {
            int depth = 0;
            int start = 0;
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c is '\'' or '"')
                {
                    quote = c;
                }
                else if (c is '(' or '[')
                {
                    depth++;
                }
                else if (c is ')' or ']')
                {
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    yield return s[start..i].Trim();
                    start = i + 1;
                }
            }

            if (s[start..].Trim().Length > 0)
            {
                yield return s[start..].Trim();
            }
        }

        private string? ResolveType(string name, TypeScope scope)
        {
            string[] parts = name.Split("::");
            for (TypeScope? s = scope; s is not null; s = s.Parent)
            {
                TypeScope? t = s;
                bool ok = true;
                foreach (string part in parts)
                {
                    if (t!.Types.TryGetValue(part, out TypeScope? next))
                    {
                        t = next;
                    }
                    else
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    return t!.HxtName;
                }
            }

            if (parts.Length == 1 && TypeName(name) == RootName)
            {
                return RootName;
            }

            // 別の .ksy (imports) の型: その最上位の型の名前
            string external = TypeName(parts[^1]);
            return _imports.Contains(external) || !_opaque ? external : null;
        }

        private readonly HashSet<string> _imports = new(StringComparer.Ordinal);

        private bool _opaque;

        private string ResolveEnum(string name, TypeScope scope, YamlNode at)
        {
            string[] parts = name.Split("::");
            for (TypeScope? s = scope; s is not null; s = s.Parent)
            {
                TypeScope? t = s;
                for (int i = 0; i < parts.Length - 1 && t is not null; i++)
                {
                    t = t.Types.GetValueOrDefault(parts[i]);
                }

                if (t is not null && t.Enums.TryGetValue(parts[^1], out string? hxt))
                {
                    return hxt;
                }
            }

            throw new KsyError($"enum '{name}' is not defined", at);
        }

        /// <summary>組み込み型 (u1〜u8、s1〜s8、f4、f8、le / be 付き、b1〜b64)。</summary>
        private static bool MapPrimitive(string t, out string hxt, out string? endian, out int bits)
        {
            endian = null;
            bits = 0;
            hxt = string.Empty;
            string core = t;
            if (t.Length > 2 && (t.EndsWith("le", StringComparison.Ordinal) || t.EndsWith("be", StringComparison.Ordinal)) && char.IsDigit(t[^3]))
            {
                endian = t[^2..];
                core = t[..^2];
            }

            if (core.Length >= 2 && core[0] == 'b' && int.TryParse(core.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 64)
            {
                bits = n;
                hxt = $"bits<{n}>";
                endian = null;
                return true;
            }

            hxt = core switch
            {
                "u1" => "u8",
                "u2" => "u16",
                "u4" => "u32",
                "u8" => "u64",
                "s1" => "s8",
                "s2" => "s16",
                "s4" => "s32",
                "s8" => "s64",
                "f4" => "f32",
                "f8" => "f64",
                _ => string.Empty,
            };
            if (hxt.Length == 0)
            {
                endian = null;
                return false;
            }

            return true;
        }

        // ───────────── 式言語 ─────────────

        private string Expr(string text, TypeScope scope, YamlNode? at, string? self = null)
        {
            try
            {
                var p = new KsyExpr(text, this, scope, self);
                string result = p.Parse();
                return result;
            }
            catch (FormatException ex)
            {
                throw new KsyError($"invalid expression '{text}': {ex.Message}", at);
            }
        }

        private sealed class KsyExpr(string text, Converter conv, TypeScope scope, string? self)
        {
            private readonly List<(string Kind, string Text)> _t = Tokenize(text);
            private int _p;

            private static List<(string, string)> Tokenize(string s)
            {
                var list = new List<(string, string)>();
                int i = 0;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (char.IsWhiteSpace(c))
                    {
                        i++;
                        continue;
                    }

                    if (char.IsDigit(c))
                    {
                        int st = i;
                        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || (s[i] == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]))))
                        {
                            i++;
                        }

                        list.Add(("num", s[st..i]));
                        continue;
                    }

                    if (char.IsLetter(c) || c == '_')
                    {
                        int st = i;
                        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_'))
                        {
                            i++;
                        }

                        list.Add(("id", s[st..i]));
                        continue;
                    }

                    if (c is '\'' or '"')
                    {
                        int st = ++i;
                        var sb = new StringBuilder();
                        while (i < s.Length && s[i] != c)
                        {
                            if (s[i] == '\\' && c == '"' && i + 1 < s.Length)
                            {
                                i++;
                                sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', '0' => '\0', _ => s[i] });
                            }
                            else
                            {
                                sb.Append(s[i]);
                            }

                            i++;
                        }

                        if (i >= s.Length)
                        {
                            throw new FormatException("unterminated string");
                        }

                        i++;
                        list.Add(("str", sb.ToString()));
                        _ = st;
                        continue;
                    }

                    string[] ops = ["::", "<<", ">>", "<=", ">=", "==", "!="];
                    string? op = ops.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0);
                    if (op is not null)
                    {
                        list.Add(("op", op));
                        i += op.Length;
                        continue;
                    }

                    list.Add(("op", c.ToString()));
                    i++;
                }

                list.Add(("end", string.Empty));
                return list;
            }

            private (string Kind, string Text) Cur => _t[_p];

            private bool IsOp(string op) => Cur.Kind == "op" && Cur.Text == op;

            private bool IsWord(string w) => Cur.Kind == "id" && Cur.Text == w;

            private void Expect(string op)
            {
                if (!IsOp(op))
                {
                    throw new FormatException($"'{op}' expected");
                }

                _p++;
            }

            public string Parse()
            {
                string e = Ternary();
                if (Cur.Kind != "end")
                {
                    throw new FormatException($"unexpected '{Cur.Text}'");
                }

                return e;
            }

            private string Ternary()
            {
                string c = Or();
                if (IsOp("?"))
                {
                    _p++;
                    string a = Ternary();
                    Expect(":");
                    string b = Ternary();
                    return $"({c} ? {a} : {b})";
                }

                return c;
            }

            private string Or()
            {
                string l = And();
                while (IsWord("or"))
                {
                    _p++;
                    l = $"({l} || {And()})";
                }

                return l;
            }

            private string And()
            {
                string l = Not();
                while (IsWord("and"))
                {
                    _p++;
                    l = $"({l} && {Not()})";
                }

                return l;
            }

            private string Not()
            {
                if (IsWord("not"))
                {
                    _p++;
                    return $"!({Not()})";
                }

                return Comparison();
            }

            private string Comparison()
            {
                string l = BinOr();
                while (Cur.Kind == "op" && Cur.Text is "==" or "!=" or "<" or "<=" or ">" or ">=")
                {
                    string op = Cur.Text;
                    _p++;
                    l = $"({l} {op} {BinOr()})";
                }

                return l;
            }

            private string BinOr() => Left(BinXor, "|");

            private string BinXor() => Left(BinAnd, "^");

            private string BinAnd() => Left(Shift, "&");

            private string Shift() => Left(Arith, "<<", ">>");

            private string Arith() => Left(Term, "+", "-");

            private string Term() => Left(Factor, "*", "/", "%");

            private string Left(Func<string> next, params string[] ops)
            {
                string l = next();
                while (Cur.Kind == "op" && ops.Contains(Cur.Text))
                {
                    string op = Cur.Text;
                    _p++;
                    l = $"({l} {op} {next()})";
                }

                return l;
            }

            private string Factor()
            {
                if (Cur.Kind == "op" && Cur.Text is "-" or "+" or "~" or "!")
                {
                    string op = Cur.Text;
                    _p++;
                    return $"({op}{Factor()})";
                }

                return Postfix(Atom());
            }

            private string Atom()
            {
                (string kind, string t) = Cur;
                _p++;
                switch (kind)
                {
                    case "num":
                        return t.Contains('.', StringComparison.Ordinal) ? t : t.Replace("_", string.Empty, StringComparison.Ordinal);
                    case "str":
                        return $"\"{Esc(t)}\"";
                    case "op" when t == "(":
                    {
                        string e = Ternary();
                        Expect(")");
                        return $"({e})";
                    }

                    case "op" when t == "[":
                    {
                        var items = new List<string>();
                        while (!IsOp("]"))
                        {
                            items.Add(Ternary());
                            if (IsOp(","))
                            {
                                _p++;
                            }
                            else if (!IsOp("]"))
                            {
                                throw new FormatException("']' expected");
                            }
                        }

                        _p++;
                        var bytes = new List<byte>();
                        foreach (string item in items)
                        {
                            if (Lexer.TryParseInteger(item, out Int128 v) && v >= 0 && v <= 255)
                            {
                                bytes.Add((byte)v);
                            }
                            else if (item.StartsWith('"'))
                            {
                                bytes.AddRange(Encoding.UTF8.GetBytes(item.Trim('"')));
                            }
                            else
                            {
                                return "std::ksy::unsupported_array()";
                            }
                        }

                        return $"x\"{string.Join(" ", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))}\"";
                    }

                    case "id":
                        return Identifier(t);
                }

                throw new FormatException($"unexpected '{t}'");
            }

            private string Identifier(string t)
            {
                switch (t)
                {
                    case "true":
                    case "false":
                        return t;
                    case "_root":
                        return "__root";
                    case "_parent":
                        return "parent";
                    case "_io":
                        return "__io";
                    case "_":
                        return self ?? "last";
                    case "_index":
                        return "std::core::array_index()";
                    case "sizeof":
                    case "bitsizeof":
                    {
                        Expect("<");
                        string type = Cur.Text;
                        _p++;
                        Expect(">");
                        string mapped = conv.MapUserOrPrim(type, scope, null!, out _);
                        return t == "sizeof" ? $"sizeof({mapped})" : $"(sizeof({mapped}) * 8)";
                    }
                }

                // 列挙子 (enum_name::value)
                if (IsOp("::"))
                {
                    var parts = new List<string> { t };
                    while (IsOp("::"))
                    {
                        _p++;
                        parts.Add(Cur.Text);
                        _p++;
                    }

                    string enumName = string.Join("::", parts.Take(parts.Count - 1));
                    string hxt = conv.ResolveEnum(enumName, scope, null!);
                    return $"{hxt}::{FieldName(parts[^1])}";
                }

                return FieldName(t);
            }

            private string Postfix(string recv)
            {
                while (true)
                {
                    if (IsOp("."))
                    {
                        _p++;
                        string name = Cur.Text;
                        _p++;
                        List<string>? args = null;
                        if (IsOp("("))
                        {
                            _p++;
                            args = [];
                            while (!IsOp(")"))
                            {
                                args.Add(Ternary());
                                if (IsOp(","))
                                {
                                    _p++;
                                }
                            }

                            _p++;
                        }

                        if (name == "as" && IsOp("<"))
                        {
                            _p++;
                            while (!IsOp(">") && Cur.Kind != "end")
                            {
                                _p++;
                            }

                            _p++;
                            continue;
                        }

                        recv = Method(recv, name, args);
                        continue;
                    }

                    if (IsOp("["))
                    {
                        _p++;
                        string index = Ternary();
                        Expect("]");
                        recv = $"{recv}[{index}]";
                        continue;
                    }

                    return recv;
                }
            }

            private static string Method(string recv, string name, List<string>? args)
            {
                if (recv == "__io")
                {
                    return name switch
                    {
                        "size" => "std::ksy::io_size()",
                        "pos" => "std::ksy::io_pos()",
                        "eof" => "std::ksy::io_eof()",
                        _ => $"std::ksy::unsupported_io_{name}()",
                    };
                }

                string a = args is null || args.Count == 0 ? string.Empty : ", " + string.Join(", ", args);
                return name switch
                {
                    "to_s" => $"std::ksy::to_s({recv}{a})",
                    "to_i" => $"std::ksy::to_i({recv}{a})",
                    "length" => $"std::core::len({recv})",
                    "size" => $"sizeof({recv})",
                    "reverse" => $"std::ksy::reverse({recv})",
                    "substring" => $"std::ksy::substring({recv}{a})",
                    "first" => $"{recv}[0]",
                    "last" => $"{recv}[std::core::len({recv}) - 1]",
                    "_io" => "__io",
                    "_parent" => $"{recv}.parent",
                    _ when args is null => $"{recv}.{FieldName(name)}",
                    _ => $"std::ksy::unsupported_{Ident(name)}({recv}{a})",
                };
            }
        }
    }
}
