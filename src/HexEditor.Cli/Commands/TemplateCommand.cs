using HexEditor.Core.Engine;

namespace HexEditor.Cli.Commands;

/// <summary>
/// <c>hexed template</c>: ファイルに構造体テンプレート (07) を適用して結果を出力する (AUTO-42)。テンプレートエンジンは別の領域が作るため、
/// ここでは引数の解釈・ファイルの読み込み・出力先の管理だけを行い、適用は <see cref="ITemplateService"/> に任せる。
/// </summary>
internal sealed class TemplateCommand : ISubcommand
{
    public string Name => "template";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        OptionSpec.Value("template"),
        OptionSpec.Flag("auto"),
        OptionSpec.Value("offset", "g"),
        OptionSpec.Value("depth"),
        OptionSpec.Value("fields"),
    ];

    private static readonly string[] Formats = ["json", "csv", "text"];

    public int Run(CommandContext ctx)
    {
        if (!Formats.Contains(ctx.Format))
        {
            throw CliException.Usage("Cli_Error_FormatNotSupported", ctx.Format, Name, string.Join(", ", Formats));
        }

        if (ctx.Args.Positional.Count != 1)
        {
            throw CliException.Usage(ctx.Args.Positional.Count == 0 ? "Cli_Error_NoInput" : "Cli_Error_TooManyInputs", Name);
        }

        string? template = ctx.Args.Value("template");
        bool auto = ctx.Args.Flag("auto");
        if (template is null == !auto)
        {
            throw CliException.Usage("Cli_Error_TemplateChoice");
        }

        string path = ctx.Host.Resolve(ctx.Args.Positional[0]);
        if (Directory.Exists(path))
        {
            throw CliException.Io("Cli_Error_IsDirectory", ctx.Args.Positional[0]);
        }

        ctx.RejectOutputOverInput([path]);
        using Document document = CliFiles.OpenDocument(path);
        long offset = ctx.Args.Value("offset") is { } o ? ctx.Evaluate("--offset", o, document.Length) : 0;
        if (offset < 0 || offset > document.Length)
        {
            throw CliException.Usage("Cli_Error_RangeOutside", "--offset", ctx.Args.Value("offset"), document.Length);
        }

        int? depth = null;
        if (ctx.Args.Value("depth") is { } d)
        {
            long value = ctx.Evaluate("--depth", d, 0);
            depth = value is >= 0 and <= int.MaxValue ? (int)value : throw CliException.Usage("Cli_Error_NumberRange", "--depth", d, "0", int.MaxValue);
        }

        IReadOnlyList<string> fields = ctx.Args.Values("fields")
            .SelectMany(f => SplitFields(f))
            .ToList();
        if (ctx.Host.Services.Templates is not { } service)
        {
            throw CliException.Usage("Cli_Error_NotAvailable", Name);
        }

        var request = new TemplateRequest(document, path, template, auto, offset, ctx.Format, depth, fields, ctx.Out, ctx.Err, ctx.M.Language);
        return service.Apply(request, ctx.Cancellation);
    }

    /// <summary>カンマ区切りのパス。<c>sections[*].name</c> のような括弧の中のカンマは区切りにしない。</summary>
    internal static IEnumerable<string> SplitFields(string text)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == ',' && depth == 0)
            {
                if (i > start)
                {
                    yield return text[start..i].Trim();
                }

                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..].Trim();
        }
    }
}
