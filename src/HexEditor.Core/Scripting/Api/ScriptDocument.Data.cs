using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Sdk;
using SdkRange = HexEditor.Sdk.ByteRange;

namespace HexEditor.Core.Scripting.Api;

/// <summary>データの読み書き (AUTO-08)。</summary>
public sealed partial class ScriptDocument
{
    private static void CheckSyncLength(long length)
    {
        if (length > SyncLimit)
        {
            throw new LimitError(Msg.T($"同期 API で扱えるのは 1 回 16 MB までです ({length:N0} バイト)。大きな範囲は doc.chunks() や一括処理 API を使ってください。", $"The synchronous API handles at most 16 MB at a time ({length:N0} bytes). Use doc.chunks() or the bulk APIs for large ranges."))
            {
                Limit = "size",
            };
        }
    }

    public byte[] ReadBytes(OffsetArg offset, OffsetArg lengthArg) => ScriptErrorMap.Guard(() =>
    {
        ThrowIfClosed();
        long o = ResolveOffset(offset);
        long length = ResolveOffset(lengthArg);
        if (length < 0)
        {
            throw new Sdk.ArgumentError(Msg.T("length は 0 以上にしてください。", "length must be 0 or greater."));
        }

        CheckSyncLength(length);
        DocumentSnapshot snapshot = Snapshot;
        CheckOffset(o, allowEnd: true);
        int n = (int)Math.Min(length, snapshot.Length - o);
        byte[] data = new byte[n];
        ReadExact(snapshot, o, data);
        return data;
    });

    public int ReadByte(OffsetArg offset) => ScriptErrorMap.Guard(() =>
    {
        ThrowIfClosed();
        long o = ResolveOffset(offset);
        CheckOffset(o, allowEnd: false);
        Span<byte> one = stackalloc byte[1];
        ReadExact(Snapshot, o, one);
        return (int)one[0];
    });

    private static (int Size, bool Signed) IntType(string type) => type switch
    {
        "i8" => (1, true),
        "u8" => (1, false),
        "i16" => (2, true),
        "u16" => (2, false),
        "i24" => (3, true),
        "u24" => (3, false),
        "i32" => (4, true),
        "u32" => (4, false),
        "i64" => (8, true),
        "u64" => (8, false),
        _ => throw new Sdk.ArgumentError(Msg.T($"整数の型が不明です: {type} (i8 u8 i16 u16 i24 u24 i32 u32 i64 u64)", $"Unknown integer type: {type} (i8 u8 i16 u16 i24 u24 i32 u32 i64 u64)")),
    };

    private bool BigEndian(string? endian) => endian switch
    {
        null => Ui(() => Host.Editor.View.BigEndian),
        "le" => false,
        "be" => true,
        _ => throw new Sdk.ArgumentError(Msg.T($"エンディアンは \"le\" か \"be\" です: {endian}", $"endian must be \"le\" or \"be\": {endian}")),
    };

    private byte[] ReadFixed(OffsetArg offset, int size)
    {
        ThrowIfClosed();
        long o = ResolveOffset(offset);
        DocumentSnapshot snapshot = Snapshot;
        if (o < 0 || o > snapshot.Length - size)
        {
            throw new Sdk.RangeError(Msg.T($"オフセット 0x{o:X} から {size} バイトはドキュメントの外です (長さ 0x{snapshot.Length:X})。", $"{size} bytes from offset 0x{o:X} are outside the document (length 0x{snapshot.Length:X})."));
        }

        byte[] data = new byte[size];
        ReadExact(snapshot, o, data);
        return data;
    }

    public BigInteger ReadInt(OffsetArg offset, string type, string? endian = null) => ScriptErrorMap.Guard(() =>
    {
        (int size, bool signed) = IntType(type);
        bool big = BigEndian(endian);
        byte[] data = ReadFixed(offset, size);
        return new BigInteger(data, isUnsigned: !signed, isBigEndian: big);
    });

    public double ReadFloat(OffsetArg offset, string type, string? endian = null) => ScriptErrorMap.Guard(() =>
    {
        bool big = BigEndian(endian);
        switch (type)
        {
            case "f16":
            {
                byte[] d = ReadFixed(offset, 2);
                return (double)(big ? BinaryPrimitives.ReadHalfBigEndian(d) : BinaryPrimitives.ReadHalfLittleEndian(d));
            }

            case "f32":
            {
                byte[] d = ReadFixed(offset, 4);
                return big ? BinaryPrimitives.ReadSingleBigEndian(d) : BinaryPrimitives.ReadSingleLittleEndian(d);
            }

            case "f64":
            {
                byte[] d = ReadFixed(offset, 8);
                return big ? BinaryPrimitives.ReadDoubleBigEndian(d) : BinaryPrimitives.ReadDoubleLittleEndian(d);
            }

            default:
                throw new Sdk.ArgumentError(Msg.T($"浮動小数点の型が不明です: {type} (f16 f32 f64)", $"Unknown floating-point type: {type} (f16 f32 f64)"));
        }
    });

    private Encoding EncodingOrDefault(EncodingArg? encoding) =>
        encoding is EncodingArg e ? ScriptText.Resolve(e) : Ui(() => ScriptText.Resolve(Host.Editor.TextEncoding.CodePage));

    public string ReadString(OffsetArg offset, OffsetArg maxLength, EncodingArg? encoding = null, ReadStringOptions? options = null) => ScriptErrorMap.Guard(() =>
    {
        Encoding enc = EncodingOrDefault(encoding);
        byte[] data = ReadBytes(offset, maxLength);
        int length = data.Length;
        if (options?.StopAtNull ?? true)
        {
            int unit = ScriptText.UnitSize(enc);
            for (int i = 0; i + unit <= data.Length; i += unit)
            {
                if (data.AsSpan(i, unit).IndexOfAnyExcept((byte)0) < 0)
                {
                    length = i;
                    break;
                }
            }
        }

        return enc.GetString(data, 0, length);
    });

    public void WriteBytes(OffsetArg offset, BytePattern data) => ScriptErrorMap.Guard(() =>
    {
        ThrowIfClosed();
        byte[] bytes = PatternBytes(data, allowRandom: false);
        CheckSyncLength(bytes.Length);
        long o = ResolveOffset(offset);
        WriteAt(o, bytes);
    });

    /// <summary>上書きする。末尾を越える部分は、長さを変えられるなら追加し、変えられないなら <see cref="Sdk.RangeError"/> (仕様 1)。</summary>
    internal void WriteAt(long o, byte[] bytes)
    {
        CheckOffset(o, allowEnd: true);
        if (bytes.Length == 0)
        {
            return;
        }

        _ctx.Edit(this, d =>
        {
            long length = d.Length;
            if (o + bytes.Length > length && !d.CanResize)
            {
                throw new Sdk.RangeError(Msg.T($"長さを変えられないドキュメントの末尾を越えて書き込めません (オフセット 0x{o:X}、{bytes.Length} バイト、長さ 0x{length:X})。", $"Cannot write past the end of a document whose length cannot be changed (offset 0x{o:X}, {bytes.Length} bytes, length 0x{length:X})."));
            }

            long inside = Math.Min(bytes.Length, length - o);
            using (d.BeginGroup(_ctx.Environment.Localize(ScriptTexts.UndoName, _ctx.Name)))
            {
                if (inside > 0)
                {
                    d.Overwrite(o, bytes.AsSpan(0, (int)inside));
                }

                if (inside < bytes.Length)
                {
                    d.Insert(length, bytes.AsSpan((int)inside));
                }
            }
        });
    }

    public void WriteInt(OffsetArg offset, string type, BigInteger value, string? endian = null) => ScriptErrorMap.Guard(() =>
    {
        (int size, bool signed) = IntType(type);
        bool big = BigEndian(endian);
        BigInteger min = signed ? -(BigInteger.One << (size * 8 - 1)) : BigInteger.Zero;
        BigInteger max = signed ? (BigInteger.One << (size * 8 - 1)) - 1 : (BigInteger.One << (size * 8)) - 1;
        if (value < min || value > max)
        {
            throw new Sdk.RangeError(Msg.T($"値 {value} は {type} の範囲 ({min}〜{max}) の外です。", $"The value {value} is outside the range of {type} ({min} to {max})."));
        }

        BigInteger raw = value < 0 ? (BigInteger.One << (size * 8)) + value : value;
        byte[] le = raw.ToByteArray(isUnsigned: true, isBigEndian: false);
        byte[] bytes = new byte[size];
        le.AsSpan(0, Math.Min(size, le.Length)).CopyTo(bytes);
        if (big)
        {
            Array.Reverse(bytes);
        }

        WriteAt(ResolveOffset(offset), bytes);
    });

    public void WriteFloat(OffsetArg offset, string type, double value, string? endian = null) => ScriptErrorMap.Guard(() =>
    {
        bool big = BigEndian(endian);
        byte[] bytes;
        switch (type)
        {
            case "f16":
                bytes = new byte[2];
                if (big)
                {
                    BinaryPrimitives.WriteHalfBigEndian(bytes, (Half)value);
                }
                else
                {
                    BinaryPrimitives.WriteHalfLittleEndian(bytes, (Half)value);
                }

                break;
            case "f32":
                bytes = new byte[4];
                if (big)
                {
                    BinaryPrimitives.WriteSingleBigEndian(bytes, (float)value);
                }
                else
                {
                    BinaryPrimitives.WriteSingleLittleEndian(bytes, (float)value);
                }

                break;
            case "f64":
                bytes = new byte[8];
                if (big)
                {
                    BinaryPrimitives.WriteDoubleBigEndian(bytes, value);
                }
                else
                {
                    BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
                }

                break;
            default:
                throw new Sdk.ArgumentError(Msg.T($"浮動小数点の型が不明です: {type} (f16 f32 f64)", $"Unknown floating-point type: {type} (f16 f32 f64)"));
        }

        WriteAt(ResolveOffset(offset), bytes);
    });

    public long WriteString(OffsetArg offset, string text, EncodingArg? encoding = null) => ScriptErrorMap.Guard(() =>
    {
        byte[] bytes = EncodingOrDefault(encoding).GetBytes(text);
        CheckSyncLength(bytes.Length);
        WriteAt(ResolveOffset(offset), bytes);
        return (long)bytes.Length;
    });

    private void RequireResizable()
    {
        if (!Ui(() => Document.CanResize))
        {
            throw new ReadOnlyError(Msg.T($"ドキュメント「{Name}」は長さを変えられません。", $"The length of the document \"{Name}\" cannot be changed."));
        }
    }

    public void InsertBytes(OffsetArg offset, BytePattern data) => ScriptErrorMap.Guard(() =>
    {
        ThrowIfClosed();
        RequireResizable();
        byte[] bytes = PatternBytes(data, allowRandom: false);
        CheckSyncLength(bytes.Length);
        long o = ResolveOffset(offset);
        CheckOffset(o, allowEnd: true);
        _ctx.Edit(this, d => d.Insert(o, bytes));
    });

    public void Delete(RangeArg range) => ScriptErrorMap.Guard(() =>
    {
        ThrowIfClosed();
        RequireResizable();
        SdkRange r = CheckedRange(range);
        if (r.Length > 0)
        {
            _ctx.Edit(this, d => d.Delete(r.Start, r.Length));
        }
    });

    /// <summary>塗りつぶし (仕様 1・4)。データエンジンの塗りつぶしのピースを使うので、量に関係なくすぐ終わる。</summary>
    public void Fill(RangeArg range, BytePattern pattern, FillOptions? options = null) => ScriptErrorMap.Guard(() =>
    {
        ThrowIfClosed();
        bool insert = options?.Insert ?? false;
        bool random = IsRandom(pattern);
        byte[] bytes = random ? [] : PatternBytes(pattern, allowRandom: false);
        if (!random && (bytes.Length == 0 || bytes.Length > GeneratedData.MaxPatternLength))
        {
            throw new Sdk.ArgumentError(Msg.T($"パターンは 1〜{GeneratedData.MaxPatternLength} バイトです。", $"The pattern must be 1 to {GeneratedData.MaxPatternLength} bytes."));
        }

        if (insert)
        {
            RequireResizable();
        }

        SdkRange r = insert ? new SdkRange(ResolveRangeStart(range), ResolveRange(range).Length) : CheckedRange(range);
        if (insert)
        {
            CheckOffset(r.Start, allowEnd: true);
        }

        if (r.Length == 0)
        {
            return;
        }

        ulong seed = random ? NewSeed() : 0;
        _ctx.Edit(this, d =>
        {
            if (insert)
            {
                if (random)
                {
                    d.InsertRandom(r.Start, r.Length, seed);
                }
                else
                {
                    d.InsertPattern(r.Start, r.Length, bytes);
                }
            }
            else if (random)
            {
                d.OverwriteRandom(r.Start, r.Length, seed);
            }
            else
            {
                d.OverwritePattern(r.Start, r.Length, bytes);
            }
        });
    });

    private long ResolveRangeStart(RangeArg range) => ResolveRange(range).Start;

    public void Resize(OffsetArg newLength, BytePattern? fill = null) => ScriptErrorMap.Guard(() =>
    {
        ThrowIfClosed();
        RequireResizable();
        long target = ResolveOffset(newLength);
        if (target < 0)
        {
            throw new Sdk.ArgumentError(Msg.T("長さは 0 以上にしてください。", "The length must be 0 or greater."));
        }

        byte[] pattern = fill is BytePattern p ? PatternBytes(p, allowRandom: false) : [0];
        _ctx.Edit(this, d =>
        {
            long length = d.Length;
            if (target < length)
            {
                d.Delete(target, length - target);
            }
            else if (target > length)
            {
                d.InsertPattern(length, target - length, pattern);
            }
        });
    });
}
