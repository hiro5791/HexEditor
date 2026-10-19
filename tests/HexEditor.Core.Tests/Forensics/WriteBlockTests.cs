using System.Security.Cryptography;
using System.Text;
using HexEditor.Core.Devices;
using HexEditor.Core.Elevation;
using HexEditor.Core.Engine;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Cases;
using HexEditor.Core.Forensics.WriteBlock;
using HexEditor.Core.Formats;
using HexEditor.Core.Plugins;
using HexEditor.Core.Processes;
using HexEditor.Core.Remote.Gdb;
using HexEditor.Core.Remote.Sftp;
using HexEditor.Core.Saving;
using HexEditor.Core.Scripting;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Scripting;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.Tools.Files;
using HexEditor.Sdk.Plugins;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Forensics;

/// <summary>
/// FOR-01 書き込み保護モードの関門 (Core)。書き込みをする部品ごとに、モードが有効な間は書き込まない (データソース・デバイス・ファイルが変わらない) ことを確かめる。
/// アプリ全体の状態 (<see cref="WriteBlockGate.Global"/>) には触れず、テストごとの状態を <see cref="WriteBlockGate.Use"/> で使う。
/// 書き込みの対象はテストの一時フォルダのファイルと偽のデバイス・偽のプロセス・偽のサーバーだけ。
/// </summary>
public sealed class WriteBlockTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-writeblock").FullName;
    private readonly WriteBlockPolicy _policy = new();
    private readonly IDisposable _scope;

    public WriteBlockTests() => _scope = WriteBlockGate.Use(_policy);

    public void Dispose()
    {
        _scope.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Copy(string id, string name)
    {
        string path = Path.Combine(_dir, name);
        File.Copy(TestDataCatalog.Get(id), path, overwrite: true);
        return path;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private SaveSettings Settings() => new() { JournalDirectory = Path.Combine(_dir, "journal") };

    // ---- 規則 ----

    [Fact]
    public void Rules_block_own_source_protected_files_devices_processes_and_listed_operations()
    {
        string evidence = Path.Combine(_dir, "evidence.bin");
        string other = Path.Combine(_dir, "other.bin");
        _policy.IsProtectedPath = p => string.Equals(p, evidence, StringComparison.OrdinalIgnoreCase);

        // 無効の間は何も拒否しない。
        Assert.Null(_policy.CheckFileSave(evidence, evidence));
        _policy.EnsureDeviceWrite(@"\\.\PhysicalDrive9");

        Assert.True(_policy.SetEnabled(true));
        Assert.False(_policy.SetEnabled(true));
        Assert.Equal(WriteBlockTarget.FileSave, _policy.CheckFileSave(other, other));
        Assert.Null(_policy.CheckFileSave(Path.Combine(_dir, "copy.bin"), other));
        Assert.Equal(WriteBlockTarget.FileSave, _policy.CheckFileSave(evidence, other));
        Assert.Throws<WriteBlockedException>(() => _policy.EnsureFileWrite(evidence));
        Assert.Throws<WriteBlockedException>(() => _policy.EnsureFileWrite(evidence + ":secret"));
        _policy.EnsureFileWrite(other);
        WriteBlockedException device = Assert.Throws<WriteBlockedException>(() => _policy.EnsureDeviceWrite(@"\\.\PhysicalDrive9"));
        Assert.Equal(WriteBlockTarget.Device, device.Target);
        Assert.Equal(unchecked((int)0x80070013), device.HResult);
        Assert.Throws<WriteBlockedException>(() => _policy.EnsureProcessWrite(1234));
        Assert.Throws<WriteBlockedException>(() => _policy.EnsureRemoteWrite("gdb://127.0.0.1:1234"));
        Assert.Throws<WriteBlockedException>(() => _policy.EnsurePluginWrite("plugin:x"));
        foreach (BlockedOperation op in Enum.GetValues<BlockedOperation>())
        {
            Assert.True(_policy.Blocks(op));
            Assert.Equal(op, Assert.Throws<WriteBlockedException>(() => _policy.EnsureOperation(op)).Operation);
        }

        // ドキュメント単位の例外 (仕様 5): その場保存と保護の対象への書き込みを許す。無効にすると例外も消える。
        Assert.True(_policy.AddException(other));
        Assert.Null(_policy.CheckFileSave(other, other));
        Assert.True(_policy.IsExcepted(other.ToUpperInvariant()));
        Assert.True(_policy.SetEnabled(false));
        Assert.Empty(_policy.Exceptions);
        Assert.False(_policy.AddException(other));
    }

    // ---- ドキュメントの保存 ----

    [Fact]
    public void Saving_in_place_is_refused_but_save_as_to_another_folder_works()
    {
        // TC-FOR-01-04 の Core の部分: 編集中のファイルでモードを有効にすると、元の場所への保存は書き込み 0 回で拒否され、内容と更新日時が変わらない。
        string evidence = Copy("TD-SEQ-1M", "evidence.bin");
        string hash = Sha(evidence);
        DateTime stamp = File.GetLastWriteTimeUtc(evidence);
        using var doc = new Document(FileByteSource.Open(evidence), DocumentAssert.Options());
        doc.Overwrite(0, [0xAA]);
        _policy.SetEnabled(true);

        foreach (bool safe in new[] { false, true })
        {
            SavePlan plan = SavePlanner.Plan(doc, null, Settings() with { AlwaysSafeSave = safe });
            Assert.True(plan.CanExecute, plan.Issue.ToString());
            Assert.Throws<WriteBlockedException>(() => SavePlanner.Execute(plan));
            SavePlanner.Abort(plan);
        }

        Assert.Throws<WriteBlockedException>(() => DocumentSaver.Save(doc.DataSnapshot, evidence));
        Assert.Equal(hash, Sha(evidence));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(evidence));
        Assert.Equal(0xAA, DocumentAssert.Read(doc.Current, 0, 1)[0]);
        Assert.True(doc.IsModified);

        // 名前を付けて保存 (証拠以外の場所) はできる。
        string copyFolder = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;
        string copy = Path.Combine(copyFolder, "copy.bin");
        SavePlan saveAs = SavePlanner.Plan(doc, copy, Settings());
        SaveResult result = SavePlanner.Execute(saveAs);
        result.SavedFile?.Dispose();
        Assert.Equal(0xAA, File.ReadAllBytes(copy)[0]);
        Assert.Equal(hash, Sha(evidence));
    }

    [Fact]
    public void Save_as_onto_a_protected_file_and_generic_file_writers_are_refused()
    {
        string evidence = Copy("TD-BYTES-256", "evidence.bin");
        string memo = Copy("TD-BYTES-256", "memo.bin");
        string hash = Sha(evidence);
        _policy.IsProtectedPath = p => string.Equals(p, evidence, StringComparison.OrdinalIgnoreCase);
        _policy.SetEnabled(true);
        using var doc = new Document(FileByteSource.Open(memo), DocumentAssert.Options());
        doc.Overwrite(0, [0x55]);

        // 証拠のファイルへの「名前を付けて保存」・書き出し (SafeFileWriter)・元の形式での保存 (EncodedFile)。
        Assert.Throws<WriteBlockedException>(() => SavePlanner.Execute(SavePlanner.Plan(doc, evidence, Settings())));
        Assert.Throws<WriteBlockedException>(() => SafeFileWriter.Write(evidence, s => s.WriteByte(1)));
        Assert.Throws<WriteBlockedException>(() => EncodedFile.Save(doc.Current, 0, new EncodedFileSettings { Format = FormatIds.IntelHex }, evidence));
        Assert.Equal(hash, Sha(evidence));

        // ファイルの分割の出力先が証拠なら書かない。
        string splitSource = Copy("TD-SEQ-1M", "split.bin");
        _policy.IsProtectedPath = p => string.Equals(p, evidence, StringComparison.OrdinalIgnoreCase)
            || (Path.GetFileName(p).StartsWith("split", StringComparison.Ordinal) && !string.Equals(p, splitSource, StringComparison.OrdinalIgnoreCase));
        using (var reader = new FileContentReader(splitSource))
        {
            SplitPlan plan = FileSplitter.Plan(reader.Length, "split.bin", _dir, new SplitOptions { Mode = SplitMode.Count, Count = 2 });
            SplitResult split = FileSplitter.Execute(plan, reader, new SplitOptions { Mode = SplitMode.Count, Count = 2 });
            Assert.IsType<WriteBlockedException>(split.Error?.InnerException);
            Assert.Empty(split.Completed);
            Assert.False(File.Exists(plan.PathOf(plan.Parts[0])));
        }

        // 保護の対象でない場所へは書ける (アプリ自身の書き込み・書き出し。仕様 6)。
        string output = Path.Combine(_dir, "export.txt");
        SafeFileWriter.Write(output, s => s.WriteByte(1));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task Replacing_in_files_does_not_touch_the_files()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_dir, "tree")).FullName;
        string file = Path.Combine(folder, "replace.bin");
        File.WriteAllBytes(file, [0, 1, 0xCA, 0xFE, 0xBA, 0xBE, 2, 3]);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            string hash = Sha(file);
            SearchPattern marker = SearchPattern.FromHex("CA FE BA BE");
            var results = new MultiFileSearchResults(marker, new MultiFileTargets { Folders = [folder] });
            await MultiFileSearch.RunAsync(results, new SearchOptions(), null, 1);
            FileSearchResult found = Assert.Single(results.Files);
            _policy.SetEnabled(true);
            FileReplaceOutcome outcome = MultiFileSearch.ReplaceInFile(found, found.Matches, marker, new MultiFileReplaceOptions
            {
                Template = ReplacementTemplate.FromHex("11 22 33 44"),
                JournalDirectory = Path.Combine(_dir, "journal"),
                ClearReadOnly = true,
            });
            Assert.Equal(FileReplaceStatus.Failed, outcome.Status);
            Assert.Equal(hash, Sha(file));
            Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_read_only_parent_refuses_edits_from_linked_views()
    {
        // 派生・連動ビューからの書き戻しは親のドキュメントの編集になる。モードで読み取り専用にした親は受け付けない (EDIT-16 の仕様 2)。
        using var parent = new Document(new MemoryByteSource(new byte[256], "parent", SourceCapabilities.CanWrite), DocumentAssert.Options());
        using Document view = parent.CreateLinkedView(0x10, 0x20);
        parent.SetReadOnly(ReadOnlyReason.WriteProtectionMode);
        Assert.Equal(ReadOnlyRelease.NotAllowed, parent.ReadOnlyRelease);
        Assert.ThrowsAny<InvalidOperationException>(() => view.Overwrite(0, [0xFF]));
        Assert.Equal(0, DocumentAssert.Read(parent.Current, 0x10, 1)[0]);
    }

    // ---- デバイス ----

    private static FakeDeviceSpec DiskSpec() => new()
    {
        Elevated = true,
        Disks = [new FakeDiskSpec { Number = 2, SectorSize = 512, Size = 8 * 1024 * 1024, Serial = "WB0001", Bus = DiskBusType.Sata }],
        Volumes = [new FakeVolumeSpec { Path = @"\\.\Y:", DriveLetter = "Y:", Disk = 2, Offset = 1024 * 1024, Size = 4 * 1024 * 1024 }],
    };

    private static string DiskHash(FakeDeviceAccess access)
    {
        byte[] all = new byte[8 * 1024 * 1024];
        access.Disk(2).Read(0, all);
        return Convert.ToHexString(SHA256.HashData(all));
    }

    [Fact]
    public void Device_documents_and_disk_writes_are_refused_before_any_lock()
    {
        using var access = new FakeDeviceAccess(DiskSpec());
        string before = DiskHash(access);
        IDeviceHandle handle = access.Open(DevicePath.PhysicalDrive(2), writable: true);
        DiskInfo disk = access.Enumerate().Disks[0];
        using var source = new DeviceByteSource(handle, access, new DeviceOpenInfo
        {
            Path = handle.Path, DisplayName = "Disk 2", SerialNumber = "WB0001", Disk = disk, Route = DeviceRoute.Elevated,
        });
        using var doc = new Document(source);
        doc.Overwrite(1024 * 1024 + 4, [0xFF]);
        _policy.SetEnabled(true);
        access.ClearCalls();

        DiskWritePlan plan = DiskWrite.Plan(doc, source, access.Enumerate());
        Assert.Throws<WriteBlockedException>(() => DiskWrite.Execute(plan, doc.Current, Path.Combine(_dir, "journal")));
        Assert.Throws<WriteBlockedException>(() => source.Write(0, new byte[512]));
        Assert.DoesNotContain(access.Calls, c => c.StartsWith("Write ", StringComparison.Ordinal) || c.StartsWith("Lock", StringComparison.Ordinal) || c.StartsWith("Dismount", StringComparison.Ordinal));
        Assert.Equal(before, DiskHash(access));
    }

    [Fact]
    public void The_device_route_refuses_writable_opens_writes_locks_and_dismounts_but_reads()
    {
        using var fake = new FakeDeviceAccess(DiskSpec());
        string before = DiskHash(fake);
        var access = new WriteBlockedDeviceAccess(fake);
        using IDeviceHandle rw = access.Open(@"\\.\Y:", writable: true);
        _policy.SetEnabled(true);

        // 開いた後でモードを有効にしても、その時点から拒否する。
        Assert.Equal(Win32Errors.WriteProtect, rw.WriteSectors(0, new byte[512]));
        Assert.Equal(Win32Errors.WriteProtect, rw.LockVolume());
        Assert.Equal(Win32Errors.WriteProtect, rw.DismountVolume());
        DeviceException open = Assert.Throws<DeviceException>(() => access.Open(DevicePath.PhysicalDrive(2), writable: true));
        Assert.Equal(Win32Errors.WriteProtect, open.ErrorCode);

        using IDeviceHandle ro = access.Open(DevicePath.PhysicalDrive(2), writable: false);
        byte[] sector = new byte[512];
        Assert.Equal(0, ro.ReadSectors(0, sector));
        Assert.Equal(0, ((HexEditor.Core.Devices.Info.IDeviceInfoSource)ro).QueryDeviceInfo(DeviceInfoKind.IdentifyDevice, out byte[] identify));
        Assert.NotEmpty(identify);
        Assert.Equal(before, DiskHash(fake));
        Assert.Same(fake, WriteBlockedDeviceAccess.Unwrap(access));
    }

    [Fact]
    [Trait("TC", "TC-FOR-01-05")]
    public async Task The_helper_refuses_write_requests_after_SetWriteBlock_and_the_disk_is_unchanged()
    {
        // 補助プロセスの処理 (PrivilegedOperations) を偽のディスクに対して走らせ、パイプの代わりの双方向ストリームで要求を送る。
        // 実機の補助プロセスと仮想ディスクでの同じ確認は、CI の管理者のランナーの ElevatedHelperIntegrationTests にある。
        var spec = DiskSpec();
        (Stream a, Stream b) = DuplexStreams.CreatePair();
        using var devices = new FakeDeviceAccess(spec);
        string before = DiskHash(devices);
        var usb = new FakeUsbWriteProtectStore();
        var operations = new PrivilegedOperations(devices, new FakeProcessAccess(new FakeProcessListSpec { Elevated = true }), null, "1.0.0", usb);
        var server = new HelperServer(b, operations) { IdleTimeout = null };
        Task serverTask = server.RunAsync();
        await using var client = new HelperClient(a) { Timeout = TimeSpan.FromSeconds(10) };
        Assert.Equal(0u, (await client.SendAsync(HelperCommand.Hello, 0, new BodyWriter().U32(HelperProtocol.Version).Text("1.0.0").ToArray())).Status);
        Assert.Equal(0u, (await client.SendAsync(HelperCommand.SetWriteBlock, 0, new byte[] { 1 })).Status);
        Assert.True(operations.WriteBlock);

        // 1. 読み取りのみの OpenDevice と ReadSectors (LBA 0)。
        (uint status, byte[] body) = await client.SendAsync(HelperCommand.OpenDevice, 0, new BodyWriter().Text(DevicePath.PhysicalDrive(2)).ToArray());
        Assert.Equal(0u, status);
        uint handle = new BodyReader(body).U32();
        Assert.Equal(0u, (await client.SendAsync(HelperCommand.ReadSectors, 0, new BodyWriter().U32(handle).I64(0).U32(512).ToArray())).Status);

        // 2. 読み書きの OpenDevice。3. WriteSectors (LBA 100、FF × 512)。
        Assert.NotEqual(0u, (await client.SendAsync(HelperCommand.OpenDevice, HelperProtocol.FlagWritable, new BodyWriter().Text(DevicePath.PhysicalDrive(2)).ToArray())).Status);
        byte[] ff = Enumerable.Repeat((byte)0xFF, 512).ToArray();
        Assert.NotEqual(0u, (await client.SendAsync(HelperCommand.WriteSectors, 0, new BodyWriter().U32(handle).I64(100 * 512).Bytes(ff).ToArray())).Status);

        // 4. ボリュームの LockVolume・DismountVolume。
        (status, body) = await client.SendAsync(HelperCommand.OpenDevice, 0, new BodyWriter().Text(@"\\.\Y:").ToArray());
        Assert.Equal(0u, status);
        uint volume = new BodyReader(body).U32();
        Assert.NotEqual(0u, (await client.SendAsync(HelperCommand.LockVolume, 0, new BodyWriter().U32(volume).ToArray())).Status);
        Assert.NotEqual(0u, (await client.SendAsync(HelperCommand.DismountVolume, 0, new BodyWriter().U32(volume).ToArray())).Status);

        // 5. GetUsbWriteProtect は成功する (OS の USB 書き込み保護の取得・切り替えはモード中も受け付ける)。
        (status, body) = await client.SendAsync(HelperCommand.GetUsbWriteProtect, 0, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(0u, status);
        Assert.Equal(HelperProtocol.UsbWriteProtectDelete, new BodyReader(body).U32());
        Assert.Equal(0u, (await client.SendAsync(HelperCommand.SetUsbWriteProtect, 0, new BodyWriter().U32(1).ToArray())).Status);
        Assert.Equal(1u, usb.Value);

        // 読み取り専用のデバイス情報の取得も受け付ける。
        Assert.Equal(0u, (await client.SendAsync(HelperCommand.QueryDeviceInfo, 0, new BodyWriter().U32(handle).U16((ushort)DeviceInfoKind.IdentifyDevice).ToArray())).Status);

        // 6. ディスク全体の SHA-256 が変わらない。
        Assert.Equal(before, DiskHash(devices));
        Assert.DoesNotContain(devices.Calls, c => c.StartsWith("Write ", StringComparison.Ordinal) || c.Contains(" rw", StringComparison.Ordinal));
        Assert.DoesNotContain(devices.LockedVolumes, v => v.Length > 0);
    }

    // ---- プロセスメモリ ----

    [Fact]
    public void Process_memory_writes_protect_changes_and_writable_opens_are_refused()
    {
        const int pid = 4242;
        var access = new FakeProcessAccess(new FakeProcessListSpec
        {
            Elevated = true,
            Processes = [new FakeProcessSpec { Pid = pid, Name = "dummy.exe", AddressLimit = 0x200000, Regions = [new FakeRegionSpec { Base = 0x10000, Size = 0x1000, Data = "11" }] }],
        });
        using var process = new ProcessMemoryByteSource(access.Open(pid, true), access, new ProcessOpenInfo { DisplayName = "dummy.exe" });
        using var doc = new Document(process);
        doc.Overwrite(0x10000, [0x22]);
        _policy.SetEnabled(true);

        Assert.Throws<WriteBlockedException>(() => ProcessWrite.Execute(doc, process, _ => true));
        Assert.Equal(Win32Errors.WriteProtect, process.TryWrite(0x10000, [0x33], out int written));
        Assert.Equal(0, written);
        ProcessAccessException open = Assert.Throws<ProcessAccessException>(() => new WriteBlockedProcessAccess(access).Open(pid, writable: true));
        Assert.Equal(Win32Errors.WriteProtect, open.ErrorCode);
        byte[] now = new byte[1];
        Assert.Equal(0, access.Open(pid, false).Read(0x10000, now, out _));
        Assert.Equal(0x11, now[0]);
    }

    // ---- リモート ----

    [Fact]
    public async Task Gdb_and_sftp_targets_are_not_written()
    {
        using var gdb = new FakeGdbServer();
        gdb.Write(0x1000, new byte[64]);
        gdb.Start();
        using (GdbByteSource source = await GdbByteSource.ConnectAsync(new GdbEndpoint { Host = "127.0.0.1", Port = gdb.Port, AddressBits = 64 }, writable: true))
        {
            using var doc = new Document(source);
            doc.Overwrite(0x1000, [0xCA]);
            _policy.SetEnabled(true);
            await Assert.ThrowsAsync<WriteBlockedException>(() => GdbWrite.ExecuteAsync(doc, source));
            await Assert.ThrowsAsync<WriteBlockedException>(() => source.WriteAsync(0x1000, new byte[] { 0xCB }));
            Assert.Equal(0, gdb.Read(0x1000, 1)[0]);
        }

        _policy.SetEnabled(false);
        string settings = Directory.CreateDirectory(Path.Combine(_dir, "sftp")).FullName;
        var server = new FakeSftpServer(new FakeSftpSpec { Passwords = new() { ["tester"] = "pw" }, KeyUsers = ["tester"] });
        server.AddFile("/data/a.bin", 4096, 7);
        var endpoint = new SftpEndpoint { Host = "localhost", Port = 22, User = "tester" };
        var hosts = new KnownHosts(Path.Combine(settings, KnownHosts.FileName));
        hosts.Trust(endpoint.Host, endpoint.Port, server.HostKey, DateTime.UtcNow);
        using SftpConnection connection = await SftpConnection.ConnectAsync(endpoint, new SftpCredentials { Method = SftpAuthMethod.KeyFile, KeyFile = @"C:\keys\id" }, server, hosts);
        SftpByteSource sftp = await SftpByteSource.OpenAsync(connection, "/data/a.bin");
        using var remote = new Document(sftp);
        byte[] first = new byte[1];
        remote.Current.Read(0, first);
        remote.Overwrite(0, [(byte)(first[0] ^ 0xFF)]);
        _policy.SetEnabled(true);
        SftpSavePlan plan = SftpSaver.Plan(remote, sftp);
        await Assert.ThrowsAsync<WriteBlockedException>(() => SftpSaver.WriteChangedRangesAsync(remote, sftp, plan));
        await Assert.ThrowsAsync<WriteBlockedException>(() => SftpSaver.ReplaceAsync(remote, sftp));
        Assert.Equal(4096, server.LengthOf("/data/a.bin"));
        Assert.Equal(first, server.ReadFile("/data/a.bin", 0, 1));
    }

    // ---- プラグインのデータソース ----

    private sealed class WritableProvider : IByteSourceProvider
    {
        public byte[] Data { get; } = new byte[64];

        public string Id => "test.writable";

        public string DisplayName => "writable";

        public bool IsReadOnly => false;

        public bool CanResize => false;

        public IPluginByteSource Open(string? argument) => new Source(Data);

        private sealed class Source(byte[] data) : IPluginByteSource
        {
            public string DisplayName => "plugin data";

            public long Length => data.Length;

            public int Read(long offset, Span<byte> buffer)
            {
                int n = (int)Math.Max(0, Math.Min(buffer.Length, data.Length - offset));
                data.AsSpan((int)offset, n).CopyTo(buffer);
                return n;
            }

            public void Write(long offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(data.AsSpan((int)offset));

            public void Dispose()
            {
            }
        }
    }

    private sealed class GuardHost : IPluginGuardHost
    {
        public DateTimeOffset Now => DateTimeOffset.UtcNow;

        public void Write(PluginRecord plugin, HexEditor.Core.Scripting.ScriptOutputKind kind, string text)
        {
        }

        public void Suspend(PluginRecord plugin, string reason)
        {
        }
    }

    [Fact]
    public void Plugin_data_sources_are_not_written()
    {
        var provider = new WritableProvider();
        var record = new PluginRecord("test", _dir, null, development: false);
        record.Guard = new PluginGuard(record, new GuardHost());
        var registration = new RegisteredExtension(record, provider, provider.Id, "byteSource", provider.DisplayName);
        using PluginByteSource source = PluginByteSource.Open(registration, provider, null);
        _policy.SetEnabled(true);
        Assert.Throws<WriteBlockedException>(() => source.Write(0, [0xFF]));
        Assert.Equal(0, provider.Data[0]);
    }

    // ---- ファイルのツール ----

    [Fact]
    public void Secure_delete_and_alternate_stream_changes_are_refused()
    {
        string file = Copy("TD-BYTES-256", "keep.bin");
        string hash = Sha(file);
        _policy.SetEnabled(true);
        WriteBlockedException shred = Assert.Throws<WriteBlockedException>(() =>
            SecureDelete.Execute([file], ShredMethod.ZeroOnce, false, WindowsShredFileSystem.Instance));
        Assert.Equal(BlockedOperation.SecureDelete, shred.Operation);
        Assert.True(File.Exists(file));
        Assert.Equal(hash, Sha(file));

        string source = Path.Combine(_dir, "import.bin");
        File.WriteAllBytes(source, [1, 2, 3]);
        Assert.Throws<WriteBlockedException>(() => AlternateStreams.Create(file, "added"));
        Assert.Throws<WriteBlockedException>(() => AlternateStreams.Delete(file, "Zone.Identifier"));
        Assert.Throws<WriteBlockedException>(() => AlternateStreams.Rename(file, "a", "b"));
        Assert.Throws<WriteBlockedException>(() => AlternateStreams.Import(file, "added", source));
        Assert.Throws<WriteBlockedException>(() => AlternateStreams.Copy(source, string.Empty, file, "copied"));
        Assert.Equal(hash, Sha(file));
        if (AlternateStreams.IsSupported(file))
        {
            Assert.Single(AlternateStreams.List(file));
        }
    }

    // ---- スクリプト ----

    [Fact]
    public async Task Scripts_cannot_write_protected_files_even_when_picked()
    {
        string evidence = Copy("TD-BYTES-256", "evidence.bin");
        string hash = Sha(evidence);
        _policy.IsProtectedPath = p => string.Equals(p, evidence, StringComparison.OrdinalIgnoreCase);
        _policy.SetEnabled(true);
        using var t = new ScriptTestHost();
        string other = Path.Combine(t.Root, "note.txt");
        int picks = 0;
        t.Ui.PickAnswer = kind => kind == "save" ? [picks++ == 0 ? evidence : other] : null;
        ScriptRunResult r = await t.RunCodeAsync("""
            const p = await ui.saveFile();
            try { fs.writeText(p, 'x'); console.log('written'); } catch (e) { console.log(e.name); }
            const q = await ui.saveFile();
            fs.writeText(q, 'note');
            console.log('ok');
            """);
        ScriptTestHost.AssertSucceeded(r);
        Assert.NotEqual("written", t.OutputText[0]);
        Assert.Equal("ok", t.OutputText[1]);
        Assert.Equal(hash, Sha(evidence));
        Assert.Equal("note", File.ReadAllText(other));
    }

    // ---- 監査ログ (FOR-01 の仕様 4・5、FOR-04 の仕様 8) ----

    [Fact]
    [Trait("TC", "TC-FOR-01-09")]
    public void Exceptions_and_disabling_are_recorded_with_time_operator_and_target()
    {
        var time = new FixedTime(new DateTime(2026, 10, 7, 3, 15, 42, DateTimeKind.Utc).AddTicks(1234567));
        string settings = Directory.CreateDirectory(Path.Combine(_dir, "settings")).FullName;
        var audit = new ForensicAudit(new AuditIdentity("tester", "TESTPC", "1.0.0"), time);
        audit.SetGlobalLogFolder(settings);
        var cases = new CaseManager(audit, "tester", time);
        string folder = Path.Combine(_dir, "case");
        cases.Create(folder, new CaseInfo { Number = "C-0001", Name = "テストケース", Investigator = "tester" });
        var service = new WriteBlockService(_policy, () => audit);
        string memo = Copy("TD-BYTES-256", "memo.bin");
        Assert.True(service.Enable("menu"));

        // 1. 例外を設定する。2. 閉じる (例外の解除)。3. 確認ダイアログで解除する。
        Assert.True(service.AddException(memo));
        Assert.True(service.RemoveException(memo));
        Assert.True(service.Disable([]));

        // 4. ケースの audit.log の行。
        List<AuditEntry> entries = ReadEntries(Path.Combine(folder, "audit.log"));
        foreach (string op in new[] { AuditOperations.WriteBlockExceptionSet, AuditOperations.WriteBlockExceptionClear, AuditOperations.WriteBlockDisable })
        {
            AuditEntry e = Assert.Single(entries, x => x.Operation == op);
            Assert.Equal("2026-10-07T03:15:42.1234567Z", e.Time);
            Assert.Contains(e.User, new[] { "tester", Environment.UserName });
            if (op != AuditOperations.WriteBlockDisable)
            {
                Assert.Equal(Path.GetFullPath(memo), e.Path);
            }
        }

        // 5. ケースを閉じ、有効にしてから解除する: 設定フォルダの forensics-audit.log に 2 行。
        cases.Close();
        Assert.True(service.Enable("menu"));
        Assert.True(service.Disable([]));
        List<AuditEntry> global = ReadEntries(Path.Combine(settings, ForensicAudit.GlobalFileName));
        Assert.Equal([AuditOperations.WriteBlockEnable, AuditOperations.WriteBlockDisable], global.Select(e => e.Operation));
        Assert.All(global, e => Assert.Equal("2026-10-07T03:15:42.1234567Z", e.Time));
        Assert.All(global, e => Assert.Equal("tester", e.User));
    }

    private static List<AuditEntry> ReadEntries(string path) =>
        [.. File.ReadAllLines(path, Encoding.UTF8).Where(l => l.Length > 0).Select(l => AuditLog.TryParse(l, out AuditEntry? e) ? e! : throw new InvalidDataException(l))];
}
