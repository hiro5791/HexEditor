using System.Text;
using HexEditor.Core.Compare;
using HexEditor.Core.Sources;

namespace HexEditor.Cli.Commands;

/// <summary><c>hexed compare</c>: 2 つのファイルの比較 (AUTO-40)。比較は GUI と同じ Core の <see cref="DataComparer"/> を使う。</summary>
internal sealed class CompareCommand : ISubcommand
{
    public string Name => "compare";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        OptionSpec.Value("mode"),
        OptionSpec.Value("range1"),
        OptionSpec.Value("range2"),
        OptionSpec.Value("max"),
        OptionSpec.Flag("brief"),
    ];

    /// <summary><c>--brief</c> の単純比較で、1 回に比べる長さ (最初の差分が先頭にあれば、この分だけ読んで終わる)。</summary>
    private const long BriefWindow = 8L * 1024 * 1024;

    public int Run(CommandContext ctx)
    {
        if (ctx.Format is not ("text" or "json"))
        {
            throw CliException.Usage("Cli_Error_FormatNotSupported", ctx.Format, Name, "text, json");
        }

        if (ctx.Args.Positional.Count != 2)
        {
            throw CliException.Usage("Cli_Error_CompareTwoFiles");
        }

        CompareMethod method = (ctx.Args.Value("mode") ?? "byte").ToLowerInvariant() switch
        {
            "byte" => CompareMethod.Simple,
            "resync" => CompareMethod.InsertDelete,
            var other => throw CliException.Usage("Cli_Error_UnknownMode", other),
        };
        string path1 = ctx.Host.Resolve(ctx.Args.Positional[0]);
        string path2 = ctx.Host.Resolve(ctx.Args.Positional[1]);
        ctx.RejectOutputOverInput([path1, path2]);
        long max = ctx.Args.Value("max") is { } m ? Math.Max(0, ctx.Evaluate("--max", m, 0)) : 1000;

        using FileByteSource source1 = CliFiles.OpenSource(path1);
        using FileByteSource source2 = CliFiles.OpenSource(path2);
        ICompareData data1 = CompareData.FromSource(source1);
        ICompareData data2 = CompareData.FromSource(source2);
        (long start1, long length1) = ctx.Args.Value("range1") is { } r1 ? ctx.EvaluateRange("--range1", r1, data1.Length) : (0, data1.Length);
        (long start2, long length2) = ctx.Args.Value("range2") is { } r2 ? ctx.EvaluateRange("--range2", r2, data2.Length) : (0, data2.Length);
        var left = new CompareRange(data1, start1, length1);
        var right = new CompareRange(data2, start2, length2);
        var options = new CompareOptions { Method = method };

        if (ctx.Args.Flag("brief"))
        {
            return Brief(ctx, options, left, right);
        }

        using var result = new CompareResult(method, left, right);
        ctx.Run(Path.GetFileName(path1), Math.Max(length1, length2), op =>
        {
            DataComparer.Run(options, result, ctx.Cancellation, position => op.Report(Math.Min(position, Math.Max(length1, length2))));
            return 0;
        });

        long count = result.Diffs.Count;
        long unreadable = result.CountOf(DiffKind.Unreadable);
        if (ctx.Json)
        {
            WriteJson(ctx, result, method, ctx.Args.Positional[0], ctx.Args.Positional[1], max);
        }
        else
        {
            WriteText(ctx, result, max);
        }

        if (unreadable > 0)
        {
            ctx.Warn("Cli_Compare_Unreadable", unreadable);
            return ExitCodes.Io;
        }

        return count == 0 ? ExitCodes.Success : ExitCodes.Negative;
    }

    private static string Hex(long value) => "0x" + value.ToString("X", System.Globalization.CultureInfo.InvariantCulture);

    private static string KindLabel(CommandContext ctx, DiffKind kind) => ctx.M.Get("Cli_Compare_Kind_" + DiffExport.KindName(kind));

    private static void WriteText(CommandContext ctx, CompareResult result, long max)
    {
        long count = result.Diffs.Count;
        long shown = Math.Min(count, max);
        long index = 0;
        foreach (DiffRange d in result.Diffs.Enumerate())
        {
            if (index++ >= shown)
            {
                break;
            }

            var line = new StringBuilder();
            line.Append(KindLabel(ctx, d.Kind)).Append("  ").Append(Hex(d.LeftOffset)).Append("  ").Append(Hex(d.RightOffset)).Append("  ").Append(d.MaxLength);
            if (d.Kind != DiffKind.Unreadable && d.MaxLength <= DiffExport.PreviewBytes)
            {
                string l = DiffExport.PreviewHex(result.Left, d.LeftOffset, d.LeftLength);
                string r = DiffExport.PreviewHex(result.Right, d.RightOffset, d.RightLength);
                line.Append("  ").Append(l.Length == 0 ? "-" : l).Append("  ").Append(r.Length == 0 ? "-" : r);
            }

            ctx.Out.Write(line.Append('\n').ToString());
        }

        if (count > shown)
        {
            ctx.Out.Write(ctx.M.Format("Cli_Compare_More", count - shown) + "\n");
        }

        ctx.Out.Write(ctx.M.Format("Cli_Compare_Summary", count, result.DifferentBytes) + "\n");
    }

    private static void WriteJson(CommandContext ctx, CompareResult result, CompareMethod method, string name1, string name2, long max)
    {
        using var json = new JsonOutput(ctx.Out, "compare");
        json.W.WriteString("mode", method == CompareMethod.Simple ? "byte" : "resync");
        WriteSide(json, "left", name1, result.Left);
        WriteSide(json, "right", name2, result.Right);
        long count = result.Diffs.Count;
        json.W.WriteBoolean("identical", count == 0);
        json.W.WriteNumber("count", count);
        json.W.WriteNumber("differentBytes", result.DifferentBytes);
        json.W.WriteBoolean("truncated", count > max);

        // 差分の配列は GUI の結果一覧のエクスポート (JSON) と同じ形 (AUTO-40 の受け入れ基準 3)。
        using var buffer = new MemoryStream();
        DiffExport.WriteJson(result, Enumerable.Range(0, (int)Math.Min(count, max)).Select(i => (long)i), buffer, ctx.Cancellation);
        json.W.WritePropertyName("differences");
        json.W.WriteRawValue(buffer.ToArray(), skipInputValidation: true);
        json.Finish();
    }

    private static void WriteSide(JsonOutput json, string name, string path, CompareRange range)
    {
        json.W.WriteStartObject(name);
        json.W.WriteString("path", path);
        json.W.WriteNumber("offset", range.Start);
        json.W.WriteNumber("length", range.Length);
        json.W.WriteEndObject();
    }

    /// <summary>
    /// <c>--brief</c>: 一致・不一致だけを 1 行で出し、最初の差分で比較を打ち切る (AUTO-40 の仕様 4)。単純比較は窓に分けて比べるため、
    /// 先頭に差分があれば 100 GB のファイルでもすぐ終わる。挿入・削除を考慮した比較は全体を比べてから結果だけを出す。
    /// </summary>
    private static int Brief(CommandContext ctx, CompareOptions options, CompareRange left, CompareRange right)
    {
        long? firstDifference = null;
        if (options.Method == CompareMethod.Simple)
        {
            firstDifference = ctx.Run(Path.GetFileName(ctx.Args.Positional[0]), Math.Min(left.Length, right.Length), op => FirstDifference(ctx, options, left, right, op));
        }
        else
        {
            using var whole = new CompareResult(options.Method, left, right);
            ctx.Run(Path.GetFileName(ctx.Args.Positional[0]), Math.Max(left.Length, right.Length), op =>
            {
                DataComparer.Run(options, whole, ctx.Cancellation, position => op.Report(position));
                return 0;
            });
            if (whole.Diffs.Count > 0)
            {
                firstDifference = whole.Diffs[0].LeftOffset;
            }
        }

        if (ctx.Json)
        {
            using var json = new JsonOutput(ctx.Out, "compare");
            json.W.WriteBoolean("identical", firstDifference is null);
            if (firstDifference is { } at)
            {
                json.W.WriteNumber("firstDifference", at);
            }

            json.Finish();
        }
        else
        {
            ctx.Out.Write((firstDifference is { } offset ? ctx.M.Format("Cli_Compare_BriefDiffer", Hex(offset)) : ctx.M.Get("Cli_Compare_BriefSame")) + "\n");
        }

        return firstDifference is null ? ExitCodes.Success : ExitCodes.Negative;
    }

    private static long? FirstDifference(CommandContext ctx, CompareOptions options, CompareRange left, CompareRange right,
        Core.Operations.LongRunningOperation op)
    {
        long common = Math.Min(left.Length, right.Length);
        for (long position = 0; position < common; position += BriefWindow)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            long length = Math.Min(BriefWindow, common - position);
            using var window = new CompareResult(CompareMethod.Simple,
                new CompareRange(left.Data, left.Start + position, length), new CompareRange(right.Data, right.Start + position, length));
            DataComparer.Run(options, window, ctx.Cancellation);
            if (window.Diffs.Count > 0)
            {
                return window.Diffs[0].LeftOffset;
            }

            op.Report(position + length);
        }

        return left.Length == right.Length ? null : left.Start + common;
    }
}
