using HexEditor.Core.Engine;

namespace HexEditor.Core.Search.Yara;

/// <summary>スキャンの対象 (ドキュメントの範囲、ファイル、バイト列)。読めない位置は例外。</summary>
public interface IYaraData
{
    long Length { get; }

    void Read(long offset, Span<byte> destination);
}

/// <summary>バイト列 (テスト用)。</summary>
public sealed class YaraBytes(byte[] data) : IYaraData
{
    public long Length => data.Length;

    public void Read(long offset, Span<byte> destination) => data.AsSpan((int)offset, destination.Length).CopyTo(destination);
}

/// <summary>ドキュメントのスナップショットの範囲 (未保存の編集を含む現在の状態。FIND-33 の仕様 4)。</summary>
public sealed class YaraSnapshotData(DocumentSnapshot snapshot, long offset, long length) : IYaraData
{
    public long Offset { get; } = offset;

    public long Length { get; } = length;

    public void Read(long position, Span<byte> destination) =>
        Editing.Codecs.RangeReader.ReadExact(snapshot, Offset + position, destination);
}

/// <summary>ファイル (フォルダのスキャン。FIND-30 と同じ対象指定)。</summary>
public sealed class YaraFileData(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long length) : IYaraData
{
    public long Length { get; } = length;

    public void Read(long offset, Span<byte> destination)
    {
        int done = 0;
        while (done < destination.Length)
        {
            int n = RandomAccess.Read(handle, destination[done..], offset + done);
            if (n <= 0)
            {
                throw new IOException("ファイルを最後まで読めませんでした。");
            }

            done += n;
        }
    }
}

/// <summary>スキャンの設定 (FIND-33 の仕様 6・7)。</summary>
public sealed record YaraScanOptions
{
    public const int DefaultBlockSize = 64 * 1024 * 1024;

    /// <summary>ブロックの重なり幅 (前のブロックの末尾 64 KB)。</summary>
    public const int DefaultOverlap = 64 * 1024;

    public int BlockSize { get; init; } = DefaultBlockSize;

    public int Overlap { get; init; } = DefaultOverlap;

    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromSeconds(600);

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>文字列 1 つあたりの一致の上限 (YARA と同じ 1,000,000)。</summary>
    public int MaxMatchesPerString { get; init; } = 1_000_000;

    /// <summary>スキャンのときに上書きする外部変数 (null ならコンパイル時の値)。</summary>
    public IReadOnlyDictionary<string, string>? Externals { get; init; }
}

/// <summary>一致した文字列 1 件 (<see cref="Offset"/> はスキャンの対象の先頭から)。</summary>
public readonly record struct YaraStringResult(string Identifier, long Offset, int Length);

/// <summary>一致したルール 1 つ。<see cref="Strings"/> は非公開でない文字列の一致 (条件だけのルールは空)。</summary>
public sealed record YaraRuleResult(YaraRule Rule, IReadOnlyList<YaraStringResult> Strings);

/// <summary>スキャンの結果。</summary>
public sealed class YaraScanResult
{
    public List<YaraRuleResult> Rules { get; } = [];

    /// <summary>時間の上限に達した (それまでの結果を残す)。</summary>
    public bool TimedOut { get; internal set; }

    public bool Cancelled { get; internal set; }

    /// <summary>一致の数の上限に達した文字列 (ルール名:識別子)。</summary>
    public List<string> TooManyMatches { get; } = [];

    /// <summary>console モジュールの出力。</summary>
    public List<string> ConsoleOutput { get; } = [];

    public long BytesScanned { get; internal set; }
}

/// <summary>
/// YARA のスキャン (FIND-33)。データを <see cref="YaraScanOptions.BlockSize"/> のブロックに分け、前のブロックの末尾
/// <see cref="YaraScanOptions.Overlap"/> と重ねて照合し、重なり部分の一致の重複を取り除く (仕様 6)。条件は、すべてのブロックの一致を集めてから
/// 1 回評価する (filesize はスキャンの対象の長さ。データを読む関数は対象のどの位置も読める)。pe・elf は先頭のブロックで解析する。
/// メモリはブロックの大きさ + 重なり幅 + 一致の一覧。
/// </summary>
public static class YaraScanner
{
    public static YaraScanResult Scan(YaraRuleSet rules, IYaraData data, YaraScanOptions? options = null, CancellationToken token = default,
        Action<long>? progress = null)
    {
        options ??= new YaraScanOptions();
        var limits = new YaraLimits { Deadline = options.Time.GetUtcNow() + options.TimeLimit, Time = options.Time, Token = token };
        var result = new YaraScanResult();
        var defs = rules.Rules.SelectMany(r => r.Strings).ToList();
        var matches = new Dictionary<YaraStringDef, List<YaraMatch>>();
        var seen = new Dictionary<YaraStringDef, HashSet<(long, int)>>();
        foreach (YaraStringDef d in defs)
        {
            matches[d] = [];
            seen[d] = [];
        }

        var modules = new Dictionary<string, YStruct>(StringComparer.Ordinal);
        long? entryPoint = null;
        long length = data.Length;
        int blockSize = Math.Max(1, options.BlockSize), overlap = Math.Clamp(options.Overlap, 0, blockSize);
        byte[] buffer = new byte[(int)Math.Min(length, (long)blockSize + overlap)];
        try
        {
            for (long nominal = 0; nominal == 0 || nominal < length; nominal += blockSize)
            {
                limits.Check();
                long start = nominal == 0 ? 0 : nominal - overlap;
                long end = Math.Min(length, nominal + blockSize);
                int n = (int)(end - start);
                data.Read(start, buffer.AsSpan(0, n));
                ReadOnlySpan<byte> block = buffer.AsSpan(0, n);
                if (nominal == 0)
                {
                    ReadOnlySpan<byte> head = block[..(int)Math.Min(n, blockSize)];
                    foreach (string m in rules.Modules)
                    {
                        modules[m] = YaraModules.Build(m, head);
                    }

                    entryPoint = YaraModules.EntryPoint(head);
                }

                foreach (YaraStringDef def in defs)
                {
                    List<YaraMatch> list = matches[def];
                    HashSet<(long, int)> once = seen[def];
                    foreach (YaraMatcher matcher in def.Matchers)
                    {
                        limits.Check();
                        bool full = false;
                        matcher.FindAll(block, start == 0, end == length, limits, (at, len) =>
                        {
                            long abs = start + at;

                            // 前のブロックの中に収まる一致は、前のブロックで見つけている。
                            if (nominal > 0 && abs < nominal && abs + len <= nominal)
                            {
                                return true;
                            }

                            if (def.Fullword && !IsFullword(data, buffer, start, n, abs, len, matcher.Wide))
                            {
                                return true;
                            }

                            // 正規表現・16 進文字列は開始位置ごとに 1 件。
                            (long, int) key = def.Kind == YaraStringKind.Text ? (abs, len) : (abs, -1);
                            if (!once.Add(key))
                            {
                                return true;
                            }

                            if (list.Count >= options.MaxMatchesPerString)
                            {
                                full = true;
                                return false;
                            }

                            list.Add(new YaraMatch(abs, len));
                            return true;
                        });
                        if (full && !result.TooManyMatches.Contains(def.Rule.Name + ":" + def.Identifier))
                        {
                            result.TooManyMatches.Add(def.Rule.Name + ":" + def.Identifier);
                        }
                    }
                }

                result.BytesScanned = end;
                progress?.Invoke(end);
                if (length == 0)
                {
                    break;
                }
            }
        }
        catch (YaraTimeoutException)
        {
            result.TimedOut = true;
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }

        foreach (List<YaraMatch> list in matches.Values)
        {
            list.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : a.Length.CompareTo(b.Length));
        }

        // 条件の評価 (時間の上限に達した・キャンセルした後も、集めた一致で評価して残す。評価にも上限を設ける)。
        var evalLimits = result.TimedOut || result.Cancelled
            ? new YaraLimits { Deadline = options.Time.GetUtcNow() + TimeSpan.FromSeconds(10), Time = options.Time }
            : limits;
        var externals = new Dictionary<string, YValue>(rules.Externals, StringComparer.Ordinal);
        if (options.Externals is { } overrides)
        {
            foreach ((string name, string text) in overrides)
            {
                if (externals.TryGetValue(name, out YValue declared))
                {
                    externals[name] = ParseExternal(declared, text);
                }
            }
        }

        var context = new YaraContext
        {
            Data = data,
            Matches = d => matches[d],
            Externals = externals,
            Modules = modules,
            EntryPoint = entryPoint,
            Limits = evalLimits,
        };
        try
        {
            foreach (YaraRule rule in rules.Rules)
            {
                evalLimits.Check();
                context.RuleResults[rule] = rule.Condition.Eval(context).Truthy;
            }
        }
        catch (YaraTimeoutException)
        {
            result.TimedOut = true;
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }

        // 大域のルールが成り立たない名前空間のルールはすべて一致しない。
        var failedNamespaces = rules.Rules.Where(r => r.IsGlobal && context.RuleResults.TryGetValue(r, out bool ok) && !ok).Select(r => r.Namespace).ToHashSet();
        foreach (YaraRule rule in rules.Rules)
        {
            if (rule.IsPrivate || failedNamespaces.Contains(rule.Namespace) || !context.RuleResults.TryGetValue(rule, out bool ok) || !ok)
            {
                continue;
            }

            var strings = new List<YaraStringResult>();
            foreach (YaraStringDef def in rule.Strings.Where(s => !s.IsPrivate))
            {
                strings.AddRange(matches[def].Select(m => new YaraStringResult(def.Identifier, m.Offset, m.Length)));
            }

            strings.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : string.CompareOrdinal(a.Identifier, b.Identifier));
            result.Rules.Add(new YaraRuleResult(rule, strings));
        }

        result.ConsoleOutput.AddRange(context.ConsoleOutput);
        return result;
    }

    private static YValue ParseExternal(YValue declared, string text) => declared.Kind switch
    {
        YKind.Boolean => bool.TryParse(text, out bool b) ? YValue.Bool(b) : declared,
        YKind.Integer => long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long l)
            ? YValue.Int(l) : declared,
        YKind.Float => double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)
            ? YValue.Float(d) : declared,
        _ => YValue.Str(text),
    };

    /// <summary>fullword: 一致の前後が英数字でない (ワイドの形は 2 バイトの文字で確かめる)。</summary>
    private static bool IsFullword(IYaraData data, byte[] buffer, long start, int n, long abs, int len, bool wide)
    {
        int unit = wide ? 2 : 1;
        return !IsWordAt(data, buffer, start, n, abs - unit, wide) && !IsWordAt(data, buffer, start, n, abs + len, wide);
    }

    private static bool IsWordAt(IYaraData data, byte[] buffer, long start, int n, long at, bool wide)
    {
        int unit = wide ? 2 : 1;
        if (at < 0 || at + unit > data.Length)
        {
            return false;
        }

        Span<byte> c = stackalloc byte[2];
        if (at >= start && at + unit <= start + n)
        {
            buffer.AsSpan((int)(at - start), unit).CopyTo(c);
        }
        else
        {
            data.Read(at, c[..unit]);
        }

        return char.IsAsciiLetterOrDigit((char)c[0]) && (!wide || c[1] == 0);
    }
}
