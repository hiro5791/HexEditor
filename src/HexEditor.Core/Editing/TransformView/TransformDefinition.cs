using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.Core.Engine;

namespace HexEditor.Core.Editing.TransformView;

/// <summary>変換ビューの演算 (EDIT-45 の仕様 1)。<see cref="Plugin"/> はプラグインの変換 (<c>ITransformView</c>。08 の AUTO-31)。</summary>
public enum TransformOpKind
{
    Xor,
    Add,
    Subtract,
    Not,
    RotateLeft,
    RotateRight,
    ByteSwap16,
    ByteSwap32,
    ByteSwap64,
    ReverseBits,
    SwapNibbles,
    Plugin,
}

/// <summary>鍵の起点 (EDIT-45 の仕様 3)。</summary>
public enum TransformKeyOrigin
{
    /// <summary>オフセット 0 から数える (既定)。</summary>
    DocumentStart,

    /// <summary>適用範囲の先頭から。</summary>
    RangeStart,

    /// <summary>オフセットを指定 (<see cref="TransformDefinition.KeyOriginOffset"/>)。</summary>
    Offset,
}

/// <summary>定義の誤り (ダイアログの入力欄のエラー。EDIT-45 の「エラー」)。</summary>
public enum TransformError
{
    None,

    /// <summary>演算がない、または 8 個を超える。</summary>
    OpCount,

    /// <summary>鍵が空。</summary>
    KeyEmpty,

    /// <summary>鍵が 256 バイトを超える。</summary>
    KeyTooLong,

    /// <summary>要素の大きさが 1 / 2 / 4 / 8 でない。</summary>
    InvalidSize,

    /// <summary>ビット数が 1〜要素のビット数 − 1 にない。</summary>
    BitCountOutOfRange,

    /// <summary>適用範囲の誤り。</summary>
    InvalidRange,

    /// <summary>プラグインの変換が見つからない。</summary>
    PluginMissing,
}

/// <summary>
/// 変換ビューの演算 1 つ (EDIT-45 の仕様 1)。要素の大きさ・エンディアン・オペランドの増分は EDIT-31 と同じ意味。
/// XOR は <see cref="Key"/> があればバイト列の鍵 (要素は 1 バイト。鍵の起点から数えて繰り返す。仕様 2)、なければ数値の <see cref="Operand"/>。
/// </summary>
public sealed record TransformOp
{
    public TransformOpKind Kind { get; init; } = TransformOpKind.Xor;

    /// <summary>要素の大きさ (1 / 2 / 4 / 8)。バイトスワップでは演算が決める。</summary>
    public int Size { get; init; } = 1;

    public bool BigEndian { get; init; }

    /// <summary>数値のオペランド (要素のビットパターン)。</summary>
    public ulong Operand { get; init; }

    /// <summary>1 要素ごとにオペランドに足す値 (要素の大きさでラップ。EDIT-31 の仕様 6)。</summary>
    public ulong Increment { get; init; }

    /// <summary>XOR のバイト列の鍵 (1〜256 バイト)。</summary>
    public byte[]? Key { get; init; }

    /// <summary>ローテートのビット数。</summary>
    public int Bits { get; init; } = 1;

    /// <summary>プラグインの変換の拡張ポイントの ID。</summary>
    public string? PluginId { get; init; }

    /// <summary>実際に使う要素の大きさ。</summary>
    public int EffectiveSize => Kind switch
    {
        TransformOpKind.ByteSwap16 => 2,
        TransformOpKind.ByteSwap32 => 4,
        TransformOpKind.ByteSwap64 => 8,
        TransformOpKind.ReverseBits or TransformOpKind.SwapNibbles or TransformOpKind.Plugin => 1,
        TransformOpKind.Xor when Key is not null => 1,
        _ => Size,
    };

    /// <summary>XOR の 1 つだけの鍵 (「XOR して表示…」の結果)。</summary>
    public static TransformOp XorKey(byte[] key) => new() { Kind = TransformOpKind.Xor, Key = key };

    public TransformError Validate()
    {
        if (Kind == TransformOpKind.Xor && Key is not null)
        {
            return Key.Length == 0 ? TransformError.KeyEmpty : Key.Length > TransformDefinition.MaxKeyLength ? TransformError.KeyTooLong : TransformError.None;
        }

        if (EffectiveSize is not (1 or 2 or 4 or 8))
        {
            return TransformError.InvalidSize;
        }

        if (Kind is TransformOpKind.RotateLeft or TransformOpKind.RotateRight && (Bits < 1 || Bits > EffectiveSize * 8 - 1))
        {
            return TransformError.BitCountOutOfRange;
        }

        return Kind == TransformOpKind.Plugin && string.IsNullOrEmpty(PluginId) ? TransformError.PluginMissing : TransformError.None;
    }

    /// <summary>短い説明 (例: <c>XOR 5A</c>、<c>ADD 0x10 (16 bit)</c>。仕様 9 のバッジ)。</summary>
    public string Label(Func<string, string?>? pluginName = null)
    {
        string bits = EffectiveSize > 1 && Kind is not (TransformOpKind.ByteSwap16 or TransformOpKind.ByteSwap32 or TransformOpKind.ByteSwap64)
            ? $" ({EffectiveSize * 8} bit{(BigEndian ? " BE" : string.Empty)})" : string.Empty;
        string operand = Increment != 0 ? $"0x{Operand:X} +0x{Increment:X}" : $"0x{Operand:X}";
        return Kind switch
        {
            TransformOpKind.Xor when Key is not null => "XOR " + Convert.ToHexString(Key).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b),
            TransformOpKind.Xor => "XOR " + operand + bits,
            TransformOpKind.Add => "ADD " + operand + bits,
            TransformOpKind.Subtract => "SUB " + operand + bits,
            TransformOpKind.Not => "NOT" + bits,
            TransformOpKind.RotateLeft => $"ROL {Bits}{bits}",
            TransformOpKind.RotateRight => $"ROR {Bits}{bits}",
            TransformOpKind.ByteSwap16 => "SWAP16",
            TransformOpKind.ByteSwap32 => "SWAP32",
            TransformOpKind.ByteSwap64 => "SWAP64",
            TransformOpKind.ReverseBits => "BITREV",
            TransformOpKind.SwapNibbles => "NIBBLE",
            _ => pluginName?.Invoke(PluginId ?? string.Empty) ?? PluginId ?? "?",
        };
    }

    // ---- 保存 (仕様 14) ----

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["op"] = Kind.ToString() };
        if (Size != 1)
        {
            o["size"] = Size;
        }

        if (BigEndian)
        {
            o["bigEndian"] = true;
        }

        if (Operand != 0)
        {
            o["operand"] = Operand.ToString(CultureInfo.InvariantCulture);
        }

        if (Increment != 0)
        {
            o["increment"] = Increment.ToString(CultureInfo.InvariantCulture);
        }

        if (Key is not null)
        {
            o["key"] = Convert.ToHexString(Key);
        }

        if (Kind is TransformOpKind.RotateLeft or TransformOpKind.RotateRight)
        {
            o["bits"] = Bits;
        }

        if (PluginId is not null)
        {
            o["plugin"] = PluginId;
        }

        return o;
    }

    public static TransformOp FromJson(JsonObject o)
    {
        if (!Enum.TryParse((string?)o["op"], out TransformOpKind kind))
        {
            throw new FormatException("op");
        }

        return new TransformOp
        {
            Kind = kind,
            Size = (int?)o["size"] ?? 1,
            BigEndian = (bool?)o["bigEndian"] ?? false,
            Operand = ulong.Parse((string?)o["operand"] ?? "0", CultureInfo.InvariantCulture),
            Increment = ulong.Parse((string?)o["increment"] ?? "0", CultureInfo.InvariantCulture),
            Key = (string?)o["key"] is { } k ? Convert.FromHexString(k) : null,
            Bits = (int?)o["bits"] ?? 1,
            PluginId = (string?)o["plugin"],
        };
    }
}

/// <summary>
/// 変換ビューの定義 (EDIT-45)。演算を最大 8 個まで順につなげる (仕様 1)。適用範囲は全体 (既定) か指定した範囲 (仕様 3)。
/// 名前を付けて保存し、ほかのドキュメントで再利用できる (仕様 14)。
/// </summary>
public sealed record TransformDefinition
{
    public const int MaxOps = 8;

    public const int MaxKeyLength = 256;

    public IReadOnlyList<TransformOp> Ops { get; init; } = [];

    /// <summary>適用範囲の先頭 (null は全体)。</summary>
    public long? RangeStart { get; init; }

    /// <summary>適用範囲の長さ (<see cref="RangeStart"/> とともに指定)。</summary>
    public long? RangeLength { get; init; }

    public TransformKeyOrigin KeyOrigin { get; init; } = TransformKeyOrigin.DocumentStart;

    /// <summary>鍵の起点のオフセット (<see cref="TransformKeyOrigin.Offset"/> のとき)。</summary>
    public long KeyOriginOffset { get; init; }

    /// <summary>保存した名前 (仕様 14)。</summary>
    public string? Name { get; init; }

    /// <summary>XOR 1 つだけの変換 (「XOR して表示…」。仕様 4)。</summary>
    public static TransformDefinition Xor(byte[] key, TransformKeyOrigin origin = TransformKeyOrigin.DocumentStart, long originOffset = 0) =>
        new() { Ops = [TransformOp.XorKey(key)], KeyOrigin = origin, KeyOriginOffset = originOffset };

    /// <summary>XOR の鍵 1 つだけの変換なら、その鍵 (「XOR して表示…」のフライアウトに戻す・「この変換をデータに適用…」)。</summary>
    public byte[]? SingleXorKey => Ops is [{ Kind: TransformOpKind.Xor, Key: { } key }] ? key : null;

    public TransformError Validate()
    {
        if (Ops.Count is 0 or > MaxOps)
        {
            return TransformError.OpCount;
        }

        if (RangeStart is { } s && (s < 0 || RangeLength is not { } l || l <= 0) || RangeStart is null && RangeLength is not null)
        {
            return TransformError.InvalidRange;
        }

        foreach (TransformOp op in Ops)
        {
            if (op.Validate() is not TransformError.None and var e)
            {
                return e;
            }
        }

        return TransformError.None;
    }

    /// <summary>短い説明 (演算を「 → 」でつなぐ)。</summary>
    public string Label(Func<string, string?>? pluginName = null) => string.Join(" → ", Ops.Select(o => o.Label(pluginName)));

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["ops"] = new JsonArray([.. Ops.Select(op => (JsonNode)op.ToJson())]) };
        if (RangeStart is { } s)
        {
            o["rangeStart"] = s;
            o["rangeLength"] = RangeLength;
        }

        if (KeyOrigin != TransformKeyOrigin.DocumentStart)
        {
            o["keyOrigin"] = KeyOrigin.ToString();
        }

        if (KeyOrigin == TransformKeyOrigin.Offset)
        {
            o["keyOriginOffset"] = KeyOriginOffset;
        }

        if (Name is not null)
        {
            o["name"] = Name;
        }

        return o;
    }

    /// <summary>JSON から読む。形式が違えば null。</summary>
    public static TransformDefinition? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o || o["ops"] is not JsonArray ops)
        {
            return null;
        }

        try
        {
            var definition = new TransformDefinition
            {
                Ops = [.. ops.OfType<JsonObject>().Select(TransformOp.FromJson)],
                RangeStart = (long?)o["rangeStart"],
                RangeLength = (long?)o["rangeLength"],
                KeyOrigin = Enum.TryParse((string?)o["keyOrigin"], out TransformKeyOrigin k) ? k : TransformKeyOrigin.DocumentStart,
                KeyOriginOffset = (long?)o["keyOriginOffset"] ?? 0,
                Name = (string?)o["name"],
            };
            return definition.Validate() == TransformError.None ? definition : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// 変換を作る。<paramref name="plugins"/> はプラグインの変換を ID から引く (見つからなければ null。<see cref="TransformError.PluginMissing"/>)。
    /// </summary>
    public ViewTransformChain Create(Func<string, IPluginTransform?>? plugins = null, Func<string, string?>? pluginName = null)
    {
        if (Validate() is not TransformError.None and var e)
        {
            throw new ArgumentException(e.ToString(), nameof(plugins));
        }

        var resolved = new IPluginTransform?[Ops.Count];
        for (int i = 0; i < Ops.Count; i++)
        {
            if (Ops[i].Kind == TransformOpKind.Plugin)
            {
                resolved[i] = plugins?.Invoke(Ops[i].PluginId!) ?? throw new ArgumentException(TransformError.PluginMissing.ToString(), nameof(plugins));
            }
        }

        return new ViewTransformChain(this, resolved, Label(pluginName));
    }
}

/// <summary>プラグインの変換 (08 の <c>ITransformView</c> をこの形に包む。Core はプラグインの SDK に依存しない)。</summary>
public interface IPluginTransform
{
    bool CanInverse { get; }

    void Forward(long offset, Span<byte> data);

    void Inverse(long offset, Span<byte> data);
}

/// <summary>
/// 変換ビューの変換 (<see cref="IViewTransform"/>)。演算を順にかけ、逆変換は逆の順にそれぞれの逆演算をかける (EDIT-45 の仕様 6)。
/// 要素は適用範囲の先頭から数え、範囲の外のバイトと最後の端数のバイト (要素に満たない部分) は変えない。
/// </summary>
public sealed class ViewTransformChain : IViewTransform
{
    private readonly IPluginTransform?[] _plugins;
    private readonly long _rangeStart;
    private readonly long _rangeEnd;
    private readonly long _keyOrigin;

    internal ViewTransformChain(TransformDefinition definition, IPluginTransform?[] plugins, string label)
    {
        Definition = definition;
        _plugins = plugins;
        Label = label;
        _rangeStart = definition.RangeStart ?? 0;
        _rangeEnd = definition.RangeStart is { } s ? s + definition.RangeLength!.Value : long.MaxValue;
        _keyOrigin = definition.KeyOrigin switch
        {
            TransformKeyOrigin.RangeStart => _rangeStart,
            TransformKeyOrigin.Offset => definition.KeyOriginOffset,
            _ => 0,
        };
        Alignment = definition.Ops.Max(o => o.EffectiveSize);
        CanInverse = plugins.All(p => p is null || p.CanInverse);
    }

    public TransformDefinition Definition { get; }

    public string Label { get; }

    public bool CanInverse { get; }

    public int Alignment { get; }

    public long AlignmentOrigin => _rangeStart;

    public void Forward(long offset, Span<byte> data)
    {
        for (int i = 0; i < Definition.Ops.Count; i++)
        {
            ApplyOp(i, offset, data, inverse: false);
        }
    }

    public void Inverse(long offset, Span<byte> data)
    {
        if (!CanInverse)
        {
            throw new InvalidOperationException("この変換ビューは逆変換を持ちません。");
        }

        for (int i = Definition.Ops.Count - 1; i >= 0; i--)
        {
            ApplyOp(i, offset, data, inverse: true);
        }
    }

    private static long Mod(long v, long m) => ((v % m) + m) % m;

    private void ApplyOp(int index, long offset, Span<byte> data, bool inverse)
    {
        // 範囲の内側の部分だけ。
        long from = Math.Max(offset, _rangeStart), to = Math.Min(offset + data.Length, _rangeEnd);
        if (to <= from)
        {
            return;
        }

        TransformOp op = Definition.Ops[index];
        if (op.Kind == TransformOpKind.Plugin)
        {
            Span<byte> part = data.Slice((int)(from - offset), (int)(to - from));
            if (inverse)
            {
                _plugins[index]!.Inverse(from, part);
            }
            else
            {
                _plugins[index]!.Forward(from, part);
            }

            return;
        }

        if (op.Kind == TransformOpKind.Xor && op.Key is { } key)
        {
            // data[o] XOR key[(o − 鍵の起点) mod 鍵の長さ] (仕様 2。起点より前も 0 以上の mod)。
            int k = (int)Mod(from - _keyOrigin, key.Length);
            for (long p = from; p < to; p++)
            {
                data[(int)(p - offset)] ^= key[k];
                if (++k == key.Length)
                {
                    k = 0;
                }
            }

            return;
        }

        int size = op.EffectiveSize;
        int bits = size * 8;
        ulong mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;

        // 範囲の先頭から数えた要素で、data に全体が入っているものだけ。
        long first = from + Mod(_rangeStart - from, size);
        for (long e = first; e + size <= to; e += size)
        {
            Span<byte> element = data.Slice((int)(e - offset), size);
            long elementIndex = (e - _rangeStart) / size;
            switch (op.Kind)
            {
                case TransformOpKind.ByteSwap16 or TransformOpKind.ByteSwap32 or TransformOpKind.ByteSwap64:
                    element.Reverse();
                    continue;
                case TransformOpKind.ReverseBits:
                    element[0] = ReverseBits(element[0]);
                    continue;
                case TransformOpKind.SwapNibbles:
                    element[0] = (byte)(element[0] << 4 | element[0] >> 4);
                    continue;
            }

            ulong value = Read(element, op.BigEndian);
            ulong operand = (op.Operand + (ulong)elementIndex * op.Increment) & mask;
            value = op.Kind switch
            {
                TransformOpKind.Xor => value ^ operand,
                TransformOpKind.Not => ~value,
                TransformOpKind.Add => inverse ? value - operand : value + operand,
                TransformOpKind.Subtract => inverse ? value + operand : value - operand,
                TransformOpKind.RotateLeft => Rotate(value, inverse ? bits - op.Bits : op.Bits, bits),
                TransformOpKind.RotateRight => Rotate(value, inverse ? op.Bits : bits - op.Bits, bits),
                _ => value,
            };
            Write(element, value & mask, op.BigEndian);
        }
    }

    /// <summary>左に <paramref name="n"/> ビットのローテート (要素の大きさで)。</summary>
    private static ulong Rotate(ulong value, int n, int bits)
    {
        n %= bits;
        if (n == 0)
        {
            return value;
        }

        ulong mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        value &= mask;
        return (value << n | value >> (bits - n)) & mask;
    }

    private static byte ReverseBits(byte b)
    {
        uint v = b;
        v = (v & 0xF0) >> 4 | (v & 0x0F) << 4;
        v = (v & 0xCC) >> 2 | (v & 0x33) << 2;
        v = (v & 0xAA) >> 1 | (v & 0x55) << 1;
        return (byte)v;
    }

    private static ulong Read(ReadOnlySpan<byte> e, bool bigEndian) => e.Length switch
    {
        1 => e[0],
        2 => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(e) : BinaryPrimitives.ReadUInt16LittleEndian(e),
        4 => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(e) : BinaryPrimitives.ReadUInt32LittleEndian(e),
        _ => bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(e) : BinaryPrimitives.ReadUInt64LittleEndian(e),
    };

    private static void Write(Span<byte> e, ulong v, bool bigEndian)
    {
        switch (e.Length)
        {
            case 1:
                e[0] = (byte)v;
                break;
            case 2:
                if (bigEndian)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(e, (ushort)v);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(e, (ushort)v);
                }

                break;
            case 4:
                if (bigEndian)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(e, (uint)v);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)v);
                }

                break;
            default:
                if (bigEndian)
                {
                    BinaryPrimitives.WriteUInt64BigEndian(e, v);
                }
                else
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(e, v);
                }

                break;
        }
    }
}
