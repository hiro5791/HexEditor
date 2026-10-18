using System.Text;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Search.Yara;

/// <summary>ルール 1 つ。</summary>
public sealed class YaraRule
{
    public required string Name { get; init; }

    public string Namespace { get; init; } = "default";

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>メタ情報 (識別子と値の表記。同じ識別子が複数あってもよい)。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Meta { get; init; } = [];

    public bool IsPrivate { get; init; }

    public bool IsGlobal { get; init; }

    /// <summary>ルールのファイル (include で読んだファイルを含む)。</summary>
    public string? File { get; init; }

    public int Line { get; init; }

    internal List<YaraStringDef> Strings { get; } = [];

    internal YaraExpr Condition { get; set; } = YaraConst.False;

    /// <summary>文字列の識別子。</summary>
    public IEnumerable<string> StringIdentifiers => Strings.Select(s => s.Identifier);
}

internal enum YaraStringKind
{
    Text,
    Hex,
    Regex,
}

/// <summary>ルールの文字列 1 つ ($a = …)。照合の部品 (<see cref="Matchers"/>) はコンパイル時に作る。</summary>
internal sealed class YaraStringDef
{
    public required string Identifier { get; init; }

    public required YaraRule Rule { get; init; }

    public YaraStringKind Kind { get; init; }

    public bool IsPrivate { get; init; }

    public bool Fullword { get; init; }

    public List<YaraMatcher> Matchers { get; } = [];

    /// <summary>規則の中での番号 (照合の結果の添字)。</summary>
    public int Index { get; set; }

    /// <summary>条件から参照されたか (参照されない文字列はエラー)。</summary>
    public bool Referenced { get; set; }
}

/// <summary>一致 1 件 (スキャンの対象の先頭からの位置)。</summary>
public readonly record struct YaraMatch(long Offset, int Length);

/// <summary>条件の評価の環境。</summary>
internal sealed class YaraContext
{
    private readonly List<Dictionary<string, YValue>> _scopes = [];
    private int _ticks;

    public required IYaraData Data { get; init; }

    /// <summary>filesize (範囲指定時は範囲の長さ)。</summary>
    public long FileSize => Data.Length;

    public required Func<YaraStringDef, IReadOnlyList<YaraMatch>> Matches { get; init; }

    public Dictionary<YaraRule, bool> RuleResults { get; } = [];

    public Dictionary<string, YValue> Externals { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, YStruct> Modules { get; init; } = new(StringComparer.Ordinal);

    public List<string> ConsoleOutput { get; } = [];

    public long? EntryPoint { get; init; }

    public YaraStringDef? Anonymous { get; set; }

    public YaraLimits Limits { get; init; } = new();

    public void Push(Dictionary<string, YValue> scope) => _scopes.Add(scope);

    public void Pop() => _scopes.RemoveAt(_scopes.Count - 1);

    public YValue Variable(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out YValue v))
            {
                return v;
            }
        }

        return YValue.Undefined;
    }

    /// <summary>ループの 4,096 回ごとに時間の上限とキャンセルを確かめる。</summary>
    public void Tick()
    {
        if ((++_ticks & 4095) == 0)
        {
            Limits.Check();
        }
    }

    /// <summary>対象の <paramref name="offset"/> から読む (範囲外なら false)。</summary>
    public bool TryRead(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > Data.Length)
        {
            return false;
        }

        Data.Read(offset, destination);
        return true;
    }
}

/// <summary>時間の上限とキャンセル (FIND-33 の仕様 7)。</summary>
internal sealed class YaraLimits
{
    public DateTimeOffset Deadline { get; init; } = DateTimeOffset.MaxValue;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public CancellationToken Token { get; init; }

    public void Check()
    {
        Token.ThrowIfCancellationRequested();
        if (Time.GetUtcNow() > Deadline)
        {
            throw new YaraTimeoutException();
        }
    }
}

/// <summary>スキャンの時間の上限に達した。</summary>
public sealed class YaraTimeoutException() : Exception("時間の上限に達しました。");

// ---- 式 ----

internal abstract class YaraExpr
{
    public abstract YValue Eval(YaraContext c);

    /// <summary>
    /// 評価の再帰の前にスタックの残りを確かめる (<see cref="InsufficientExecutionStackException"/>)。左に長く連なる式 (<c>a + a + …</c>、
    /// <c>x.y.z…</c>) は解釈では深さの上限にかからないが、評価では 1 段ごとに再帰するため、スタックオーバーフローにしない。
    /// </summary>
    protected static void EnsureStack() => System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();

    /// <summary>コンパイル時に分かる値の形 (モジュールの構造体・配列・辞書・関数。名前の確かめに使う)。</summary>
    public virtual object? Shape => null;
}

internal sealed class YaraConst(YValue value) : YaraExpr
{
    public static readonly YaraConst False = new(YValue.False);

    public YValue Value { get; } = value;

    public override YValue Eval(YaraContext c) => Value;
}

internal sealed class YaraFileSize : YaraExpr
{
    public override YValue Eval(YaraContext c) => YValue.Int(c.FileSize);
}

internal sealed class YaraEntryPoint : YaraExpr
{
    public override YValue Eval(YaraContext c) => c.EntryPoint is long e ? YValue.Int(e) : YValue.Undefined;
}

/// <summary>$a (null なら for … of の中の $)、$a at x、$a in (a..b)。</summary>
internal sealed class YaraStringTest(YaraStringDef? def, YaraExpr? at, (YaraExpr Lo, YaraExpr Hi)? range) : YaraExpr
{
    public override YValue Eval(YaraContext c)
    {
        YaraStringDef? d = def ?? c.Anonymous;
        if (d is null)
        {
            return YValue.False;
        }

        return Test(d, c, at, range);
    }

    public static YValue Test(YaraStringDef d, YaraContext c, YaraExpr? at, (YaraExpr Lo, YaraExpr Hi)? range)
    {
        IReadOnlyList<YaraMatch> matches = c.Matches(d);
        if (at is not null)
        {
            YValue where = at.Eval(c);
            if (where.Kind != YKind.Integer)
            {
                return YValue.False;
            }

            return YValue.Bool(BinarySearch(matches, where.I) >= 0);
        }

        if (range is { } r)
        {
            YValue lo = r.Lo.Eval(c), hi = r.Hi.Eval(c);
            if (lo.Kind != YKind.Integer || hi.Kind != YKind.Integer)
            {
                return YValue.False;
            }

            return YValue.Bool(CountIn(matches, lo.I, hi.I) > 0);
        }

        return YValue.Bool(matches.Count > 0);
    }

    internal static int BinarySearch(IReadOnlyList<YaraMatch> matches, long offset)
    {
        int lo = 0, hi = matches.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            long v = matches[mid].Offset;
            if (v == offset)
            {
                return mid;
            }

            if (v < offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }

    internal static long CountIn(IReadOnlyList<YaraMatch> matches, long lo, long hi)
    {
        long n = 0;
        foreach (YaraMatch m in matches)
        {
            if (m.Offset > hi)
            {
                break;
            }

            if (m.Offset >= lo)
            {
                n++;
            }
        }

        return n;
    }
}

/// <summary>#a、#a in (a..b)。</summary>
internal sealed class YaraStringCount(YaraStringDef? def, (YaraExpr Lo, YaraExpr Hi)? range) : YaraExpr
{
    public override YValue Eval(YaraContext c)
    {
        YaraStringDef? d = def ?? c.Anonymous;
        if (d is null)
        {
            return YValue.Undefined;
        }

        IReadOnlyList<YaraMatch> matches = c.Matches(d);
        if (range is { } r)
        {
            YValue lo = r.Lo.Eval(c), hi = r.Hi.Eval(c);
            return lo.Kind == YKind.Integer && hi.Kind == YKind.Integer ? YValue.Int(YaraStringTest.CountIn(matches, lo.I, hi.I)) : YValue.Undefined;
        }

        return YValue.Int(matches.Count);
    }
}

/// <summary>@a[i] (位置) と !a[i] (長さ)。i は 1 から。</summary>
internal sealed class YaraStringOffset(YaraStringDef? def, YaraExpr? index, bool length) : YaraExpr
{
    public override YValue Eval(YaraContext c)
    {
        YaraStringDef? d = def ?? c.Anonymous;
        if (d is null)
        {
            return YValue.Undefined;
        }

        long i = 1;
        if (index is not null)
        {
            YValue v = index.Eval(c);
            if (v.Kind != YKind.Integer)
            {
                return YValue.Undefined;
            }

            i = v.I;
        }

        IReadOnlyList<YaraMatch> matches = c.Matches(d);
        if (i < 1 || i > matches.Count)
        {
            return YValue.Undefined;
        }

        YaraMatch m = matches[(int)(i - 1)];
        return YValue.Int(length ? m.Length : m.Offset);
    }
}

/// <summary>int8〜uint32be(offset)。</summary>
internal sealed class YaraReadInt(int size, bool signed, bool bigEndian, YaraExpr offset) : YaraExpr
{
    public override YValue Eval(YaraContext c)
    {
        YValue at = offset.Eval(c);
        if (at.Kind != YKind.Integer)
        {
            return YValue.Undefined;
        }

        Span<byte> b = stackalloc byte[4];
        if (!c.TryRead(at.I, b[..size]))
        {
            return YValue.Undefined;
        }

        ulong v = 0;
        for (int i = 0; i < size; i++)
        {
            v = bigEndian ? (v << 8) | b[i] : v | ((ulong)b[i] << (8 * i));
        }

        if (signed)
        {
            int shift = 64 - 8 * size;
            return YValue.Int((long)(v << shift) >> shift);
        }

        return YValue.Int((long)v);
    }
}

/// <summary>ほかのルールの結果。</summary>
internal sealed class YaraRuleRef(YaraRule rule) : YaraExpr
{
    public YaraRule Rule { get; } = rule;

    public override YValue Eval(YaraContext c) => YValue.Bool(c.RuleResults.TryGetValue(Rule, out bool r) && r);
}

/// <summary>外部変数 (ルールセットごとの設定。FIND-33 の仕様 5)。</summary>
internal sealed class YaraExternalRef(string name) : YaraExpr
{
    public override YValue Eval(YaraContext c) => c.Externals.TryGetValue(name, out YValue v) ? v : YValue.Undefined;
}

internal sealed class YaraModuleRef(string name, YStruct schema) : YaraExpr
{
    public override object? Shape => schema;

    public override YValue Eval(YaraContext c) => c.Modules.TryGetValue(name, out YStruct? m) ? YValue.Obj(m) : YValue.Undefined;
}

/// <summary>for のループ変数。</summary>
internal sealed class YaraVariable(string name, object? shape) : YaraExpr
{
    public override object? Shape => shape;

    public override YValue Eval(YaraContext c) => c.Variable(name);
}

internal sealed class YaraMember(YaraExpr target, string name, object? shape) : YaraExpr
{
    public override object? Shape => shape;

    public override YValue Eval(YaraContext c)
    {
        EnsureStack();
        YValue t = target.Eval(c);
        return t.O is YStruct s ? s[name] : YValue.Undefined;
    }
}

internal sealed class YaraIndex(YaraExpr target, YaraExpr index, object? shape) : YaraExpr
{
    public override object? Shape => shape;

    public override YValue Eval(YaraContext c)
    {
        EnsureStack();
        YValue t = target.Eval(c), i = index.Eval(c);
        if (t.O is YArray array && i.Kind == YKind.Integer)
        {
            return i.I >= 0 && i.I < array.Items.Count ? array.Items[(int)i.I] : YValue.Undefined;
        }

        if (t.O is YDictionary dict && i.Kind == YKind.String)
        {
            return dict.Items.TryGetValue(i.AsText, out YValue v) ? v : YValue.Undefined;
        }

        return YValue.Undefined;
    }
}

internal sealed class YaraCall(YaraExpr target, YaraExpr[] args) : YaraExpr
{
    public override YValue Eval(YaraContext c)
    {
        EnsureStack();
        if (target.Eval(c).O is not YFunction f)
        {
            return YValue.Undefined;
        }

        var values = new YValue[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            values[i] = args[i].Eval(c);
        }

        return f.Invoke(values, c);
    }
}

internal sealed class YaraUnary(string op, YaraExpr operand) : YaraExpr
{
    public override YValue Eval(YaraContext c)
    {
        EnsureStack();
        YValue v = operand.Eval(c);
        switch (op)
        {
            case "defined":
                return YValue.Bool(!v.IsUndefined);
            case "not":
                return v.IsUndefined ? YValue.Undefined : YValue.Bool(!v.Truthy);
            case "-":
                return v.Kind == YKind.Float ? YValue.Float(-v.F) : v.IsNumber ? YValue.Int(-v.I) : YValue.Undefined;
            default:
                return v.Kind is YKind.Integer or YKind.Boolean ? YValue.Int(~v.I) : YValue.Undefined;
        }
    }
}

internal sealed class YaraBinary(string op, YaraExpr left, YaraExpr right) : YaraExpr
{
    public override YValue Eval(YaraContext c)
    {
        EnsureStack();
        if (op == "and")
        {
            YValue l = left.Eval(c);
            if (!l.IsUndefined && !l.Truthy)
            {
                return YValue.False;
            }

            YValue r = right.Eval(c);
            if (!r.IsUndefined && !r.Truthy)
            {
                return YValue.False;
            }

            return l.IsUndefined || r.IsUndefined ? YValue.Undefined : YValue.True;
        }

        if (op == "or")
        {
            YValue l = left.Eval(c);
            if (l.Truthy)
            {
                return YValue.True;
            }

            YValue r = right.Eval(c);
            if (r.Truthy)
            {
                return YValue.True;
            }

            return l.IsUndefined || r.IsUndefined ? YValue.Undefined : YValue.False;
        }

        YValue a = left.Eval(c), b = right.Eval(c);
        if (a.IsUndefined || b.IsUndefined)
        {
            return YValue.Undefined;
        }

        if (a.Kind == YKind.String || b.Kind == YKind.String)
        {
            return StringOp(a, b);
        }

        if (a.Kind == YKind.Float || b.Kind == YKind.Float)
        {
            double x = a.AsDouble, y = b.AsDouble;
            return op switch
            {
                "+" => YValue.Float(x + y),
                "-" => YValue.Float(x - y),
                "*" => YValue.Float(x * y),
                "\\" => y == 0 ? YValue.Undefined : YValue.Float(x / y),
                "==" => YValue.Bool(x == y),
                "!=" => YValue.Bool(x != y),
                "<" => YValue.Bool(x < y),
                "<=" => YValue.Bool(x <= y),
                ">" => YValue.Bool(x > y),
                ">=" => YValue.Bool(x >= y),
                _ => YValue.Undefined,
            };
        }

        long p = a.I, q = b.I;
        return op switch
        {
            "+" => YValue.Int(unchecked(p + q)),
            "-" => YValue.Int(unchecked(p - q)),
            "*" => YValue.Int(unchecked(p * q)),
            "\\" => q == 0 || (p == long.MinValue && q == -1) ? YValue.Undefined : YValue.Int(p / q),
            "%" => q == 0 || (p == long.MinValue && q == -1) ? YValue.Undefined : YValue.Int(p % q),
            "&" => YValue.Int(p & q),
            "|" => YValue.Int(p | q),
            "^" => YValue.Int(p ^ q),
            "<<" => YValue.Int(q is < 0 or >= 64 ? 0 : p << (int)q),
            ">>" => YValue.Int(q is < 0 or >= 64 ? 0 : p >> (int)q),
            "==" => YValue.Bool(p == q),
            "!=" => YValue.Bool(p != q),
            "<" => YValue.Bool(p < q),
            "<=" => YValue.Bool(p <= q),
            ">" => YValue.Bool(p > q),
            ">=" => YValue.Bool(p >= q),
            _ => YValue.Undefined,
        };
    }

    private YValue StringOp(YValue a, YValue b)
    {
        if (a.Kind != YKind.String || b.Kind != YKind.String)
        {
            return YValue.Undefined;
        }

        ReadOnlySpan<byte> x = a.S, y = b.S;
        int cmp = x.SequenceCompareTo(y);
        return op switch
        {
            "==" => YValue.Bool(cmp == 0),
            "!=" => YValue.Bool(cmp != 0),
            "<" => YValue.Bool(cmp < 0),
            "<=" => YValue.Bool(cmp <= 0),
            ">" => YValue.Bool(cmp > 0),
            ">=" => YValue.Bool(cmp >= 0),
            "contains" => YValue.Bool(x.IndexOf(y) >= 0),
            "startswith" => YValue.Bool(x.StartsWith(y)),
            "endswith" => YValue.Bool(x.EndsWith(y)),
            "icontains" => YValue.Bool(Lower(x).AsSpan().IndexOf(Lower(y)) >= 0),
            "istartswith" => YValue.Bool(Lower(x).AsSpan().StartsWith(Lower(y))),
            "iendswith" => YValue.Bool(Lower(x).AsSpan().EndsWith(Lower(y))),
            "iequals" => YValue.Bool(Lower(x).AsSpan().SequenceEqual(Lower(y))),
            _ => YValue.Undefined,
        };
    }

    private static byte[] Lower(ReadOnlySpan<byte> s)
    {
        byte[] r = s.ToArray();
        for (int i = 0; i < r.Length; i++)
        {
            if (r[i] is >= (byte)'A' and <= (byte)'Z')
            {
                r[i] += 32;
            }
        }

        return r;
    }
}

/// <summary>文字列 matches /…/。</summary>
internal sealed class YaraMatchesRegex(YaraExpr target, Regex regex) : YaraExpr
{
    public override YValue Eval(YaraContext c)
    {
        YValue v = target.Eval(c);
        return v.Kind == YKind.String ? YValue.Bool(regex.IsMatch(v.AsText)) : YValue.Undefined;
    }
}

/// <summary>all・any・none・N・N% (for と of の数え方)。</summary>
internal sealed class YaraQuantifier(string kind, YaraExpr? count)
{
    public bool Satisfied(long trues, long total, YaraContext c)
    {
        switch (kind)
        {
            case "all":
                return trues == total;
            case "any":
                return trues > 0;
            case "none":
                return trues == 0;
        }

        YValue n = count!.Eval(c);
        if (n.Kind != YKind.Integer)
        {
            return false;
        }

        return kind == "percent" ? trues * 100 >= n.I * total : trues >= n.I;
    }
}

/// <summary>N of ($a, $b*) [at x | in (a..b)]、N of (rule1, rule2*)。</summary>
internal sealed class YaraOf(YaraQuantifier quantifier, IReadOnlyList<YaraStringDef>? strings, IReadOnlyList<YaraRule>? rules, YaraExpr? at,
    (YaraExpr Lo, YaraExpr Hi)? range) : YaraExpr
{
    public override YValue Eval(YaraContext c)
    {
        long trues = 0, total;
        if (strings is not null)
        {
            total = strings.Count;
            foreach (YaraStringDef s in strings)
            {
                if (YaraStringTest.Test(s, c, at, range).Truthy)
                {
                    trues++;
                }
            }
        }
        else
        {
            total = rules!.Count;
            foreach (YaraRule r in rules)
            {
                if (c.RuleResults.TryGetValue(r, out bool ok) && ok)
                {
                    trues++;
                }
            }
        }

        return YValue.Bool(quantifier.Satisfied(trues, total, c));
    }
}

/// <summary>for N of ($a, $b) : (式)。式の中の $・#・@・! はその文字列。</summary>
internal sealed class YaraForOf(YaraQuantifier quantifier, IReadOnlyList<YaraStringDef> strings, YaraExpr body) : YaraExpr
{
    public override YValue Eval(YaraContext c)
    {
        long trues = 0;
        YaraStringDef? saved = c.Anonymous;
        try
        {
            foreach (YaraStringDef s in strings)
            {
                c.Tick();
                c.Anonymous = s;
                if (body.Eval(c).Truthy)
                {
                    trues++;
                }
            }
        }
        finally
        {
            c.Anonymous = saved;
        }

        return YValue.Bool(quantifier.Satisfied(trues, strings.Count, c));
    }
}

/// <summary>for N i in (a..b) / (x, y, …) / 配列・辞書 : (式)。</summary>
internal sealed class YaraForIn(YaraQuantifier quantifier, string[] names, YaraExpr? lo, YaraExpr? hi, YaraExpr[]? items, YaraExpr? collection,
    YaraExpr body) : YaraExpr
{
    public override YValue Eval(YaraContext c)
    {
        long trues = 0, total = 0;
        var scope = new Dictionary<string, YValue>(StringComparer.Ordinal);
        c.Push(scope);
        try
        {
            foreach (YValue[] values in Iterate(c))
            {
                c.Tick();
                total++;
                for (int i = 0; i < names.Length; i++)
                {
                    scope[names[i]] = i < values.Length ? values[i] : YValue.Undefined;
                }

                if (body.Eval(c).Truthy)
                {
                    trues++;
                }
            }
        }
        finally
        {
            c.Pop();
        }

        return YValue.Bool(quantifier.Satisfied(trues, total, c));
    }

    private IEnumerable<YValue[]> Iterate(YaraContext c)
    {
        if (lo is not null)
        {
            YValue a = lo.Eval(c), b = hi!.Eval(c);
            if (a.Kind != YKind.Integer || b.Kind != YKind.Integer)
            {
                yield break;
            }

            for (long i = a.I; i <= b.I; i++)
            {
                yield return [YValue.Int(i)];
                if (i == long.MaxValue)
                {
                    yield break;
                }
            }

            yield break;
        }

        if (items is not null)
        {
            foreach (YaraExpr e in items)
            {
                yield return [e.Eval(c)];
            }

            yield break;
        }

        YValue v = collection!.Eval(c);
        if (v.O is YArray array)
        {
            foreach (YValue item in array.Items)
            {
                yield return [item];
            }
        }
        else if (v.O is YDictionary dict)
        {
            foreach ((string key, YValue value) in dict.Items)
            {
                yield return [YValue.Str(Encoding.UTF8.GetBytes(key)), value];
            }
        }
    }
}
