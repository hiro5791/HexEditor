using HexEditor.App.Hosting;
using HexEditor.App.ViewModels;
using Microsoft.UI.Xaml;

namespace HexEditor.App.Services;

#if HEX_TEST_HOOKS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Recovery;
using HexEditor.Core.Sources;

/// <summary>
/// 異常を再現する仕組み (テスト方針 7.2)。テスト用のビルド (HEX_TEST_HOOKS) だけに入り、
/// コマンドラインの <c>--test-hooks &lt;設定ファイル&gt;</c> (8.3) とテスト用のメニュー (8.4) で有効にする。
/// 設定ファイルの書式は <see cref="TestHookSettings"/>。
/// </summary>
public static class TestHooks
{
    private static readonly object Lock = new();
    private static readonly HashSet<LongRunningOperation> Watched = new(ReferenceEqualityComparer.Instance);
    private static int _startupThrown;

    /// <summary>true なら --test-hooks で起動した (テスト用の命令の通り道を開き、ウィンドウを前面に出さない)。</summary>
    public static bool Active { get; private set; }

    public static TestHookSettings Settings { get; private set; } = new();

    /// <summary>管理者として実行している扱い (テスト用の設定 elevated。TC-UI-02-02)。</summary>
    public static bool SimulatesElevation => Active && Settings.Elevated;

    /// <summary>設定ファイルのパス (--test-hooks の値)。</summary>
    public static string? SettingsPath { get; private set; }

    /// <summary>--test-profile の値。</summary>
    public static string? TestProfile { get; private set; }

    /// <summary>true ならウィンドウをアクティブにしない (起動時・転送された起動のどちらも)。</summary>
    public static bool SuppressActivation => Active && Settings.NoActivate;

    /// <summary>時刻の固定 (7.2)。固定しないときは null (既定の時計)。</summary>
    public static TimeProvider? Time => Settings.FrozenTime is { } t ? new FrozenTimeProvider(t) : null;

    /// <summary>起動の最初 (Program.Main) に呼ぶ。設定ファイルを読む。</summary>
    public static void Initialize(IReadOnlyList<string> args, CommandLine commandLine)
    {
        TestProfile = commandLine.TestProfile;
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == "--test-hooks")
            {
                SettingsPath = Path.GetFullPath(args[i + 1]);
            }
        }

        if (SettingsPath is null)
        {
            return;
        }

        Active = true;

        // 異常終了のテストで Windows のエラー報告の画面 (WerFault) を出さない (前面に出て作業の邪魔になるため)。
        SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox);
        _ = WerSetFlags(WerFaultReportingNoUi);
        try
        {
            Settings = TestHookSettings.Parse(File.ReadAllText(SettingsPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            // 設定が読めなくてもテスト用の命令の通り道は開く (原因はログで分かる)。
            AppLog.Error($"Test hooks: cannot read {SettingsPath}: {ex.Message}");
        }

        if (Settings.AnsiCodePage is { } codePage)
        {
            Core.View.TextEncoding.UseAnsiCodePage(codePage);
        }

        AppLog.Info("Test hooks enabled");

        // 地域設定の上書き (7.2)。OS の設定は変えず、このプロセスの数値・日付の書式だけを変える。
        if (Settings.Culture is { Length: > 0 } culture)
        {
            var info = new System.Globalization.CultureInfo(culture);
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = info;
            System.Globalization.CultureInfo.CurrentCulture = info;
            AppLog.Info($"Test hooks: culture {culture}");
        }
    }

    // ---- ダイアログ・外部の起動の差し替え (前面に出るものをテストで出さない) ----

    /// <summary>「開く」のダイアログの代わりに返すファイル。null なら本物のダイアログを出す。</summary>
    public static IReadOnlyList<string>? OpenPickerResult(string settingsIdentifier)
    {
        if (!Active || Settings.OpenPicker is not { } paths)
        {
            return null;
        }

        AppLog.Info($"Test hooks: open picker ({settingsIdentifier}) -> {paths.Count} file(s)");
        return paths;
    }

    /// <summary>
    /// 「名前を付けて保存」のダイアログの代わりの結果。差し替えるなら true で、<paramref name="path"/> は選んだパス (キャンセルは null)。
    /// </summary>
    public static bool TrySavePicker(string suggestedName, out string? path)
    {
        path = null;
        if (!Active || Settings.SavePicker is not { } chosen)
        {
            return false;
        }

        AppLog.Info($"Test hooks: save picker ({suggestedName}) -> {(chosen.Length == 0 ? "cancel" : chosen)}");
        path = chosen.Length == 0 ? null : chosen;
        return true;
    }

    /// <summary>ブラウザなどの起動の代わりにログに書く (テスト中に他のアプリを前面に出さない)。差し替えたら true。</summary>
    public static bool InterceptLaunch(Uri uri)
    {
        if (!Active)
        {
            return false;
        }

        AppLog.Info($"Test hooks: launch {uri.AbsoluteUri}");
        return true;
    }

    /// <summary>空き容量を上書きしたボリュームの情報 (ENG-25)。上書きしないときは null。</summary>
    public static Core.Saving.IVolumeInfoProvider? Volumes =>
        Active && Settings.FreeSpace is { } free ? new FixedFreeSpaceVolumes(free) : null;

    /// <summary>
    /// 開くときの書き込みの確認 (ENG-14 の仕様 1) の差し替え: 設定 writeErrors に合うファイルでは、権限がない・共有違反・書き込み禁止の
    /// メディアのエラーを起こす。合わなければ null (本物の確認)。
    /// </summary>
    public static Func<string, Microsoft.Win32.SafeHandles.SafeFileHandle>? OpenForWrite =>
        Active && Settings.WriteErrors.Count > 0 ? OpenForWriteWithErrors : null;

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenForWriteWithErrors(string path)
    {
        if (Settings.WriteErrorFor(path) is { } spec)
        {
            AppLog.Info($"Test hooks: write probe {spec.Error} ({Path.GetFileName(path)})");
            throw spec.Error.ToUpperInvariant() switch
            {
                "SHARINGVIOLATION" => new IOException("Sharing violation (test hooks).", unchecked((int)0x80070020)),
                "WRITEPROTECT" => new IOException("The media is write protected (test hooks).", unchecked((int)0x80070013)),
                _ => new UnauthorizedAccessException("Access denied (test hooks)."),
            };
        }

        return File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    private sealed class FixedFreeSpaceVolumes(long free) : Core.Saving.IVolumeInfoProvider
    {
        public Core.Saving.VolumeInfo? GetVolume(string folder) =>
            Core.Saving.SystemVolumeInfoProvider.Instance.GetVolume(folder) is { } volume ? volume with { AvailableFreeSpace = free } : null;
    }

    /// <summary>偽のディスク (ENG-29 の UI テスト)。直接の経路は管理者権限なしを再現し、昇格の経路は管理者権限ありを再現する。</summary>
    public static FakeDeviceAccessPair? FakeDevices =>
        Active && Settings.FakeDevices is { Length: > 0 } path ? FakeDeviceAccessPair.Load(path) : null;

    /// <summary>偽のプロセス (ENG-32 の UI テスト)。</summary>
    public static FakeProcessAccessPair? FakeProcesses =>
        Active && Settings.FakeProcesses is { Length: > 0 } path ? FakeProcessAccessPair.Load(path) : null;

    /// <summary>
    /// 単一インスタンスのキー (UI-15)。--test-profile を指定したときは、設定フォルダごとに別のインスタンスにする
    /// (同じ実行ファイルの普段使いのインスタンスや、並行して走るほかのテストに転送しないため)。
    /// </summary>
    public static string AdjustInstanceKey(string key)
    {
        if (TestProfile is null)
        {
            return key;
        }

        string full = Path.GetFullPath(TestProfile).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        return key + "-Test-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..8];
    }

    /// <summary>App.OnLaunched の最初に呼ぶ。ウィンドウを作る前の設定 (復旧用データの保存間隔など)。</summary>
    public static void BeforeLaunch()
    {
        // 設定ファイルの書き込みの途中での強制終了 (TC-UI-23-05)。
        if (Settings.KillAt is KillPoint.SettingsTemp or KillPoint.SettingsBeforeReplace or KillPoint.SettingsAfterReplace)
        {
            Core.Settings.SettingsStore.WriteHook = point =>
            {
                if ((point, Settings.KillAt) is (Core.Settings.SettingsWritePoint.TempHalfWritten, KillPoint.SettingsTemp)
                    or (Core.Settings.SettingsWritePoint.BeforeReplace, KillPoint.SettingsBeforeReplace)
                    or (Core.Settings.SettingsWritePoint.AfterReplace, KillPoint.SettingsAfterReplace))
                {
                    Kill("settings " + point);
                }
            };
        }

        if (Settings.RecoveryInterval is { } interval)
        {
            App.RecoveryInterval = interval;
        }
    }

    /// <summary>
    /// 起動時に開くファイルのうち、遅延・読み込みエラーを指定したものは包んで開き、仮想のデータソースを開く。
    /// 残りのファイル (普通に開くもの) を返す。
    /// </summary>
    public static CommandLine OpenStartupSources(MainViewModel vm, CommandLine commandLine)
    {
        if (!Active)
        {
            return commandLine;
        }

        var rest = new List<string>();
        foreach (string file in commandLine.Files)
        {
            if (Settings.FileSourceFor(file) is { } spec)
            {
                try
                {
                    OpenFile(vm, file, spec);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error($"Test hooks: cannot open: {ex.Message}");
                }
            }
            else
            {
                rest.Add(file);
            }
        }

        foreach (VirtualSourceSpec spec in Settings.VirtualSources)
        {
            OpenVirtual(vm, spec);
        }

        return commandLine with { Files = rest };
    }

    /// <summary>
    /// ウィンドウを前面に出さずに表示する。表示したら true (呼び出し側は Activate しない)。
    /// WS_EX_NOACTIVATE を付けてから Activate する: Activate しないとタブの中身が読み込まれず、付けないと XAML の
    /// フォーカスの移動 (Focus) でウィンドウがアクティブになり、作業中の利用者のキー入力を奪ってしまう。
    /// </summary>
    public static bool ShowWithoutActivation(Window window)
    {
        if (!SuppressActivation)
        {
            return false;
        }

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SetWindowLongPtr(hwnd, GwlExStyle, GetWindowLongPtr(hwnd, GwlExStyle) | WsExNoActivate);
        window.Activate();

        // 作業中のウィンドウを覆わないよう、一番後ろに回す。
        SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        return true;
    }

    /// <summary>ウィンドウを表示した後に呼ぶ。テスト用のメニュー・命令の通り道・保存の異常の再現をつなぐ。</summary>
    public static void OnLaunched(MainWindow window, MainViewModel vm)
    {
        window.AttachTestMenu();
        if (!Active)
        {
            return;
        }

        vm.Operations.Changed += (_, _) => WatchOperations(vm.Operations);
        TestSavePoints.AfterJournalWritten = () =>
        {
            if (Settings.KillAt == KillPoint.InPlaceAfterJournal)
            {
                Kill("in-place save after journal");
            }
        };

        // 命令はウィンドウを指定できる (複数ウィンドウ。UI-14)。指定がなければ最後にアクティブだったウィンドウ。
        TestChannel.Start(WindowManager.HandleTestCommandAsync);

        if (Settings.UnhandledException is { } place && Interlocked.Exchange(ref _startupThrown, 1) == 0
            && place != ExceptionPlace.Save)
        {
            var timer = App.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Settings.UnhandledExceptionDelayMs));
            timer.IsRepeating = false;
            timer.Tick += (_, _) => Throw(place);
            timer.Start();
        }
    }

    /// <summary>2 つ目以降のウィンドウを作った (テスト用のメニューを付ける)。</summary>
    public static void OnWindowCreated(MainWindow window) => window.AttachTestMenu();

    // ---- データソース ----

    /// <summary>ファイルを、遅延・読み込みエラーを加えたデータソースで開く (7.2「遅いデータソース」「読み込みエラー」)。</summary>
    public static DocumentViewModel OpenFile(MainViewModel vm, string path, FileSourceSpec spec)
    {
        string full = Path.GetFullPath(path);
        var source = new FaultyByteSource(FileByteSource.Open(full))
        {
            Delay = TimeSpan.FromMilliseconds(spec.DelayMs),
            DelayFromOffset = spec.DelayFromOffset,
        };
        foreach ((long offset, long length) in spec.ReadErrors)
        {
            source.AddReadError(offset, length);
        }

        return AddDocument(vm, new Document(source, vm.DocumentOptions), full, Path.GetFileName(full));
    }

    /// <summary>
    /// 挿入・塗りつぶしの内容として読むファイル (EDIT-30)。設定の fileSources に当てはまれば、遅延・読み込みエラーを加えたデータソースで開く
    /// (TC-EDIT-30-03 の遅いデータソース)。
    /// </summary>
    public static IByteSource OpenContentSource(string path)
    {
        if (!Active || Settings.FileSourceFor(path) is not { } spec)
        {
            return FileByteSource.Open(path);
        }

        var source = new FaultyByteSource(FileByteSource.Open(path))
        {
            Delay = TimeSpan.FromMilliseconds(spec.DelayMs),
            DelayFromOffset = spec.DelayFromOffset,
        };
        foreach ((long offset, long length) in spec.ReadErrors)
        {
            source.AddReadError(offset, length);
        }

        return source;
    }

    /// <summary>仮想のデータソース (7.2) を新しいタブで開く。</summary>
    public static DocumentViewModel OpenVirtual(MainViewModel vm, VirtualSourceSpec spec)
    {
        var inner = new VirtualByteSource(spec.Length, spec.Content, spec.Resizable, spec.Fill, spec.Seed, spec.Name);
        IByteSource source = inner;
        if (spec.DelayMs > 0 || spec.ReadErrors.Count > 0)
        {
            var faulty = new FaultyByteSource(inner) { Delay = TimeSpan.FromMilliseconds(spec.DelayMs), DelayFromOffset = spec.DelayFromOffset };
            foreach ((long offset, long length) in spec.ReadErrors)
            {
                faulty.AddReadError(offset, length);
            }

            source = faulty;
        }

        return AddDocument(vm, new Document(source, vm.DocumentOptions), null, spec.Name);
    }

    /// <summary>MainViewModel の新規作成・開くと同じ手順でタブを加える (復旧用データの準備を含む)。</summary>
    private static DocumentViewModel AddDocument(MainViewModel vm, Document doc, string? path, string name)
    {
        DocumentRecovery? recovery = null;
        try
        {
            recovery = new DocumentRecovery(vm.RecoveryRoot, doc.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Recovery folder unavailable: {ex.Message}");
        }

        var document = new DocumentViewModel(doc, path, name) { Recovery = recovery, Notifications = vm.Notifications };
        vm.Memory.Register(doc);
        vm.Documents.Add(document);
        vm.Selected = document;
        return document;
    }

    // ---- 保存の異常 (7.2「書き込みエラー・空き容量不足」「強制終了」) ----

    private static void WatchOperations(OperationCenter center)
    {
        foreach (LongRunningOperation op in center.Active)
        {
            lock (Lock)
            {
                if (!Watched.Add(op))
                {
                    continue;
                }
            }

            if (op.Kind == OperationKind.WritesExternal)
            {
                op.ProgressChanged += (_, _) => OnSaveProgress(op);
            }
        }

        // 終わった保存: 置き換えの直後 (Document.CompleteSave の前) に止める。
        foreach (LongRunningOperation op in center.History.Take(4))
        {
            bool first;
            lock (Lock)
            {
                first = Watched.Remove(op);
            }

            if (first && op.Kind == OperationKind.WritesExternal && op.State == OperationState.Completed
                && Settings.KillAt == KillPoint.SaveAfterReplace)
            {
                Kill("save after replace");
            }
        }
    }

    /// <summary>
    /// 保存の進捗の通知 (別スレッド)。保存処理は 4 MiB ごとに進捗を報告するため、指定の位置はその粒度で判定する
    /// (通知は 100 ms に 1 回までなので、指定の位置以降の最初の通知で起こす)。
    /// </summary>
    private static void OnSaveProgress(LongRunningOperation op)
    {
        long done = op.ProcessedBytes;
        if (done <= 0 || op.State != OperationState.Running)
        {
            return;
        }

        if (Settings.KillAt == KillPoint.SaveWrite
            || (Settings.KillAt == KillPoint.SaveBeforeReplace && op.TotalBytes is long total && done >= total))
        {
            Kill("save " + Settings.KillAt);
        }

        if (Settings.UnhandledException == ExceptionPlace.Save)
        {
            throw new TestHookException("save");
        }

        if (Settings.SaveFault is { } fault && done >= fault.AtByte)
        {
            if (fault.Once)
            {
                Settings = Settings with { SaveFault = null };
            }

            AppLog.Info($"Test hooks: save fault ({fault.Kind}) at {done}");
            throw fault.Kind == SaveFaultKind.DiskFull
                ? new IOException("There is not enough space on the disk. (test hook)", unchecked((int)0x80070070))
                : new IOException("The request could not be performed because of an I/O device error. (test hook)", unchecked((int)0x8007045D));
        }
    }

    /// <summary>プロセスをその場で終える (TerminateProcess。終了の処理は何も行わない)。</summary>
    public static void Kill(string point)
    {
        AppLog.Info($"Test hooks: kill at {point}");
        Process.GetCurrentProcess().Kill();
    }

    // ---- 未処理の例外 (7.2) ----

    /// <summary>指定の場所で未処理の例外を起こす。</summary>
    public static void Throw(ExceptionPlace place)
    {
        AppLog.Info($"Test hooks: throw ({place})");
        switch (place)
        {
            case ExceptionPlace.UiThread:
                // XAML のタイマーで起こす (DispatcherQueue の処理の中の例外は Application.UnhandledException に来ないため)。
                App.DispatcherQueue.TryEnqueue(() =>
                {
                    var timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        throw new TestHookException("UI thread");
                    };
                    timer.Start();
                });
                break;
            case ExceptionPlace.Background:
                new Thread(() => throw new TestHookException("background thread")) { IsBackground = true }.Start();
                break;
            case ExceptionPlace.UnobservedTask:
                _ = Task.Run(() => throw new TestHookException("unobserved task"));
                Task.Run(async () =>
                {
                    await Task.Delay(200);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                });
                break;
            case ExceptionPlace.Save:
                Settings = Settings with { UnhandledException = ExceptionPlace.Save };
                break;
        }
    }

    /// <summary>実行中に設定を変える (テスト用のメニュー・命令の通り道から)。</summary>
    public static void Update(Func<TestHookSettings, TestHookSettings> change) => Settings = change(Settings);

    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;
    private const uint WerFaultReportingNoUi = 0x0020;

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    private static extern int WerSetFlags(uint flags);

    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;
    private const nint HwndBottom = 1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtr(nint hWnd, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>時刻を固定した時計。経過時間の計測 (GetTimestamp) は実際の時計のまま。</summary>
    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}

/// <summary>テスト用に起こした例外。</summary>
public sealed class TestHookException(string place) : Exception($"Unhandled exception raised by the test hooks ({place}).");

/// <summary>偽のディスクの「直接」(管理者権限なし) と「昇格」(管理者権限あり) の 2 つのアクセス手段。同じディスクの内容を共有する。</summary>
public sealed class FakeDeviceAccessPair
{
    private static readonly Dictionary<string, FakeDeviceAccessPair> Cache = [];

    private FakeDeviceAccessPair(Core.Devices.FakeDeviceAccess shared)
    {
        Elevated = shared;
        Direct = new NonAdminDevices(shared);
    }

    public Core.Devices.IDeviceAccess Direct { get; }

    public Core.Devices.IDeviceAccess Elevated { get; }

    public static FakeDeviceAccessPair Load(string path)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(path, out FakeDeviceAccessPair? pair))
            {
                var spec = Core.Devices.FakeDeviceSpec.Parse(File.ReadAllText(path));
                pair = new FakeDeviceAccessPair(new Core.Devices.FakeDeviceAccess(spec, elevated: true));
                Cache[path] = pair;
            }

            return pair;
        }
    }

    /// <summary>管理者権限なしの経路: 管理者権限が要るデバイスはアクセス拒否にし、それ以外は共有のアクセスに委ねる。</summary>
    private sealed class NonAdminDevices(Core.Devices.FakeDeviceAccess shared) : Core.Devices.IDeviceAccess
    {
        public Core.Devices.DeviceCatalog Enumerate() => shared.Enumerate();

        public Core.Devices.IDeviceHandle Open(string path, bool writable)
        {
            bool requiresAdmin = shared.Spec.Disks.Any(d => string.Equals(Core.Devices.DevicePath.PhysicalDrive(d.Number), path, StringComparison.OrdinalIgnoreCase) && d.RequiresAdmin)
                || shared.Spec.Volumes.Any(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase) && v.RequiresAdmin);
            if (requiresAdmin)
            {
                throw Core.Devices.DeviceException.FromError(Core.Devices.Win32Errors.AccessDenied, path);
            }

            return shared.Open(path, writable);
        }
    }
}

/// <summary>偽のプロセスの「直接」と「昇格」の 2 つのアクセス手段。</summary>
public sealed class FakeProcessAccessPair
{
    private static readonly Dictionary<string, FakeProcessAccessPair> Cache = [];

    private FakeProcessAccessPair(Core.Processes.FakeProcessListSpec spec)
    {
        Direct = new Core.Processes.FakeProcessAccess(spec, elevated: false);
        Elevated = new Core.Processes.FakeProcessAccess(spec, elevated: true);
    }

    public Core.Processes.IProcessAccess Direct { get; }

    public Core.Processes.IProcessAccess Elevated { get; }

    public static FakeProcessAccessPair Load(string path)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(path, out FakeProcessAccessPair? pair))
            {
                pair = new FakeProcessAccessPair(Core.Processes.FakeProcessListSpec.Parse(File.ReadAllText(path)));
                Cache[path] = pair;
            }

            return pair;
        }
    }
}

#else

/// <summary>製品版では何もしない (異常を再現する仕組みはテスト用のビルドだけ。テスト方針 7.2)。</summary>
public static class TestHooks
{
    public static bool Active => false;

    public static bool SimulatesElevation => false;

    public static bool SuppressActivation => false;

    public static TimeProvider? Time => null;

    public static void Initialize(IReadOnlyList<string> args, CommandLine commandLine)
    {
    }

    public static string AdjustInstanceKey(string key) => key;

    public static void BeforeLaunch()
    {
    }

    public static CommandLine OpenStartupSources(MainViewModel vm, CommandLine commandLine) => commandLine;

    public static bool ShowWithoutActivation(Window window) => false;

    public static void OnLaunched(MainWindow window, MainViewModel vm)
    {
    }

    public static void OnWindowCreated(MainWindow window)
    {
    }

    public static IReadOnlyList<string>? OpenPickerResult(string settingsIdentifier) => null;

    public static bool TrySavePicker(string suggestedName, out string? path)
    {
        path = null;
        return false;
    }

    public static bool InterceptLaunch(Uri uri) => false;

    public static Core.Saving.IVolumeInfoProvider? Volumes => null;

    public static Func<string, Microsoft.Win32.SafeHandles.SafeFileHandle>? OpenForWrite => null;

    public static Core.Sources.IByteSource OpenContentSource(string path) => Core.Sources.FileByteSource.Open(path);
}
#endif
