using System.Text;
using HexEditor.Cli.Commands;
using HexEditor.Platform;
using HexEditor.Platform.Localization;

namespace HexEditor.Cli;

/// <summary>
/// <c>hexed</c> の入口 (AUTO-36)。サブコマンドの振り分け、共通のオプション、<c>--help</c> / <c>--version</c> / <c>--unregister</c>、
/// 終了コードへの変換を行う。プロセスの外から呼べる (<see cref="Run"/> に実行環境 <see cref="CliHost"/> を渡す)。
/// </summary>
public static class CliApp
{
    private static readonly ISubcommand[] Subcommands =
    [
        new OpenCommand(), new HashCommand(), new DumpCommand(), new CompareCommand(), new RunCommand(),
        new TemplateCommand(), new PatchCommand(), new ConvertCommand(), new FindCommand(),
    ];

    /// <summary>共通のオプション (AUTO-36 の仕様 3、9)。<c>--test-profile</c> は書かれていない補助 (テスト方針 8.3)。</summary>
    private static readonly OptionSpec[] Globals =
    [
        OptionSpec.Flag("help", "h"),
        OptionSpec.Flag("version"),
        OptionSpec.Flag("quiet", "q"),
        OptionSpec.Value("format"),
        OptionSpec.Value("output", "o"),
        OptionSpec.Value("lang"),
        OptionSpec.Flag("no-color"),
        OptionSpec.Value("test-profile"),
        OptionSpec.Flag("unregister"),
    ];

    private static readonly HashSet<string> ValueGlobals = ["--lang", "--format", "--output", "-o", "--test-profile"];

    /// <summary>サブコマンドの名前。</summary>
    public static IReadOnlyList<string> CommandNames { get; } = [.. Subcommands.Select(c => c.Name)];

    /// <summary>実行して終了コードを返す。</summary>
    public static int Run(IReadOnlyList<string> args, CliHost host)
    {
        Messages messages = new("en");
        CommandContext? context = null;
        try
        {
            messages = new Messages(ResolveLanguage(args, host));
            return Dispatch(args, host, messages, out context);
        }
        catch (CliException ex)
        {
            context?.CloseOutput(keep: false);
            Report(host, messages, ex);
            return ex.ExitCode;
        }
        catch (OperationCanceledException)
        {
            context?.CloseOutput(keep: false);
            if (!(context?.Quiet ?? false))
            {
                host.Err.WriteLine(messages.Get("Cli_Cancelled"));
            }

            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            context?.CloseOutput(keep: false);
            string log = WriteErrorLog(ex);
            host.Err.WriteLine(messages.Format("Cli_Error_Internal", ex.GetType().Name, log));
            return ExitCodes.Internal;
        }
        finally
        {
            context?.Dispose();
            host.Out.Flush();
            host.Err.Flush();
        }
    }

    private static void Report(CliHost host, Messages messages, CliException ex)
    {
        host.Err.WriteLine(messages.Format("Cli_Error_Prefix", messages.Format(ex.MessageKey, ex.Arguments)));
        if (ex.ExitCode == ExitCodes.Usage)
        {
            host.Err.WriteLine(messages.Get("Cli_Hint_Help"));
        }
    }

    private static int Dispatch(IReadOnlyList<string> args, CliHost host, Messages messages, out CommandContext? context)
    {
        context = null;
        (ISubcommand? sub, IReadOnlyList<string> rest) = Choose(args, host);
        IEnumerable<OptionSpec> specs = Globals;
        if (sub is not null)
        {
            specs = Globals.Where(g => !sub.Options.Any(o => o.Name == g.Name || o.Short is not null && o.Short == g.Short)).Concat(sub.Options);
        }

        ParsedArguments parsed = ArgumentParser.Parse(rest, specs, sub?.Name ?? "hexed");
        if (parsed.Flag("version"))
        {
            host.Out.Write(messages.Format("Cli_Version", VersionInfo.Informational) + "\n");
            return ExitCodes.Success;
        }

        if (parsed.Flag("help"))
        {
            host.Out.Write(messages.Get(sub is null ? "Cli_Help_Main" : "Cli_Help_" + sub.Name).TrimEnd() + "\n");
            return ExitCodes.Success;
        }

        if (sub is null)
        {
            if (parsed.Flag("unregister"))
            {
                return Unregister(host, messages, parsed);
            }

            // 引数が何もなければ使い方を出す。
            host.Out.Write(messages.Get("Cli_Help_Main").TrimEnd() + "\n");
            return parsed.Positional.Count == 0 && !parsed.ValueNames.Any() ? ExitCodes.Success : ExitCodes.Usage;
        }

        if (parsed.Flag("unregister"))
        {
            return Unregister(host, messages, parsed);
        }

        context = new CommandContext(host, messages, parsed, sub.Name, sub.RedirectsOutput);
        int code = sub.Run(context);
        context.CloseOutput(keep: code is ExitCodes.Success or ExitCodes.Negative);
        return code;
    }

    /// <summary>サブコマンドを決める (AUTO-36 の仕様 2)。省略されて最初の引数がパスなら <c>open</c> とみなす。</summary>
    private static (ISubcommand? Sub, IReadOnlyList<string> Remaining) Choose(IReadOnlyList<string> args, CliHost host)
    {
        int i = 0;
        while (i < args.Count)
        {
            string arg = args[i];
            if (!arg.StartsWith('-') || arg == "-")
            {
                break;
            }

            i += ValueGlobals.Contains(arg) ? 2 : 1;
        }

        if (i >= args.Count)
        {
            return (null, args);
        }

        string token = args[i];
        if (Subcommands.FirstOrDefault(c => c.Name.Equals(token, StringComparison.Ordinal)) is { } sub)
        {
            return (sub, [.. args.Take(i), .. args.Skip(i + 1)]);
        }

        if (LooksLikePath(host, token))
        {
            return (Subcommands[0], args);
        }

        throw CliException.Usage("Cli_Error_UnknownCommand", token);
    }

    private static bool LooksLikePath(CliHost host, string token) =>
        token.AsSpan().IndexOfAny(['\\', '/', '.', ':']) >= 0 || File.Exists(host.Resolve(token)) || Directory.Exists(host.Resolve(token));

    // ---- 言語 ----

    /// <summary>メッセージの言語: <c>--lang</c>、設定 <c>ui.language</c>、Windows の表示言語の順。</summary>
    private static string ResolveLanguage(IReadOnlyList<string> args, CliHost host)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string? value = null;
            if (args[i] == "--lang" && i + 1 < args.Count)
            {
                value = args[i + 1];
            }
            else if (args[i].StartsWith("--lang=", StringComparison.Ordinal))
            {
                value = args[i][7..];
            }

            if (value is not null)
            {
                return DisplayLanguages.Find(value)?.Tag ?? throw CliException.Usage("Cli_Error_UnknownLang", value);
            }
        }

        string? setting = null;
        try
        {
            string? profile = ProfileOf(args) ?? host.TestProfile;
            EnvironmentContext context = EnvironmentContext.ForCurrentProcess(typeof(CliApp).Assembly) with { ProbeWritable = _ => true };
            AppEnvironment environment = AppEnvironment.Detect(context, profile is null ? null : host.Resolve(profile));
            setting = DisplayLanguages.ReadSetting(environment.Locations.Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            // 設定を読めなければ Windows の表示言語に従う。
        }

        return DisplayLanguages.Choose(setting, host.WindowsLanguages());
    }

    private static string? ProfileOf(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--test-profile" && i + 1 < args.Count)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith("--test-profile=", StringComparison.Ordinal))
            {
                return args[i]["--test-profile=".Length..];
            }
        }

        return null;
    }

    // ---- --unregister (AUTO-36 の仕様 9) ----

    /// <summary>
    /// 登録の解除は GUI の実行ファイル (<c>HexEditor.exe --unregister</c>) に任せる (ジャンプリスト・トースト通知の登録を消すのに Windows App SDK が要るため)。
    /// 結果の一覧はファイルで受け取って標準出力に出す。終了コードは HexEditor.exe のもの (すべて消せたら 0、消せない項目があれば 3)。
    /// </summary>
    private static int Unregister(CliHost host, Messages messages, ParsedArguments parsed)
    {
        string? executable = host.Services.Gui.FindExecutable();
        if (executable is null)
        {
            throw CliException.Io("Cli_Error_GuiNotFound");
        }

        string result = Path.Combine(Path.GetTempPath(), $"hexed-unregister-{Guid.NewGuid():N}.txt");
        var arguments = new List<string> { "--unregister", "--unregister-result", result };
        string? profile = parsed.Value("test-profile") ?? host.TestProfile;
        if (profile is not null)
        {
            arguments.AddRange(["--test-profile", host.Resolve(profile)]);
        }

        int code;
        try
        {
            code = host.Services.Gui.RunAndWait(executable, arguments, host.CurrentDirectory, host.Cancellation);
            if (File.Exists(result))
            {
                foreach (string line in File.ReadAllLines(result, new UTF8Encoding(false)))
                {
                    host.Out.Write(UnregisterLine(messages, line) + "\n");
                }
            }
            else if (code == 0)
            {
                host.Out.Write(messages.Get("Cli_Unregister_Done") + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw CliException.Io("Cli_Error_GuiStart", executable, ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(result);
            }
            catch (IOException)
            {
                // 消せなくても結果には影響しない。
            }
        }

        return code;
    }

    /// <summary>HexEditor.exe が書いた結果の 1 行 (<c>ok|fail|info</c>、タブ、項目 ID、タブ、詳細) を表示用にする。</summary>
    internal static string UnregisterLine(Messages messages, string line)
    {
        string[] parts = line.Split('\t');
        string status = parts[0];
        string item = parts.Length > 1 ? parts[1] : string.Empty;
        string detail = parts.Length > 2 ? parts[2] : string.Empty;
        string label = messages.Get("Cli_Unregister_Item_" + item) is var l && l != "Cli_Unregister_Item_" + item ? l : item;
        return status switch
        {
            "ok" => messages.Format("Cli_Unregister_Removed", label),
            "fail" => messages.Format("Cli_Unregister_Failed", label, detail),
            _ => messages.Get("Cli_Unregister_" + item),
        };
    }

    private static string WriteErrorLog(Exception ex)
    {
        try
        {
            string folder = Path.Combine(Path.GetTempPath(), "HexEditor", "logs");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"hexed-error-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, ex.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch (Exception log) when (log is IOException or UnauthorizedAccessException)
        {
            return "-";
        }
    }
}
