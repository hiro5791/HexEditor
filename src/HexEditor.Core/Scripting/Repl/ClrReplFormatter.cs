using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace HexEditor.Core.Scripting.Repl;

/// <summary>
/// C# の値をコンソールの表示用 (<see cref="ReplValue"/>) にする (AUTO-23 の仕様 2: バイト列は Hex ダンプ、オブジェクトは展開できるツリー、
/// 整数の <c>BigInteger</c> は 10 進と 16 進)。公開されたプロパティだけを読む (副作用のあるメソッドは呼ばない)。
/// </summary>
public static class ClrReplFormatter
{
    private const int MaxDepth = 3;
    private const int MaxNodes = 2000;

    public static ReplValue Format(object? value) => Convert(value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance), new Counter());

    private sealed class Counter
    {
        public int Nodes;
    }

    private static ReplValue Convert(object? value, int depth, HashSet<object> path, Counter counter)
    {
        counter.Nodes++;
        switch (value)
        {
            case null:
                return ReplValue.Of(ReplValueKind.Null, "null", "null");
            case bool b:
                return ReplValue.Of(ReplValueKind.Boolean, "bool", b ? "true" : "false");
            case string s:
                return ReplValue.Of(ReplValueKind.String, "string", Quote(s));
            case char c:
                return ReplValue.Of(ReplValueKind.String, "char", "'" + c + "'");
            case BigInteger big:
                return ReplValue.FromBigInteger(big);
            case long or ulong or int or uint or short or ushort or byte or sbyte:
                return ReplValue.Of(ReplValueKind.Number, value.GetType().Name, System.Convert.ToString(value, CultureInfo.InvariantCulture)!);
            case float or double or decimal:
                return ReplValue.Of(ReplValueKind.Number, value.GetType().Name, System.Convert.ToString(value, CultureInfo.InvariantCulture)!);
            case byte[] bytes:
                return new ReplValue { Kind = ReplValueKind.Bytes, Type = $"byte[{bytes.Length}]", Text = ReplValue.HexDump(bytes, bytes.Length), ByteLength = bytes.Length };
            case Enum e:
                return ReplValue.Of(ReplValueKind.Other, e.GetType().Name, e.ToString());
            case DateTime or DateTimeOffset or TimeSpan or Guid:
                return ReplValue.Of(ReplValueKind.Other, value.GetType().Name, System.Convert.ToString(value, CultureInfo.InvariantCulture)!);
            case Exception ex:
                return new ReplValue { Kind = ReplValueKind.Error, Type = ex.GetType().Name, Text = $"{ex.GetType().Name}: {ex.Message}", Stack = ex.StackTrace };
            case Delegate:
                return ReplValue.Of(ReplValueKind.Function, "delegate", "ƒ()");
        }

        if (path.Contains(value) || depth > MaxDepth || counter.Nodes > MaxNodes)
        {
            return ReplValue.Of(ReplValueKind.Object, value.GetType().Name, depth > MaxDepth ? "{…}" : "[Circular]");
        }

        path.Add(value);
        try
        {
            if (value is IDictionary dictionary)
            {
                var children = new List<ReplProperty>();
                int total = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    total++;
                    if (children.Count < ReplValue.MaxChildren)
                    {
                        children.Add(new ReplProperty(System.Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "", Convert(entry.Value, depth + 1, path, counter)));
                    }
                }

                return Container(ReplValueKind.Object, value.GetType().Name, "{", "}", children, total, named: true);
            }

            if (value is IEnumerable enumerable)
            {
                var children = new List<ReplProperty>();
                int total = 0;
                foreach (object? item in enumerable)
                {
                    total++;
                    if (children.Count < ReplValue.MaxChildren)
                    {
                        children.Add(new ReplProperty(children.Count.ToString(CultureInfo.InvariantCulture), Convert(item, depth + 1, path, counter)));
                    }
                    else if (total > ReplValue.MaxChildren * 100)
                    {
                        break;
                    }
                }

                return Container(ReplValueKind.Array, $"{value.GetType().Name}({total})", "[", "]", children, total, named: false);
            }

            {
                var children = new List<ReplProperty>();
                foreach (PropertyInfo p in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead && p.Name != "EqualityContract").Take(ReplValue.MaxChildren))
                {
                    object? item;
                    try
                    {
                        item = p.GetValue(value);
                    }
                    catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or NotSupportedException)
                    {
                        item = "(" + (ex.InnerException ?? ex).GetType().Name + ")";
                    }

                    children.Add(new ReplProperty(p.Name, Convert(item, depth + 1, path, counter)));
                }

                return Container(ReplValueKind.Object, value.GetType().Name, "{", "}", children, children.Count, named: true);
            }
        }
        finally
        {
            path.Remove(value);
        }
    }

    private static ReplValue Container(ReplValueKind kind, string type, string open, string close, List<ReplProperty> children, int total, bool named)
    {
        var preview = new System.Text.StringBuilder(open);
        foreach (ReplProperty c in children)
        {
            if (preview.Length > 100)
            {
                preview.Append(", …");
                break;
            }

            if (preview.Length > 1)
            {
                preview.Append(", ");
            }

            if (named)
            {
                preview.Append(c.Name).Append(": ");
            }

            preview.Append(c.Value.Text.Contains('\n', StringComparison.Ordinal) ? c.Value.Type : c.Value.Text);
        }

        if (total > children.Count && preview.Length <= 100)
        {
            preview.Append(", …");
        }

        preview.Append(close);
        return new ReplValue { Kind = kind, Type = type, Text = preview.ToString(), Children = children, ChildCount = total };
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
}
