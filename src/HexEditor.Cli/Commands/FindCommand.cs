using System.Globalization;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;

namespace HexEditor.Cli.Commands;

/// <summary><c>hexed find</c>: 検索して一致の位置を出力 (AUTO-44 の 2)。検索は GUI と同じ Core の <see cref="SearchEngine"/> を使う。</summary>
internal sealed class FindCommand : ISubcommand
{
    public string Name => "find";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        OptionSpec.Value("hex"),
        OptionSpec.Value("text"),
        OptionSpec.Value("regex"),
        OptionSpec.Value("encoding"),
        OptionSpec.Flag("case-sensitive"),
        OptionSpec.Value("range"),
        OptionSpec.Value("max"),
        OptionSpec.Flag("recurse", "r"),
    ];

    private const int Page = 10_000;

    public int Run(CommandContext ctx)
    {
        if (ctx.Format is not ("text" or "json"))
        {
            throw CliException.Usage("Cli_Error_FormatNotSupported", ctx.Format, Name, "text, json");
        }

        int kinds = new[] { "hex", "text", "regex" }.Count(ctx.Args.Has);
        if (kinds != 1)
        {
            throw CliException.Usage("Cli_Error_FindOnePattern");
        }

        if (ctx.Args.Positional.Count == 0)
        {
            throw CliException.Usage("Cli_Error_NoInput", Name);
        }

        long max = ctx.Args.Value("max") is { } m ? Math.Max(0, ctx.Evaluate("--max", m, 0)) : long.MaxValue;
        SearchQuery query = BuildQuery(ctx);
        BuiltSearchPattern built;
        try
        {
            built = SearchQueryBuilder.Build(query, new SearchQueryEnvironment());
        }
        catch (PatternException ex)
        {
            throw CliException.Usage("Cli_Error_Pattern", ex.Error.ToString());
        }

        IReadOnlyList<PathExpander.Entry> entries = PathExpander.Expand(ctx.Host, ctx.Args.Positional, ctx.Args.Flag("recurse"), allowStdin: false);
        ctx.RejectOutputOverInput(entries.Select(e => e.FullPath));
        JsonOutput? json = ctx.Json ? new JsonOutput(ctx.Out, "find") : null;
        json?.W.WriteStartArray("matches");
        long found = 0;
        bool ioError = false;
        foreach (PathExpander.Entry entry in entries)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            if (found >= max)
            {
                break;
            }

            try
            {
                found += SearchFile(ctx, entry, built, max - found, json);
            }
            catch (CliException ex) when (ex.ExitCode is ExitCodes.Io or ExitCodes.Denied)
            {
                ctx.Err.WriteLine(ctx.M.Format(ex.MessageKey, ex.Arguments));
                ioError = true;
            }
        }

        if (json is not null)
        {
            json.W.WriteEndArray();
            json.W.WriteNumber("count", found);
            json.Finish();
            json.Dispose();
        }

        if (ioError)
        {
            return ExitCodes.Io;
        }

        return found > 0 ? ExitCodes.Success : ExitCodes.Negative;
    }

    private static SearchQuery BuildQuery(CommandContext ctx)
    {
        bool caseSensitive = ctx.Args.Flag("case-sensitive");
        string encoding = ctx.Args.Value("encoding") ?? "utf-8";
        if (ctx.Args.Has("encoding") && TextEncodings.FromCatalogId(encoding) is null)
        {
            throw CliException.Usage("Cli_Error_UnknownEncoding", encoding);
        }

        if (ctx.Args.Value("hex") is { } hex)
        {
            return new SearchQuery { Kind = SearchKind.Hex, Text = hex };
        }

        if (ctx.Args.Value("text") is { } text)
        {
            return new SearchQuery { Kind = SearchKind.Text, Text = text, EncodingId = encoding, CaseSensitive = caseSensitive };
        }

        return new SearchQuery
        {
            Kind = SearchKind.RegexText,
            Text = ctx.Args.Value("regex")!,
            EncodingId = encoding,
            CaseSensitive = caseSensitive,
            RegexIgnoreCase = !caseSensitive,
        };
    }

    /// <summary>1 つのファイルを検索して一致を出力する。見つかった件数を返す。</summary>
    private static long SearchFile(CommandContext ctx, PathExpander.Entry entry, BuiltSearchPattern built, long remaining, JsonOutput? json)
    {
        if (Directory.Exists(entry.FullPath))
        {
            throw CliException.Io("Cli_Error_IsDirectory", entry.Display);
        }

        using Document doc = CliFiles.OpenDocument(entry.FullPath);
        DocumentSnapshot snapshot = doc.Current;
        SearchScope scope = SearchScope.WholeDocument;
        if (ctx.Args.Value("range") is { } range)
        {
            (long offset, long length) = ctx.EvaluateRange("--range", range, snapshot.Length);
            scope = SearchScope.Of(offset, length);
        }

        var options = new SearchOptions
        {
            Scope = scope,
            MaxMatches = remaining,
            OnUnreadable = _ => UnreadableAction.Skip,
        };
        using SearchResults results = ctx.Run(entry.Display, scope.TotalLength(snapshot.Length),
            op => SearchEngine.FindAll(snapshot, built.Pattern, options, op, ctx.Cancellation));
        long count = results.LongCount;
        for (long index = 0; index < count; index += Page)
        {
            foreach (SearchMatch match in results.GetRange(index, (int)Math.Min(Page, count - index)))
            {
                if (json is not null)
                {
                    json.W.WriteStartObject();
                    json.W.WriteString("path", entry.Display);
                    json.W.WriteNumber("offset", match.Offset);
                    json.W.WriteNumber("length", match.Length);
                    json.W.WriteEndObject();
                }
                else
                {
                    ctx.Out.Write(string.Create(CultureInfo.InvariantCulture, $"{entry.Display}  0x{match.Offset:X}  {match.Length}\n"));
                }
            }
        }

        if (results.SkippedRanges.Count > 0)
        {
            ctx.Warn("Cli_Find_Skipped", entry.Display, results.SkippedRanges.Count);
        }

        return count;
    }
}
