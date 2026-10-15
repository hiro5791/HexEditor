using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HexEditor.Core.Templates;

/// <summary>結果のエクスポートの設定 (TPL-25 の仕様 4)。</summary>
public sealed record TemplateExportOptions
{
    /// <summary>配列の要素の最大数 (超えた要素を省き <c>"truncated": true</c> を付ける)。null は制限なし。</summary>
    public long? MaxArrayElements { get; init; }

    /// <summary>階層の最大の深さ。null は制限なし。</summary>
    public int? MaxDepth { get; init; }

    /// <summary><c>hidden</c> のパターンを含める (<c>export(false)</c> は常に含めない)。</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>オフセットを 16 進文字列で書く (既定は 10 進の数値)。</summary>
    public bool HexOffsets { get; init; }

    /// <summary><c>display</c> の地域設定 (比較用の既定は en-US・UTC)。</summary>
    public TemplateDisplayContext Display { get; init; } = TemplateDisplayContext.Invariant;

    public bool Indented { get; init; } = true;
}

/// <summary>
/// 結果ツリーの JSON 形式 (TPL-25 の仕様 2)。ノードごとに <c>name</c>、<c>path</c>、<c>type</c>、<c>offset</c>、<c>size</c>、<c>value</c>、
/// <c>display</c>、<c>comment</c>、<c>children</c>。ビットフィールドは <c>bit_offset</c>・<c>bit_size</c>、エラー・警告のノードは
/// <c>error</c> / <c>warning</c> を付ける。未評価の部分はエクスポートのために評価し、ストリームに書き出す (結果全体をメモリに持たない)。
/// </summary>
public static class TemplateJsonExporter
{
    /// <summary>u8 の配列の値を JSON に書く最大のバイト数 (超える場合は display だけ)。</summary>
    public const int MaxInlineBytes = 64 * 1024;

    public static void Write(TemplateResult result, Stream output, TemplateExportOptions? options = null, CancellationToken cancellationToken = default) =>
        Write(result.Root, output, options, cancellationToken);

    public static void Write(TemplateNode node, Stream output, TemplateExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new TemplateExportOptions();
        node.Result.Invoke(() =>
        {
            using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
            {
                Indented = options.Indented,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            WriteNode(writer, node, options, 0, cancellationToken);
            writer.Flush();
            return 0;
        });
    }

    public static string ToJson(TemplateNode node, TemplateExportOptions? options = null)
    {
        using var ms = new MemoryStream();
        Write(node, ms, options);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string ToJson(TemplateResult result, TemplateExportOptions? options = null) => ToJson(result.Root, options);

    private static void WriteNode(Utf8JsonWriter w, TemplateNode node, TemplateExportOptions o, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        w.WriteStartObject();
        w.WriteString("name", node.DisplayName);
        if (node.Kind != TemplateNodeKind.Application)
        {
            w.WriteString("path", node.Path);
        }

        w.WriteString("type", node.Kind == TemplateNodeKind.Application ? "application" : node.TypeName);
        WriteOffset(w, "offset", node.Offset, o);
        WriteOffset(w, "size", node.Size, o);
        if (node.BitOffset is { } bo)
        {
            w.WriteNumber("bit_offset", bo);
            w.WriteNumber("bit_size", node.BitSize ?? 0);
        }

        if (node.Section is { } section && node.Kind != TemplateNodeKind.Section)
        {
            w.WriteString("section", section.Name);
        }

        if (node.Kind != TemplateNodeKind.Application)
        {
            object? value = node.Kind is TemplateNodeKind.Struct or TemplateNodeKind.Union or TemplateNodeKind.Bitfield
                or TemplateNodeKind.Section or TemplateNodeKind.Pointer ? null : node.Kind == TemplateNodeKind.Array && node.Value is not string ? null : node.Value;
            if (node.Kind == TemplateNodeKind.Pointer)
            {
                value = node.RawValue;
            }
            else if (node.IsByteOrCharArray && value is null && node.Size <= MaxInlineBytes)
            {
                value = node.Result.Evaluator.ArrayBytes(node);
            }

            WriteValue(w, value);
            w.WriteString("display", node.GetDisplay(o.Display));
            if (node.Comment is { } comment)
            {
                w.WriteString("comment", comment);
            }

            if (node.Pointer?.DuplicateOf is { } dup)
            {
                w.WriteString("duplicate_of", dup);
            }
        }

        TemplateDiagnostic? error = node.Diagnostics.FirstOrDefault(d => d.Severity == TemplateSeverity.Error);
        if (error is not null)
        {
            w.WriteString("error", error.Message);
        }

        TemplateDiagnostic? warning = node.Diagnostics.FirstOrDefault(d => d.Severity == TemplateSeverity.Warning);
        if (warning is not null)
        {
            w.WriteString("warning", warning.Message);
        }

        if (node.HasChildren && (o.MaxDepth is null || depth < o.MaxDepth) && !node.Attributes.Sealed && !node.IsByteOrCharArray)
        {
            long count = node.ChildCount;
            long limit = node.Kind == TemplateNodeKind.Array && o.MaxArrayElements is { } m ? Math.Min(m, count) : count;
            w.WriteStartArray("children");
            for (long i = 0; i < limit; i++)
            {
                TemplateNode child = node.GetChild(i);
                if (!child.Attributes.Export || (child.Attributes.Hidden && !o.IncludeHidden))
                {
                    continue;
                }

                WriteNode(w, child, o, depth + 1, ct);
            }

            w.WriteEndArray();
            if (limit < count)
            {
                w.WriteBoolean("truncated", true);
            }
        }

        w.WriteEndObject();
    }

    private static void WriteOffset(Utf8JsonWriter w, string name, long value, TemplateExportOptions o)
    {
        if (o.HexOffsets)
        {
            w.WriteString(name, "0x" + value.ToString("X", CultureInfo.InvariantCulture));
        }
        else
        {
            w.WriteNumber(name, value);
        }
    }

    /// <summary>数値は数値として、64 bit を超える整数・バイト列は文字列として書く (TPL-25 の仕様 2)。</summary>
    private static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case long l:
                w.WriteNumber("value", l);
                break;
            case ulong u:
                w.WriteNumber("value", u);
                break;
            case double d when double.IsFinite(d):
                w.WriteNumber("value", d);
                break;
            case double d:
                w.WriteString("value", double.IsNaN(d) ? "NaN" : d > 0 ? "Inf" : "-Inf");
                break;
            case bool b:
                w.WriteBoolean("value", b);
                break;
            case string s:
                w.WriteString("value", s);
                break;
            case byte[] bytes:
                w.WriteString("value", Convert.ToHexString(bytes));
                break;
            case DateTime dt:
                w.WriteString("value", dt.ToString("o", CultureInfo.InvariantCulture));
                break;
            default:
                w.WriteString("value", Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
