using System.Globalization;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.Sources;

namespace HexEditor.Cli.Commands;

/// <summary>
/// <c>hexed convert</c>: インポート・エクスポート形式 (TOOL-04〜TOOL-11) の変換 (AUTO-44 の 1)。変換の部品は GUI のインポート・エクスポートと同じ
/// <see cref="Importer"/> / <see cref="Exporter"/>、形式ごとのオプションはダイアログの欄 (<see cref="TransferOptions"/>) のキーを
/// kebab-case にした名前 (<c>recordLength</c> → <c>--record-length</c>)。
/// </summary>
internal sealed class ConvertCommand : ISubcommand
{
    public string Name => "convert";

    /// <summary>出力は 2 番目の引数で指定するため、標準出力のリダイレクトはしない。</summary>
    public bool RedirectsOutput => false;

    public IReadOnlyList<OptionSpec> Options { get; } = BuildOptions();

    /// <summary>形式の欄のキーに対する別名 (AUTO-44 の例: <c>--fill FF</c>、<c>--address-mode linear</c>)。</summary>
    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["fill"] = ["gapFill", "gapValue"],
        ["address-mode"] = ["intelMode", "srecMode"],
        ["newline"] = ["newLine"],
    };

    private static List<OptionSpec> BuildOptions()
    {
        var options = new List<OptionSpec>
        {
            OptionSpec.Value("from"),
            OptionSpec.Value("to"),
            OptionSpec.Flag("ignore-checksum"),
            OptionSpec.Value("fill"),
            OptionSpec.Value("address-mode"),
            OptionSpec.Value("output", "o"),
        };
        var seen = new HashSet<string>(options.Select(o => o.Name));
        foreach (string format in FormatIds.Importable.Concat(FormatIds.Exportable).Distinct())
        {
            foreach (TransferField field in TransferOptions.ExportFields(format).Concat(TransferOptions.ImportFields(format)))
            {
                string name = DumpCommand.ExportOptionName(field.Key);
                if (!seen.Add(name))
                {
                    continue;
                }

                if (field.Kind == TransferFieldKind.Check)
                {
                    options.Add(OptionSpec.Flag(name));
                    options.Add(OptionSpec.Flag("no-" + name));
                }
                else
                {
                    options.Add(OptionSpec.Value(name));
                }
            }
        }

        return options;
    }

    public int Run(CommandContext ctx)
    {
        if (ctx.Format != "text")
        {
            throw CliException.Usage("Cli_Error_FormatNotSupported", ctx.Format, Name, "text");
        }

        IReadOnlyList<string> positional = ctx.Args.Positional;
        string? outputArg = positional.Count >= 2 ? positional[1] : ctx.Args.Value("output");
        if (positional.Count < 1 || outputArg is null || positional.Count > 2)
        {
            throw CliException.Usage("Cli_Error_ConvertUsage");
        }

        string input = ctx.Host.Resolve(positional[0]);
        string output = ctx.Host.Resolve(outputArg);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
        {
            throw CliException.Usage("Cli_Error_OutputIsInput", output);
        }

        if (!File.Exists(input))
        {
            throw CliException.Io(Directory.Exists(input) ? "Cli_Error_IsDirectory" : "Cli_Error_FileNotFound", positional[0]);
        }

        string from = ParseFormat(ctx, "from", FormatIds.Importable) ?? DetectFromInput(input);
        string to = ParseFormat(ctx, "to", FormatIds.Exportable) ?? FormatForOutput(output);
        if (from is FormatIds.Ips or FormatIds.Ips32 || to is FormatIds.Ips or FormatIds.Ips32)
        {
            throw CliException.Usage("Cli_Error_ConvertPatch");
        }

        IReadOnlyList<TransferField> importFields = TransferOptions.ImportFields(from);
        Dictionary<string, string> importValues = TransferOptions.Load(null, importFields);
        string displayName = Path.GetFileName(input);

        // 入力を読んで (インポート)、ドキュメントにする。
        ImportResult? imported = null;
        Document document;
        ApplyOptions(ctx, importValues, importFields, null, null);
        if (from == FormatIds.Binary)
        {
            document = CliFiles.OpenDocument(input);
        }
        else
        {
            imported = Import(ctx, input, from, importValues);
            document = new Document(imported.TakeImage(), CliFiles.DocumentOptions());
        }

        using (imported)
        using (document)
        {
            IReadOnlyList<TransferField> exportFields = TransferOptions.ExportFields(to, null, ExportDefaults.From(displayName, imported?.Settings));
            Dictionary<string, string> exportValues = TransferOptions.Load(null, exportFields);
            ApplyOptions(ctx, exportValues, exportFields, importFields, to);
            RejectUnusedOptions(ctx, importFields, exportFields, from, to);
            long length = document.Length;
            IReadOnlyList<string> bad = TransferOptions.Validate(to, exportValues, text => EvaluateOptional(ctx, text, length));
            if (bad.Count > 0)
            {
                throw CliException.Usage("Cli_Error_BadOptions", string.Join(", ", bad.Select(DumpCommand.ExportOptionName).Select(n => "--" + n)));
            }

            ExportOptions options = TransferOptions.ToExportOptions(to, exportValues, text => EvaluateOptional(ctx, text, length), new DumpOptions());
            DocumentSnapshot snapshot = document.Current;
            var source = new ExportSource
            {
                Read = (offset, destination) => snapshot.Read(offset, destination),
                Length = snapshot.Length,
                BaseAddress = document.Source.BaseAddress,
                DataRanges = snapshot.DataRanges,
                FileName = displayName,
            };
            try
            {
                ctx.Run(displayName, length, op =>
                {
                    Exporter.WriteFile(output, stream => Exporter.Write(source, [(0, length)], options, stream, ctx.Cancellation, op.Report));
                    return 0;
                });
            }
            catch (FormatLimitException ex)
            {
                throw new CliException(ExitCodes.Format, "Cli_Error_FormatLimit", ex.Format, $"0x{ex.Limit:X}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw ex is UnauthorizedAccessException
                    ? new CliException(ExitCodes.Denied, "Cli_Error_AccessDenied", outputArg)
                    : CliException.Io("Cli_Error_CannotWrite", outputArg, ex.Message);
            }
        }

        return ExitCodes.Success;
    }

    private static long? EvaluateOptional(CommandContext ctx, string text, long length)
    {
        try
        {
            return ctx.Evaluate("--option", text, length);
        }
        catch (CliException)
        {
            return null;
        }
    }

    private static string? ParseFormat(CommandContext ctx, string option, IReadOnlyList<string> allowed)
    {
        if (ctx.Args.Value(option) is not { } text)
        {
            return null;
        }

        string id = text.Trim().ToLowerInvariant() switch
        {
            "intelhex" or "ihex" or "hex" => FormatIds.IntelHex,
            "srecord" or "srec" or "s19" or "mot" => FormatIds.SRecord,
            "binary" or "bin" or "raw" => FormatIds.Binary,
            "uuencode" or "uu" => FormatIds.UUEncode,
            "b64" => FormatIds.Base64,
            var other => other,
        };
        return allowed.Contains(id) ? id : throw CliException.Usage("Cli_Error_UnknownFormat", text, string.Join(", ", allowed));
    }

    /// <summary>入力の形式を、拡張子と内容から判定する (内容を優先する。拡張子が .bin などならバイナリ)。</summary>
    private static string DetectFromInput(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".bin" or ".dat" or ".img" or ".rom" or ".raw" or ".dmp" or ".iso")
        {
            return FormatIds.Binary;
        }

        try
        {
            string detected = Importer.DetectFile(path);
            if (detected != FormatIds.Binary)
            {
                return detected;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw CliException.Io("Cli_Error_CannotRead", path, ex.Message);
        }

        return FormatIds.ForOpenExtension(path) ?? FormatIds.Binary;
    }

    /// <summary>出力の拡張子から形式を決める。わからなければバイナリ。</summary>
    private static string FormatForOutput(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".hex" or ".ihx" or ".ihex" => FormatIds.IntelHex,
        ".s19" or ".s28" or ".s37" or ".srec" or ".mot" or ".mhx" => FormatIds.SRecord,
        ".b64" => FormatIds.Base64,
        ".b32" => FormatIds.Base32,
        ".a85" => FormatIds.Ascii85,
        ".uu" => FormatIds.UUEncode,
        ".qp" => FormatIds.QuotedPrintable,
        ".txt" => FormatIds.HexText,
        ".c" or ".h" => FormatIds.C,
        ".cpp" or ".hpp" => FormatIds.Cpp,
        ".cs" => FormatIds.CSharp,
        ".java" => FormatIds.Java,
        ".js" => FormatIds.JavaScript,
        ".py" => FormatIds.Python,
        ".rs" => FormatIds.Rust,
        ".go" => FormatIds.Go,
        ".pas" => FormatIds.Pascal,
        ".vb" => FormatIds.VisualBasic,
        _ => FormatIds.Binary,
    };

    private static ImportResult Import(CommandContext ctx, string input, string from, Dictionary<string, string> values)
    {
        ImportOptions options = TransferOptions.ToImportOptions(from, values);
        string temp = CliFiles.DocumentOptions().TempDirectory;
        Directory.CreateDirectory(temp);
        ImportResult result;
        try
        {
            result = ctx.Run(Path.GetFileName(input), new FileInfo(input).Length,
                op => Importer.DecodeFile(input, options, temp, new ImportProgress(ctx.Cancellation, op.Report)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw CliException.Io("Cli_Error_CannotRead", input, ex.Message);
        }
        catch (FormatLimitException ex)
        {
            throw new CliException(ExitCodes.Format, "Cli_Error_FormatLimit", ex.Format, $"0x{ex.Limit:X}");
        }

        if (result.HasErrors)
        {
            IEnumerable<ImportIssue> errors = result.Issues.Items.Where(i => !i.IsWarning);
            bool onlyChecksum = errors.All(i => i.Kind == ImportIssueKind.Checksum) && result.Issues.ErrorCount <= errors.Count();
            if (!(onlyChecksum && ctx.Args.Flag("ignore-checksum")))
            {
                ImportIssue first = errors.First();
                var report = new StringBuilder();
                foreach (ImportIssue issue in errors.Take(10))
                {
                    report.Append(ctx.M.Format("Cli_Convert_Issue", issue.Line, issue.Column, issue.Kind)).Append('\n');
                }

                result.Dispose();
                throw new CliException(ExitCodes.Format, first.Kind == ImportIssueKind.Checksum ? "Cli_Error_ImportChecksum" : "Cli_Error_ImportFormat",
                    Path.GetFileName(input), first.Line, first.Column, first.Kind, report.ToString().TrimEnd());
            }

            ctx.Warn("Cli_Convert_ChecksumIgnored", result.Issues.ErrorCount);
        }

        return result;
    }

    /// <summary>コマンドラインのオプションを欄の値にする。</summary>
    private static void ApplyOptions(CommandContext ctx, Dictionary<string, string> values, IReadOnlyList<TransferField> fields,
        IReadOnlyList<TransferField>? otherFields, string? exportFormat)
    {
        HashSet<string> keys = [.. fields.Select(f => f.Key)];
        foreach (TransferField field in fields)
        {
            string name = DumpCommand.ExportOptionName(field.Key);
            if (field.Kind == TransferFieldKind.Check)
            {
                if (ctx.Args.Flag(name))
                {
                    values[field.Key] = "true";
                }

                if (ctx.Args.Flag("no-" + name))
                {
                    values[field.Key] = "false";
                }
            }
            else if (ctx.Args.Value(name) is { } value)
            {
                values[field.Key] = field.Kind == TransferFieldKind.Choice ? CheckChoice(name, value, field) : value;
            }
        }

        foreach ((string alias, string[] targets) in Aliases)
        {
            if (ctx.Args.Value(alias) is not { } aliasValue)
            {
                continue;
            }

            foreach (string target in targets.Where(keys.Contains))
            {
                values[target] = target == "intelMode" ? IntelMode(aliasValue) : target == "srecMode" ? SrecMode(aliasValue) : aliasValue;
            }
        }

        _ = (otherFields, exportFormat);
    }

    private static string CheckChoice(string name, string value, TransferField field)
    {
        string normalized = value.Trim();
        string? match = field.Choices?.FirstOrDefault(c => c.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        return match ?? throw CliException.Usage("Cli_Error_BadChoice", "--" + name, value, string.Join(", ", field.Choices ?? []));
    }

    /// <summary>Intel HEX のアドレスの形式。<c>linear</c> は 32 bit の線形アドレス (04 レコード)、<c>segment</c> は 16 bit のセグメント。</summary>
    private static string IntelMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "linear" or "i32" or "i32hex" => "i32",
        "segment" or "i16" or "i16hex" => "i16",
        "i8" or "i8hex" => "i8",
        "auto" => "auto",
        _ => throw CliException.Usage("Cli_Error_BadChoice", "--address-mode", value, "auto, linear, segment, i8, i16, i32"),
    };

    private static string SrecMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "s1" => "s1",
        "s2" => "s2",
        "s3" or "linear" => "s3",
        "auto" => "auto",
        _ => throw CliException.Usage("Cli_Error_BadChoice", "--address-mode", value, "auto, s1, s2, s3"),
    };

    /// <summary>使った形式に関係のないオプションが指定されていたら誤りにする (打ち間違いに気づけるように)。</summary>
    private static void RejectUnusedOptions(CommandContext ctx, IReadOnlyList<TransferField> importFields, IReadOnlyList<TransferField> exportFields,
        string from, string to)
    {
        HashSet<string> known = ["from", "to", "ignore-checksum", "fill", "address-mode", "output", "format", "lang", "test-profile", "quiet", "help", "version", "no-color"];
        foreach (TransferField f in importFields.Concat(exportFields))
        {
            string name = DumpCommand.ExportOptionName(f.Key);
            known.Add(name);
            known.Add("no-" + name);
        }

        foreach (string name in ctx.Args.ValueNames.Concat(AllFlags(ctx)).Distinct())
        {
            if (!known.Contains(name))
            {
                throw CliException.Usage("Cli_Error_OptionNotForFormat", "--" + name, from, to);
            }
        }

        _ = CultureInfo.InvariantCulture;
    }

    private static IEnumerable<string> AllFlags(CommandContext ctx)
    {
        foreach (string name in ctx.Args.FlagNames)
        {
            yield return name;
        }
    }
}
