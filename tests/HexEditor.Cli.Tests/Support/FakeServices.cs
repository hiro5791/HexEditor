using HexEditor.Cli;

namespace HexEditor.Cli.Tests.Support;

/// <summary>テンプレートエンジン・スクリプトのホスト・GUI の代わり (別の領域のマージ前に、コマンドラインの側の動作を確かめる)。</summary>
internal sealed class FakeScripts(Func<ScriptRunRequest, int> handler) : IScriptRunner
{
    public ScriptRunRequest? Last { get; private set; }

    public int Run(ScriptRunRequest request, CancellationToken cancellationToken)
    {
        Last = request;
        return handler(request);
    }
}

internal sealed class FakeTemplates(Func<TemplateRequest, int> handler) : ITemplateService
{
    public TemplateRequest? Last { get; private set; }

    public int Apply(TemplateRequest request, CancellationToken cancellationToken)
    {
        Last = request;
        return handler(request);
    }
}

internal sealed class FakeGui : IGuiLauncher
{
    public string? Executable { get; set; } = "C:\\fake\\HexEditor.exe";

    public List<string[]> Started { get; } = [];

    public string? WorkingDirectory { get; private set; }

    /// <summary>起動したときに呼ぶ (GUI の代わりに合図を送るなど)。</summary>
    public Action<string[]>? OnStart { get; set; }

    /// <summary>登録の解除の結果のファイルに書く行と、終了コード。</summary>
    public (string[] Lines, int ExitCode)? Unregister { get; set; }

    public string? FindExecutable() => Executable;

    public int Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        string[] copy = [.. arguments];
        Started.Add(copy);
        WorkingDirectory = workingDirectory;
        OnStart?.Invoke(copy);
        return Environment.ProcessId;
    }

    public int RunAndWait(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        Started.Add([.. arguments]);
        int at = arguments.ToList().IndexOf("--unregister-result");
        (string[] lines, int code) = Unregister ?? ([], 0);
        if (at >= 0 && lines.Length > 0)
        {
            File.WriteAllLines(arguments[at + 1], lines);
        }

        return code;
    }

    public static CliServices Services(FakeGui gui, IScriptRunner? scripts = null, ITemplateService? templates = null) =>
        new() { Gui = gui, Scripts = scripts, Templates = templates };
}
