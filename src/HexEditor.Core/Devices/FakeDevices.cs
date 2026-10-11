#if HEX_TEST_HOOKS
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Devices;

// テスト用のビルドだけに入れる、偽のディスク・ボリューム (テスト方針 7.2)。実際のディスクに触れずに、ディスクを開く・書き込む・
// ロックする・取り外すの振る舞いを再現する。補助プロセス (HexEditor.Elevated.exe) のテスト用の起動 (--test-fake-devices) と、
// App のテスト用の設定 (fakeDevices) からも使う。製品版には含めない。

/// <summary>偽のボリュームのロックの振る舞い。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FakeLockBehavior>))]
public enum FakeLockBehavior
{
    /// <summary>ロックできる。</summary>
    Ok,

    /// <summary>開いているファイルがありロックできない。ディスマウントはできる。</summary>
    InUse,

    /// <summary>ロックもディスマウントもできない。</summary>
    Refuse,
}

/// <summary>偽のディスクの定義 (JSON で書ける)。</summary>
public sealed record FakeDiskSpec
{
    public int Number { get; init; }

    public string Model { get; init; } = "Fake Disk";

    public string Serial { get; init; } = "FAKE0001";

    public long Size { get; init; } = 64L * 1024 * 1024;

    public int SectorSize { get; init; } = 512;

    public int PhysicalSectorSize { get; init; }

    public DiskBusType Bus { get; init; } = DiskBusType.Virtual;

    public DiskPartitionStyle PartitionStyle { get; init; } = DiskPartitionStyle.Mbr;

    public bool Removable { get; init; }

    public bool System { get; init; }

    /// <summary>開くのに管理者権限が要る (UI のプロセスから直接開くとアクセス拒否)。物理ディスクは既定で真。</summary>
    public bool RequiresAdmin { get; init; } = true;

    /// <summary>書き込み禁止のメディア。</summary>
    public bool WriteProtected { get; init; }

    /// <summary>内容を置くファイル (別のプロセスと共有し、再起動しても残す)。null ならメモリ上 (初期内容は種から計算)。</summary>
    public string? Image { get; init; }

    /// <summary>メモリ上の初期内容の種。0 なら 8 バイトごとにディスク上の位置を書いた値。</summary>
    public ulong Seed { get; init; }
}

/// <summary>偽のボリュームの定義。</summary>
public sealed record FakeVolumeSpec
{
    public required string Path { get; init; }

    public string? DriveLetter { get; init; }

    public string? Label { get; init; }

    public string? FileSystem { get; init; } = "NTFS";

    public int Disk { get; init; }

    public long Offset { get; init; }

    public long Size { get; init; }

    public bool RemovableUsb { get; init; }

    public bool System { get; init; }

    public bool PageFile { get; init; }

    public BitLockerState BitLocker { get; init; } = BitLockerState.NotEncrypted;

    public FakeLockBehavior Lock { get; init; } = FakeLockBehavior.Ok;

    /// <summary>開くのに管理者権限が要る。取り外し可能な USB ストレージは偽、内蔵ディスクのボリュームは真。</summary>
    public bool RequiresAdmin { get; init; } = true;
}

/// <summary>偽のデバイスの一覧全体 (テスト用の設定ファイルの形)。</summary>
public sealed record FakeDeviceSpec
{
    public List<FakeDiskSpec> Disks { get; init; } = [];

    public List<FakeVolumeSpec> Volumes { get; init; } = [];

    public List<OpticalDriveInfo> OpticalDrives { get; init; } = [];

    /// <summary>このプロセスは管理者として動いている扱い (管理者権限の要るデバイスも開ける)。</summary>
    public bool Elevated { get; init; }

    /// <summary>テスト用: 書き込みの N 回目 (1 から数える) の後にプロセスを強制終了する (TC-ENG-30-05)。0 なら行わない。</summary>
    public int KillAfterWrites { get; init; }

    /// <summary>テスト用: ロックの後、書き込みの前に止める (ロックを保持した状態。TC-ENG-28-08)。止めた印のファイル。</summary>
    public string? HoldAfterLockMarker { get; init; }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static FakeDeviceSpec Parse(string json) => JsonSerializer.Deserialize<FakeDeviceSpec>(json, Options) ?? new FakeDeviceSpec();

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

/// <summary>偽のディスクの中身 (セクタ単位の疎な記憶、またはファイル)。</summary>
public sealed class FakeDisk : IDisposable
{
    private readonly Dictionary<long, byte[]> _sectors = [];
    private readonly SafeFileHandle? _file;

    public FakeDisk(FakeDiskSpec spec)
    {
        Spec = spec;
        if (spec.Image is { } image)
        {
            bool exists = File.Exists(image);
            _file = File.OpenHandle(image, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            if (!exists || RandomAccess.GetLength(_file) < spec.Size)
            {
                Sources.SparseFiles.TryMakeSparse(_file);
                RandomAccess.SetLength(_file, spec.Size);
            }
        }
    }

    public FakeDiskSpec Spec { get; }

    public int SectorSize => Spec.SectorSize;

    public long Length => Spec.Size;

    /// <summary>取り外された (以後の読み書きは ERROR_DEVICE_NOT_CONNECTED)。</summary>
    public bool Removed { get; set; }

    /// <summary>読み込みエラーにするセクタの範囲 (位置, 長さ)。</summary>
    public List<(long Offset, long Length)> BadRanges { get; } = [];

    public void Read(long offset, Span<byte> buffer)
    {
        if (_file is not null)
        {
            int n = RandomAccess.Read(_file, buffer, offset);
            buffer[n..].Clear();
            return;
        }

        lock (_sectors)
        {
            for (int pos = 0; pos < buffer.Length;)
            {
                long at = offset + pos;
                long index = at / SectorSize;
                int inSector = (int)(at % SectorSize);
                int n = Math.Min(SectorSize - inSector, buffer.Length - pos);
                if (_sectors.TryGetValue(index, out byte[]? data))
                {
                    data.AsSpan(inSector, n).CopyTo(buffer.Slice(pos, n));
                }
                else
                {
                    Initial(at, buffer.Slice(pos, n));
                }

                pos += n;
            }
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (_file is not null)
        {
            RandomAccess.Write(_file, data, offset);
            return;
        }

        lock (_sectors)
        {
            for (int pos = 0; pos < data.Length;)
            {
                long at = offset + pos;
                long index = at / SectorSize;
                int inSector = (int)(at % SectorSize);
                int n = Math.Min(SectorSize - inSector, data.Length - pos);
                if (!_sectors.TryGetValue(index, out byte[]? sector))
                {
                    sector = new byte[SectorSize];
                    Initial(index * SectorSize, sector);
                    _sectors[index] = sector;
                }

                data.Slice(pos, n).CopyTo(sector.AsSpan(inSector, n));
                pos += n;
            }
        }
    }

    /// <summary>初期内容: 種が 0 なら 8 バイトごとにディスク上の位置 (リトルエンディアン)。それ以外は種と位置から計算した値。</summary>
    private void Initial(long offset, Span<byte> buffer)
    {
        Span<byte> word = stackalloc byte[8];
        for (int i = 0; i < buffer.Length;)
        {
            long at = offset + i;
            long aligned = at & ~7L;
            ulong value = Spec.Seed == 0 ? (ulong)aligned : Mix((ulong)aligned ^ Spec.Seed);
            BinaryPrimitives.WriteUInt64LittleEndian(word, value);
            int from = (int)(at - aligned);
            int n = Math.Min(8 - from, buffer.Length - i);
            word.Slice(from, n).CopyTo(buffer.Slice(i, n));
            i += n;
        }
    }

    private static ulong Mix(ulong x)
    {
        x ^= x >> 33;
        x *= 0xff51afd7ed558ccdUL;
        x ^= x >> 33;
        x *= 0xc4ceb9fe1a85ec53UL;
        x ^= x >> 33;
        return x;
    }

    public void Dispose() => _file?.Dispose();
}

/// <summary>
/// 偽のデバイスの実装 (<see cref="IDeviceAccess"/>)。呼び出しの記録 (<see cref="Calls"/>) で、ロック → 書き込み → 反映 → ロックの解除の
/// 順序や書き込みの回数を確かめられる。
/// </summary>
public sealed class FakeDeviceAccess : IDeviceAccess, IDisposable
{
    private readonly List<FakeDisk> _disks;
    private readonly List<string> _calls = [];
    private int _writes;

    public FakeDeviceAccess(FakeDeviceSpec spec, bool? elevated = null)
    {
        Spec = spec;
        Elevated = elevated ?? spec.Elevated;
        _disks = [.. spec.Disks.Select(d => new FakeDisk(d))];
    }

    public FakeDeviceSpec Spec { get; }

    /// <summary>管理者権限がある扱い。偽なら <see cref="FakeDiskSpec.RequiresAdmin"/> のデバイスはアクセス拒否。</summary>
    public bool Elevated { get; set; }

    public IReadOnlyList<FakeDisk> Disks => _disks;

    /// <summary>テスト用: 書き込みに失敗させる回 (1 から数える)。0 なら失敗させない。</summary>
    public int FailWriteAt { get; set; }

    /// <summary>テスト用: 書き込みの後に呼ぶ (回数)。</summary>
    public Action<int>? AfterWrite { get; set; }

    /// <summary>テスト用: ロックの後に呼ぶ (ボリュームのパス)。</summary>
    public Action<string>? AfterLock { get; set; }

    /// <summary>呼び出しの記録 ("Open path rw"、"Lock path"、"Write path offset length"、"Flush path"、"Unlock path" など)。</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>ロック中のボリューム。</summary>
    public HashSet<string> LockedVolumes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ボリュームをロックしたハンドル (Windows と同じく、ロックしたボリュームはロックしたハンドルからしか読み書きできない)。
    /// <see cref="LockedVolumes"/> と同じロックで守る。
    /// </summary>
    private readonly Dictionary<string, object> _lockOwners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>強制的にディスマウントしたボリューム (開いているファイルがなくなり、ロックできる。ロックの解除で再マウントされる)。</summary>
    private readonly HashSet<string> _dismounted = new(StringComparer.OrdinalIgnoreCase);

    public int WriteCount => _writes;

    internal void Log(string call)
    {
        lock (_calls)
        {
            _calls.Add(call);
        }
    }

    public void ClearCalls()
    {
        lock (_calls)
        {
            _calls.Clear();
        }
    }

    public FakeDisk Disk(int number) => _disks.Single(d => d.Spec.Number == number);

    public DeviceCatalog Enumerate()
    {
        Log("Enumerate");
        return new DeviceCatalog
        {
            Disks =
            [
                .. _disks.Where(d => !d.Removed).Select(d => new DiskInfo
                {
                    Number = d.Spec.Number,
                    Model = d.Spec.Model,
                    SerialNumber = d.Spec.Serial,
                    Size = d.Spec.Size,
                    Bus = d.Spec.Bus,
                    PartitionStyle = d.Spec.PartitionStyle,
                    LogicalSectorSize = d.Spec.SectorSize,
                    PhysicalSectorSize = d.Spec.PhysicalSectorSize > 0 ? d.Spec.PhysicalSectorSize : d.Spec.SectorSize,
                    Removable = d.Spec.Removable,
                    IsSystem = d.Spec.System,
                }),
            ],
            Volumes =
            [
                .. Spec.Volumes.Where(v => !Disk(v.Disk).Removed).Select(v => new VolumeDeviceInfo
                {
                    Path = v.Path,
                    DriveLetter = v.DriveLetter,
                    Label = v.Label,
                    FileSystem = v.FileSystem,
                    Size = v.Size,
                    DiskNumber = v.Disk,
                    DiskOffset = v.Offset,
                    IsRemovableUsb = v.RemovableUsb,
                    BitLocker = v.BitLocker,
                    IsSystem = v.System,
                    HasPageFile = v.PageFile,
                }),
            ],
            OpticalDrives = Spec.OpticalDrives,
        };
    }

    public IDeviceHandle Open(string path, bool writable)
    {
        if (!DevicePath.IsValid(path))
        {
            throw new DeviceException(Win32Errors.InvalidParameter, "Not a device path.");
        }

        Log($"Open {path} {(writable ? "rw" : "r")}");
        if (DevicePath.DiskNumber(path) is { } number)
        {
            FakeDisk disk = _disks.FirstOrDefault(d => d.Spec.Number == number)
                ?? throw DeviceException.FromError(Win32Errors.FileNotFound);
            Check(disk, disk.Spec.RequiresAdmin);
            return new Handle(this, path, disk, 0, disk.Length, writable, null);
        }

        FakeVolumeSpec volume = Spec.Volumes.FirstOrDefault(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase))
            ?? throw DeviceException.FromError(Win32Errors.FileNotFound);
        if (volume.BitLocker == BitLockerState.Locked)
        {
            throw new DeviceException(Win32Errors.FveLocked, "The volume is locked by BitLocker.");
        }

        FakeDisk owner = Disk(volume.Disk);
        Check(owner, volume.RequiresAdmin);
        return new Handle(this, path, owner, volume.Offset, volume.Size, writable, volume);
    }

    private void Check(FakeDisk disk, bool requiresAdmin)
    {
        if (disk.Removed)
        {
            throw DeviceException.FromError(Win32Errors.FileNotFound);
        }

        if (requiresAdmin && !Elevated)
        {
            throw DeviceException.FromError(Win32Errors.AccessDenied);
        }
    }

    public void Dispose()
    {
        foreach (FakeDisk disk in _disks)
        {
            disk.Dispose();
        }
    }

    private sealed class Handle(FakeDeviceAccess owner, string path, FakeDisk disk, long start, long length, bool writable, FakeVolumeSpec? volume)
        : IDeviceHandle
    {
        private bool _locked;
        private bool _closed;

        public string Path { get; } = path;

        public bool Writable { get; } = writable;

        public DeviceGeometry Geometry { get; } = new(length, disk.SectorSize,
            disk.Spec.PhysicalSectorSize > 0 ? disk.Spec.PhysicalSectorSize : disk.SectorSize, disk.SectorSize);

        private int Check(long offset, int count)
        {
            if (_closed)
            {
                return Win32Errors.InvalidHandle;
            }

            if (disk.Removed)
            {
                return Win32Errors.DeviceNotConnected;
            }

            if (offset < 0 || offset % disk.SectorSize != 0 || count % disk.SectorSize != 0 || offset + count > length)
            {
                return Win32Errors.InvalidParameter;
            }

            return 0;
        }

        /// <summary>このボリュームを別のハンドルがロックしている (このハンドルからは読み書きできない)。</summary>
        private bool LockedByOther()
        {
            if (volume is null)
            {
                return false;
            }

            lock (owner.LockedVolumes)
            {
                return owner._lockOwners.TryGetValue(volume.Path, out object? holder) && !ReferenceEquals(holder, this);
            }
        }

        public int ReadSectors(long offset, Span<byte> buffer)
        {
            int error = Check(offset, buffer.Length);
            if (error != 0)
            {
                return error;
            }

            if (LockedByOther())
            {
                return Win32Errors.AccessDenied;
            }

            long at = start + offset;
            foreach ((long badOffset, long badLength) in disk.BadRanges)
            {
                if (badOffset < at + buffer.Length && at < badOffset + badLength)
                {
                    return Win32Errors.SectorNotFound;
                }
            }

            disk.Read(at, buffer);
            return 0;
        }

        public int WriteSectors(long offset, ReadOnlySpan<byte> data)
        {
            if (!Writable)
            {
                return Win32Errors.AccessDenied;
            }

            int error = Check(offset, data.Length);
            if (error != 0)
            {
                return error;
            }

            if (disk.Spec.WriteProtected)
            {
                return Win32Errors.WriteProtect;
            }

            if (LockedByOther())
            {
                return Win32Errors.AccessDenied;
            }

            // マウント中のボリュームの範囲への直接の書き込みは、ロックしていなければ OS が拒否する (ENG-30 の仕様 2)。
            long at = start + offset;
            foreach (FakeVolumeSpec v in owner.Spec.Volumes.Where(v => v.Disk == disk.Spec.Number && v.FileSystem is not null))
            {
                bool overlaps = v.Offset < at + data.Length && at < v.Offset + v.Size;
                if (overlaps && !owner.LockedVolumes.Contains(v.Path) && !_locked)
                {
                    owner.Log($"WriteDenied {Path} 0x{offset:X} {data.Length}");
                    return Win32Errors.AccessDenied;
                }
            }

            int count = Interlocked.Increment(ref owner._writes);
            if (owner.FailWriteAt > 0 && count == owner.FailWriteAt)
            {
                owner.Log($"WriteFailed {Path} 0x{offset:X} {data.Length}");
                return Win32Errors.SectorNotFound;
            }

            disk.Write(at, data);
            owner.Log($"Write {Path} 0x{offset:X} {data.Length}");
            owner.AfterWrite?.Invoke(count);
            if (owner.Spec.KillAfterWrites > 0 && count >= owner.Spec.KillAfterWrites)
            {
                Environment.FailFast("Fake devices: kill after writes (test hooks).");
            }

            return 0;
        }

        public int LockVolume()
        {
            if (volume is null)
            {
                return Win32Errors.InvalidParameter;
            }

            owner.Log($"Lock {Path}");
            lock (owner.LockedVolumes)
            {
                // 別のハンドルがロック中、開いているファイルがある (ディスマウントしていない)、ロックできないボリューム。
                bool heldByOther = owner._lockOwners.TryGetValue(volume.Path, out object? holder) && !ReferenceEquals(holder, this);
                bool inUse = volume.Lock == FakeLockBehavior.InUse && !owner._dismounted.Contains(volume.Path);
                if (heldByOther || inUse || volume.Lock == FakeLockBehavior.Refuse)
                {
                    return Win32Errors.AccessDenied;
                }

                _locked = true;
                owner.LockedVolumes.Add(volume.Path);
                owner._lockOwners[volume.Path] = this;
            }

            owner.AfterLock?.Invoke(volume.Path);
            if (owner.Spec.HoldAfterLockMarker is { } marker)
            {
                File.WriteAllText(marker, volume.Path);
                Thread.Sleep(Timeout.Infinite);
            }

            return 0;
        }

        public int DismountVolume()
        {
            if (volume is null)
            {
                return Win32Errors.InvalidParameter;
            }

            owner.Log($"Dismount {Path}");
            if (volume.Lock == FakeLockBehavior.Refuse)
            {
                return Win32Errors.AccessDenied;
            }

            // 強制的なディスマウント: 開いているファイルは閉じられる。ロックはしない (書き込む前に改めてロックする)。
            lock (owner.LockedVolumes)
            {
                owner._dismounted.Add(volume.Path);
            }

            return 0;
        }

        public int UnlockVolume()
        {
            if (volume is null)
            {
                return Win32Errors.InvalidParameter;
            }

            owner.Log($"Unlock {Path}");
            lock (owner.LockedVolumes)
            {
                if (!_locked)
                {
                    return Win32Errors.NotLocked;
                }

                _locked = false;
                owner.LockedVolumes.Remove(volume.Path);
                owner._lockOwners.Remove(volume.Path);
                owner._dismounted.Remove(volume.Path);
            }

            return 0;
        }

        public int Flush()
        {
            owner.Log($"Flush {Path}");
            return _closed ? Win32Errors.InvalidHandle : 0;
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            if (_locked)
            {
                UnlockVolume();
            }

            owner.Log($"Close {Path}");
        }
    }
}
#endif
