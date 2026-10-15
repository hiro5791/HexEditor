namespace HexEditor.Cli.Commands;

/// <summary>サブコマンド (AUTO-36 の仕様 2)。</summary>
internal interface ISubcommand
{
    /// <summary>名前 (<c>hash</c> など)。</summary>
    string Name { get; }

    /// <summary>このサブコマンド固有のオプション (共通のオプションに加える。同じ名前なら置き換える)。</summary>
    IReadOnlyList<OptionSpec> Options { get; }

    /// <summary>共通の <c>--output</c> で標準出力をファイルに向けるか (patch などは自分の出力先を持つため偽)。</summary>
    bool RedirectsOutput => true;

    /// <summary>実行して終了コードを返す。</summary>
    int Run(CommandContext ctx);
}

/// <summary>パスの指定を実際のファイルに展開する (ワイルドカード・フォルダ・<c>-</c> = 標準入力。AUTO-38 の仕様 3)。</summary>
internal static class PathExpander
{
    /// <summary>標準入力を表す名前。</summary>
    public const string Stdin = "-";

    public sealed record Entry(string Display, string FullPath, bool IsStdin = false);

    public static IReadOnlyList<Entry> Expand(CliHost host, IEnumerable<string> patterns, bool recurse, bool allowStdin)
    {
        var result = new List<Entry>();
        foreach (string pattern in patterns)
        {
            if (pattern == Stdin && allowStdin)
            {
                result.Add(new Entry(Stdin, Stdin, IsStdin: true));
                continue;
            }

            if (pattern.AsSpan().IndexOfAny('*', '?') >= 0)
            {
                string? directory = Path.GetDirectoryName(pattern);
                string mask = Path.GetFileName(pattern);
                string searchDirectory = host.Resolve(string.IsNullOrEmpty(directory) ? "." : directory);
                if (!Directory.Exists(searchDirectory))
                {
                    throw CliException.Io("Cli_Error_FileNotFound", pattern);
                }

                var matches = new List<string>();
                try
                {
                    matches.AddRange(Directory.EnumerateFiles(searchDirectory, mask, recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw CliException.Io("Cli_Error_CannotRead", pattern, ex.Message);
                }

                if (matches.Count == 0)
                {
                    throw CliException.Io("Cli_Error_NoMatch", pattern);
                }

                matches.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string match in matches)
                {
                    result.Add(new Entry(DisplayOf(host, match, pattern), match));
                }

                continue;
            }

            string full = host.Resolve(pattern);
            if (Directory.Exists(full))
            {
                List<string> files;
                try
                {
                    files = [.. Directory.EnumerateFiles(full, "*", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)];
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw CliException.Io("Cli_Error_CannotRead", pattern, ex.Message);
                }

                files.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    result.Add(new Entry(DisplayOf(host, file, pattern), file));
                }

                continue;
            }

            result.Add(new Entry(pattern, full));
        }

        return result;
    }

    /// <summary>指定の書き方に合わせた表示用のパス (相対で指定されたら相対、絶対なら絶対)。</summary>
    private static string DisplayOf(CliHost host, string full, string pattern)
    {
        if (Path.IsPathRooted(pattern))
        {
            return full;
        }

        string relative = Path.GetRelativePath(host.CurrentDirectory, full);
        return relative;
    }
}
