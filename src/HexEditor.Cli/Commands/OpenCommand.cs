using System.Diagnostics;
using HexEditor.Platform;
using HexEditor.Platform.Localization;

namespace HexEditor.Cli.Commands;

/// <summary>
/// <c>hexed open</c> / <c>hexed ファイル</c>: GUI (HexEditor.exe) にファイルを渡す (AUTO-37)。<c>hexed</c> は相対パスを自分のカレントフォルダ
/// を基準に絶対パスにし、<c>hexed</c> の時点で分かる誤り (引数の誤り、パスが存在しない) を終了コード 2 / 3 で返してから、
/// <c>HexEditor.exe</c> に引数を渡す。既存のインスタンスへの転送は <c>HexEditor.exe</c> 側 (AppInstance のリダイレクト) が行う。
/// </summary>
internal sealed class OpenCommand : ISubcommand
{
    public string Name => "open";

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        OptionSpec.Value("offset", "g"),
        OptionSpec.Value("select"),
        OptionSpec.Flag("readonly", "r"),
        OptionSpec.Value("disk"),
        OptionSpec.Value("volume"),
        OptionSpec.Value("process"),
        OptionSpec.Value("encoding"),
        OptionSpec.Value("template"),
        new OptionSpec("compare", null, true) { ValueCount = 2 },
        OptionSpec.Value("run"),
        OptionSpec.Flag("new-window"),
        OptionSpec.Flag("new-document"),
        OptionSpec.Flag("open-disk"),
        OptionSpec.Flag("new-instance"),
        OptionSpec.Flag("wait", "w"),
        OptionSpec.Flag("safe-mode"),
        OptionSpec.Flag("write-block"),
        OptionSpec.Value("pseudo-locale"),
        OptionSpec.Value("plugin-dev"),
        OptionSpec.Value("ui-lang"),
        OptionSpec.Value("test-hooks"),
    ];

    /// <summary>GUI の起動を待つ間隔 (起動に失敗していないかを見る)。</summary>
    private static readonly TimeSpan WaitPoll = TimeSpan.FromMilliseconds(250);

    public int Run(CommandContext ctx)
    {
        ParsedArguments a = ctx.Args;
        var gui = new List<string>();

        // 引数の検査 (hexed の時点で分かる誤りは終了コード 2)。
        if (a.Value("ui-lang") is { } uiLang)
        {
            if (DisplayLanguages.Find(uiLang) is not { } language)
            {
                throw CliException.Usage("Cli_Error_UnknownUiLang", uiLang);
            }

            gui.AddRange(["--ui-lang", language.Tag]);
        }

        if (a.Value("pseudo-locale") is { } pseudo)
        {
            if (pseudo is not ("qps-ploc" or "qps-plocm"))
            {
                throw CliException.Usage("Cli_Error_UnknownPseudoLocale", pseudo);
            }

            // 疑似翻訳を同梱しない安定版では使えない (AUTO-37 の仕様 2)。
            if (SemanticVersion.FromInformationalVersion(VersionInfo.Informational).Channel == ReleaseChannel.Stable)
            {
                throw CliException.Usage("Cli_Error_NotInThisVersion", "--pseudo-locale");
            }

            gui.AddRange(["--pseudo-locale", pseudo]);
        }

        string? hooks = null;
        if (a.Value("test-hooks") is { } hooksFile)
        {
#if HEX_TEST_HOOKS
            hooks = ctx.Host.Resolve(hooksFile);
#else
            throw CliException.Usage("Cli_Error_NotInThisVersion", "--test-hooks");
#endif
        }

        // パス: 存在しなければ終了コード 3 (AUTO-37 の仕様 5)。
        var paths = new List<string>();
        foreach (string path in a.Positional)
        {
            string full = ctx.Host.Resolve(path);
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                throw CliException.Io("Cli_Error_FileNotFound", path);
            }

            paths.Add(full);
        }

        IReadOnlyList<string> compare = a.Values("compare");
        string[] comparePaths = [.. compare.Select(ctx.Host.Resolve)];
        foreach ((string given, string full) in compare.Zip(comparePaths))
        {
            if (!File.Exists(full))
            {
                throw CliException.Io("Cli_Error_FileNotFound", given);
            }
        }

        string? run = null;
        if (a.Value("run") is { } script)
        {
            run = ctx.Host.Resolve(script);
            if (!File.Exists(run))
            {
                throw CliException.Io("Cli_Error_FileNotFound", script);
            }
        }

        long firstLength = paths.Count > 0 && File.Exists(paths[0]) ? new FileInfo(paths[0]).Length : 0;
        string? offset = a.Value("offset");
        if (offset is not null)
        {
            ctx.Evaluate("--offset", offset, firstLength);
        }

        string? select = a.Value("select");
        if (select is not null)
        {
            // 範囲の書式だけを確かめる (ファイルの外かどうかは GUI が開いたドキュメントの長さで確かめる)。
            long length = firstLength;
            CheckRangeSyntax(ctx, select, length);
        }

        // GUI に渡す引数を作る (パスは絶対パス)。
        gui.InsertRange(0, paths);
        AddValue(gui, "--offset", offset);
        AddValue(gui, "--select", select);
        AddFlag(gui, a, "readonly");
        AddValue(gui, "--disk", a.Value("disk"));
        AddValue(gui, "--volume", a.Value("volume"));
        AddValue(gui, "--process", a.Value("process"));
        AddValue(gui, "--encoding", a.Value("encoding"));
        AddValue(gui, "--template", a.Value("template") is { } t ? ResolveTemplate(ctx, t) : null);
        if (comparePaths.Length == 2)
        {
            gui.AddRange(["--compare", comparePaths[0], comparePaths[1]]);
        }

        AddValue(gui, "--run", run);
        AddFlag(gui, a, "new-window");
        AddFlag(gui, a, "new-document");
        AddFlag(gui, a, "open-disk");
        AddFlag(gui, a, "new-instance");
        AddFlag(gui, a, "safe-mode");
        AddFlag(gui, a, "write-block");
        AddValue(gui, "--plugin-dev", a.Value("plugin-dev") is { } dev ? ctx.Host.Resolve(dev) : null);
        AddValue(gui, "--test-hooks", hooks);
        if (a.Value("test-profile") is { } profile)
        {
            AddValue(gui, "--test-profile", ctx.Host.Resolve(profile));
        }
        else if (ctx.Host.TestProfile is { } envProfile)
        {
            AddValue(gui, "--test-profile", envProfile);
        }

        string? executable = ctx.Host.Services.Gui.FindExecutable();
        if (executable is null)
        {
            throw CliException.Io("Cli_Error_GuiNotFound");
        }

        bool wait = a.Flag("wait");
        EventWaitHandle? signal = null;
        try
        {
            if (wait)
            {
                string id = WaitSignal.NewId();
                signal = new EventWaitHandle(false, EventResetMode.ManualReset, WaitSignal.EventName(id));
                gui.AddRange([WaitSignal.Option, id]);
            }

            int pid;
            try
            {
                pid = ctx.Host.Services.Gui.Start(executable, gui, ctx.Host.CurrentDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                throw CliException.Io("Cli_Error_GuiStart", executable, ex.Message);
            }

            return signal is null ? ExitCodes.Success : WaitForClose(ctx, signal, pid);
        }
        finally
        {
            signal?.Dispose();
        }
    }

    /// <summary>
    /// 開いたドキュメントがすべて閉じられるまで待つ (AUTO-37 の仕様 2 の <c>--wait</c>)。起動した GUI のプロセスが失敗で終わったら、
    /// その終了コードを引数の誤り・入出力のエラーとして返す (永遠に待たない)。既存のインスタンスに転送した場合、起動したプロセスは
    /// 0 で終わるが、待ち続ける。
    /// </summary>
    private static int WaitForClose(CommandContext ctx, EventWaitHandle signal, int pid)
    {
        WaitHandle[] handles = [signal, ctx.Cancellation.WaitHandle];
        while (true)
        {
            int index = WaitHandle.WaitAny(handles, WaitPoll);
            if (index == 0)
            {
                return ExitCodes.Success;
            }

            if (index == 1)
            {
                throw new OperationCanceledException(ctx.Cancellation);
            }

            if (ProcessExitCode(pid) is { } code and not 0)
            {
                throw CliException.Io("Cli_Error_GuiExited", code);
            }
        }
    }

    private static int? ProcessExitCode(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.HasExited ? process.ExitCode : null;
        }
        catch (ArgumentException)
        {
            // もう存在しない (転送して終わった)。終了コードは分からないため、合図を待ち続ける。
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void AddValue(List<string> args, string name, string? value)
    {
        if (value is not null)
        {
            args.AddRange([name, value]);
        }
    }

    private static void AddFlag(List<string> args, ParsedArguments parsed, string name)
    {
        if (parsed.Flag(name))
        {
            args.Add("--" + name);
        }
    }

    /// <summary>テンプレートの指定がファイルのパスなら絶対パスにする (名前ならそのまま)。</summary>
    private static string ResolveTemplate(CommandContext ctx, string template)
    {
        string full = ctx.Host.Resolve(template);
        return File.Exists(full) ? full : template;
    }

    private static void CheckRangeSyntax(CommandContext ctx, string text, long length)
    {
        int dots = text.IndexOf("..", StringComparison.Ordinal);
        int colon = text.IndexOf(':', StringComparison.Ordinal);
        if (dots >= 0)
        {
            ctx.Evaluate("--select", text[..dots], length);
            ctx.Evaluate("--select", text[(dots + 2)..], length);
        }
        else if (colon >= 0)
        {
            ctx.Evaluate("--select", text[..colon], length);
            ctx.Evaluate("--select", text[(colon + 1)..], length);
        }
        else
        {
            ctx.Evaluate("--select", text, length);
        }
    }
}

/// <summary>実行ファイルの版。</summary>
internal static class VersionInfo
{
    public static string Informational { get; } = System.Reflection.Assembly.GetEntryAssembly()?
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
        ?? SemanticVersion.Local.Informational;
}
