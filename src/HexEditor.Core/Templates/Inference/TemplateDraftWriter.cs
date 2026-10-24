using System.Globalization;
using System.Text;

namespace HexEditor.Core.Templates.Inference;

/// <summary>下書きの先頭のコメントと配列の宣言に使う情報 (TPL-40 の仕様 5)。</summary>
public sealed record TemplateDraftInfo(string DocumentName, long Start, int RecordLength, long RecordCount, DateTime Created, bool BigEndian)
{
    public long End => Start + ((long)RecordLength * RecordCount) - 1;
}

/// <summary>
/// テンプレートの推定の下書き (HXT。TPL-40 の仕様 5) を作る。先頭のコメント、<c>#pragma name</c> / <c>endian</c>、列挙・フラグの型、
/// レコードの構造体、配列の宣言 (<c>Record records[数] @ 範囲の開始;</c>) の順。各フィールドには種類・確度・理由の最初の文を
/// <c>[[comment("…")]]</c> で付け、オフセットのフィールドには <c>format("hex")</c> を付ける。文は表示言語で書く (<paramref name="text"/>)。
/// </summary>
public static class TemplateDraftWriter
{
    public static string Write(IReadOnlyList<InferenceRow> rows, TemplateDraftInfo info, Func<InferenceMessage, string> text)
    {
        var sb = new StringBuilder();
        string Hex(long v) => "0x" + v.ToString("X", CultureInfo.InvariantCulture);
        sb.Append("// ").Append(OneLine(text(new InferenceMessage("TplInfer_Draft_Header1")))).Append('\n');
        sb.Append("// ").Append(OneLine(text(new InferenceMessage("TplInfer_Draft_Header2", info.DocumentName, Hex(info.Start), Hex(info.End),
            info.RecordLength, info.RecordCount, info.Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))))).Append('\n');
        sb.Append("#pragma name \"").Append(Escape(text(new InferenceMessage("TplInfer_Draft_PragmaName", info.DocumentName)))).Append("\"\n");
        sb.Append("#pragma endian ").Append(info.BigEndian ? "big" : "little").Append("\n\n");

        // 列挙・フラグの型
        foreach (InferenceRow row in rows.Where(r => r.Type.IsEnumOrFlags && r.TypeName is not null))
        {
            string baseType = InferenceType.UnsignedOf(row.Size);
            bool flags = row.Type.Id == "flags";
            sb.Append(flags ? "flags " : "enum ").Append(row.TypeName).Append(" : ").Append(baseType).Append(" {\n");
            foreach (ulong value in row.Values.Order())
            {
                if (flags)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(value);
                    sb.Append("    bit").Append(bit.ToString(CultureInfo.InvariantCulture)).Append(" = 0x")
                        .Append(value.ToString("X" + (row.Size * 2).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)).Append(",\n");
                }
                else
                {
                    string v = value.ToString(CultureInfo.InvariantCulture);
                    sb.Append("    v").Append(v).Append(" = ").Append(v).Append(",\n");
                }
            }

            sb.Append("};\n\n");
        }

        // レコードの構造体
        var lines = new List<(string Type, string Name, string Attributes)>();
        foreach (InferenceRow row in rows)
        {
            if (row.Type.Id == "padding")
            {
                lines.Add(("padding[" + row.Size.ToString(CultureInfo.InvariantCulture) + "]", string.Empty, string.Empty));
                continue;
            }

            string prefix = row.Type.IsEndianSensitive && row.BigEndian is bool big && big != info.BigEndian ? (big ? "be " : "le ") : string.Empty;
            string type = row.Type.Id switch
            {
                "char" or "char16" => row.Type.Id,
                "bytes" => "u8",
                "enum" or "flags" => row.TypeName ?? InferenceType.UnsignedOf(row.Size),
                _ => row.Type.Id,
            };
            string name = row.Name ?? FieldInferrer.BaseName(row.Kind, row.Offset) ?? "field";
            if (row.Type.IsArray)
            {
                name += "[" + row.Type.ArrayLength.ToString(CultureInfo.InvariantCulture) + "]";
            }

            string reason = FirstSentence(text(row.Reason));
            string comment = text(new InferenceMessage("TplInfer_Draft_Comment", text(new InferenceMessage("TplInfer_Kind_" + row.Kind)),
                text(new InferenceMessage("TplInfer_Confidence_" + row.Confidence)), reason));
            string attributes = (row.Kind == InferredKind.Offset && row.Type.Id is not ("char" or "char16" or "bytes" or "padding") ? "format(\"hex\"), " : string.Empty)
                + "comment(\"" + Escape(comment) + "\")";
            lines.Add((prefix + type, name, "[[" + attributes + "]]"));
        }

        int typeWidth = lines.Where(l => l.Name.Length > 0).Select(l => l.Type.Length).DefaultIfEmpty(0).Max();
        int nameWidth = lines.Select(l => l.Name.Length).DefaultIfEmpty(0).Max();
        sb.Append("struct Record {\n");
        foreach ((string type, string name, string attributes) in lines)
        {
            sb.Append("    ");
            if (name.Length == 0)
            {
                sb.Append(type).Append(";\n");
                continue;
            }

            sb.Append(type.PadRight(typeWidth)).Append(' ').Append(name.PadRight(nameWidth)).Append(' ').Append(attributes).Append(";\n");
        }

        sb.Append("};\n\n");
        sb.Append("Record records[").Append(info.RecordCount.ToString(CultureInfo.InvariantCulture)).Append("] @ ").Append(Hex(info.Start)).Append(";\n");
        return sb.ToString();
    }

    /// <summary>文字列のリテラルの中に入れる (\ と " と改行をエスケープする)。</summary>
    public static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string OneLine(string s) => s.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ');

    /// <summary>理由の最初の文 (。/ ". " までを含む)。</summary>
    public static string FirstSentence(string s)
    {
        int ja = s.IndexOf('。', StringComparison.Ordinal);
        if (ja >= 0 && ja < s.Length - 1)
        {
            return s[..(ja + 1)];
        }

        int en = s.IndexOf(". ", StringComparison.Ordinal);
        return en >= 0 ? s[..(en + 1)] : s;
    }
}
