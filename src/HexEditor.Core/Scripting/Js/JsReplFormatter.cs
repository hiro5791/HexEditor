using System.Globalization;
using System.Text;
using HexEditor.Core.Scripting.Repl;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;

namespace HexEditor.Core.Scripting.Js;

/// <summary>
/// JavaScript の値をコンソール・デバッガの表示用 (<see cref="ReplValue"/>) にする (AUTO-21 の仕様 4、AUTO-23 の仕様 2)。
/// スクリプトのスレッドで呼ぶ。ゲッターは呼ばない (副作用・例外を避けるため。ゲッターは <c>(getter)</c> と表示する)。
/// </summary>
internal sealed class JsReplFormatter
{
    private const int MaxNodes = 2000;
    private const int MaxPreview = 100;

    private readonly Stack<ObjectInstance> _path = new();
    private int _nodes;

    /// <summary>展開する深さ (トップを 0 として、この深さまで子を作る)。</summary>
    public int MaxDepth { get; init; } = 3;

    public static ReplValue Format(JsValue value, int maxDepth = 3) => new JsReplFormatter { MaxDepth = maxDepth }.Convert(value, 0);

    public ReplValue Convert(JsValue value, int depth)
    {
        _nodes++;
        if (value.IsUndefined())
        {
            return ReplValue.Undefined;
        }

        if (value.IsNull())
        {
            return ReplValue.Of(ReplValueKind.Null, "null", "null");
        }

        if (value.IsBoolean())
        {
            return ReplValue.Of(ReplValueKind.Boolean, "boolean", value.AsBoolean() ? "true" : "false");
        }

        if (value.IsNumber())
        {
            return ReplValue.Of(ReplValueKind.Number, "number", value.ToString());
        }

        if (value.IsBigInt())
        {
            return ReplValue.FromBigInteger(TypeConverter.ToBigInt(value));
        }

        if (value.IsString())
        {
            return ReplValue.Of(ReplValueKind.String, "string", Quote(value.AsString()));
        }

        if (value.IsSymbol())
        {
            return ReplValue.Of(ReplValueKind.Symbol, "symbol", value.ToString());
        }

        if (value.IsUint8Array() || value.IsUint8ClampedArray())
        {
            byte[] bytes = value.IsUint8Array() ? value.AsUint8Array() : value.AsUint8ClampedArray();
            return new ReplValue
            {
                Kind = ReplValueKind.Bytes,
                Type = $"Uint8Array({bytes.Length})",
                Text = ReplValue.HexDump(bytes, bytes.Length),
                ByteLength = bytes.Length,
            };
        }

        if (value is not ObjectInstance obj)
        {
            return ReplValue.Of(ReplValueKind.Other, "other", value.ToString());
        }

        if (value.IsCallable())
        {
            string name = SafeString(obj, "name");
            return ReplValue.Of(ReplValueKind.Function, "Function", $"ƒ {name}()");
        }

        if (value.IsPromise())
        {
            return ReplValue.Of(ReplValueKind.Other, "Promise", "Promise");
        }

        if (value.IsDate())
        {
            JsValue toIso = obj.Get("toISOString");
            return ReplValue.Of(ReplValueKind.Date, "Date", toIso.IsCallable() ? SafeCall(toIso, obj) : value.ToString());
        }

        if (IsError(obj))
        {
            string stack = SafeString(obj, "stack");
            return new ReplValue
            {
                Kind = ReplValueKind.Error,
                Type = SafeString(obj, "name") is { Length: > 0 } n ? n : "Error",
                Text = $"{SafeString(obj, "name")}: {SafeString(obj, "message")}",
                Stack = stack.Length > 0 ? stack : null,
            };
        }

        if (_path.Contains(obj))
        {
            return ReplValue.Of(ReplValueKind.Other, "Object", "[Circular]");
        }

        _path.Push(obj);
        try
        {
            return value.IsArray() ? ConvertArray(obj, depth) : ConvertObject(obj, depth);
        }
        finally
        {
            _path.Pop();
        }
    }

    private ReplValue ConvertArray(ObjectInstance array, int depth)
    {
        uint length = (uint)array.Get("length").AsNumber();
        var children = new List<ReplProperty>();
        var preview = new StringBuilder("[");
        int shown = (int)Math.Min(length, ReplValue.MaxChildren);
        for (int i = 0; i < shown; i++)
        {
            JsValue item = array.Get(i.ToString(CultureInfo.InvariantCulture));
            ReplValue child = depth < MaxDepth && _nodes < MaxNodes ? Convert(item, depth + 1) : Shallow(item);
            children.Add(new ReplProperty(i.ToString(CultureInfo.InvariantCulture), child));
            if (preview.Length < MaxPreview)
            {
                preview.Append(i > 0 ? ", " : "").Append(child.Kind == ReplValueKind.Bytes ? child.Type : OneLine(child));
            }
        }

        if (length > shown || preview.Length >= MaxPreview)
        {
            preview.Append(", …");
        }

        preview.Append(']');
        return new ReplValue
        {
            Kind = ReplValueKind.Array,
            Type = $"Array({length})",
            Text = preview.ToString(),
            Children = children,
            ChildCount = (int)Math.Min(length, int.MaxValue),
        };
    }

    private ReplValue ConvertObject(ObjectInstance obj, int depth)
    {
        string type = ConstructorName(obj);
        var children = new List<ReplProperty>();
        var preview = new StringBuilder("{");
        int total = 0;
        foreach (JsValue key in obj.GetOwnPropertyKeys(Types.String))
        {
            string name = key.ToString();
            PropertyDescriptor? descriptor = obj.GetOwnProperty(name);
            if (descriptor is null || !descriptor.Enumerable)
            {
                continue;
            }

            total++;
            if (children.Count >= ReplValue.MaxChildren)
            {
                continue;
            }

            ReplValue child;
            if (descriptor.IsAccessorDescriptor())
            {
                child = ReplValue.Of(ReplValueKind.Other, "getter", "(getter)");
            }
            else
            {
                JsValue item = descriptor.Value;
                child = depth < MaxDepth && _nodes < MaxNodes ? Convert(item, depth + 1) : Shallow(item);
            }

            children.Add(new ReplProperty(name, child));
            if (preview.Length < MaxPreview)
            {
                preview.Append(children.Count > 1 ? ", " : "").Append(name).Append(": ").Append(child.Kind == ReplValueKind.Bytes ? child.Type : OneLine(child));
            }
        }

        if (total > children.Count || preview.Length >= MaxPreview)
        {
            preview.Append(", …");
        }

        preview.Append('}');
        string text = type == "Object" ? preview.ToString() : type + " " + preview;
        return new ReplValue { Kind = ReplValueKind.Object, Type = type, Text = text, Children = children, ChildCount = total };
    }

    /// <summary>深さの上限に達した値: 子を展開せず、種類だけ表す。</summary>
    private static ReplValue Shallow(JsValue value)
    {
        if (value is ObjectInstance o && !value.IsCallable() && !value.IsUint8Array())
        {
            bool array = value.IsArray();
            return ReplValue.Of(array ? ReplValueKind.Array : ReplValueKind.Object, array ? "Array" : ConstructorName(o), array ? "[…]" : "{…}");
        }

        return new JsReplFormatter { MaxDepth = 0 }.Convert(value, 0);
    }

    private static string OneLine(ReplValue v) => v.Text.Contains('\n', StringComparison.Ordinal) ? v.Type : v.Text;

    private static bool IsError(ObjectInstance obj)
    {
        JsValue stack = obj.Get("stack");
        return stack.IsString() && obj.Get("message").IsString() && obj.Get("name").IsString();
    }

    private static string ConstructorName(ObjectInstance obj)
    {
        try
        {
            JsValue ctor = obj.Get("constructor");
            if (ctor is ObjectInstance c && c.Get("name") is { } n && n.IsString() && n.AsString().Length > 0)
            {
                return n.AsString();
            }
        }
        catch (JavaScriptException)
        {
        }

        return "Object";
    }

    private static string SafeString(ObjectInstance obj, string property)
    {
        try
        {
            JsValue v = obj.Get(property);
            return v.IsUndefined() || v.IsNull() ? "" : v.ToString();
        }
        catch (JavaScriptException)
        {
            return "";
        }
    }

    private static string SafeCall(JsValue function, ObjectInstance thisObject)
    {
        try
        {
            return thisObject.Engine.Call(function, thisObject).ToString();
        }
        catch (JavaScriptException)
        {
            return thisObject.ToString();
        }
    }

    /// <summary>文字列を JSON と同じ書式で引用符に入れる。</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }
}
