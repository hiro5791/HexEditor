using System.Globalization;
using System.Text;
using HexEditor.Core.Clipboard;

namespace HexEditor.Core.Formats;

/// <summary>エクスポートする範囲のアドレスが形式で表せる上限を超える (TOOL-05 の仕様 4、TOOL-06 の仕様 4、TOOL-11 の「エラー」)。</summary>
public sealed class FormatLimitException(string format, long lastAddress, long limit)
    : InvalidOperationException($"{format} では 0x{limit:X} までのアドレスしか表せません (最大のアドレス: 0x{lastAddress:X})。")
{
    public string Format { get; } = format;

    public long LastAddress { get; } = lastAddress;

    public long Limit { get; } = limit;
}

/// <summary>ギャップの省略 (TOOL-05 の仕様 3: 指定値が N バイト以上続く範囲を出力しない)。</summary>
public readonly record struct GapOmission(byte Value, int MinimumRun);

/// <summary>Intel HEX・S-record のエクスポートの設定 (TOOL-05 の仕様 3、TOOL-06 の仕様 3)。</summary>
public sealed record RecordExportOptions
{
    /// <summary>1 レコードのデータ長 (Intel HEX は 1〜255、S-record は 1〜250。既定 16)。</summary>
    public int RecordLength { get; init; } = 16;

    public IntelHexAddressMode IntelMode { get; init; } = IntelHexAddressMode.Auto;

    public SRecordAddressMode SRecordMode { get; init; } = SRecordAddressMode.Auto;

    /// <summary>実行開始アドレス。null なら出力しない (S-record の終わりのレコードは 0)。</summary>
    public long? ExecAddress { get; init; }

    /// <summary>Intel HEX の実行開始アドレスを <c>03</c> (CS:IP) で出す。偽なら <c>05</c>。</summary>
    public bool ExecIsSegment { get; init; }

    /// <summary>ギャップの省略。null ならしない (既定)。</summary>
    public GapOmission? OmitGaps { get; init; }

    public bool UpperCase { get; init; } = true;

    /// <summary>S0 の文字列 (最大 64 文字)。</summary>
    public string Header { get; init; } = string.Empty;

    /// <summary>レコード数 (S5 / S6) を出力する。</summary>
    public bool WriteCount { get; init; } = true;

    /// <summary>最初のデータレコードの前に拡張アドレスのレコードを必ず出す (値が 0 でも。元のファイルの形を保つ)。</summary>
    public bool LeadingExtendedRecord { get; init; }
}

/// <summary>
/// アドレス付きのレコードに分けて書く共通部分。データは範囲ごとに渡し、範囲の切れ目・レコードの長さ・境界 (<see cref="BreaksBefore"/>) で
/// レコードを分ける。ギャップの省略 (<see cref="GapOmission"/>) もここで行う。
/// </summary>
internal abstract class RecordSink(TextWriter writer, int recordLength, string newLine, bool upper, GapOmission? omit)
{
    private readonly byte[] _record = new byte[recordLength];
    private int _count;
    private long _recordAddress;
    private long _runAddress = -1;
    private long _runLength;

    protected TextWriter W { get; } = writer;

    protected string NL { get; } = newLine;

    /// <summary>出力したデータレコードの数。</summary>
    public long DataRecords { get; private set; }

    protected string Hex(long value, int digits) => value.ToString((upper ? "X" : "x") + digits, CultureInfo.InvariantCulture);

    /// <summary>この位置の前でレコードを分けるか (Intel HEX の 64 KB の境界)。</summary>
    protected virtual bool BreaksBefore(long address) => false;

    protected abstract void EmitData(long address, ReadOnlySpan<byte> data);

    /// <summary><paramref name="address"/> からのデータを加える。前のデータと続いていなければレコードを分ける。</summary>
    public void Put(long address, ReadOnlySpan<byte> data)
    {
        if (omit is not { } gap)
        {
            PutRaw(address, data);
            return;
        }

        // 指定値の連続は、長さが分かるまで数だけを持つ。
        int i = 0;
        while (i < data.Length)
        {
            if (data[i] == gap.Value)
            {
                if (_runAddress < 0 || _runAddress + _runLength != address + i)
                {
                    EndRun();
                    _runAddress = address + i;
                }

                int j = i;
                while (j < data.Length && data[j] == gap.Value)
                {
                    j++;
                }

                _runLength += j - i;
                i = j;
                continue;
            }

            EndRun();
            int k = i;
            while (k < data.Length && data[k] != gap.Value)
            {
                k++;
            }

            PutRaw(address + i, data[i..k]);
            i = k;
        }
    }

    private void EndRun()
    {
        if (_runAddress < 0)
        {
            return;
        }

        long at = _runAddress;
        long length = _runLength;
        _runAddress = -1;
        _runLength = 0;
        if (length >= omit!.Value.MinimumRun)
        {
            Flush(); // 省略した範囲で切る
            return;
        }

        Span<byte> fill = stackalloc byte[256];
        fill.Fill(omit.Value.Value);
        while (length > 0)
        {
            int n = (int)Math.Min(fill.Length, length);
            PutRaw(at, fill[..n]);
            at += n;
            length -= n;
        }
    }

    private void PutRaw(long address, ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_count > 0 && (address != _recordAddress + _count || BreaksBefore(address)))
            {
                Flush();
            }

            if (_count == 0)
            {
                _recordAddress = address;
            }

            int n = Math.Min(data.Length, _record.Length - _count);

            // 境界をまたがないように、境界の手前で切る。
            for (int k = 1; k < n; k++)
            {
                if (BreaksBefore(address + k))
                {
                    n = k;
                    break;
                }
            }

            data[..n].CopyTo(_record.AsSpan(_count));
            _count += n;
            address += n;
            data = data[n..];
            if (_count == _record.Length)
            {
                Flush();
            }
        }
    }

    /// <summary>ためているレコードを書く。</summary>
    public void Flush()
    {
        if (_count == 0)
        {
            return;
        }

        EmitData(_recordAddress, _record.AsSpan(0, _count));
        DataRecords++;
        _count = 0;
    }

    /// <summary>範囲の終わり (続くデータとはつながない)。</summary>
    public void Break()
    {
        EndRun();
        Flush();
    }

    /// <summary>残りを書く。</summary>
    public void Finish()
    {
        EndRun();
        Flush();
    }
}

/// <summary>Intel HEX のエクスポート (TOOL-05 の仕様 3、Copy As の Intel HEX)。</summary>
internal sealed class IntelHexSink : RecordSink
{
    private readonly IntelHexAddressMode _mode;
    private long _upper;
    private bool _first = true;
    private readonly bool _leading;

    public IntelHexSink(TextWriter writer, RecordExportOptions o, IntelHexAddressMode resolvedMode, string newLine)
        : base(writer, Math.Clamp(o.RecordLength, 1, 255), newLine, o.UpperCase, o.OmitGaps)
    {
        _mode = resolvedMode;
        _leading = o.LeadingExtendedRecord;
    }

    protected override bool BreaksBefore(long address) => (address & 0xFFFF) == 0;

    protected override void EmitData(long address, ReadOnlySpan<byte> data)
    {
        long upper = _mode switch
        {
            IntelHexAddressMode.I16Hex => (address & 0xF0000) >> 4,
            IntelHexAddressMode.I32Hex => address >> 16,
            _ => 0,
        };
        if (_mode != IntelHexAddressMode.I8Hex && (upper != _upper || (_first && _leading)))
        {
            Line(0, (byte)(_mode == IntelHexAddressMode.I16Hex ? 2 : 4), [(byte)(upper >> 8), (byte)upper]);
            _upper = upper;
        }

        _first = false;
        Line((int)(address & 0xFFFF), 0, data);
    }

    public void Line(int address, byte type, ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(11 + data.Length * 2);
        sb.Append(':');
        int sum = data.Length + (address >> 8) + (address & 0xFF) + type;
        sb.Append(Hex(data.Length, 2)).Append(Hex(address, 4)).Append(Hex(type, 2));
        foreach (byte b in data)
        {
            sb.Append(Hex(b, 2));
            sum += b;
        }

        sb.Append(Hex((byte)(-sum), 2));
        W.Write(sb);
        W.Write(NL);
    }

    /// <summary>実行開始アドレスと終わりのレコード。</summary>
    public void End(long? exec, bool segment, bool finalNewLine)
    {
        Finish();
        if (exec is { } start)
        {
            Line(0, (byte)(segment ? 3 : 5), [(byte)(start >> 24), (byte)(start >> 16), (byte)(start >> 8), (byte)start]);
        }

        W.Write(":00000001");
        W.Write(Hex(0xFF, 2));
        if (finalNewLine)
        {
            W.Write(NL);
        }
    }
}

/// <summary>Motorola S-record のエクスポート (TOOL-06 の仕様 3、Copy As の S-record)。</summary>
internal sealed class SRecordSink(TextWriter writer, RecordExportOptions o, int addressBytes, string newLine)
    : RecordSink(writer, Math.Clamp(o.RecordLength, 1, 255 - addressBytes - 1), newLine, o.UpperCase, o.OmitGaps)
{
    public void WriteHeader(string header) =>
        Line(0, 0, Encoding.ASCII.GetBytes(header.Length > 64 ? header[..64] : header), 2);

    protected override void EmitData(long address, ReadOnlySpan<byte> data) => Line(addressBytes - 1, address, data, addressBytes);

    public void Line(int type, long address, ReadOnlySpan<byte> data, int bytesOfAddress)
    {
        var sb = new StringBuilder(4 + (bytesOfAddress + data.Length + 1) * 2);
        sb.Append('S').Append(type.ToString(CultureInfo.InvariantCulture));
        int count = bytesOfAddress + data.Length + 1;
        int sum = count;
        sb.Append(Hex(count, 2));
        for (int i = bytesOfAddress - 1; i >= 0; i--)
        {
            int b = (int)((address >> (i * 8)) & 0xFF);
            sb.Append(Hex(b, 2));
            sum += b;
        }

        foreach (byte b in data)
        {
            sb.Append(Hex(b, 2));
            sum += b;
        }

        sb.Append(Hex((byte)~sum, 2));
        W.Write(sb);
        W.Write(NL);
    }

    /// <summary>レコード数 (S5 / S6) と終わりのレコード (S9 / S8 / S7)。</summary>
    public void End(bool writeCount, long exec, bool finalNewLine)
    {
        Finish();
        if (writeCount)
        {
            long n = DataRecords;
            if (n <= 0xFFFF)
            {
                Line(5, n, [], 2);
            }
            else
            {
                Line(6, n, [], 3);
            }
        }

        // 終わりのレコードは改行の有無を選べるよう、Line を使わずに書く。
        var end = new StringWriter(CultureInfo.InvariantCulture);
        var sink = new SRecordSink(end, o, addressBytes, string.Empty);
        sink.Line(11 - addressBytes, exec, [], addressBytes);
        W.Write(end.ToString());
        if (finalNewLine)
        {
            W.Write(NL);
        }
    }
}

/// <summary>
/// Intel HEX と S-record のエクスポート (TOOL-05、TOOL-06)。データは範囲ごと (ギャップのある内容では、データのある範囲だけ) に渡す。
/// アドレスは「オフセット + <c>addressOfOffsetZero</c>」。
/// </summary>
public static class RecordExporter
{
    /// <summary>Intel HEX のアドレスの形式の上限 (TOOL-05 の仕様 4)。</summary>
    public static long Limit(IntelHexAddressMode mode) => mode switch
    {
        IntelHexAddressMode.I8Hex => 0x10000,
        IntelHexAddressMode.I16Hex => 0x100000,
        _ => 0x100000000,
    };

    /// <summary>S-record のアドレスの形式の上限 (TOOL-06 の仕様 4)。</summary>
    public static long Limit(SRecordAddressMode mode) => mode switch
    {
        SRecordAddressMode.S1 => 0x10000,
        SRecordAddressMode.S2 => 0x1000000,
        _ => 0x100000000,
    };

    /// <summary>「自動」を解決したアドレスの形式。</summary>
    public static IntelHexAddressMode Resolve(IntelHexAddressMode mode, long lastAddress) =>
        mode != IntelHexAddressMode.Auto ? mode : lastAddress <= 0xFFFF ? IntelHexAddressMode.I8Hex : IntelHexAddressMode.I32Hex;

    public static SRecordAddressMode Resolve(SRecordAddressMode mode, long lastAddress) =>
        mode != SRecordAddressMode.Auto ? mode
        : lastAddress <= 0xFFFF ? SRecordAddressMode.S1 : lastAddress <= 0xFFFFFF ? SRecordAddressMode.S2 : SRecordAddressMode.S3;

    /// <summary>最大のアドレス (データがなければ開始アドレス)。</summary>
    private static long LastAddress(IReadOnlyList<(long Offset, long Length)> ranges, long addressOfOffsetZero) =>
        ranges.Count == 0 ? addressOfOffsetZero : ranges[^1].Offset + ranges[^1].Length - 1 + addressOfOffsetZero;

    /// <summary>範囲が形式で表せるかを確かめる。表せなければ <see cref="FormatLimitException"/>。</summary>
    public static void Validate(string format, RecordExportOptions o, IReadOnlyList<(long Offset, long Length)> ranges, long addressOfOffsetZero)
    {
        long first = ranges.Count == 0 ? addressOfOffsetZero : ranges[0].Offset + addressOfOffsetZero;
        long last = LastAddress(ranges, addressOfOffsetZero);
        long limit = format == FormatIds.IntelHex ? Limit(Resolve(o.IntelMode, last)) : Limit(Resolve(o.SRecordMode, last));
        if (first < 0 || last >= limit)
        {
            throw new FormatLimitException(format == FormatIds.IntelHex ? "Intel HEX" : "S-record", last, limit - 1);
        }
    }

    public static void WriteIntelHex(TextWriter writer, ByteReader read, IReadOnlyList<(long Offset, long Length)> ranges, long addressOfOffsetZero,
        RecordExportOptions o, string newLine, bool finalNewLine = true, CancellationToken cancellationToken = default, Action<long>? progress = null)
    {
        Validate(FormatIds.IntelHex, o, ranges, addressOfOffsetZero);
        IntelHexAddressMode mode = Resolve(o.IntelMode, LastAddress(ranges, addressOfOffsetZero));
        var sink = new IntelHexSink(writer, o, mode, newLine);
        Feed(sink, read, ranges, addressOfOffsetZero, cancellationToken, progress);
        sink.End(o.ExecAddress, o.ExecIsSegment, finalNewLine);
    }

    public static void WriteSRecord(TextWriter writer, ByteReader read, IReadOnlyList<(long Offset, long Length)> ranges, long addressOfOffsetZero,
        RecordExportOptions o, string newLine, bool finalNewLine = true, CancellationToken cancellationToken = default, Action<long>? progress = null)
    {
        Validate(FormatIds.SRecord, o, ranges, addressOfOffsetZero);
        SRecordAddressMode mode = Resolve(o.SRecordMode, LastAddress(ranges, addressOfOffsetZero));
        int addressBytes = mode switch { SRecordAddressMode.S1 => 2, SRecordAddressMode.S2 => 3, _ => 4 };
        var sink = new SRecordSink(writer, o, addressBytes, newLine);
        sink.WriteHeader(o.Header);
        Feed(sink, read, ranges, addressOfOffsetZero, cancellationToken, progress);
        sink.End(o.WriteCount, o.ExecAddress ?? 0, finalNewLine);
    }

    private static void Feed(RecordSink sink, ByteReader read, IReadOnlyList<(long Offset, long Length)> ranges, long addressOfOffsetZero,
        CancellationToken cancellationToken, Action<long>? progress)
    {
        byte[] buffer = new byte[CopyFormatter.ChunkSize];
        long done = 0;
        foreach ((long offset, long length) in ranges)
        {
            for (long pos = 0; pos < length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, length - pos);
                read(offset + pos, buffer.AsSpan(0, n));
                sink.Put(offset + pos + addressOfOffsetZero, buffer.AsSpan(0, n));
                pos += n;
                done += n;
                progress?.Invoke(done);
            }

            sink.Break();
        }
    }
}
