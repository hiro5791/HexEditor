namespace HexEditor.Cli;

/// <summary>オプション 1 つの定義。<see cref="Name"/> は長い名前 (<c>--</c> なし)、<see cref="Short"/> は 1 文字の別名。</summary>
public sealed record OptionSpec(string Name, string? Short = null, bool HasValue = false, bool Repeatable = false, params string[] Aliases)
{
    /// <summary>値の数 (<c>--compare パス1 パス2</c> は 2。値は <see cref="ParsedArguments.Values"/> に 1 つずつ入る)。</summary>
    public int ValueCount { get; init; } = 1;

    public static OptionSpec Flag(string name, string? shortName = null) => new(name, shortName);

    public static OptionSpec Value(string name, string? shortName = null, bool repeatable = false) => new(name, shortName, true, repeatable);
}

/// <summary>解析した引数。</summary>
public sealed class ParsedArguments
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    /// <summary>オプションでない引数 (出現順)。</summary>
    public List<string> Positional { get; } = [];

    internal void AddValue(string name, string value)
    {
        if (!_values.TryGetValue(name, out List<string>? list))
        {
            _values[name] = list = [];
        }

        list.Add(value);
    }

    internal void AddFlag(string name) => _flags.Add(name);

    public bool Has(string name) => _flags.Contains(name) || _values.ContainsKey(name);

    public bool Flag(string name) => _flags.Contains(name);

    /// <summary>最後に指定された値。なければ null。</summary>
    public string? Value(string name) => _values.TryGetValue(name, out List<string>? list) ? list[^1] : null;

    /// <summary>指定されたすべての値 (出現順)。</summary>
    public IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out List<string>? list) ? list : [];

    /// <summary>指定された値のあるオプションの名前。</summary>
    public IEnumerable<string> ValueNames => _values.Keys;

    /// <summary>指定された値のないオプション (フラグ) の名前。</summary>
    public IEnumerable<string> FlagNames => _flags;
}

/// <summary>
/// オプションの解析 (AUTO-36 の仕様 2・3)。<c>--名前 値</c>、<c>--名前=値</c>、<c>-x 値</c>、<c>--</c> (以降はすべて引数)。
/// 定義にないオプションは終了コード 2。値を取るオプションは、次の引数がオプションの形でも値として取る (<c>--offset -16</c>)。
/// </summary>
public static class ArgumentParser
{
    public static ParsedArguments Parse(IReadOnlyList<string> args, IEnumerable<OptionSpec> specs, string commandName)
    {
        var byLong = new Dictionary<string, OptionSpec>(StringComparer.Ordinal);
        var byShort = new Dictionary<string, OptionSpec>(StringComparer.Ordinal);
        foreach (OptionSpec spec in specs)
        {
            byLong[spec.Name] = spec;
            foreach (string alias in spec.Aliases)
            {
                byLong[alias] = spec;
            }

            if (spec.Short is not null)
            {
                byShort[spec.Short] = spec;
            }
        }

        var result = new ParsedArguments();
        bool optionsEnded = false;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (optionsEnded || arg == "-" || !arg.StartsWith('-'))
            {
                result.Positional.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                optionsEnded = true;
                continue;
            }

            OptionSpec? spec;
            string? inline = null;
            string shown = arg;
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                string name = arg[2..];
                int eq = name.IndexOf('=', StringComparison.Ordinal);
                if (eq >= 0)
                {
                    inline = name[(eq + 1)..];
                    name = name[..eq];
                    shown = "--" + name;
                }

                byLong.TryGetValue(name, out spec);
            }
            else
            {
                // 数値の引数 (-16 など) をオプションとみなさない。
                if (long.TryParse(arg, out _))
                {
                    result.Positional.Add(arg);
                    continue;
                }

                string name = arg[1..];
                int eq = name.IndexOf('=', StringComparison.Ordinal);
                if (eq >= 0)
                {
                    inline = name[(eq + 1)..];
                    name = name[..eq];
                    shown = "-" + name;
                }

                byShort.TryGetValue(name, out spec);
            }

            if (spec is null)
            {
                throw CliException.Usage("Cli_Error_UnknownOption", shown, commandName);
            }

            if (!spec.HasValue)
            {
                if (inline is not null)
                {
                    throw CliException.Usage("Cli_Error_OptionTakesNoValue", shown, commandName);
                }

                result.AddFlag(spec.Name);
                continue;
            }

            // 繰り返せないオプションを繰り返したら、最後の値を使う (シェルのエイリアスで既定値を付けて上書きできるように)。
            for (int v = 0; v < spec.ValueCount; v++)
            {
                if (v == 0 && inline is not null)
                {
                    result.AddValue(spec.Name, inline);
                }
                else if (i + 1 < args.Count)
                {
                    result.AddValue(spec.Name, args[++i]);
                }
                else
                {
                    throw CliException.Usage("Cli_Error_MissingValue", shown, commandName);
                }
            }
        }

        return result;
    }
}
