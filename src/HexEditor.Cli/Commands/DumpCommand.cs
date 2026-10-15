using System.Globalization;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.View;

namespace HexEditor.Cli.Commands;

/// <summary><c>hexed dump</c>: Hex ダンプの出力 (AUTO-39)。ストリームで 1 行ずつ書くため、メモリはファイルの大きさに関係しない。</summary>
internal sealed class DumpCommand : ISubcommand
{
    public string Name => "dump";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        OptionSpec.Value("range"),
        OptionSpec.Value("width"),
        OptionSpec.Value("group"),
        OptionSpec.Value("encoding"),
        OptionSpec.Value("base-address"),
        OptionSpec.Value("style"),
        OptionSpec.Flag("squeeze"),
    ];

    private const int ReadChunk = 1 << 20;

    public int Run(CommandContext ctx)
    {
        if (ctx.Format != "text")
        {
            throw CliException.Usage("Cli_Error_FormatNotSupported", ctx.Format, Name, "text");
        }

        if (ctx.Args.Positional.Count != 1)
        {
            throw CliException.Usage(ctx.Args.Positional.Count == 0 ? "Cli_Error_NoInput" : "Cli_Error_TooManyInputs", Name);
        }

        string style = (ctx.Args.Value("style") ?? "default").ToLowerInvariant();
        string path = ctx.Host.Resolve(ctx.Args.Positional[0]);
        if (Directory.Exists(path))
        {
            throw CliException.Io("Cli_Error_IsDirectory", ctx.Args.Positional[0]);
        }

        ctx.RejectOutputOverInput([path]);
        TextEncoding encoding = ParseEncoding(ctx.Args.Value("encoding"));
        int width = ParseNumber(ctx, "--width", ctx.Args.Value("width"), 1, 1024, 16);
        TextDumpStyle? textStyle = style switch
        {
            "default" => TextDumpStyle.Default,
            "xxd" => TextDumpStyle.Xxd,
            "hexdump-c" => TextDumpStyle.HexdumpC,
            "plain" => TextDumpStyle.Plain,
            _ => null,
        };
        string? format = textStyle is null ? SourceFormat(style) : null;
        if (textStyle is null && format is null)
        {
            throw CliException.Usage("Cli_Error_UnknownStyle", style);
        }

        using Document doc = CliFiles.OpenDocument(path);
        DocumentSnapshot snapshot = doc.Current;
        long start = 0, length = snapshot.Length;
        if (ctx.Args.Value("range") is { } range)
        {
            (start, length) = ctx.EvaluateRange("--range", range, snapshot.Length);
        }

        long baseAddress = ctx.Args.Value("base-address") is { } b ? ctx.Evaluate("--base-address", b, snapshot.Length) : 0;
        if (textStyle is { } ts)
        {
            int group = ParseNumber(ctx, "--group", ctx.Args.Value("group"), 0, 1024, TextDumpWriter.DefaultGroup(ts));
            WriteTextDump(ctx, snapshot, ts, width, group, encoding, start, length, start + baseAddress);
        }
        else
        {
            WriteSource(ctx, snapshot, format!, start, length, baseAddress, Path.GetFileName(path));
        }

        return ExitCodes.Success;
    }

    private static void WriteTextDump(CommandContext ctx, DocumentSnapshot snapshot, TextDumpStyle style, int width, int group,
        TextEncoding encoding, long start, long length, long displayBase)
    {
        var writer = new TextDumpWriter(ctx.Out, style, width, group, encoding, displayBase, ctx.Args.Flag("squeeze"));
        ctx.Run(Path.GetFileName(snapshot.OriginalSource.DisplayName), length, op =>
        {
            byte[] buffer = new byte[ReadChunk];
            long done = 0;
            while (done < length)
            {
                int n = (int)Math.Min(buffer.Length, length - done);
                Core.Sources.ReadResult r = snapshot.Read(start + done, buffer.AsSpan(0, n));
                if (r.Unreadable.Count > 0 || r.BytesReturned < n)
                {
                    long at = start + done + (r.Unreadable.Count > 0 ? r.Unreadable[0].Offset - (start + done) : r.BytesReturned);
                    throw CliException.Io("Cli_Error_ReadAt", snapshot.OriginalSource.DisplayName, $"0x{at:X}");
                }

                writer.Write(buffer.AsSpan(0, n));
                done += n;
                op.Report(done);
            }

            writer.Finish();
            return 0;
        });
    }

    /// <summary>ソースコード・Base64 などの形式 (エクスポートと同じ部品 <see cref="Exporter"/> を使う)。</summary>
    private static string? SourceFormat(string style) => style switch
    {
        "c" => FormatIds.C,
        "cpp" => FormatIds.Cpp,
        "csharp" or "cs" => FormatIds.CSharp,
        "java" => FormatIds.Java,
        "js" or "javascript" => FormatIds.JavaScript,
        "python" or "py" => FormatIds.Python,
        "rust" or "rs" => FormatIds.Rust,
        "go" => FormatIds.Go,
        "pascal" => FormatIds.Pascal,
        "vbnet" or "vb" => FormatIds.VisualBasic,
        "base64" => FormatIds.Base64,
        "base32" => FormatIds.Base32,
        "ascii85" => FormatIds.Ascii85,
        "uu" => FormatIds.UUEncode,
        "qp" => FormatIds.QuotedPrintable,
        "hextext" => FormatIds.HexText,
        "dectext" => FormatIds.DecimalText,
        _ => null,
    };

    private static void WriteSource(CommandContext ctx, DocumentSnapshot snapshot, string format, long start, long length, long baseAddress, string fileName)
    {
        IReadOnlyList<TransferField> fields = TransferOptions.ExportFields(format, null, fileName);
        Dictionary<string, string> values = TransferOptions.Load(null, fields);
        values["newLine"] = "lf";
        values["encoding"] = "utf8";
        if (ctx.Args.Value("width") is { } width && values.ContainsKey("bytesPerLine"))
        {
            values["bytesPerLine"] = width;
        }

        ExportOptions options = TransferOptions.ToExportOptions(format, values, text => ctx.Evaluate("--style", text, snapshot.Length), new DumpOptions());
        var source = new ExportSource
        {
            Read = (offset, destination) => snapshot.Read(offset, destination),
            Length = snapshot.Length,
            BaseAddress = baseAddress,
            DataRanges = snapshot.DataRanges,
            FileName = fileName,
            Now = DateTimeOffset.Now,
        };
        try
        {
            ctx.Run(fileName, length, op =>
            {
                Exporter.WriteText(source, [(start, length)], options, ctx.Out, ctx.Cancellation, op.Report);
                return 0;
            });
        }
        catch (FormatLimitException ex)
        {
            throw CliException.Usage("Cli_Error_FormatLimit", ex.Format, $"0x{ex.Limit:X}");
        }
    }

    /// <summary>ダイアログの欄のキー (camelCase) をオプション名 (kebab-case) にする。</summary>
    internal static string ExportOptionName(string key)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in key)
        {
            if (char.IsUpper(c))
            {
                sb.Append('-').Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static TextEncoding ParseEncoding(string? id)
    {
        if (id is null)
        {
            return TextEncoding.Ascii;
        }

        string trimmed = id.Trim();
        bool known = TextEncoding.SelectableIds.Contains(trimmed, StringComparer.OrdinalIgnoreCase) || trimmed.Equals("oem", StringComparison.OrdinalIgnoreCase)
            || EncodingCatalog.Find(trimmed) is not null
            || trimmed.StartsWith("cp", StringComparison.OrdinalIgnoreCase) && int.TryParse(trimmed.AsSpan(2), out int page) && page > 0;
        return known ? TextEncoding.FromId(trimmed) : throw CliException.Usage("Cli_Error_UnknownEncoding", id);
    }

    private static int ParseNumber(CommandContext ctx, string option, string? text, int min, int max, int fallback)
    {
        if (text is null)
        {
            return fallback;
        }

        long value = ctx.Evaluate(option, text, 0);
        return value < min || value > max
            ? throw CliException.Usage("Cli_Error_NumberRange", option, text, min.ToString(CultureInfo.InvariantCulture), max.ToString(CultureInfo.InvariantCulture))
            : (int)value;
    }
}
