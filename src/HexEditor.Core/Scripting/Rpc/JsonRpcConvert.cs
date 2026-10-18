using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Core.Scripting.Js;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting.Rpc;

/// <summary>引数を変換できない (JSON-RPC の -32602 Invalid params)。</summary>
internal sealed class JsonRpcParamsException(string message) : Exception(message);

/// <summary>
/// JSON と API の値の変換 (AUTO-45 の仕様 5)。バイト列は Base64 の文字列、ドキュメントは <c>id</c>、そのほかの API のオブジェクト
/// (テンプレートのノード、購読など) は接続ごとの <c>handle</c> で渡す。範囲は <c>{ "start", "length" }</c> か入力式の文字列、
/// 数値の引数は数値か入力式の文字列 (AUTO-04 の仕様 5・6)。バイト列の引数 (<see cref="BytePattern"/>) は Base64 の文字列のほか、
/// Hex の文字列・キーワードを <c>{ "hex": "DE AD" }</c> / <c>{ "text": "random" }</c> で渡せる。
/// </summary>
internal sealed class JsonRpcConvert(IApp app)
{
    private const int MaxDepth = 16;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, object> _handles = new(StringComparer.Ordinal);
    private readonly Dictionary<object, string> _handleOf = new(ReferenceEqualityComparer.Instance);
    private int _next;

    // ───────────── 参照 ─────────────

    public string HandleOf(object value)
    {
        lock (_lock)
        {
            if (!_handleOf.TryGetValue(value, out string? h))
            {
                h = "h" + (++_next).ToString(CultureInfo.InvariantCulture);
                _handleOf[value] = h;
                _handles[h] = value;
            }

            return h;
        }
    }

    public object ResolveHandle(string handle, Type type)
    {
        object? value;
        lock (_lock)
        {
            _handles.TryGetValue(handle, out value);
        }

        return value is not null && type.IsInstanceOfType(value)
            ? value
            : throw new JsonRpcParamsException($"Unknown handle for {JsonRpcApi.ObjectName(type)}: {handle}");
    }

    public IDocument ResolveDocument(string id) =>
        app.Documents.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal))
        ?? throw new Sdk.ArgumentError(Msg.T($"ドキュメント {id} は開いていません。", $"The document {id} is not open."));

    // ───────────── JSON → 値 ─────────────

    public object? FromJson(JsonNode? node, Type type, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            throw new JsonRpcParamsException("The value is nested too deeply.");
        }

        Type? underlying = Nullable.GetUnderlyingType(type);
        if (node is null)
        {
            return type.IsValueType && underlying is null ? throw new JsonRpcParamsException($"A value of {type.Name} is required.") : null;
        }

        type = underlying ?? type;
        if (type == typeof(JsonNode))
        {
            return node.DeepClone();
        }

        if (type == typeof(object))
        {
            return Plain(node);
        }

        if (type == typeof(string))
        {
            return node is JsonValue v && v.TryGetValue(out string? s) ? s : throw Expected("a string");
        }

        if (type == typeof(bool))
        {
            return node is JsonValue v && v.TryGetValue(out bool b) ? b : throw Expected("true or false");
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(uint) || type == typeof(ulong))
        {
            return Convert.ChangeType(Integer(node), type, CultureInfo.InvariantCulture);
        }

        if (type == typeof(double) || type == typeof(float))
        {
            return node is JsonValue v && v.TryGetValue(out double d) ? Convert.ChangeType(d, type, CultureInfo.InvariantCulture) : throw Expected("a number");
        }

        if (type == typeof(BigInteger))
        {
            if (node is JsonValue v && v.TryGetValue(out string? s) && BigInteger.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger parsed))
            {
                return parsed;
            }

            return BigInteger.Parse(node.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        if (type == typeof(OffsetArg))
        {
            return node is JsonValue v && v.TryGetValue(out string? expr) ? (OffsetArg)expr : (OffsetArg)Integer(node);
        }

        if (type == typeof(RangeArg))
        {
            return node is JsonValue v && v.TryGetValue(out string? expr) ? (RangeArg)expr : (RangeArg)Range(node);
        }

        if (type == typeof(ByteRange))
        {
            return Range(node);
        }

        if (type == typeof(EncodingArg))
        {
            return node is JsonValue v && v.TryGetValue(out string? name) ? (EncodingArg)name : (EncodingArg)(int)Integer(node);
        }

        if (type == typeof(byte[]))
        {
            return Bytes(node);
        }

        if (type == typeof(BytePattern))
        {
            if (node is JsonObject o)
            {
                if (o["hex"] is JsonValue hex && hex.TryGetValue(out string? h))
                {
                    return (BytePattern)h;
                }

                if (o["text"] is JsonValue text && text.TryGetValue(out string? t))
                {
                    return (BytePattern)t;
                }

                throw Expected("Base64 text, { \"hex\": … } or { \"text\": … }");
            }

            return (BytePattern)Bytes(node);
        }

        if (type == typeof(IDocument))
        {
            return ResolveDocument(Reference(node, "id"));
        }

        if (JsonRpcApi.IsReferenceType(type))
        {
            return ResolveHandle(Reference(node, "handle"), type);
        }

        if (type.IsEnum)
        {
            return node is JsonValue v && v.TryGetValue(out string? s) && Enum.TryParse(type, s, ignoreCase: true, out object? e) ? e : throw Expected(type.Name);
        }

        if (ListElement(type) is { } element)
        {
            if (node is not JsonArray array)
            {
                throw Expected("an array");
            }

            Array items = Array.CreateInstance(element, array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                items.SetValue(FromJson(array[i], element, depth + 1), i);
            }

            if (type.IsArray || type.IsAssignableFrom(items.GetType()))
            {
                return items;
            }

            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            foreach (object? item in items)
            {
                list.Add(item);
            }

            return list;
        }

        if (DictionaryValue(type) is { } valueType)
        {
            if (node is not JsonObject o)
            {
                throw Expected("an object");
            }

            var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType))!;
            foreach ((string key, JsonNode? value) in o)
            {
                dict[key] = FromJson(value, valueType, depth + 1);
            }

            return dict;
        }

        if (type.IsClass && node is JsonObject obj)
        {
            return Options(obj, type, depth);
        }

        throw new JsonRpcParamsException($"Cannot convert to {type.Name}.");
    }

    private static JsonRpcParamsException Expected(string what) => new($"Expected {what}.");

    private static long Integer(JsonNode node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue(out long l))
            {
                return l;
            }

            if (v.TryGetValue(out double d) && d == Math.Floor(d) && Math.Abs(d) < 9.2e18)
            {
                return (long)d;
            }
        }

        throw Expected("an integer");
    }

    private ByteRange Range(JsonNode node)
    {
        if (node is not JsonObject o || o["start"] is not { } start)
        {
            throw Expected("{ \"start\": …, \"length\": … }");
        }

        long s = Integer(start);
        long length = o["length"] is { } len ? Integer(len) : o["end"] is { } end ? Integer(end) - s : throw Expected("\"length\" or \"end\"");
        return new ByteRange(s, length);
    }

    private static byte[] Bytes(JsonNode node)
    {
        if (node is JsonValue v && v.TryGetValue(out string? text))
        {
            try
            {
                return Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                throw Expected("Base64 text");
            }
        }

        if (node is JsonArray array)
        {
            byte[] bytes = new byte[array.Count];
            for (int i = 0; i < bytes.Length; i++)
            {
                long b = array[i] is { } item ? Integer(item) : -1;
                bytes[i] = b is >= 0 and <= 255 ? (byte)b : throw Expected("numbers 0-255");
            }

            return bytes;
        }

        throw Expected("Base64 text");
    }

    private static string Reference(JsonNode node, string key) => node switch
    {
        JsonValue v when v.TryGetValue(out string? s) => s,
        JsonObject o when o[key] is JsonValue r && r.TryGetValue(out string? s) => s,
        _ => throw Expected($"a {key}"),
    };

    private object Options(JsonObject obj, Type type, int depth)
    {
        ConstructorInfo? ctor = type.GetConstructor(Type.EmptyTypes);
        object instance;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ctor is not null)
        {
            instance = ctor.Invoke(null);
        }
        else
        {
            // 引数付きのコンストラクタ (レコード)。引数の名前で値を探す。
            ConstructorInfo primary = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            object?[] args = [.. primary.GetParameters().Select(p =>
            {
                JsonNode? value = Find(obj, p.Name!);
                used.Add(p.Name!);
                return value is null && p.HasDefaultValue ? p.DefaultValue : FromJson(value, p.ParameterType, depth + 1);
            })];
            instance = primary.Invoke(args);
        }

        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || used.Contains(p.Name) || typeof(Delegate).IsAssignableFrom(p.PropertyType) || IsCancellation(p.PropertyType))
            {
                continue;
            }

            if (Find(obj, p.Name) is { } value)
            {
                p.SetValue(instance, FromJson(value, p.PropertyType, depth + 1));
            }
        }

        return instance;
    }

    private static bool IsCancellation(Type t) => t == typeof(CancellationToken) || t == typeof(CancellationToken?);

    private static JsonNode? Find(JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out JsonNode? exact))
        {
            return exact;
        }

        return obj.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static Type? ListElement(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType)
        {
            Type def = type.GetGenericTypeDefinition();
            if (def == typeof(IReadOnlyList<>) || def == typeof(IList<>) || def == typeof(List<>) || def == typeof(IEnumerable<>)
                || def == typeof(IReadOnlyCollection<>) || def == typeof(ICollection<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return null;
    }

    private static Type? DictionaryValue(Type type)
    {
        if (type.IsGenericType)
        {
            Type def = type.GetGenericTypeDefinition();
            if ((def == typeof(IReadOnlyDictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(Dictionary<,>)) && type.GetGenericArguments()[0] == typeof(string))
            {
                return type.GetGenericArguments()[1];
            }
        }

        return null;
    }

    /// <summary>型の決まっていない値 (<c>settings.set</c> など): 文字列・数値・真偽値・配列・辞書にする。</summary>
    private static object? Plain(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out bool b) => b,
        JsonValue v when v.TryGetValue(out long l) => l,
        JsonValue v when v.TryGetValue(out double d) => d,
        JsonValue v when v.TryGetValue(out string? s) => s,
        JsonArray a => a.Select(Plain).ToList(),
        JsonObject o => o.ToDictionary(kv => kv.Key, kv => Plain(kv.Value), StringComparer.Ordinal),
        _ => node.ToJsonString(),
    };

    // ───────────── 値 → JSON ─────────────

    public JsonNode? ToJson(object? value, int depth = 0)
    {
        if (value is null)
        {
            return null;
        }

        if (depth > MaxDepth)
        {
            return JsonValue.Create(value.ToString());
        }

        switch (value)
        {
            case JsonNode node:
                return node.DeepClone();
            case string s:
                return JsonValue.Create(s);
            case bool b:
                return JsonValue.Create(b);
            case char c:
                return JsonValue.Create(c.ToString());
            case byte[] bytes:
                return JsonValue.Create(Convert.ToBase64String(bytes));
            case BigInteger big:
                return JsonRpcApi.IsSafeInteger(big) ? JsonValue.Create((long)big) : JsonValue.Create(big.ToString(CultureInfo.InvariantCulture));
            case Int128 i128:
                return ToJson((BigInteger)i128, depth);
            case UInt128 u128:
                return ToJson((BigInteger)u128, depth);
            case long or int or short or sbyte or byte or ushort or uint:
                return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case ulong ul:
                return ToJson(new BigInteger(ul), depth);
            case double d:
                return double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture));
            case float f:
                return ToJson((double)f, depth);
            case Enum e:
                return JsonValue.Create(JsBinder.CamelCase(e.ToString()));
            case DateTime dt:
                return JsonValue.Create(dt.ToString("O", CultureInfo.InvariantCulture));
            case DateTimeOffset dto:
                return JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture));
            case ByteRange r:
                return new JsonObject { ["start"] = r.Start, ["length"] = r.Length };
            case OffsetArg o:
                return o.Value is long ov ? JsonValue.Create(ov) : JsonValue.Create(o.Expression);
            case IDocument doc:
                return Describe(doc);
            case SearchResults results:
                return new JsonObject { ["items"] = new JsonArray([.. results.Select(r => ToJson(r, depth + 1))]), ["truncated"] = results.Truncated };
        }

        Type type = value.GetType();
        if (IsReference(value))
        {
            var o = new JsonObject { ["handle"] = HandleOf(value), ["type"] = TypeName(value) };
            foreach (PropertyInfo p in ReferenceType(value).GetProperties())
            {
                if (p.GetIndexParameters().Length == 0 && IsSimple(p.PropertyType))
                {
                    try
                    {
                        o[JsBinder.JsName(p)] = ToJson(p.GetValue(value), depth + 1);
                    }
                    catch (TargetInvocationException)
                    {
                        // 読めないプロパティ (評価のエラーなど) は省く。
                    }
                }
            }

            // 一覧でもある参照 (ブックマークの一覧など) は要素も渡す。
            if (value is IEnumerable list and not string)
            {
                o["items"] = new JsonArray([.. list.Cast<object?>().Select(x => ToJson(x, depth + 1))]);
            }

            return o;
        }

        if (value is IDictionary dict)
        {
            var o = new JsonObject();
            foreach (DictionaryEntry e in dict)
            {
                o[Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? string.Empty] = ToJson(e.Value, depth + 1);
            }

            return o;
        }

        if (value is IEnumerable enumerable)
        {
            return new JsonArray([.. enumerable.Cast<object?>().Select(x => ToJson(x, depth + 1))]);
        }

        var result = new JsonObject();
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || typeof(Delegate).IsAssignableFrom(p.PropertyType) || IsCancellation(p.PropertyType)
                || p.Name == "EqualityContract")
            {
                continue;
            }

            result[JsBinder.CamelCase(p.Name)] = ToJson(p.GetValue(value), depth + 1);
        }

        return result;
    }

    /// <summary>ドキュメントの情報 (参照は <c>id</c>)。</summary>
    public JsonObject Describe(IDocument doc) => new()
    {
        ["id"] = doc.Id,
        ["kind"] = doc.Kind,
        ["path"] = doc.Path,
        ["name"] = doc.Name,
        ["length"] = doc.Length,
        ["isModified"] = doc.IsModified,
        ["isReadOnly"] = doc.IsReadOnly,
    };

    private static bool IsReference(object value) =>
        value.GetType().GetInterfaces().Any(JsonRpcApi.IsReferenceType) || (value.GetType().BaseType is { } b && JsonRpcApi.IsReferenceType(b));

    private static Type ReferenceType(object value) =>
        value.GetType().GetInterfaces().FirstOrDefault(JsonRpcApi.IsReferenceType)
        ?? (value.GetType().BaseType is { } b && JsonRpcApi.IsReferenceType(b) ? b : value.GetType());

    private static string TypeName(object value) => JsBinder.TsName(ReferenceType(value));

    private static bool IsSimple(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t == typeof(string) || t == typeof(BigInteger) || t.IsEnum || t == typeof(ByteRange);
    }

    /// <summary>JSON の文字列を作る (応答・通知)。</summary>
    public static string Serialize(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
