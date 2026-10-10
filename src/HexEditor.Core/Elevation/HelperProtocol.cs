using System.Buffers.Binary;
using System.Text;

namespace HexEditor.Core.Elevation;

/// <summary>補助プロセスのコマンド (ENG-28 の仕様 4)。これ以外の番号は拒否して切断する。</summary>
public enum HelperCommand : ushort
{
    Hello = 1,
    EnumDevices = 2,
    OpenDevice = 3,
    GetGeometry = 4,
    ReadSectors = 5,
    WriteSectors = 6,
    LockVolume = 7,
    DismountVolume = 8,
    UnlockVolume = 9,
    Flush = 10,
    OpenProcess = 11,
    QueryRegions = 12,
    EnumModules = 13,
    ReadMemory = 14,
    WriteMemory = 15,
    ProtectMemory = 16,
    QueryDeviceInfo = 17,
    GetUsbWriteProtect = 18,
    SetUsbWriteProtect = 19,
    SetWriteBlock = 20,
    Close = 21,
    Ping = 22,
}

/// <summary><c>QueryDeviceInfo</c> で受け付ける情報の種類 (読み取り専用のものだけ。FOR-27、FOR-28)。</summary>
public enum DeviceInfoKind : ushort
{
    IdentifyDevice = 1,
    ReadNativeMaxAddress = 2,
    DeviceConfigurationIdentify = 3,
    SmartReadData = 4,
    SmartReadThresholds = 5,
    NvmeSmartHealth = 6,
}

/// <summary>
/// 補助プロセスとのメッセージの形式 (ENG-28 の仕様 3。リトルエンディアン):
/// <code>
/// 要求:  [長さ u32][要求 ID u32][コマンド u16][フラグ u16][本体 ...]
/// 応答:  [長さ u32][要求 ID u32][状態 u32 (0 = 成功、それ以外は Win32 エラーコード)][本体 ...]
/// </code>
/// 長さはヘッダを除く本体のバイト数。本体の最大は 1 MiB + 64 バイト。
/// </summary>
public static class HelperProtocol
{
    /// <summary>プロトコルの版。UI と補助プロセスで違えば補助プロセスを起動し直す (<c>Hello</c>)。</summary>
    public const uint Version = 1;

    public const int HeaderSize = 12;

    /// <summary>1 回の読み書きの最大 (ENG-28 の仕様 4)。</summary>
    public const int MaxTransfer = 1024 * 1024;

    /// <summary>本体の最大 (超えたら切断する)。</summary>
    public const int MaxBody = MaxTransfer + 64;

    /// <summary>応答を待たずに送れる要求の数 (ENG-28 の仕様 3)。</summary>
    public const int MaxInFlight = 16;

    /// <summary>合言葉の長さ (ENG-28 の仕様 2 の 2)。</summary>
    public const int SecretSize = 32;

    /// <summary>フラグ: 読み書きで開く (<c>OpenDevice</c> / <c>OpenProcess</c>)。</summary>
    public const ushort FlagWritable = 1;

    /// <summary><c>SetUsbWriteProtect</c> の「値の削除」。</summary>
    public const uint UsbWriteProtectDelete = 0xFFFF_FFFF;

    /// <summary>コマンドの番号が許可リストにあるか。</summary>
    public static bool IsKnown(ushort command) => command is >= (ushort)HelperCommand.Hello and <= (ushort)HelperCommand.Ping;

    public static byte[] Request(uint id, HelperCommand command, ushort flags, ReadOnlySpan<byte> body)
    {
        byte[] frame = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), id);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), (ushort)command);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(10), flags);
        body.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static byte[] Response(uint id, uint status, ReadOnlySpan<byte> body)
    {
        byte[] frame = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), id);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), status);
        body.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>ヘッダと本体を 1 つ読む。ストリームの終わりなら null。本体が上限を超えたら <see cref="HelperProtocolException"/>。</summary>
    public static async Task<(uint Length, uint Id, uint Word, byte[] Body)?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderSize];
        if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        uint word = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        if (length > MaxBody)
        {
            throw new HelperProtocolException($"message too large ({length} bytes)");
        }

        byte[] body = new byte[length];
        if (!await ReadExactlyOrEndAsync(stream, body, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (length, id, word, body);
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[done..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            done += n;
        }

        return true;
    }
}

/// <summary>不正なメッセージ・許可リストにない要求 (補助プロセスは切断して終了する。ENG-28 の「エラー」)。</summary>
public sealed class HelperProtocolException(string message) : Exception(message);

/// <summary>本体を組み立てる。</summary>
public sealed class BodyWriter
{
    private readonly MemoryStream _stream = new();

    public BodyWriter U8(byte value)
    {
        _stream.WriteByte(value);
        return this;
    }

    public BodyWriter U16(ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        _stream.Write(b);
        return this;
    }

    public BodyWriter U32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        _stream.Write(b);
        return this;
    }

    public BodyWriter I64(long value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, value);
        _stream.Write(b);
        return this;
    }

    /// <summary>文字列 ([長さ u16][UTF-8])。</summary>
    public BodyWriter Text(string? value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        U16((ushort)Math.Min(bytes.Length, ushort.MaxValue));
        _stream.Write(bytes, 0, Math.Min(bytes.Length, ushort.MaxValue));
        return this;
    }

    public BodyWriter Bytes(ReadOnlySpan<byte> data)
    {
        _stream.Write(data);
        return this;
    }

    public byte[] ToArray() => _stream.ToArray();
}

/// <summary>本体を読む。足りなければ <see cref="HelperProtocolException"/>。</summary>
public ref struct BodyReader(ReadOnlySpan<byte> body)
{
    private readonly ReadOnlySpan<byte> _body = body;
    private int _pos;

    public readonly int Remaining => _body.Length - _pos;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > _body.Length)
        {
            throw new HelperProtocolException("message too short");
        }

        ReadOnlySpan<byte> s = _body.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public byte U8() => Take(1)[0];

    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public string Text() => Encoding.UTF8.GetString(Take(U16()));

    public ReadOnlySpan<byte> Rest() => Take(Remaining);

    /// <summary>余りがあれば不正なメッセージ。</summary>
    public readonly void End()
    {
        if (Remaining != 0)
        {
            throw new HelperProtocolException("unexpected data at the end of the message");
        }
    }
}
