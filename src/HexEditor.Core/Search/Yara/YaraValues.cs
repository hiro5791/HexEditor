using System.Text;

namespace HexEditor.Core.Search.Yara;

internal enum YKind : byte
{
    Undefined,
    Integer,
    Boolean,
    Float,
    String,
    Object,
}

/// <summary>条件の評価の値 (整数・真偽・浮動小数点・文字列 (バイト列)・モジュールの構造体など・未定義)。</summary>
internal readonly struct YValue
{
    private YValue(YKind kind, long i = 0, double f = 0, byte[]? s = null, object? o = null)
    {
        Kind = kind;
        I = i;
        F = f;
        S = s;
        O = o;
    }

    public YKind Kind { get; }

    public long I { get; }

    public double F { get; }

    public byte[]? S { get; }

    public object? O { get; }

    public static YValue Undefined => default;

    public static YValue True => new(YKind.Boolean, 1);

    public static YValue False => new(YKind.Boolean, 0);

    public bool IsUndefined => Kind == YKind.Undefined;

    public static YValue Int(long value) => new(YKind.Integer, value);

    public static YValue Bool(bool value) => new(YKind.Boolean, value ? 1 : 0);

    public static YValue Float(double value) => new(YKind.Float, f: value);

    public static YValue Str(byte[] value) => new(YKind.String, s: value);

    public static YValue Str(string value) => new(YKind.String, s: Encoding.UTF8.GetBytes(value));

    public static YValue Obj(object value) => new(YKind.Object, o: value);

    /// <summary>真偽の文脈での値 (未定義は偽)。</summary>
    public bool Truthy => Kind switch
    {
        YKind.Integer or YKind.Boolean => I != 0,
        YKind.Float => F != 0,
        YKind.String => S!.Length > 0,
        YKind.Object => true,
        _ => false,
    };

    public bool IsNumber => Kind is YKind.Integer or YKind.Boolean or YKind.Float;

    public double AsDouble => Kind == YKind.Float ? F : I;

    public string AsText => S is null ? string.Empty : Encoding.Latin1.GetString(S);

    public override string ToString() => Kind switch
    {
        YKind.Integer => I.ToString(System.Globalization.CultureInfo.InvariantCulture),
        YKind.Boolean => I != 0 ? "true" : "false",
        YKind.Float => F.ToString(System.Globalization.CultureInfo.InvariantCulture),
        YKind.String => Encoding.UTF8.GetString(S!),
        YKind.Object => O?.ToString() ?? "object",
        _ => "undefined",
    };
}

/// <summary>モジュールの構造体 (フィールドの名前と値)。値のないフィールドも名前は持つ (コンパイル時の名前の確かめ)。</summary>
internal sealed class YStruct
{
    public Dictionary<string, YValue> Fields { get; } = new(StringComparer.Ordinal);

    public YValue this[string name]
    {
        get => Fields.TryGetValue(name, out YValue v) ? v : YValue.Undefined;
        set => Fields[name] = value;
    }

    public YStruct Set(string name, YValue value)
    {
        Fields[name] = value;
        return this;
    }

    public YStruct Set(string name, long value) => Set(name, YValue.Int(value));

    public YStruct Set(string name, string value) => Set(name, YValue.Str(value));

    public YStruct Set(string name, byte[] value) => Set(name, YValue.Str(value));

    public YStruct Func(string name, Func<YValue[], YaraContext, YValue> body, params int[] arities) =>
        Set(name, YValue.Obj(new YFunction(name, body, arities)));
}

/// <summary>モジュールの配列 (<see cref="Element"/> は要素の形。名前の確かめに使う)。</summary>
internal sealed class YArray(YStruct? element = null)
{
    public List<YValue> Items { get; } = [];

    public YStruct? Element { get; } = element;
}

/// <summary>モジュールの辞書 (文字列のキー)。</summary>
internal sealed class YDictionary(YStruct? element = null)
{
    public Dictionary<string, YValue> Items { get; } = new(StringComparer.Ordinal);

    public YStruct? Element { get; } = element;
}

/// <summary>モジュールの関数 (<see cref="Arities"/> は受け付ける引数の数)。</summary>
internal sealed class YFunction(string name, Func<YValue[], YaraContext, YValue> body, int[] arities)
{
    public string Name { get; } = name;

    public int[] Arities { get; } = arities;

    public YValue Invoke(YValue[] args, YaraContext context) => body(args, context);
}
