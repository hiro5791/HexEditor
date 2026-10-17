using System.Globalization;
using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.Cli.Commands;

/// <summary>
/// <c>hexed run</c>: スクリプトのヘッドレス実行 (AUTO-41)。スクリプトのホストは別の領域が作るため、ここでは引数・ファイルの読み込み・
/// 保存の扱い (<c>--save</c> / <c>--save-as</c> / <c>--dry-run</c>) だけを行い、実行は <see cref="IScriptRunner"/> に任せる。
/// </summary>
internal sealed class RunCommand : ISubcommand
{
    public string Name => "run";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        OptionSpec.Value("arg", repeatable: true),
        OptionSpec.Flag("trust"),
        OptionSpec.Value("allow", repeatable: true),
        OptionSpec.Flag("save"),
        OptionSpec.Value("save-as"),
        OptionSpec.Flag("dry-run"),
        OptionSpec.Value("timeout"),
        OptionSpec.Flag("readonly", "r"),
    ];

    public int Run(CommandContext ctx)
    {
        if (ctx.Args.Positional.Count == 0)
        {
            throw CliException.Usage("Cli_Error_RunNeedsScript");
        }

        int saveModes = (ctx.Args.Flag("save") ? 1 : 0) + (ctx.Args.Has("save-as") ? 1 : 0) + (ctx.Args.Flag("dry-run") ? 1 : 0);
        if (saveModes > 1)
        {
            throw CliException.Usage("Cli_Error_RunSaveModes");
        }

        bool readOnly = ctx.Args.Flag("readonly");
        if (readOnly && saveModes > 0 && !ctx.Args.Flag("dry-run"))
        {
            throw CliException.Usage("Cli_Error_RunReadonlySave");
        }

        var arguments = new List<KeyValuePair<string, string>>();
        foreach (string arg in ctx.Args.Values("arg"))
        {
            int eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw CliException.Usage("Cli_Error_ArgSyntax", arg);
            }

            arguments.Add(new(arg[..eq], arg[(eq + 1)..]));
        }

        TimeSpan? timeout = null;
        if (ctx.Args.Value("timeout") is { } t)
        {
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds <= 0 || seconds > 86_400 * 30)
            {
                throw CliException.Usage("Cli_Error_Timeout", t);
            }

            timeout = TimeSpan.FromSeconds(seconds);
        }

        string script = ctx.Host.Resolve(ctx.Args.Positional[0]);
        if (!File.Exists(script))
        {
            throw CliException.Io(Directory.Exists(script) ? "Cli_Error_IsDirectory" : "Cli_Error_FileNotFound", ctx.Args.Positional[0]);
        }

        string[] files = [.. ctx.Args.Positional.Skip(1).Select(ctx.Host.Resolve)];
        string? saveAs = ctx.Args.Value("save-as") is { } s ? ctx.Host.Resolve(s) : null;
        if (saveAs is not null && files.Length == 0)
        {
            throw CliException.Usage("Cli_Error_RunSaveAsNeedsFile");
        }

        if (saveAs is not null && string.Equals(saveAs, files[0], StringComparison.OrdinalIgnoreCase))
        {
            throw CliException.Usage("Cli_Error_RunSaveAsSame", ctx.Args.Value("save-as"));
        }

        if (ctx.Host.Services.Scripts is not { } runner)
        {
            throw CliException.Usage("Cli_Error_NotAvailable", Name);
        }

        var documents = new List<Document>();
        var names = new Dictionary<Document, string>();
        try
        {
            foreach (string file in files)
            {
                Document document = CliFiles.OpenDocument(file);
                documents.Add(document);
                names[document] = file;
                if (readOnly)
                {
                    document.SetReadOnly(ReadOnlyReason.User);
                }
            }

            bool dryRun = ctx.Args.Flag("dry-run");
            var saved = new HashSet<Document>();
            bool SaveDocument(Document document)
            {
                if (dryRun)
                {
                    return true;
                }

                try
                {
                    SaveTo(ctx, document, names[document]);
                    saved.Add(document);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ctx.Err.WriteLine(ctx.M.Format("Cli_Error_CannotWrite", names[document], ex.Message));
                    return false;
                }
            }

            var request = new ScriptRunRequest(script, documents, arguments, ctx.Args.Flag("trust"),
                [.. ctx.Args.Values("allow").SelectMany(a => a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))],
                timeout, readOnly, SaveDocument, ctx.Out, ctx.Err, _ => null, files, dryRun, ctx.M.Language);
            int exit = runner.Run(request, ctx.Cancellation);
            if (exit != ExitCodes.Success)
            {
                return exit;
            }

            return Finish(ctx, documents, names, saved, dryRun, saveAs);
        }
        finally
        {
            foreach (Document document in documents)
            {
                document.Dispose();
            }
        }
    }

    /// <summary>実行後の保存の扱い (AUTO-41 の仕様 4)。</summary>
    private static int Finish(CommandContext ctx, List<Document> documents, Dictionary<Document, string> names, HashSet<Document> saved,
        bool dryRun, string? saveAs)
    {
        var modified = documents.Where(d => d.IsModified && !saved.Contains(d)).ToList();
        try
        {
            if (dryRun)
            {
                foreach (Document document in documents.Where(d => d.IsModified))
                {
                    long bytes = document.Current.EnumerateModifiedRanges().Sum(r => r.Length);
                    ctx.Err.WriteLine(ctx.M.Format("Cli_Run_DryRunSummary", names[document], bytes));
                }

                return ExitCodes.Success;
            }

            if (saveAs is not null)
            {
                SaveTo(ctx, documents[0], saveAs, complete: false);
                modified.Remove(documents[0]);
            }
            else if (ctx.Args.Flag("save"))
            {
                foreach (Document document in modified)
                {
                    SaveTo(ctx, document, names[document]);
                }

                modified.Clear();
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new CliException(ExitCodes.Denied, "Cli_Error_AccessDenied", saveAs ?? "?");
        }
        catch (Exception ex) when (ex is IOException or InsufficientSpaceException)
        {
            throw CliException.Io("Cli_Error_CannotWrite", saveAs ?? "?", ex.Message);
        }

        foreach (Document document in modified)
        {
            ctx.Warn("Cli_Run_UnsavedWarning", names[document]);
        }

        return ExitCodes.Success;
    }

    /// <summary>保存する。<paramref name="complete"/> なら保存したファイルをドキュメントの元データにする (保存済みの状態にする)。</summary>
    private static void SaveTo(CommandContext ctx, Document document, string path, bool complete = true)
    {
        FileByteSource written = ctx.Run(Path.GetFileName(path), document.Length, op => DocumentSaver.Save(document.Current, path, op));
        if (complete)
        {
            document.CompleteSave(written);
        }
        else
        {
            written.Dispose();
        }
    }
}
