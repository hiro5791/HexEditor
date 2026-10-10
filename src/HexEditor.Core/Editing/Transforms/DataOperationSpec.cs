using System.Globalization;
using System.Numerics;

namespace HexEditor.Core.Editing.Transforms;

/// <summary>データ演算の分類 (EDIT-31 の「画面」のドロップダウン)。</summary>
public enum DataOperationCategory
{
    Arithmetic,
    Bitwise,
    ShiftRotate,
    Reorder,
    Clamp,
    BitInsertDelete,
}

/// <summary>データ演算の種類 (EDIT-32〜EDIT-37)。</summary>
public enum DataOperationKind
{
    // ---- 算術 (EDIT-32) ----
    Add,
    Subtract,
    ReverseSubtract,
    Multiply,
    Divide,
    ReverseDivide,
    Modulo,
    Negate,
    Abs,

    // ---- ビット演算 (EDIT-33) ----
    And,
    Or,
    Xor,
    Not,
    Nand,
    Nor,
    Xnor,

    // ---- シフト・ローテート (EDIT-34) ----
    ShiftLeft,
    ShiftRightLogical,
    ShiftRightArithmetic,
    RotateLeft,
    RotateRight,

    // ---- 並べ替え (EDIT-35) ----
    ByteSwap16,
    ByteSwap32,
    ByteSwap64,
    WordSwap32,
    ReverseRange,
    ReverseBits,
    SwapNibbles,

    // ---- 制限 (EDIT-36) ----
    Clamp,

    // ---- ビットの挿入・削除 (EDIT-37) ----
    InsertBits,
    DeleteBits,
}

/// <summary>要素の型 (EDIT-31 の仕様 2)。</summary>
public enum ElementType
{
    Unsigned,

    /// <summary>符号付き整数 (2 の補数)。</summary>
    Signed,

    /// <summary>浮動小数点 (IEEE 754。算術演算と制限だけで使える)。</summary>
    Float,
}

/// <summary>あふれたときの扱い (EDIT-32 の仕様 2。整数のみ)。</summary>
public enum OverflowMode
{
    Wrap,
    Saturate,
}

/// <summary>ビット演算のオペランドの指定方法 (EDIT-33 の仕様 2)。</summary>
public enum OperandSource
{
    Number,

    /// <summary>バイト列の鍵 (要素は 1 バイトに固定し、鍵を繰り返す)。</summary>
    KeyBytes,
}

/// <summary>マルチ選択・矩形選択でのオペランドの増分 (EDIT-31 の仕様 9)。</summary>
public enum IncrementScope
{
    /// <summary>範囲ごとにやり直す (既定)。</summary>
    PerRange,

    /// <summary>範囲をまたいで続ける。</summary>
    Continuous,
}

/// <summary>ビットの挿入・削除でずらす範囲 (EDIT-37 の仕様 2)。</summary>
public enum BitShiftScope
{
    /// <summary>選択範囲の中だけ (既定。長さは変わらない)。</summary>
    SelectionOnly,

    /// <summary>ドキュメントの末尾まで (長さが変わる)。</summary>
    ToEnd,
}

/// <summary>演算の設定の誤り (入力欄のエラー。EDIT-31〜37 の「エラー」)。</summary>
public enum DataOperationError
{
    None,

    /// <summary>オペランドが要素の型と大きさで表せない。</summary>
    OperandOutOfRange,

    /// <summary>0 では割れません。</summary>
    DivideByZero,

    /// <summary>増分によって途中で除数が 0 になる。</summary>
    DivisorBecomesZero,

    /// <summary>この型では使えない演算 (浮動小数点のビット演算、符号なしの絶対値、浮動小数点の剰余など)。</summary>
    TypeNotSupported,

    /// <summary>要素の大きさが型に合わない (浮動小数点は 4 / 8 バイト)。</summary>
    InvalidSize,

    /// <summary>ビット数が範囲外。</summary>
    BitCountOutOfRange,

    /// <summary>最小値 &gt; 最大値。</summary>
    MinGreaterThanMax,

    /// <summary>鍵が空。</summary>
    KeyEmpty,

    /// <summary>鍵は 16 MiB までです。</summary>
    KeyTooLong,

    /// <summary>処理と飛ばしの数が範囲外 (処理は 1 以上、飛ばしは 0 以上)。</summary>
    InvalidStride,

    /// <summary>位置が範囲外 (ビットの挿入・削除)。</summary>
    PositionOutOfRange,

    /// <summary>固定長ドキュメントでは「ドキュメントの末尾まで」を使えない。</summary>
    FixedLength,
}

/// <summary>
/// データ演算の設定 (EDIT-31〜EDIT-37)。ダイアログの設定をそのまま表す。整数のオペランドは要素のビットパターンとして持つ
/// (<see cref="Operand"/>。符号付きは 2 の補数)。浮動小数点は <see cref="FloatOperand"/>。
/// </summary>
public sealed record DataOperationSpec
{
    public const int MaxKeyLength = 16 * 1024 * 1024;

    public DataOperationKind Kind { get; init; } = DataOperationKind.Add;

    public ElementType Type { get; init; } = ElementType.Unsigned;

    /// <summary>要素の大きさ (整数は 1 / 2 / 4 / 8、浮動小数点は 4 / 8)。並べ替えでは演算が決める大きさを使う (EDIT-35 の仕様 2)。</summary>
    public int Size { get; init; } = 1;

    public bool BigEndian { get; init; }

    /// <summary>整数のオペランド (要素の値。符号付きは負の値も可)。</summary>
    public Int128 Operand { get; init; }

    /// <summary>整数のオペランドの増分 (EDIT-31 の仕様 6)。</summary>
    public Int128 Increment { get; init; }

    public double FloatOperand { get; init; }

    public double FloatIncrement { get; init; }

    /// <summary>処理する要素の数 N (EDIT-31 の仕様 7)。</summary>
    public int ProcessCount { get; init; } = 1;

    /// <summary>飛ばす要素の数 M。</summary>
    public int SkipCount { get; init; }

    public IncrementScope IncrementScope { get; init; } = IncrementScope.PerRange;

    public OverflowMode Overflow { get; init; } = OverflowMode.Wrap;

    // ---- ビット演算の鍵 (EDIT-33) ----

    public OperandSource OperandSource { get; init; } = OperandSource.Number;

    public byte[]? Key { get; init; }

    public PatternOrigin KeyOrigin { get; init; } = PatternOrigin.RangeStart;

    /// <summary>鍵を 1 周するごとに鍵の各バイトに足す値 (EDIT-33 の仕様 4)。</summary>
    public int KeyIncrement { get; init; }

    // ---- シフト・ローテート (EDIT-34) ----

    public int ShiftBits { get; init; } = 1;

    /// <summary>回転量の増分 (EDIT-34 の仕様 3)。</summary>
    public int RotateIncrement { get; init; }

    // ---- 制限 (EDIT-36) ----

    public Int128? Min { get; init; }

    public Int128? Max { get; init; }

    public double? FloatMin { get; init; }

    public double? FloatMax { get; init; }

    // ---- ビットの挿入・削除 (EDIT-37) ----

    /// <summary>位置のバイトのオフセット。</summary>
    public long BitOffset { get; init; }

    /// <summary>位置のバイト内のビット (0〜7。7 が最上位)。</summary>
    public int BitIndex { get; init; } = 7;

    /// <summary>挿入・削除するビット数 (1〜2^31)。</summary>
    public long BitCount { get; init; } = 1;

    /// <summary>挿入するビットの値。</summary>
    public bool FillWithOne { get; init; }

    /// <summary>ビットの並び順が最下位ビットから。</summary>
    public bool LsbFirst { get; init; }

    public BitShiftScope BitScope { get; init; } = BitShiftScope.SelectionOnly;

    /// <summary>演算の分類。</summary>
    public DataOperationCategory Category => CategoryOf(Kind);

    public static DataOperationCategory CategoryOf(DataOperationKind kind) => kind switch
    {
        <= DataOperationKind.Abs => DataOperationCategory.Arithmetic,
        <= DataOperationKind.Xnor => DataOperationCategory.Bitwise,
        <= DataOperationKind.RotateRight => DataOperationCategory.ShiftRotate,
        <= DataOperationKind.SwapNibbles => DataOperationCategory.Reorder,
        DataOperationKind.Clamp => DataOperationCategory.Clamp,
        _ => DataOperationCategory.BitInsertDelete,
    };

    /// <summary>オペランドを使う演算か。</summary>
    public bool UsesOperand => Kind is not (DataOperationKind.Negate or DataOperationKind.Abs or DataOperationKind.Not)
        && Category is DataOperationCategory.Arithmetic or DataOperationCategory.Bitwise;

    /// <summary>バイト列の鍵を使うか。</summary>
    public bool UsesKey => Category == DataOperationCategory.Bitwise && UsesOperand && OperandSource == OperandSource.KeyBytes;

    /// <summary>実際に使う要素の大きさ (並べ替えと鍵では演算が決める)。</summary>
    public int EffectiveSize => Kind switch
    {
        DataOperationKind.ByteSwap16 => 2,
        DataOperationKind.ByteSwap32 or DataOperationKind.WordSwap32 => 4,
        DataOperationKind.ByteSwap64 => 8,
        DataOperationKind.ReverseRange or DataOperationKind.ReverseBits or DataOperationKind.SwapNibbles => 1,
        DataOperationKind.InsertBits or DataOperationKind.DeleteBits => 1,
        _ when UsesKey => 1,
        _ => Size,
    };

    /// <summary>要素の型として使う型 (浮動小数点を使えない演算では整数として扱う)。</summary>
    public bool IsFloat => Type == ElementType.Float && Category is DataOperationCategory.Arithmetic or DataOperationCategory.Clamp;

    /// <summary>要素単位の演算か (範囲全体の反転、ビットの挿入・削除は違う)。</summary>
    public bool IsElementWise => Kind is not (DataOperationKind.ReverseRange or DataOperationKind.InsertBits or DataOperationKind.DeleteBits);

    /// <summary>長さを変えうるか (ビットの挿入・削除の「ドキュメントの末尾まで」)。</summary>
    public bool ChangesLength => Category == DataOperationCategory.BitInsertDelete && BitScope == BitShiftScope.ToEnd;

    /// <summary>
    /// 設定の誤り (入力欄のエラー)。<paramref name="processedElements"/> は 1 つの範囲で処理する要素の最大数 (増分で除数が 0 に
    /// なるかの判定に使う。範囲をまたいで続ける場合は全体の数)。
    /// </summary>
    public DataOperationError Validate(long processedElements = long.MaxValue)
    {
        if (ProcessCount < 1 || SkipCount < 0)
        {
            return DataOperationError.InvalidStride;
        }

        switch (Category)
        {
            case DataOperationCategory.Reorder:
                return DataOperationError.None;
            case DataOperationCategory.BitInsertDelete:
                return BitCount is < 1 or > 1L << 31 ? DataOperationError.BitCountOutOfRange
                    : BitIndex is < 0 or > 7 || BitOffset < 0 ? DataOperationError.PositionOutOfRange
                    : DataOperationError.None;
        }

        if (Type == ElementType.Float)
        {
            if (Size is not (4 or 8))
            {
                return DataOperationError.InvalidSize;
            }

            if (Category is not (DataOperationCategory.Arithmetic or DataOperationCategory.Clamp) || Kind == DataOperationKind.Modulo)
            {
                return DataOperationError.TypeNotSupported;
            }

            return Kind == DataOperationKind.Clamp && FloatMin is double fmin && FloatMax is double fmax && fmin > fmax
                ? DataOperationError.MinGreaterThanMax
                : DataOperationError.None;
        }

        if (!UsesKey && Size is not (1 or 2 or 4 or 8))
        {
            return DataOperationError.InvalidSize;
        }

        if (Kind == DataOperationKind.Abs && Type == ElementType.Unsigned)
        {
            return DataOperationError.TypeNotSupported;
        }

        int bits = EffectiveSize * 8;
        switch (Category)
        {
            case DataOperationCategory.ShiftRotate:
                if (Kind is DataOperationKind.RotateLeft or DataOperationKind.RotateRight)
                {
                    return ShiftBits >= 1 && ShiftBits <= bits - 1 ? DataOperationError.None : DataOperationError.BitCountOutOfRange;
                }

                return ShiftBits >= 1 ? DataOperationError.None : DataOperationError.BitCountOutOfRange;
            case DataOperationCategory.Clamp:
                if (Min is { } min && !Fits(min, bits) || Max is { } max && !Fits(max, bits))
                {
                    return DataOperationError.OperandOutOfRange;
                }

                return Min is { } a && Max is { } b && a > b ? DataOperationError.MinGreaterThanMax : DataOperationError.None;
        }

        if (UsesKey)
        {
            return Key is not { Length: > 0 } ? DataOperationError.KeyEmpty
                : Key.Length > MaxKeyLength ? DataOperationError.KeyTooLong
                : DataOperationError.None;
        }

        if (!UsesOperand)
        {
            return DataOperationError.None;
        }

        if (!Fits(Operand, bits) || !FitsIncrement(Increment, bits))
        {
            return DataOperationError.OperandOutOfRange;
        }

        if (Kind is DataOperationKind.Divide or DataOperationKind.Modulo)
        {
            UInt128 mask = DataOperationMath.Mask(bits);
            UInt128 op = (UInt128)Operand & mask;
            if (op == 0)
            {
                return DataOperationError.DivideByZero;
            }

            if (DataOperationMath.FirstZeroIndex(op, (UInt128)Increment & mask, bits) is { } zero && zero < processedElements)
            {
                return DataOperationError.DivisorBecomesZero;
            }
        }

        return DataOperationError.None;
    }

    /// <summary>値が要素の型と大きさで表せるか (EDIT-31 の仕様 5)。</summary>
    public bool Fits(Int128 value, int bits)
    {
        if (Type == ElementType.Signed)
        {
            Int128 min = -((Int128)1 << (bits - 1));
            Int128 max = ((Int128)1 << (bits - 1)) - 1;
            return value >= min && value <= max;
        }

        return value >= 0 && value <= (Int128)DataOperationMath.Mask(bits);
    }

    /// <summary>増分は符号付きの値も受け付ける (要素の大きさでラップする)。</summary>
    private static bool FitsIncrement(Int128 value, int bits) =>
        value >= -((Int128)1 << bits) && value <= (Int128)DataOperationMath.Mask(bits);

    /// <summary>値が変わらないことが設定から分かる演算か (EDIT-31 の「巨大ファイル」5。実行せずに知らせる)。</summary>
    public bool IsIdentity()
    {
        bool constant = IsFloat ? FloatIncrement == 0 : Increment == 0;
        int bits = EffectiveSize * 8;
        UInt128 op = (UInt128)Operand & DataOperationMath.Mask(Math.Min(bits, 64));
        switch (Kind)
        {
            case DataOperationKind.Add or DataOperationKind.Subtract:
                return constant && (IsFloat ? FloatOperand == 0 && !double.IsNaN(FloatOperand) : op == 0);
            case DataOperationKind.Multiply or DataOperationKind.Divide:
                return constant && (IsFloat ? FloatOperand == 1 : Operand == 1);
            case DataOperationKind.Xor or DataOperationKind.Or:
                return UsesKey ? Key is { } k && KeyIncrement % 256 == 0 && k.All(b => b == 0) : constant && op == 0;
            case DataOperationKind.And:
                return UsesKey ? Key is { } k2 && KeyIncrement % 256 == 0 && k2.All(b => b == 0xFF) : constant && op == DataOperationMath.Mask(bits);
            case DataOperationKind.RotateLeft or DataOperationKind.RotateRight:
                return ShiftBits % bits == 0 && RotateIncrement % bits == 0;
            case DataOperationKind.Clamp:
                return IsFloat ? FloatMin is null && FloatMax is null : Min is null && Max is null;
            default:
                return false;
        }
    }

    /// <summary>浮動小数点のオペランドの入力 (小数点は <c>.</c> のみ、指数表記可。EDIT-31 の仕様 5)。</summary>
    public static bool TryParseFloat(string text, out double value)
    {
        text = text.Trim();
        value = 0;
        if (text.Length == 0 || text.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        return double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }
}

/// <summary>演算の計算の補助。</summary>
public static class DataOperationMath
{
    /// <summary>下位 <paramref name="bits"/> ビットのマスク (bits ≤ 64)。</summary>
    public static UInt128 Mask(int bits) => bits >= 128 ? UInt128.MaxValue : ((UInt128)1 << bits) - 1;

    /// <summary>
    /// オペランド <c>op + i × inc (mod 2^bits)</c> が最初に 0 になる i。ならなければ null (EDIT-31 の「エラー」の「増分によって途中で 0 になる」)。
    /// </summary>
    public static long? FirstZeroIndex(UInt128 op, UInt128 inc, int bits)
    {
        var modulus = BigInteger.One << bits;
        var a = new BigInteger((ulong)(inc & ulong.MaxValue)) + (new BigInteger((ulong)(inc >> 64)) << 64);
        var b = modulus - (new BigInteger((ulong)(op & ulong.MaxValue)) + (new BigInteger((ulong)(op >> 64)) << 64));
        a %= modulus;
        b %= modulus;
        if (b == 0)
        {
            return 0;
        }

        if (a == 0)
        {
            return null;
        }

        // a·i ≡ b (mod m) を解く。g = gcd(a, m) が b を割り切るときだけ解がある。
        BigInteger g = BigInteger.GreatestCommonDivisor(a, modulus);
        if (b % g != 0)
        {
            return null;
        }

        BigInteger m = modulus / g;
        BigInteger inverse = ModInverse(a / g % m, m);
        BigInteger i = b / g * inverse % m;
        return i > long.MaxValue ? null : (long)i;
    }

    private static BigInteger ModInverse(BigInteger a, BigInteger m)
    {
        // 拡張ユークリッドの互除法 (a と m は互いに素)。
        BigInteger t = 0, newT = 1, r = m, newR = a % m;
        while (newR != 0)
        {
            BigInteger q = r / newR;
            (t, newT) = (newT, t - q * newT);
            (r, newR) = (newR, r - q * newR);
        }

        return ((t % m) + m) % m;
    }
}
