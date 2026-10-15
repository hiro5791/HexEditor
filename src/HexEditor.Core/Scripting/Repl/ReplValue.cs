using System.Globalization;
using System.Numerics;
using System.Text;

namespace HexEditor.Core.Scripting.Repl;

/// <summary>結果の値の種類 (AUTO-23 の仕様 2)。</summary>
public enum ReplValueKind
{
    Undefined,
    Null,
    Boolean,
    Number,
    BigInt,
    String,

    /// <summary>バイト列 (<c>Uint8Array</c>・<c>byte[]</c>)。Hex ダンプ形式で表示する。</summary>
    Bytes,
    Array,
    Object,
    Function,
    Error,
    Symbol,
    Date,
    Other,
}

/// <summary>展開できるツリーの子 (プロパティ・要素)。</summary>
public sealed record ReplProperty(string Name, ReplValue Value);

/// <summary>
/// コンソールの結果・デバッガの変数の表示用の値 (AUTO-21 の仕様 4、AUTO-23 の仕様 2)。スクリプトのスレッドで作り、
/// 作ったあとはエンジンに触れずに読める (ツリーは深さ・個数の上限まで先に展開する)。
/// </summary>
public sealed class ReplValue
{
    /// <summary>バイト列を Hex ダンプにする最大の長さ (超える分は省略して長さを表示する。AUTO-23 の仕様 2)。</summary>
    public const int MaxDumpBytes = 256;

    /// <summary>1 つのオブジェクト・配列で展開する子の最大数。</summary>
    public const int MaxChildren = 100;

    public required ReplValueKind Kind { get; init; }

    /// <summary>型の名前 (<c>Object</c>、<c>Array(3)</c>、<c>Uint8Array(16)</c>、<c>string</c> など)。</summary>
    public required string Type { get; init; }

    /// <summary>1 行の表示 (オブジェクトは <c>{a: 1, b: 2}</c> のような要約)。バイト列は Hex ダンプ全体 (複数行)。</summary>
    public required string Text { get; init; }

    /// <summary>
    /// <c>BigInt</c> の 16 進の表示 (AUTO-23 の仕様 2: 10 進と 16 進の両方で表示する)。他の値では null。
    /// </summary>
    public string? Hex { get; init; }

    /// <summary>子 (展開できるもの)。</summary>
    public IReadOnlyList<ReplProperty> Children { get; init; } = [];

    /// <summary>子の総数 (展開した数より多いとき <see cref="Children"/> は先頭だけ)。</summary>
    public int ChildCount { get; init; }

    public bool Expandable => ChildCount > 0;

    /// <summary>バイト列の長さ (<see cref="ReplValueKind.Bytes"/> のみ)。</summary>
    public long ByteLength { get; init; }

    /// <summary>例外の場合のスタックトレース。</summary>
    public string? Stack { get; init; }

    /// <summary>表示用の文字列 (複数の表現があるものは並べる)。</summary>
    public string Display => Hex is null ? Text : Text + " (" + Hex + ")";

    public static ReplValue Of(ReplValueKind kind, string type, string text) => new() { Kind = kind, Type = type, Text = text };

    public static readonly ReplValue Undefined = Of(ReplValueKind.Undefined, "undefined", "undefined");

    /// <summary>バイト列を Hex ダンプにする (1 行 16 バイト。<c>00000000  00 01 ...  |....|</c>)。先頭の 256 バイトまで。</summary>
    public static string HexDump(ReadOnlySpan<byte> bytes, long totalLength)
    {
        var sb = new StringBuilder();
        int shown = (int)Math.Min(bytes.Length, MaxDumpBytes);
        for (int i = 0; i < shown; i += 16)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append(i.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            int n = Math.Min(16, shown - i);
            for (int j = 0; j < 16; j++)
            {
                if (j < n)
                {
                    sb.Append(bytes[i + j].ToString("X2", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append("  ");
                }

                sb.Append(j == 7 ? "  " : " ");
            }

            sb.Append(" |");
            for (int j = 0; j < n; j++)
            {
                byte b = bytes[i + j];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            sb.Append('|');
        }

        if (totalLength > shown)
        {
            sb.Append('\n').Append(Msg.T($"… 以降を省略 (全体で {totalLength.ToString("N0", CultureInfo.InvariantCulture)} バイト)", $"… omitted (total {totalLength.ToString("N0", CultureInfo.InvariantCulture)} bytes)"));
        }

        return sb.ToString();
    }

    /// <summary>BigInt の 10 進と 16 進。</summary>
    public static ReplValue FromBigInteger(BigInteger value)
    {
        string hex = (value.Sign < 0 ? "-0x" : "0x") + BigInteger.Abs(value).ToString("X", CultureInfo.InvariantCulture).TrimStart('0').PadLeft(1, '0');
        return new ReplValue
        {
            Kind = ReplValueKind.BigInt,
            Type = "bigint",
            Text = value.ToString(CultureInfo.InvariantCulture) + "n",
            Hex = hex,
        };
    }
}
