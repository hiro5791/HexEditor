namespace HexEditor.Platform;

/// <summary>
/// GUI のコマンドライン (AUTO-37 のうち、今の版で扱うもの、と AUTO-36 の 9 の <c>--unregister</c>)。値を取るオプションの値はファイル名として扱わない。
/// </summary>
public sealed record CommandLine(
    IReadOnlyList<string> Files,
    string? UiLanguage,
    string? TestProfile,
    string? Offset,
    bool NewWindow,
    bool NewInstance,
    bool SafeMode,
    string? PseudoLocale = null,
    bool NewDocument = false,
    bool Unregister = false,
    bool OpenDisk = false)
{
    private static readonly HashSet<string> OptionsWithValue =
    [
        "--ui-lang", "--test-profile", "--test-hooks", "--offset", "-g", "--select", "--encoding", "--template",
        "--disk", "--volume", "--process", "--run", "--plugin-dev", "--pseudo-locale",
    ];

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var files = new List<string>();
        var values = new Dictionary<string, string>();
        var flags = new HashSet<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (OptionsWithValue.Contains(a))
            {
                if (i + 1 < args.Count)
                {
                    values[a] = args[++i];
                }
            }
            else if (a.StartsWith('-'))
            {
                flags.Add(a);
            }
            else
            {
                files.Add(a);
            }
        }

        return new CommandLine(
            files,
            values.GetValueOrDefault("--ui-lang"),
            values.GetValueOrDefault("--test-profile"),
            values.GetValueOrDefault("--offset") ?? values.GetValueOrDefault("-g"),
            flags.Contains("--new-window"),
            flags.Contains("--new-instance"),
            flags.Contains("--safe-mode"),
            values.GetValueOrDefault("--pseudo-locale"),
            flags.Contains("--new-document"),
            flags.Contains("--unregister"),
            flags.Contains("--open-disk"));
    }

    /// <summary>
    /// 1 つの文字列のコマンドライン (起動の転送で受け取るもの) を引数に分ける。Windows の規則 (CommandLineToArgvW) に従う。
    /// 先頭の実行ファイル名は取り除く。
    /// </summary>
    public static CommandLine ParseString(string commandLine)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        bool any = false;
        foreach (char c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
        {
            args.Add(current.ToString());
        }

        // 先頭は実行ファイル (転送で受け取る文字列には含まれる)。
        if (args.Count > 0 && args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            args.RemoveAt(0);
        }

        return Parse(args);
    }
}
