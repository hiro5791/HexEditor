using System.Globalization;
using System.Text;

namespace HexEditor.Core.Templates;

/// <summary>結果のエクスポートの形式 (TPL-25)。</summary>
public enum TemplateExportFormat
{
    Json,
    Csv,
}

/// <summary>
/// 結果ツリーのエクスポート (TPL-25)。JSON (仕様 2) と CSV (仕様 3: 1 行 1 ノード、列は <c>path,type,offset,size,value,display,comment</c>、
/// UTF-8 の BOM 付き、CRLF、RFC 4180 の引用)。未評価の部分はエクスポートのために評価し、ストリームに書き出す (結果全体をメモリに持たない)。
/// ファイルへの書き出しは同じフォルダの一時ファイルに書いてから置き換え、キャンセル・失敗したら書きかけのファイルを消す。
/// </summary>
public static class TemplateResultExporter
{
    /// <summary>CSV の見出しの行。</summary>
    public const string CsvHeader = "path,type,offset,size,value,display,comment";

    /// <summary>
    /// <paramref name="node"/> (適用のノードなら適用の全体、ほかは部分木) をファイルに書き出す。<paramref name="openStream"/> はテスト用
    /// (書き込みの失敗を再現する)。キャンセル・失敗の例外はそのまま投げ、書きかけのファイル (一時ファイルを含む) は残さない。
    /// </summary>
    public static void ExportToFile(TemplateNode node, string path, TemplateExportFormat format, TemplateExportOptions? options = null,
        CancellationToken cancellationToken = default, Func<string, Stream>? openStream = null)
    {
        string full = Path.GetFullPath(path);
        string temp = Path.Combine(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (Stream stream = openStream?.Invoke(temp) ?? new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                Write(node, stream, format, options, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static void Write(TemplateNode node, Stream output, TemplateExportFormat format, TemplateExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TemplateExportOptions();
        if (format == TemplateExportFormat.Json)
        {
            TemplateJsonExporter.Write(node, output, options, cancellationToken);
            return;
        }

        WriteCsv(node, output, options, cancellationToken);
    }

    /// <summary>CSV (TPL-25 の仕様 3)。</summary>
    public static void WriteCsv(TemplateNode root, Stream output, TemplateExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new TemplateExportOptions();
        TemplateExportOptions o = options;
        Exception? failure = null;
        root.Result.Invoke(() =>
        {
            try
            {
                using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen: true)
                {
                    NewLine = "\r\n",
                };
                writer.WriteLine(CsvHeader);
                long count = 0;
                Walk(root, o, 0, cancellationToken, node =>
                {
                    if (node.Kind == TemplateNodeKind.Application)
                    {
                        return;
                    }

                    writer.Write(Csv(node.Path));
                    writer.Write(',');
                    writer.Write(Csv(node.TypeName));
                    writer.Write(',');
                    writer.Write(Number(node.Offset, o));
                    writer.Write(',');
                    writer.Write(Number(node.Size, o));
                    writer.Write(',');
                    writer.Write(Csv(ValueText(node)));
                    writer.Write(',');
                    writer.Write(Csv(SafeDisplay(node, o)));
                    writer.Write(',');
                    writer.Write(Csv(node.Comment ?? string.Empty));
                    writer.WriteLine();
                    if ((++count & 4095) == 0)
                    {
                        o.Progress?.Invoke(count);
                    }
                });
                writer.Flush();
                o.Progress?.Invoke(count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                failure = ex;
            }

            return 0;
        });
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }

    private static void Walk(TemplateNode node, TemplateExportOptions o, int depth, CancellationToken ct, Action<TemplateNode> visit)
    {
        ct.ThrowIfCancellationRequested();
        visit(node);
        bool expand = node.HasChildren && (o.MaxDepth is null || depth < o.MaxDepth) && !node.Attributes.Sealed && !node.IsByteOrCharArray;
        if (!expand)
        {
            return;
        }

        long count = node.ChildCount;
        long limit = node.Kind == TemplateNodeKind.Array && o.MaxArrayElements is { } m ? Math.Min(m, count) : count;
        IReadOnlyList<TemplateNode> children = node.Children;
        for (long i = 0; i < limit; i++)
        {
            TemplateNode child = i < children.Count ? children[(int)i] : node.GetChild(i);
            if (!child.Attributes.Export || (child.Attributes.Hidden && !o.IncludeHidden))
            {
                continue;
            }

            Walk(child, o, depth + 1, ct, visit);
        }
    }

    private static string SafeDisplay(TemplateNode node, TemplateExportOptions o)
    {
        try
        {
            return node.GetDisplay(o.Display);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return string.Empty;
        }
    }

    private static string ValueText(TemplateNode node)
    {
        if (node.Kind is TemplateNodeKind.Struct or TemplateNodeKind.Union or TemplateNodeKind.Section or TemplateNodeKind.Bitfield
            || (node.Kind == TemplateNodeKind.Array && !node.IsByteOrCharArray))
        {
            return string.Empty;
        }

        object? value = node.Kind == TemplateNodeKind.Pointer ? node.RawValue : node.Value;
        return value switch
        {
            null => string.Empty,
            byte[] bytes => Convert.ToHexString(bytes),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    private static string Number(long value, TemplateExportOptions o) =>
        o.HexOffsets ? "0x" + value.ToString("X", CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>RFC 4180 の引用 (区切り・引用符・改行を含む場合)。</summary>
    public static string Csv(string text) =>
        text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても続ける
        }
    }
}
