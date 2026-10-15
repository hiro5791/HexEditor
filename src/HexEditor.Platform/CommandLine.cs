namespace HexEditor.Platform;

/// <summary>
/// GUI のコマンドライン (AUTO-37 と AUTO-36 の 9 の <c>--unregister</c>)。値を取るオプションの値はファイル名として扱わない。
/// <c>hexed open</c> が絶対パスにして渡した引数も、ここで読む。<c>--compare</c> だけは値を 2 つ取る。
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
        "--disk", "--volume", "--process", "--run", "--plugin-dev", "--pseudo-locale", "--wait-id", "--unregister-result",
    ];

    /// <summary>選択する範囲 (<c>--select</c>。<c>開始:長さ</c> または <c>開始..終了</c>)。</summary>
    public string? Select { get; init; }

    /// <summary>読み取り専用で開く (<c>--readonly</c> / <c>-r</c>)。</summary>
    public bool ReadOnly { get; init; }

    /// <summary>物理ディスク (<c>--disk</c>。番号または <c>\\.\PhysicalDriveN</c>)。</summary>
    public string? Disk { get; init; }

    /// <summary>論理ボリューム (<c>--volume</c>。<c>C:</c> など)。</summary>
    public string? Volume { get; init; }

    /// <summary>プロセスのメモリ (<c>--process</c>。PID または名前)。</summary>
    public string? Process { get; init; }

    /// <summary>テキスト列の文字コード (<c>--encoding</c>)。</summary>
    public string? Encoding { get; init; }

    /// <summary>開いた後に適用するテンプレート (<c>--template</c>。名前またはパス)。</summary>
    public string? Template { get; init; }

    /// <summary>比較の画面で開く 2 つのファイル (<c>--compare パス1 パス2</c>)。なければ空。</summary>
    public IReadOnlyList<string> Compare { get; init; } = [];

    /// <summary>開いた後に実行するスクリプト (<c>--run</c>)。</summary>
    public string? Run { get; init; }

    /// <summary>アプリ全体の書き込み保護 (<c>--write-block</c>。12 の FOR-01)。</summary>
    public bool WriteBlock { get; init; }

    /// <summary>開発中のプラグインのフォルダ (<c>--plugin-dev</c>。AUTO-33)。</summary>
    public string? PluginDev { get; init; }

    /// <summary><c>hexed open --wait</c> の合図の ID (<see cref="WaitSignal"/>)。</summary>
    public string? WaitId { get; init; }

    /// <summary><c>hexed --unregister</c> が結果を受け取るファイル (<c>--unregister-result</c>)。指定されたらメッセージボックスを出さない。</summary>
    public string? UnregisterResult { get; init; }

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var files = new List<string>();
        var values = new Dictionary<string, string>();
        var flags = new HashSet<string>();
        var compare = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--compare")
            {
                // 値を 2 つ取る (比べる 2 つのファイル)。
                for (int n = 0; n < 2 && i + 1 < args.Count; n++)
                {
                    compare.Add(args[++i]);
                }
            }
            else if (OptionsWithValue.Contains(a))
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
            flags.Contains("--open-disk"))
        {
            Select = values.GetValueOrDefault("--select"),
            ReadOnly = flags.Contains("--readonly") || flags.Contains("-r"),
            Disk = values.GetValueOrDefault("--disk"),
            Volume = values.GetValueOrDefault("--volume"),
            Process = values.GetValueOrDefault("--process"),
            Encoding = values.GetValueOrDefault("--encoding"),
            Template = values.GetValueOrDefault("--template"),
            Compare = compare.Count == 2 ? compare : [],
            Run = values.GetValueOrDefault("--run"),
            WriteBlock = flags.Contains("--write-block"),
            PluginDev = values.GetValueOrDefault("--plugin-dev"),
            WaitId = values.GetValueOrDefault("--wait-id") is { } id && WaitSignal.IsValidId(id) ? id : null,
            UnregisterResult = values.GetValueOrDefault("--unregister-result"),
        };
    }

    /// <summary>
    /// ファイルを開く以外に、開いた後にすることがあるか (位置・選択・文字コード・テンプレート・スクリプト・比較・デバイスなど)。
    /// </summary>
    public bool HasOpenActions => Offset is not null || Select is not null || Encoding is not null || Template is not null || Run is not null
        || Compare.Count == 2 || Disk is not null || Volume is not null || Process is not null;

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
