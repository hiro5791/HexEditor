using System.Globalization;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Sources;

namespace HexEditor.Cli.Commands;

/// <summary><c>hexed hash</c>: ハッシュ・チェックサムの計算と検証 (AUTO-38)。計算は Core の <see cref="HashEngine"/> をそのまま使う。</summary>
internal sealed class HashCommand : ISubcommand
{
    public string Name => "hash";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        OptionSpec.Value("algorithm", "a", repeatable: true),
        OptionSpec.Value("range"),
        OptionSpec.Value("check"),
        OptionSpec.Flag("recurse", "r"),
    ];

    private static readonly string[] Formats = ["text", "json", "gnu", "bsd"];

    public int Run(CommandContext ctx)
    {
        if (!Formats.Contains(ctx.Format))
        {
            throw CliException.Usage("Cli_Error_FormatNotSupported", ctx.Format, Name, string.Join(", ", Formats));
        }

        IReadOnlyList<string> algorithmArgs = ctx.Args.Values("algorithm");
        if (algorithmArgs.Count == 1 && algorithmArgs[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            ListAlgorithms(ctx);
            return ExitCodes.Success;
        }

        IReadOnlyList<HashAlgorithmChoice> algorithms = ParseAlgorithms(ctx, algorithmArgs);
        if (ctx.Args.Value("check") is { } checkFile)
        {
            return Check(ctx, checkFile, algorithmArgs.Count > 0 ? algorithms : null);
        }

        if (ctx.Args.Positional.Count == 0)
        {
            throw CliException.Usage("Cli_Error_NoInput", Name);
        }

        if (ctx.Format == "gnu" && algorithms.Count != 1)
        {
            throw CliException.Usage("Cli_Error_GnuOneAlgorithm");
        }

        IReadOnlyList<PathExpander.Entry> entries = PathExpander.Expand(ctx.Host, ctx.Args.Positional, ctx.Args.Flag("recurse"), allowStdin: true);
        ctx.RejectOutputOverInput(entries.Where(e => !e.IsStdin).Select(e => e.FullPath));
        string? range = ctx.Args.Value("range");
        if (range is not null && entries.Any(e => e.IsStdin))
        {
            throw CliException.Usage("Cli_Error_RangeStdin");
        }

        var results = new List<FileResult>();
        bool ioError = false;
        foreach (PathExpander.Entry entry in entries)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            FileResult result = Hash(ctx, entry, algorithms, range);
            ioError |= result.Error is not null;
            results.Add(result);
            if (!ctx.Json)
            {
                WriteText(ctx, result);
            }
        }

        if (ctx.Json)
        {
            WriteJson(ctx, results);
        }

        return ioError ? ExitCodes.Io : ExitCodes.Success;
    }

    private sealed record FileResult(string Display, long Length, IReadOnlyList<(HashAlgorithmChoice Choice, string Value)> Values, string? Error);

    private static IReadOnlyList<HashAlgorithmChoice> ParseAlgorithms(CommandContext ctx, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return [new HashAlgorithmChoice(HashCatalog.Get("sha256"))];
        }

        var choices = new List<HashAlgorithmChoice>();
        foreach (string token in args.SelectMany(a => a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            HashAlgorithmInfo info = HashCatalog.Find(token) ?? throw CliException.Usage("Cli_Error_UnknownAlgorithm", token);
            if (!info.IsAvailable)
            {
                throw CliException.Usage("Cli_Error_AlgorithmUnavailable", token);
            }

            if (!choices.Any(c => c.Algorithm == info))
            {
                choices.Add(new HashAlgorithmChoice(info));
            }
        }

        return choices.Count == 0 ? throw CliException.Usage("Cli_Error_UnknownAlgorithm", string.Join(",", args)) : choices;
    }

    private static void ListAlgorithms(CommandContext ctx)
    {
        if (ctx.Json)
        {
            using var json = new JsonOutput(ctx.Out, "hash");
            json.W.WriteStartArray("algorithms");
            foreach (HashAlgorithmInfo a in HashCatalog.All)
            {
                json.W.WriteStartObject();
                json.W.WriteString("id", a.Id);
                json.W.WriteString("name", a.Name);
                json.W.WriteNumber("bits", a.Bits);
                json.W.WriteEndObject();
            }

            json.W.WriteEndArray();
            json.Finish();
            return;
        }

        foreach (HashAlgorithmInfo a in HashCatalog.All)
        {
            ctx.Out.Write($"{a.Id}  {a.Name}  {a.Bits}\n");
        }
    }

    private static string FormatValue(HashResultRow row) =>
        HashValueFormatter.Format(row.Value, row.IsNumeric, HashValueFormat.HexLower);

    private static FileResult Hash(CommandContext ctx, PathExpander.Entry entry, IReadOnlyList<HashAlgorithmChoice> algorithms, string? range)
    {
        try
        {
            if (entry.IsStdin)
            {
                return HashStdin(ctx, entry, algorithms);
            }

            if (Directory.Exists(entry.FullPath))
            {
                throw CliException.Io("Cli_Error_IsDirectory", entry.Display);
            }

            using Document doc = CliFiles.OpenDocument(entry.FullPath);
            DocumentSnapshot snapshot = doc.Current;
            IReadOnlyList<HashRange> ranges = [];
            if (range is not null)
            {
                (long offset, long length) = ctx.EvaluateRange("--range", range, snapshot.Length);
                ranges = [new HashRange(offset, length)];
            }

            var request = new HashRequest { Algorithms = algorithms, Ranges = ranges };
            long total = HashEngine.TotalBytes(snapshot, request);
            HashComputation computation = ctx.Run(entry.Display, total, op => HashEngine.Compute(snapshot, request, op, ctx.Cancellation));
            return new FileResult(entry.Display, snapshot.Length,
                [.. computation.Rows.Select(r => (r.Choice, FormatValue(r)))], null);
        }
        catch (HashReadException ex)
        {
            return Failed(ctx, entry, ctx.M.Format("Cli_Error_ReadAt", entry.Display, $"0x{ex.Range.Offset:X}"));
        }
        catch (CliException ex) when (ex.ExitCode is ExitCodes.Io or ExitCodes.Denied)
        {
            return Failed(ctx, entry, ctx.M.Format(ex.MessageKey, ex.Arguments));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(ctx, entry, ctx.M.Format("Cli_Error_CannotRead", entry.Display, ex.Message));
        }
    }

    /// <summary>読めないファイルはそのファイルの行にエラーを出して続ける (AUTO-38 の「エラー」)。</summary>
    private static FileResult Failed(CommandContext ctx, PathExpander.Entry entry, string message)
    {
        ctx.Err.WriteLine(message);
        return new FileResult(entry.Display, 0, [], message);
    }

    private static FileResult HashStdin(CommandContext ctx, PathExpander.Entry entry, IReadOnlyList<HashAlgorithmChoice> algorithms)
    {
        IHasher[] hashers = [.. algorithms.Select(a => a.Algorithm.CreateHasher(a.Parameters))];
        using Stream input = ctx.Host.OpenInput();
        byte[] buffer = new byte[1 << 20];
        long total = 0;
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            foreach (IHasher hasher in hashers)
            {
                hasher.Append(buffer.AsSpan(0, n));
            }

            total += n;
        }

        var values = new List<(HashAlgorithmChoice, string)>();
        for (int i = 0; i < hashers.Length; i++)
        {
            byte[] value = hashers[i].Finish();
            values.Add((algorithms[i], HashValueFormatter.Format(value, algorithms[i].Algorithm.IsNumericFor(algorithms[i].Parameters), HashValueFormat.HexLower)));
        }

        return new FileResult(entry.Display, total, values, null);
    }

    private static string Slash(string path) => path.Replace('\\', '/');

    private static void WriteText(CommandContext ctx, FileResult result)
    {
        foreach ((HashAlgorithmChoice choice, string value) in result.Values)
        {
            string line = ctx.Format switch
            {
                "gnu" => $"{value}  {Slash(result.Display)}",
                "bsd" => $"{choice.Algorithm.Id.ToUpperInvariant()} ({Slash(result.Display)}) = {value}",
                _ => $"{choice.Algorithm.Id}  {value}  {result.Display}",
            };
            ctx.Out.Write(line);
            ctx.Out.Write('\n');
        }
    }

    private static void WriteJson(CommandContext ctx, List<FileResult> results)
    {
        using var json = new JsonOutput(ctx.Out, "hash");
        json.W.WriteStartArray("files");
        foreach (FileResult r in results)
        {
            json.W.WriteStartObject();
            json.W.WriteString("path", r.Display);
            if (r.Error is not null)
            {
                json.W.WriteString("error", r.Error);
            }
            else
            {
                json.W.WriteNumber("length", r.Length);
                json.W.WriteStartArray("hashes");
                foreach ((HashAlgorithmChoice choice, string value) in r.Values)
                {
                    json.W.WriteStartObject();
                    json.W.WriteString("algorithm", choice.Algorithm.Id);
                    json.W.WriteString("value", value);
                    json.W.WriteEndObject();
                }

                json.W.WriteEndArray();
            }

            json.W.WriteEndObject();
        }

        json.W.WriteEndArray();
        json.Finish();
    }

    // ---- 検証 (--check) ----

    private int Check(CommandContext ctx, string checkFile, IReadOnlyList<HashAlgorithmChoice>? forced)
    {
        string path = ctx.Host.Resolve(checkFile);
        string content;
        try
        {
            content = File.ReadAllText(path, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw CliException.Io("Cli_Error_FileNotFound", checkFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw CliException.Io("Cli_Error_CannotRead", checkFile, ex.Message);
        }

        IReadOnlyList<ChecksumEntry> entries = ChecksumFile.Parse(content, Path.GetExtension(path));
        if (entries.Count == 0)
        {
            throw new CliException(ExitCodes.Format, "Cli_Error_NoChecksums", checkFile);
        }

        string baseDirectory = Path.GetDirectoryName(path) ?? ctx.Host.CurrentDirectory;
        int failed = 0, missing = 0, unreadable = 0, ok = 0;
        var rows = new List<(string File, string Status)>();
        foreach (ChecksumEntry entry in entries)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            string status = CheckEntry(ctx, entry, baseDirectory, forced);
            switch (status)
            {
                case "OK": ok++; break;
                case "FAILED": failed++; break;
                case "MISSING": missing++; break;
                default: unreadable++; break;
            }

            rows.Add((entry.FileName, status));
            if (!ctx.Json)
            {
                ctx.Out.Write($"{entry.FileName}: {status}\n");
            }
        }

        if (ctx.Json)
        {
            using var json = new JsonOutput(ctx.Out, "hash");
            json.W.WriteString("checkFile", checkFile);
            json.W.WriteStartArray("files");
            foreach ((string file, string status) in rows)
            {
                json.W.WriteStartObject();
                json.W.WriteString("path", file);
                json.W.WriteString("status", status);
                json.W.WriteEndObject();
            }

            json.W.WriteEndArray();
            json.W.WriteNumber("ok", ok);
            json.W.WriteNumber("failed", failed);
            json.W.WriteNumber("missing", missing);
            json.Finish();
        }

        if (failed > 0)
        {
            ctx.Warn("Cli_Check_Failed", failed);
        }

        if (missing > 0)
        {
            ctx.Warn("Cli_Check_Missing", missing);
        }

        if (unreadable > 0)
        {
            return ExitCodes.Io;
        }

        return failed + missing > 0 ? ExitCodes.Negative : ExitCodes.Success;
    }

    private static string CheckEntry(CommandContext ctx, ChecksumEntry entry, string baseDirectory, IReadOnlyList<HashAlgorithmChoice>? forced)
    {
        string file = Path.GetFullPath(entry.FileName.Replace('/', Path.DirectorySeparatorChar), baseDirectory);
        if (!File.Exists(file))
        {
            return "MISSING";
        }

        HashAlgorithmInfo? algorithm = forced?[0].Algorithm;
        ExpectedHash? expected;
        if (algorithm is null)
        {
            if (ChecksumFile.Resolve(entry) is not { } resolved)
            {
                ctx.Err.WriteLine(ctx.M.Format("Cli_Error_UnknownChecksumAlgorithm", entry.FileName));
                return "ERROR";
            }

            (algorithm, expected) = resolved;
        }
        else
        {
            expected = new ExpectedHash(entry.Value, entry.FileName);
        }

        try
        {
            using Document doc = CliFiles.OpenDocument(file);
            var request = new HashRequest { Algorithms = [new HashAlgorithmChoice(algorithm)] };
            HashComputation computation = ctx.Run(entry.FileName, HashEngine.TotalBytes(doc.Current, request),
                op => HashEngine.Compute(doc.Current, request, op, ctx.Cancellation));
            return expected.Compare(computation.Rows[0]) == HashMatch.Match ? "OK" : "FAILED";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HashReadException or CliException)
        {
            ctx.Err.WriteLine(ctx.M.Format("Cli_Error_CannotRead", entry.FileName, ex.Message));
            return "ERROR";
        }
    }
}
