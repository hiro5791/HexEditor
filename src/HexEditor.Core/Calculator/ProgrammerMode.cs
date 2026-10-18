using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace HexEditor.Core.Calculator;

/// <summary>プログラマモードの演算のボタン (TOOL-23 の仕様 4)。</summary>
public enum ProgrammerOperation
{
    And,
    Or,
    Xor,
    Not,
    Nand,
    Nor,
    ShiftLeft,
    ShiftRightLogical,
    ShiftRightArithmetic,
    RotateLeft,
    RotateRight,
    Negate,
    ByteSwap,
}

/// <summary>ビットの範囲の選択の結果: ビットフィールドの値と、取り出すマスクとシフトの式 (TOOL-23 の仕様 3)。</summary>
public sealed record BitFieldSelection(int LowBit, int HighBit, ulong Value, string Expression)
{
    public int Width => HighBit - LowBit + 1;
}

/// <summary>
/// プログラマモード (TOOL-23)。値は「ビット幅に収まるビット列」(<c>ulong</c>) で持ち、符号の解釈は表示のときにだけ行う。
/// ビット幅 8 / 16 / 32 / 64 (既定 32)、符号なし / 符号付き (2 の補数。既定 符号なし)。
/// </summary>
public static class ProgrammerMode
{
    public static readonly int[] Widths = [8, 16, 32, 64];

    public const int DefaultWidth = 32;

    /// <summary>ビット幅に収める。</summary>
    public static ulong Mask(ulong bits, int width) => width >= 64 ? bits : bits & ((1UL << width) - 1);

    /// <summary>ビット <paramref name="index"/> (0 が最下位) を反転する。</summary>
    public static ulong ToggleBit(ulong bits, int index, int width) =>
        index < 0 || index >= width ? bits : Mask(bits ^ (1UL << index), width);

    public static bool GetBit(ulong bits, int index) => ((bits >> index) & 1) != 0;

    /// <summary>ビットの表の 1 セルの名前 (スクリーンリーダー用。例: 「ビット 31、値 1」は書式 <c>Bit {0}, value {1}</c> で作る)。</summary>
    public static (int Index, int Value) Cell(ulong bits, int index) => (index, GetBit(bits, index) ? 1 : 0);

    /// <summary>
    /// ビットの範囲 [<paramref name="a"/>, <paramref name="b"/>] (順不同) のビットフィールド。式は <c>(x &gt;&gt; 4) &amp; 0xF</c> の形
    /// (最下位のビットから始まるときは <c>x &amp; 0xF</c>、全ビットなら <c>x</c>)。
    /// </summary>
    public static BitFieldSelection Select(ulong bits, int a, int b, int width)
    {
        int low = Math.Clamp(Math.Min(a, b), 0, width - 1);
        int high = Math.Clamp(Math.Max(a, b), 0, width - 1);
        int count = high - low + 1;
        ulong fieldMask = count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
        ulong value = (bits >> low) & fieldMask;
        string mask = "0x" + fieldMask.ToString("X", CultureInfo.InvariantCulture);
        string expression = count == width
            ? "x"
            : low == 0 ? $"x & {mask}" : $"(x >> {low}) & {mask}";
        return new BitFieldSelection(low, high, value, expression);
    }

    /// <summary>演算を適用する (結果はビット幅で折り返す。<paramref name="operand"/> は AND / OR / XOR / NAND / NOR の右辺)。</summary>
    public static ulong Apply(ProgrammerOperation operation, ulong value, ulong operand, int width, bool signed)
    {
        value = Mask(value, width);
        operand = Mask(operand, width);
        ulong signBit = 1UL << (width - 1);
        ulong result = operation switch
        {
            ProgrammerOperation.And => value & operand,
            ProgrammerOperation.Or => value | operand,
            ProgrammerOperation.Xor => value ^ operand,
            ProgrammerOperation.Not => ~value,
            ProgrammerOperation.Nand => ~(value & operand),
            ProgrammerOperation.Nor => ~(value | operand),
            ProgrammerOperation.ShiftLeft => value << 1,
            ProgrammerOperation.ShiftRightLogical => value >> 1,
            ProgrammerOperation.ShiftRightArithmetic => (value >> 1) | (value & signBit),
            ProgrammerOperation.RotateLeft => (value << 1) | (value >> (width - 1)),
            ProgrammerOperation.RotateRight => (value >> 1) | (value << (width - 1)),
            ProgrammerOperation.Negate => 0UL - value,
            ProgrammerOperation.ByteSwap => ByteSwap(value, width),
            _ => value,
        };
        _ = signed;
        return Mask(result, width);
    }

    /// <summary>バイト順の反転 (ビット幅の範囲で)。</summary>
    public static ulong ByteSwap(ulong value, int width) => width switch
    {
        8 => value & 0xFF,
        16 => BinaryPrimitives.ReverseEndianness((ushort)value),
        32 => BinaryPrimitives.ReverseEndianness((uint)value),
        _ => BinaryPrimitives.ReverseEndianness(value),
    };

    /// <summary>「カーソル位置から読む」(仕様 5): ビット幅ぶんのバイトを、エンディアンを指定して読んだビット列。足りなければ null。</summary>
    public static ulong? FromBytes(ReadOnlySpan<byte> bytes, int width, bool bigEndian)
    {
        int size = width / 8;
        if (bytes.Length < size)
        {
            return null;
        }

        ulong value = 0;
        for (int i = 0; i < size; i++)
        {
            byte b = bigEndian ? bytes[i] : bytes[size - 1 - i];
            value = (value << 8) | b;
        }

        return value;
    }

    /// <summary>ビット列を、ビット幅ぶんのバイト列にする (書き込み用)。</summary>
    public static byte[] ToBytes(ulong bits, int width, bool bigEndian)
    {
        int size = width / 8;
        byte[] bytes = new byte[size];
        for (int i = 0; i < size; i++)
        {
            bytes[bigEndian ? size - 1 - i : i] = (byte)(bits >> (8 * i));
        }

        return bytes;
    }

    /// <summary>ビット幅と符号の条件 (評価器に渡す)。</summary>
    public static CalculatorOptions Options(int width, bool signed, CalcValue answer = default) =>
        new() { BitWidth = width, Signed = signed, Answer = answer };

    /// <summary>ビット列を、ビット幅と符号に従った値にする。</summary>
    public static CalcValue ValueOf(ulong bits, int width, bool signed) =>
        CalcValue.FromInteger(signed ? CalculatorFormat.SignedValue(Mask(bits, width), width) : Mask(bits, width));

    /// <summary>上位から 4 ビットごとの区切りの数 (表の表示用)。</summary>
    public static int Nibbles(int width) => width / 4;

    /// <summary>立っているビットの数。</summary>
    public static int PopCount(ulong bits) => BitOperations.PopCount(bits);
}
