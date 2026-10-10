using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Devices;

/// <summary>
/// Windows のデバイスを直接開く実装 (ENG-29)。一覧の取得は管理者権限なしで行う (アクセス権 0 で開いて問い合わせる。読めない情報は null)。
/// 開く処理は、UI のプロセス (取り外し可能な USB ストレージのボリューム)、補助プロセス、管理者として実行中のアプリの 3 か所から使う。
/// </summary>
/// <remarks>実際のディスクを使う確認はレビューと CI (仮想ディスク) で行う。この PC のテストでは偽のデバイスを使う。</remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed unsafe class Win32DeviceAccess : IDeviceAccess
{
    public static Win32DeviceAccess Instance { get; } = new();

    // ---- 一覧 ----

    public DeviceCatalog Enumerate()
    {
        var volumes = EnumerateVolumes(out List<OpticalDriveInfo> optical);
        int? systemDisk = volumes.FirstOrDefault(v => v.IsSystem)?.DiskNumber;
        var disks = new List<DiskInfo>();
        int misses = 0;
        for (int n = 0; n < 128 && misses < 16; n++)
        {
            DiskInfo? disk = QueryDisk(n, systemDisk);
            if (disk is null)
            {
                misses++;
                continue;
            }

            misses = 0;
            disks.Add(disk);
        }

        return new DeviceCatalog { Disks = disks, Volumes = volumes, OpticalDrives = optical };
    }

    private static DiskInfo? QueryDisk(int number, int? systemDisk)
    {
        using SafeFileHandle handle = CreateFileW(DevicePath.PhysicalDrive(number), 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        StorageDescriptor? descriptor = QueryDescriptor(handle);
        (int logical, int physical)? sectors = QueryAlignment(handle);
        long? size = null;
        int? geometrySector = null;
        byte* buffer = stackalloc byte[256];
        if (DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, null, 0, buffer, 256, out _, IntPtr.Zero))
        {
            geometrySector = *(int*)(buffer + 20);
            size = *(long*)(buffer + 24);
        }

        DiskPartitionStyle style = DiskPartitionStyle.Unknown;
        byte* layout = stackalloc byte[4096];
        if (DeviceIoControl(handle, IoctlDiskGetDriveLayoutEx, null, 0, layout, 4096, out _, IntPtr.Zero))
        {
            style = *(int*)layout switch
            {
                0 => DiskPartitionStyle.Mbr,
                1 => DiskPartitionStyle.Gpt,
                2 => DiskPartitionStyle.Raw,
                _ => DiskPartitionStyle.Unknown,
            };
        }

        return new DiskInfo
        {
            Number = number,
            Model = descriptor?.Model,
            SerialNumber = descriptor?.Serial,
            Size = size,
            Bus = descriptor?.Bus ?? DiskBusType.Unknown,
            PartitionStyle = style,
            LogicalSectorSize = sectors?.logical ?? geometrySector,
            PhysicalSectorSize = sectors?.physical ?? geometrySector,
            Removable = descriptor?.Removable,
            IsSystem = systemDisk is null ? null : systemDisk == number,
        };
    }

    private sealed record StorageDescriptor(string? Model, string? Serial, DiskBusType Bus, bool Removable);

    private static StorageDescriptor? QueryDescriptor(SafeFileHandle handle)
    {
        // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceProperty (0), QueryType = PropertyStandardQuery (0) }
        int* query = stackalloc int[3];
        query[0] = 0;
        query[1] = 0;
        query[2] = 0;
        const int size = 1024;
        byte* output = stackalloc byte[size];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, 12, output, size, out int returned, IntPtr.Zero) || returned < 36)
        {
            return null;
        }

        string? Text(int offsetField)
        {
            int at = *(int*)(output + offsetField);
            if (at <= 0 || at >= returned)
            {
                return null;
            }

            int end = at;
            while (end < returned && output[end] != 0)
            {
                end++;
            }

            string s = Encoding.ASCII.GetString(output + at, end - at).Trim();
            return s.Length == 0 ? null : s;
        }

        bool removable = output[10] != 0;
        string? vendor = Text(12);
        string? product = Text(16);
        string? serial = Text(24);
        int busType = *(int*)(output + 28);
        string? model = vendor is null ? product : product is null ? vendor : vendor + " " + product;
        DiskBusType bus = busType switch
        {
            1 => DiskBusType.Scsi,
            3 => DiskBusType.Ata,
            7 => DiskBusType.Usb,
            10 => DiskBusType.Sas,
            11 => DiskBusType.Sata,
            12 => DiskBusType.Sd,
            13 => DiskBusType.Mmc,
            14 => DiskBusType.Virtual,
            15 => DiskBusType.Virtual,
            17 => DiskBusType.Nvme,
            0 => DiskBusType.Unknown,
            _ => DiskBusType.Other,
        };
        return new StorageDescriptor(model, serial, bus, removable);
    }

    private static (int Logical, int Physical)? QueryAlignment(SafeFileHandle handle)
    {
        // StorageAccessAlignmentProperty (6)
        int* query = stackalloc int[3];
        query[0] = 6;
        query[1] = 0;
        query[2] = 0;
        int* output = stackalloc int[7];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, 12, output, 28, out int returned, IntPtr.Zero) || returned < 28)
        {
            return null;
        }

        // Version, Size, BytesPerCacheLine, BytesOffsetForCacheAlignment, BytesPerLogicalSector, BytesPerPhysicalSector, ...
        return (output[4], output[5]);
    }

    private static List<VolumeDeviceInfo> EnumerateVolumes(out List<OpticalDriveInfo> optical)
    {
        optical = [];
        var result = new List<VolumeDeviceInfo>();
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        string appRoot = Path.GetPathRoot(AppContext.BaseDirectory) ?? string.Empty;
        char* name = stackalloc char[MaxPath];
        char* label = stackalloc char[MaxPath];
        char* fs = stackalloc char[MaxPath];
        IntPtr find = FindFirstVolumeW(name, MaxPath);
        if (find == new IntPtr(-1))
        {
            return result;
        }

        try
        {
            do
            {
                string guidPath = new string(name).TrimEnd('\\');
                string withSlash = guidPath + "\\";
                IReadOnlyList<string> mounts = MountPoints(withSlash);
                string? letter = mounts.FirstOrDefault(m => m.Length == 3 && m[1] == ':')?[..2];
                uint driveType = GetDriveTypeW(withSlash);
                if (driveType == DriveCdRom)
                {
                    bool media = GetVolumeInformationW(withSlash, null, 0, out _, out _, out _, null, 0);
                    optical.Add(new OpticalDriveInfo { Path = letter is null ? guidPath : DevicePath.Drive(letter[0]), DriveLetter = letter, HasMedia = media });
                    continue;
                }

                string? labelText = null, fsText = null;
                if (GetVolumeInformationW(withSlash, label, MaxPath, out _, out _, out _, fs, MaxPath))
                {
                    labelText = new string(label);
                    fsText = new string(fs);
                }

                string openPath = letter is null ? guidPath : DevicePath.Drive(letter[0]);
                (int? diskNumber, long? offset, long? length) = Extents(openPath);
                bool usb = false;
                using (SafeFileHandle h = CreateFileW(openPath, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
                {
                    if (!h.IsInvalid)
                    {
                        usb = QueryDescriptor(h) is { Bus: DiskBusType.Usb };
                    }
                }

                string? root = mounts.FirstOrDefault();
                bool isSystem = root is not null && string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase);
                bool pageFile = root is not null && SafeExists(Path.Combine(root, "pagefile.sys"));
                bool hostsApp = root is not null && string.Equals(root, appRoot, StringComparison.OrdinalIgnoreCase);
                result.Add(new VolumeDeviceInfo
                {
                    Path = openPath,
                    DriveLetter = letter,
                    MountPoints = mounts,
                    Label = string.IsNullOrEmpty(labelText) ? null : labelText,
                    FileSystem = string.IsNullOrEmpty(fsText) ? null : fsText,
                    Size = length,
                    DiskNumber = diskNumber,
                    DiskOffset = offset,
                    IsRemovableUsb = usb && driveType is DriveRemovable or DriveFixed,
                    BitLocker = BitLockerState.Unknown,
                    IsSystem = isSystem,
                    HasPageFile = pageFile,
                    HostsApplication = hostsApp,
                });
            }
            while (FindNextVolumeW(find, name, MaxPath));
        }
        finally
        {
            FindVolumeClose(find);
        }

        return result;
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> MountPoints(string volumeName)
    {
        char* buffer = stackalloc char[4096];
        if (!GetVolumePathNamesForVolumeNameW(volumeName, buffer, 4096, out _))
        {
            return [];
        }

        var list = new List<string>();
        for (char* p = buffer; *p != 0;)
        {
            string s = new(p);
            list.Add(s);
            p += s.Length + 1;
        }

        return list;
    }

    private static (int? Disk, long? Offset, long? Length) Extents(string volumePath)
    {
        using SafeFileHandle handle = CreateFileW(volumePath, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return (null, null, null);
        }

        // VOLUME_DISK_EXTENTS { DWORD Count; DISK_EXTENT Extents[] { DWORD DiskNumber; LARGE_INTEGER Start; LARGE_INTEGER Length } }
        byte* buffer = stackalloc byte[256];
        if (!DeviceIoControl(handle, IoctlVolumeGetVolumeDiskExtents, null, 0, buffer, 256, out _, IntPtr.Zero) || *(int*)buffer < 1)
        {
            return (null, null, null);
        }

        return (*(int*)(buffer + 8), *(long*)(buffer + 16), *(long*)(buffer + 24));
    }

    // ---- 開く ----

    public IDeviceHandle Open(string path, bool writable)
    {
        if (!DevicePath.IsValid(path))
        {
            throw new DeviceException(Win32Errors.InvalidParameter, "Not a device path.");
        }

        uint access = GenericRead | (writable ? GenericWrite : 0);
        SafeFileHandle handle = CreateFileW(path, access, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw DeviceException.FromError(error, path);
        }

        try
        {
            bool volume = DevicePath.IsVolume(path);
            if (volume)
            {
                // ファイルシステムの範囲外の末尾のセクタも読めるようにする (ENG-29 の仕様 4)。
                DeviceIoControl(handle, FsctlAllowExtendedDasdIo, null, 0, null, 0, out _, IntPtr.Zero);
            }

            long length;
            byte* buffer = stackalloc byte[256];
            if (DeviceIoControl(handle, IoctlDiskGetLengthInfo, null, 0, buffer, 8, out _, IntPtr.Zero))
            {
                length = *(long*)buffer;
            }
            else
            {
                int error = Marshal.GetLastPInvokeError();
                throw DeviceException.FromError(error is Win32Errors.NotReady ? Win32Errors.NotReady : error, path);
            }

            (int logical, int physical)? sectors = QueryAlignment(handle);
            int logicalSize = sectors?.logical ?? 0;
            if (logicalSize <= 0 && DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, null, 0, buffer, 256, out _, IntPtr.Zero))
            {
                logicalSize = *(int*)(buffer + 20);
            }

            if (logicalSize <= 0)
            {
                logicalSize = 512;
            }

            int physicalSize = sectors is { physical: > 0 } known ? known.physical : logicalSize;
            return new Handle(path, handle, writable, new DeviceGeometry(length / logicalSize * logicalSize, logicalSize, physicalSize, logicalSize));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private sealed class Handle(string path, SafeFileHandle handle, bool writable, DeviceGeometry geometry) : IDeviceHandle
    {
        private const int Alignment = 4096;

        public string Path { get; } = path;

        public bool Writable { get; } = writable;

        public DeviceGeometry Geometry { get; } = geometry;

        public int ReadSectors(long offset, Span<byte> buffer)
        {
            // デバイスによってはバッファの境界も揃える必要があるため、揃えた一時領域を通す。
            void* aligned = NativeMemory.AlignedAlloc((nuint)Math.Max(buffer.Length, 1), Alignment);
            try
            {
                var span = new Span<byte>(aligned, buffer.Length);
                int done = 0;
                while (done < buffer.Length)
                {
                    int n = RandomAccess.Read(handle, span[done..], offset + done);
                    if (n == 0)
                    {
                        return Win32Errors.SectorNotFound;
                    }

                    done += n;
                }

                span.CopyTo(buffer);
                return 0;
            }
            catch (IOException ex)
            {
                return ex.HResult & 0xFFFF;
            }
            catch (UnauthorizedAccessException)
            {
                return Win32Errors.AccessDenied;
            }
            finally
            {
                NativeMemory.AlignedFree(aligned);
            }
        }

        public int WriteSectors(long offset, ReadOnlySpan<byte> data)
        {
            void* aligned = NativeMemory.AlignedAlloc((nuint)Math.Max(data.Length, 1), Alignment);
            try
            {
                var span = new Span<byte>(aligned, data.Length);
                data.CopyTo(span);
                RandomAccess.Write(handle, span, offset);
                return 0;
            }
            catch (IOException ex)
            {
                return ex.HResult & 0xFFFF;
            }
            catch (UnauthorizedAccessException)
            {
                return Win32Errors.AccessDenied;
            }
            finally
            {
                NativeMemory.AlignedFree(aligned);
            }
        }

        public int LockVolume() => Control(FsctlLockVolume);

        public int DismountVolume() => Control(FsctlDismountVolume);

        public int UnlockVolume() => Control(FsctlUnlockVolume);

        public int Flush() => FlushFileBuffers(handle) ? 0 : Marshal.GetLastPInvokeError();

        private int Control(uint code) => DeviceIoControl(handle, code, null, 0, null, 0, out _, IntPtr.Zero) ? 0 : Marshal.GetLastPInvokeError();

        public void Dispose() => handle.Dispose();
    }

    // ---- Win32 ----

    private const int MaxPath = 1024;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint DriveRemovable = 2;
    private const uint DriveFixed = 3;
    private const uint DriveCdRom = 5;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    private const uint IoctlDiskGetDriveLayoutEx = 0x00070050;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlUnlockVolume = 0x0009001C;
    private const uint FsctlDismountVolume = 0x00090020;
    private const uint FsctlAllowExtendedDasdIo = 0x00090083;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, void* input, int inputSize, void* output, int outputSize,
        out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstVolumeW(char* name, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextVolumeW(IntPtr find, char* name, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindVolumeClose(IntPtr find);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNamesForVolumeNameW(string volume, char* names, int length, out int returned);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string root, char* label, int labelLength, out uint serial, out uint maxComponent,
        out uint flags, char* fileSystem, int fileSystemLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);
}
