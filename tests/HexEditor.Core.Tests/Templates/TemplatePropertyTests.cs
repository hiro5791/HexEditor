using System.Globalization;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HexEditor.Core.Templates;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>演算子の優先順位とビットフィールドの切り出しを、無作為の値で基準の実装と比べる (TC-TPL-02-01、TC-TPL-06-01 の手順 3)。</summary>
public sealed class TemplatePropertyTests
{
    // 仕様 7 の表の 2 項演算子の段 (高い順)。同じ段の代表の演算子。
    private static readonly string[][] Levels =
    [
        ["*", "/", "%"],
        ["+", "-"],
        ["<<", ">>"],
        ["<", "<=", ">", ">="],
        ["==", "!="],
        ["&"],
        ["^"],
        ["|"],
        ["&&"],
        ["||"],
    ];

    private static Int128 Apply(string op, Int128 a, Int128 b) => op switch
    {
        "*" => a * b,
        "/" => b == 0 ? 0 : a / b,
        "%" => b == 0 ? 0 : a % b,
        "+" => a + b,
        "-" => a - b,
        "<<" => b >= 128 || b < 0 ? 0 : a << (int)b,
        ">>" => b >= 128 || b < 0 ? (a < 0 ? -1 : 0) : a >> (int)b,
        "<" => a < b ? 1 : 0,
        "<=" => a <= b ? 1 : 0,
        ">" => a > b ? 1 : 0,
        ">=" => a >= b ? 1 : 0,
        "==" => a == b ? 1 : 0,
        "!=" => a != b ? 1 : 0,
        "&" => a & b,
        "^" => a ^ b,
        "|" => a | b,
        "&&" => a != 0 && b != 0 ? 1 : 0,
        _ => a != 0 || b != 0 ? 1 : 0,
    };

    private sealed record Case(int Pair, int Op1, int Op2, int A, int B, int C);

    private static Arbitrary<Case> Cases() =>
        (from pair in Gen.Choose(0, Levels.Length + 2)
         from o1 in Gen.Choose(0, 3)
         from o2 in Gen.Choose(0, 3)
         from a in Gen.Choose(0, 1000)
         from b in Gen.Choose(1, 1000)
         from c in Gen.Choose(1, 1000)
         select new Case(pair, o1, o2, a, b, c)).ToArbitrary();

    [Property(MaxTest = 500)]
    [Trait(TC, "TC-TPL-02-01")]
    public Property AdjacentPrecedenceLevelsMatchTheParenthesizedForm() => Prop.ForAll(Cases(), c =>
    {
        string expr;
        Int128 expected;
        if (c.Pair < Levels.Length - 1)
        {
            // 隣り合う 2 つの段 (高い段の演算子は左右のどちらに置いても先に計算する)
            string hi = Levels[c.Pair][c.Op1 % Levels[c.Pair].Length];
            string lo = Levels[c.Pair + 1][c.Op2 % Levels[c.Pair + 1].Length];
            if ((c.A & 1) == 0)
            {
                expr = $"{c.A} {hi} {c.B} {lo} {c.C}";
                expected = Apply(lo, Apply(hi, c.A, c.B), c.C);
            }
            else
            {
                expr = $"{c.A} {lo} {c.B} {hi} {c.C}";
                expected = Apply(lo, c.A, Apply(hi, c.B, c.C));
            }
        }
        else if (c.Pair == Levels.Length - 1)
        {
            // 単項 (2) と乗除 (3)
            expr = $"-{c.A} * {c.B}";
            expected = -(Int128)c.A * c.B;
        }
        else if (c.Pair == Levels.Length)
        {
            // || (12) と ?: (13)
            expr = $"{c.A % 2} || {c.B % 2} ? {c.C} : 7";
            expected = (c.A % 2 != 0 || c.B % 2 != 0) ? c.C : 7;
        }
        else
        {
            // ?: (13) と = (14): 代入の右辺全体が条件式
            expr = $"x = {c.A % 2} ? {c.B} : {c.C}";
            expected = c.A % 2 != 0 ? c.B : c.C;
        }

        string template = expr.StartsWith("x =", StringComparison.Ordinal) ? $"local auto x = 0; {expr}; local auto v = x [[show]];" : $"local auto x = 0; local auto v = {expr} [[show]];";
        TemplateResult r = Run(template, []);
        object? actual = r.FindByPath("v")?.Value;
        Int128? got = actual switch { long l => l, Int128 i => i, ulong u => u, bool b => b ? 1 : 0, _ => null };
        return (got == expected).Label($"{expr} = {actual} (expected {expected}) {string.Join("; ", r.Diagnostics)}");
    });

    private const string TcpFlags = """
        bitfield TcpFlags : u16 {
            data_offset : 4;
            reserved    : 3;
            ns  : 1; cwr : 1; ece : 1; urg : 1;
            ack : 1; psh : 1; rst : 1; syn : 1; fin : 1;
        };
        be TcpFlags f;
        """;

    private static readonly (string Name, int Width)[] TcpFields =
    [
        ("data_offset", 4), ("reserved", 3), ("ns", 1), ("cwr", 1), ("ece", 1), ("urg", 1), ("ack", 1), ("psh", 1), ("rst", 1), ("syn", 1),
        ("fin", 1),
    ];

    [Property(MaxTest = 500)]
    [Trait(TC, "TC-TPL-06-01")]
    public Property BitfieldsMatchTheReferenceExtraction() => Prop.ForAll(
        (from v in Gen.Choose(0, 0xFFFF) from msb in Gen.Elements(true, false) select (v, msb)).ToArbitrary(), x =>
    {
        (int value, bool msb) = x;
        TemplateResult r = Run($"#pragma bitfield_order {(msb ? "msb_first" : "lsb_first")}\n" + TcpFlags, [(byte)(value >> 8), (byte)value]);
        int used = 0;
        var errors = new StringBuilder();
        foreach ((string name, int width) in TcpFields)
        {
            int shift = msb ? 16 - used - width : used;
            used += width;
            long expected = (value >> shift) & ((1 << width) - 1);
            long actual = Convert.ToInt64(r.FindByPath("f." + name)!.Value, CultureInfo.InvariantCulture);
            if (expected != actual)
            {
                errors.Append(CultureInfo.InvariantCulture, $"{name}: {actual} != {expected}; ");
            }
        }

        return (errors.Length == 0).Label($"0x{value:X4} {(msb ? "msb" : "lsb")}: {errors}");
    });
}
