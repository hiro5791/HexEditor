using System.Text.Json;
using HexEditor.Core.Devices;
using HexEditor.Core.Processes;

namespace HexEditor.Core.Elevation;

/// <summary>
/// 補助プロセス経由のデバイス (ENG-28)。UI のプロセスはこれを <see cref="IDeviceAccess"/> として使い、直接開くときと同じ
/// <see cref="DeviceByteSource"/> で読み書きする。
/// </summary>
public sealed class HelperDeviceAccess(HelperClient client) : IDeviceAccess
{
    public HelperClient Client { get; } = client;

    public DeviceCatalog Enumerate()
    {
        (uint status, byte[] body) = Client.Send(HelperCommand.EnumDevices, 0, []);
        Check(status, "EnumDevices");
        return JsonSerializer.Deserialize<DeviceCatalog>(body, DeviceJson.Options) ?? DeviceCatalog.Empty;
    }

    public IDeviceHandle Open(string path, bool writable)
    {
        (uint status, byte[] body) = Send(HelperCommand.OpenDevice, writable ? HelperProtocol.FlagWritable : (ushort)0,
            new BodyWriter().Text(path).ToArray());
        Check(status, path);
        uint id = new BodyReader(body).U32();
        (status, body) = Send(HelperCommand.GetGeometry, 0, new BodyWriter().U32(id).ToArray());
        if (status != 0)
        {
            _ = Send(HelperCommand.Close, 0, new BodyWriter().U32(id).ToArray());
            Check(status, path);
        }

        var reader = new BodyReader(body);
        var geometry = new DeviceGeometry(reader.I64(), (int)reader.U32(), (int)reader.U32(), (int)reader.U32());
        return new Handle(Client, id, path, writable, geometry);
    }

    private (uint, byte[]) Send(HelperCommand command, ushort flags, byte[] body)
    {
        try
        {
            return Client.Send(command, flags, body);
        }
        catch (HelperTimeoutException)
        {
            throw new DeviceException(Win32Errors.Timeout, "The elevated helper did not answer in time.");
        }
        catch (HelperDisconnectedException)
        {
            throw new DeviceException(Win32Errors.BrokenPipe, "The elevated helper process has exited.");
        }
    }

    private static void Check(uint status, string what)
    {
        if (status != 0)
        {
            throw DeviceException.FromError((int)status, what);
        }
    }

    private sealed class Handle(HelperClient client, uint id, string path, bool writable, DeviceGeometry geometry) : IDeviceHandle
    {
        private int _closed;

        public string Path { get; } = path;

        public bool Writable { get; } = writable;

        public DeviceGeometry Geometry { get; } = geometry;

        private int Call(HelperCommand command, byte[] body, out byte[] response)
        {
            response = [];
            try
            {
                (uint status, response) = client.Send(command, 0, body);
                return (int)status;
            }
            catch (HelperTimeoutException)
            {
                return Win32Errors.Timeout;
            }
            catch (HelperDisconnectedException)
            {
                return Win32Errors.BrokenPipe;
            }
        }

        public int ReadSectors(long offset, Span<byte> buffer)
        {
            // 1 MiB ずつの要求に分けて並行して送る (ENG-28 の「巨大ファイル・長時間処理」)。
            int count = (buffer.Length + HelperProtocol.MaxTransfer - 1) / HelperProtocol.MaxTransfer;
            if (count <= 1)
            {
                int error = Call(HelperCommand.ReadSectors, new BodyWriter().U32(id).I64(offset).U32((uint)buffer.Length).ToArray(), out byte[] data);
                if (error == 0)
                {
                    data.AsSpan(0, Math.Min(data.Length, buffer.Length)).CopyTo(buffer);
                }

                return error;
            }

            var tasks = new Task<(uint Status, byte[] Body)>[count];
            for (int i = 0; i < count; i++)
            {
                int at = i * HelperProtocol.MaxTransfer;
                int n = Math.Min(HelperProtocol.MaxTransfer, buffer.Length - at);
                tasks[i] = client.SendAsync(HelperCommand.ReadSectors, 0, new BodyWriter().U32(id).I64(offset + at).U32((uint)n).ToArray());
            }

            int first = 0;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    (uint status, byte[] body) = tasks[i].GetAwaiter().GetResult();
                    if (status == 0)
                    {
                        body.CopyTo(buffer[(i * HelperProtocol.MaxTransfer)..]);
                    }
                    else if (first == 0)
                    {
                        first = (int)status;
                    }
                }
                catch (HelperTimeoutException)
                {
                    first = first == 0 ? Win32Errors.Timeout : first;
                }
                catch (HelperDisconnectedException)
                {
                    first = first == 0 ? Win32Errors.BrokenPipe : first;
                }
            }

            return first;
        }

        public int WriteSectors(long offset, ReadOnlySpan<byte> data)
        {
            for (int pos = 0; pos < data.Length; pos += HelperProtocol.MaxTransfer)
            {
                int n = Math.Min(HelperProtocol.MaxTransfer, data.Length - pos);
                int error = Call(HelperCommand.WriteSectors, new BodyWriter().U32(id).I64(offset + pos).Bytes(data.Slice(pos, n)).ToArray(), out _);
                if (error != 0)
                {
                    return error;
                }
            }

            return 0;
        }

        public int LockVolume() => Call(HelperCommand.LockVolume, new BodyWriter().U32(id).ToArray(), out _);

        public int DismountVolume() => Call(HelperCommand.DismountVolume, new BodyWriter().U32(id).ToArray(), out _);

        public int UnlockVolume() => Call(HelperCommand.UnlockVolume, new BodyWriter().U32(id).ToArray(), out _);

        public int Flush() => Call(HelperCommand.Flush, new BodyWriter().U32(id).ToArray(), out _);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0 && client.IsConnected)
            {
                _ = Call(HelperCommand.Close, new BodyWriter().U32(id).ToArray(), out _);
            }
        }
    }
}

/// <summary>
/// 補助プロセス経由のプロセスメモリ (ENG-28、ENG-32 の仕様 4 の 2)。一覧は UI のプロセスで取る (<paramref name="local"/>)。
/// 対象のプロセスの終了は、UI のプロセスが同期用のハンドルで見張る (<paramref name="watchExit"/>)。
/// </summary>
public sealed class HelperProcessAccess(HelperClient client, IProcessAccess local, Func<int, Action, IDisposable?>? watchExit = null) : IProcessAccess
{
    public HelperClient Client { get; } = client;

    public IReadOnlyList<ProcessEntry> Enumerate() => local.Enumerate();

    public IProcessMemory Open(int pid, bool writable)
    {
        (uint status, byte[] body) result;
        try
        {
            result = Client.Send(HelperCommand.OpenProcess, writable ? HelperProtocol.FlagWritable : (ushort)0, new BodyWriter().U32((uint)pid).ToArray());
        }
        catch (Exception ex) when (ex is HelperTimeoutException or HelperDisconnectedException)
        {
            throw new ProcessAccessException(ProcessOpenFailure.Other, Win32Errors.BrokenPipe, ex.Message);
        }

        if (result.status != 0)
        {
            int error = (int)result.status;
            ProcessOpenFailure failure = error switch
            {
                PrivilegedOperations.ProtectedProcessStatus => ProcessOpenFailure.Protected,
                Win32Errors.AccessDenied => ProcessOpenFailure.AccessDenied,
                Win32Errors.InvalidParameter => ProcessOpenFailure.NotFound,
                _ => ProcessOpenFailure.Other,
            };
            throw new ProcessAccessException(failure, error, failure.ToString());
        }

        var reader = new BodyReader(result.body);
        uint id = reader.U32();
        long limit = reader.I64();
        var arch = (ProcessArchitecture)reader.U16();
        string name = reader.Text();
        return new Memory(Client, id, pid, name, arch, limit, writable, watchExit);
    }

    private sealed class Memory : IProcessMemory
    {
        private readonly HelperClient _client;
        private readonly uint _id;
        private readonly IDisposable? _watch;
        private int _closed;
        private volatile bool _exited;

        public Memory(HelperClient client, uint id, int pid, string name, ProcessArchitecture arch, long limit, bool writable,
            Func<int, Action, IDisposable?>? watchExit)
        {
            _client = client;
            _id = id;
            Pid = pid;
            Name = name;
            Architecture = arch;
            AddressLimit = limit;
            Writable = writable;
            _watch = watchExit?.Invoke(pid, () =>
            {
                _exited = true;
                Exited?.Invoke(this, EventArgs.Empty);
            });
        }

        public int Pid { get; }

        public string Name { get; }

        public ProcessArchitecture Architecture { get; }

        public long AddressLimit { get; }

        public bool Writable { get; }

        public bool HasExited => _exited;

        public event EventHandler? Exited;

        private (int Error, byte[] Body) Call(HelperCommand command, byte[] body)
        {
            try
            {
                (uint status, byte[] response) = _client.Send(command, 0, body);
                return ((int)status, response);
            }
            catch (HelperTimeoutException)
            {
                return (Win32Errors.Timeout, []);
            }
            catch (HelperDisconnectedException)
            {
                return (Win32Errors.BrokenPipe, []);
            }
        }

        public IReadOnlyList<MemoryRegion> QueryRegions()
        {
            var list = new List<MemoryRegion>();
            long start = 0;
            while (start >= 0)
            {
                (int error, byte[] body) = Call(HelperCommand.QueryRegions, new BodyWriter().U32(_id).I64(start).U32(4096).ToArray());
                if (error != 0)
                {
                    throw new IOException(new System.ComponentModel.Win32Exception(error).Message);
                }

                var reader = new BodyReader(body);
                uint count = reader.U32();
                for (uint i = 0; i < count; i++)
                {
                    long baseAddress = reader.I64();
                    long size = reader.I64();
                    var state = (RegionState)reader.U8();
                    uint protect = reader.U32();
                    var type = (RegionType)reader.U8();
                    long allocation = reader.I64();
                    string mapped = reader.Text();
                    list.Add(new MemoryRegion(baseAddress, size, state, protect, type, mapped.Length == 0 ? null : mapped, allocation));
                }

                long next = reader.I64();
                start = next > start ? next : -1;
            }

            return list;
        }

        public IReadOnlyList<ProcessModule> EnumModules()
        {
            (int error, byte[] body) = Call(HelperCommand.EnumModules, new BodyWriter().U32(_id).ToArray());
            if (error != 0)
            {
                return [];
            }

            var reader = new BodyReader(body);
            uint count = reader.U32();
            var list = new List<ProcessModule>((int)count);
            for (uint i = 0; i < count; i++)
            {
                list.Add(new ProcessModule(reader.Text(), reader.I64(), reader.I64(), reader.Text()));
            }

            return list;
        }

        public int Read(long address, Span<byte> buffer, out int read)
        {
            read = 0;
            for (int pos = 0; pos < buffer.Length;)
            {
                int n = Math.Min(HelperProtocol.MaxTransfer, buffer.Length - pos);
                (int error, byte[] body) = Call(HelperCommand.ReadMemory, new BodyWriter().U32(_id).I64(address + pos).U32((uint)n).ToArray());
                int got = 0;
                if (body.Length >= 4)
                {
                    var reader = new BodyReader(body);
                    got = (int)Math.Min(reader.U32(), (uint)n);
                    reader.Rest()[..got].CopyTo(buffer[pos..]);
                }

                read += got;
                pos += got;
                if (error != 0 || got < n)
                {
                    return error == 0 ? Win32Errors.PartialCopy : error;
                }
            }

            return 0;
        }

        public int Write(long address, ReadOnlySpan<byte> data, out int written)
        {
            written = 0;
            for (int pos = 0; pos < data.Length;)
            {
                int n = Math.Min(HelperProtocol.MaxTransfer, data.Length - pos);
                (int error, byte[] body) = Call(HelperCommand.WriteMemory, new BodyWriter().U32(_id).I64(address + pos).Bytes(data.Slice(pos, n)).ToArray());
                int done = body.Length >= 4 ? (int)new BodyReader(body).U32() : 0;
                written += done;
                if (error != 0 || done < n)
                {
                    return error == 0 ? Win32Errors.PartialCopy : error;
                }

                pos += n;
            }

            return 0;
        }

        public int Protect(long address, long size, uint protect, out uint oldProtect)
        {
            (int error, byte[] body) = Call(HelperCommand.ProtectMemory, new BodyWriter().U32(_id).I64(address).I64(size).U32(protect).ToArray());
            oldProtect = body.Length >= 4 ? new BodyReader(body).U32() : 0;
            return error;
        }

        public void Dispose()
        {
            _watch?.Dispose();
            if (Interlocked.Exchange(ref _closed, 1) == 0 && _client.IsConnected)
            {
                _ = Call(HelperCommand.Close, new BodyWriter().U32(_id).ToArray());
            }
        }
    }
}
