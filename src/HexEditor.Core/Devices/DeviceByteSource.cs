using System.Buffers;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Devices;

/// <summary>デバイスを開いた経路 (ENG-28、ENG-29 の仕様 3)。</summary>
public enum DeviceRoute
{
    /// <summary>UI のプロセスで直接 (取り外し可能な USB ストレージのボリューム、テスト用の偽のデバイス)。</summary>
    Direct,

    /// <summary>昇格した補助プロセス経由。</summary>
    Helper,

    /// <summary>アプリ全体を管理者として実行していて、同じプロセスで処理する (ENG-28 の仕様 10)。</summary>
    Elevated,
}

/// <summary>開くデバイスの情報 (表示名・識別子・範囲)。</summary>
public sealed record DeviceOpenInfo
{
    public required string Path { get; init; }

    /// <summary>タブに出す名前 (「ディスク 1 (モデル名, サイズ)」。App が地域設定に合わせて作る)。</summary>
    public required string DisplayName { get; init; }

    /// <summary>シリアル番号 (同じデバイスかの判定。ENG-01 の仕様 2、ENG-29 の仕様 11)。不明なら null。</summary>
    public string? SerialNumber { get; init; }

    /// <summary>範囲を指定して開く場合の開始 (バイト。セクタ境界)。</summary>
    public long RangeStart { get; init; }

    /// <summary>範囲を指定して開く場合の長さ。null ならデバイスの末尾まで。</summary>
    public long? RangeLength { get; init; }

    public DeviceRoute Route { get; init; }

    /// <summary>ディスクの情報 (書き込みの確認の文言、影響するボリュームの判定)。ボリュームなら null。</summary>
    public DiskInfo? Disk { get; init; }

    /// <summary>ボリュームの情報。ディスクなら null。</summary>
    public VolumeDeviceInfo? Volume { get; init; }
}

/// <summary>
/// 物理ディスク・ボリューム・光学ドライブのデータソース (ENG-29)。長さ固定 (ENG-07)、<see cref="SourceCapabilities.IsVolatile"/>、
/// <see cref="SourceCapabilities.NeedsAlignment"/>。読み込みは常にセクタ境界に揃えて行う (仕様 6)。デバイスがなくなったら
/// 「切断」を通知し、以後の読み込みは <see cref="UnreadableReason.Disconnected"/> になる (仕様 11)。
/// </summary>
public sealed class DeviceByteSource : ByteSourceBase, View.IViewDefaultsSource
{
    /// <summary>
    /// データソースの種類ごとの表示の既定値 (VIEW-42 の仕様 2 の 3): 区切り線「セクタ」。範囲を指定して開いたら、その開始位置をベースアドレスにする。
    /// </summary>
    public System.Text.Json.Nodes.JsonObject? ViewDefaults => Info.RangeStart != 0
        ? new() { ["separator"] = "sector", ["baseAddress"] = (ulong)Info.RangeStart }
        : new() { ["separator"] = "sector" };

    /// <summary>1 回の読み書きの最大 (補助プロセスの 1 要求の上限と同じ。ENG-28 の仕様 4)。</summary>
    public const int MaxTransfer = 1024 * 1024;

    private readonly object _lock = new();
    private IDeviceHandle _handle;
    private IDeviceAccess _access;
    private volatile bool _disconnected;
    private int _disconnectError;

    public DeviceByteSource(IDeviceHandle handle, IDeviceAccess access, DeviceOpenInfo info)
    {
        _handle = handle;
        _access = access;
        Info = info;
        DeviceGeometry geometry = handle.Geometry;
        if (geometry.LogicalSectorSize < 1 || (geometry.LogicalSectorSize & (geometry.LogicalSectorSize - 1)) != 0)
        {
            throw new DeviceException(Win32Errors.InvalidParameter, "The device reported an invalid sector size.");
        }

        if (info.RangeStart < 0 || info.RangeStart % geometry.LogicalSectorSize != 0 || info.RangeStart > geometry.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(info), "The start of the range must be on a sector boundary inside the device.");
        }

        long available = geometry.Length - info.RangeStart;
        Length = Math.Min(info.RangeLength ?? available, available);
        Geometry = geometry;
    }

    public DeviceOpenInfo Info { get; }

    public DeviceGeometry Geometry { get; }

    public string Path => Info.Path;

    /// <summary>開いた経路 (書き込み・ロックは同じ経路で行う。ENG-30 の仕様 3)。</summary>
    public DeviceRoute Route => Info.Route;

    /// <summary>このデバイスを開いた実装 (ロックするボリュームを開く、再接続する)。</summary>
    public IDeviceAccess Access => _access;

    public IDeviceHandle Handle => _handle;

    public override string DisplayName => Info.DisplayName;

    public override string Identity => IdentityOf(Info.Path, Info.SerialNumber);

    /// <summary>同じデバイスかを比べるキー (ENG-29 の仕様 8)。</summary>
    public static string IdentityOf(string path, string? serial) =>
        "device:" + path.ToUpperInvariant() + (string.IsNullOrEmpty(serial) ? string.Empty : "#" + serial.Trim());

    public override long Length { get; }

    /// <summary>オフセット 0 に対応するデバイス上の位置 (範囲を指定して開いた場合)。</summary>
    public override long BaseAddress => Info.RangeStart;

    public override int LogicalSectorSize => Geometry.LogicalSectorSize;

    public override int PhysicalSectorSize => Geometry.PhysicalSectorSize;

    public override SourceCapabilities Capabilities =>
        SourceCapabilities.IsVolatile | SourceCapabilities.NeedsAlignment | (_handle.Writable && !_disconnected ? SourceCapabilities.CanWrite : 0);

    /// <summary>デバイスがなくなった (取り外し、補助プロセスの終了)。</summary>
    public bool IsDisconnected => _disconnected;

    /// <summary>切断の原因のエラーコード。</summary>
    public int DisconnectError => _disconnectError;

    /// <summary>テスト・診断用: 実際にデバイスに出した読み込みの (位置, 長さ)。<see cref="RecordReads"/> が真のときだけ記録する。</summary>
    public List<(long Offset, int Length)> ReadLog { get; } = [];

    public bool RecordReads { get; set; }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int length = ClampToLength(offset, buffer.Length);
        if (length == 0)
        {
            return new ReadResult(0);
        }

        Span<byte> target = buffer[..length];
        if (_disconnected)
        {
            target.Clear();
            return new ReadResult(length, [new UnreadableRange(offset, length, UnreadableReason.Disconnected, _disconnectError)]);
        }

        int sector = LogicalSectorSize;
        long deviceOffset = Info.RangeStart + offset;
        long from = deviceOffset / sector * sector;
        long end = deviceOffset + length;
        long to = Math.Min((end + sector - 1) / sector * sector, Geometry.Length);
        List<UnreadableRange>? bad = null;
        byte[] rented = ArrayPool<byte>.Shared.Rent((int)Math.Min(MaxTransfer, to - from));
        try
        {
            IDeviceHandle handle = _handle;
            for (long pos = from; pos < to; pos += MaxTransfer)
            {
                int n = (int)Math.Min(MaxTransfer, to - pos);
                Span<byte> chunk = rented.AsSpan(0, n);
                int error = handle.ReadSectors(pos, chunk);
                if (RecordReads)
                {
                    lock (ReadLog)
                    {
                        ReadLog.Add((pos, n));
                    }
                }

                // 要求した範囲と、この読み込みの重なり。
                long copyFrom = Math.Max(pos, deviceOffset);
                long copyTo = Math.Min(pos + n, end);
                if (copyFrom >= copyTo)
                {
                    continue;
                }

                Span<byte> dest = target.Slice((int)(copyFrom - deviceOffset), (int)(copyTo - copyFrom));
                if (error == 0)
                {
                    chunk.Slice((int)(copyFrom - pos), dest.Length).CopyTo(dest);
                    continue;
                }

                dest.Clear();
                UnreadableReason reason = UnreadableReason.IoError;
                if (Win32Errors.IsDisconnect(error))
                {
                    MarkDisconnected(error);
                    reason = UnreadableReason.Disconnected;
                }

                (bad ??= []).Add(new UnreadableRange(copyFrom - Info.RangeStart, copyTo - copyFrom, reason, error));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return new ReadResult(length, bad);
    }

    /// <summary>
    /// セクタ境界に揃えた範囲を書く (保存処理 ENG-30 だけが呼ぶ)。1 MiB ずつに分ける。
    /// </summary>
    /// <exception cref="DeviceException">書き込みに失敗した。</exception>
    public override void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (!_handle.Writable)
        {
            throw new DeviceException(Win32Errors.AccessDenied, "The device is open for reading only.");
        }

        int sector = LogicalSectorSize;
        if (offset < 0 || offset % sector != 0 || data.Length % sector != 0 || offset + data.Length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Writes to a device must be aligned to sectors.");
        }

        for (int pos = 0; pos < data.Length; pos += MaxTransfer)
        {
            int n = Math.Min(MaxTransfer, data.Length - pos);
            int error = _handle.WriteSectors(Info.RangeStart + offset + pos, data.Slice(pos, n));
            if (error != 0)
            {
                if (Win32Errors.IsDisconnect(error))
                {
                    MarkDisconnected(error);
                }

                throw new DeviceWriteException(error, offset + pos);
            }
        }
    }

    /// <summary>書き込みのディスクへの反映。</summary>
    public void Flush()
    {
        int error = _handle.Flush();
        if (error != 0)
        {
            throw DeviceException.FromError(error, "Flush");
        }
    }

    /// <summary>切断の状態にする (デバイスの取り外しの通知、補助プロセスの終了)。</summary>
    public void MarkDisconnected(int error = Win32Errors.DeviceNotConnected)
    {
        lock (_lock)
        {
            if (_disconnected)
            {
                return;
            }

            _disconnected = true;
            _disconnectError = error;
        }

        OnChanged(SourceChangeKind.Disconnected);
    }

    /// <summary>
    /// 新しいハンドルに付け替える: 再接続 (ENG-29 の仕様 11、ENG-28 の仕様 7) と、読み書きでの開き直し (ENG-14 の仕様 3)。
    /// 長さ・セクタサイズが違うデバイスは受け付けない (別のデバイス)。古いハンドルは閉じる。
    /// </summary>
    public void ReplaceHandle(IDeviceHandle handle, IDeviceAccess access)
    {
        if (handle.Geometry.LogicalSectorSize != Geometry.LogicalSectorSize || handle.Geometry.Length < Info.RangeStart + Length)
        {
            handle.Dispose();
            throw new DeviceException(Win32Errors.NoSuchDevice, "The reconnected device is not the same device.");
        }

        IDeviceHandle old;
        lock (_lock)
        {
            old = _handle;
            _handle = handle;
            _access = access;
            _disconnected = false;
            _disconnectError = 0;
        }

        if (!ReferenceEquals(old, handle))
        {
            old.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _handle.Dispose();
        }
    }
}

/// <summary>デバイスへの書き込みの失敗 (ENG-30 の「エラー」)。<see cref="Offset"/> はドキュメント上の位置。</summary>
public sealed class DeviceWriteException(int errorCode, long offset)
    : DeviceException(errorCode, $"{new System.ComponentModel.Win32Exception(errorCode).Message} (offset 0x{offset:X})")
{
    public long Offset { get; } = offset;
}
