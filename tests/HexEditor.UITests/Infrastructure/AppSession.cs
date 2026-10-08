using System.Diagnostics;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace HexEditor.UITests.Infrastructure;

/// <summary>起動の設定。</summary>
public sealed record AppOptions
{
    /// <summary>開くファイル (コマンドラインに渡す)。</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>設定フォルダ (--test-profile)。null ならテストごとに新しいフォルダを作る。</summary>
    public string? Profile { get; init; }

    /// <summary>表示言語 (--ui-lang)。既定は英語 (画面の文言で判定するテストのため)。</summary>
    public string? UiLanguage { get; init; } = "en";

    /// <summary>異常を再現する仕組みの設定 (--test-hooks の設定ファイルの内容。テスト方針 7.2)。</summary>
    public JsonObject? Hooks { get; init; }

    /// <summary>true なら --new-instance を付ける (単一インスタンスの転送をしない)。</summary>
    public bool NewInstance { get; init; } = true;

    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>true なら、選択中のタブの Hex ビューが表示されるまで待つ。</summary>
    public bool WaitForEditor { get; init; } = true;
}

/// <summary>
/// 起動した HexEditor 1 つ。テスト用の命令の通り道と UI オートメーションで操作する。
/// ウィンドウはアクティブにならないように起動し (テスト用のビルドの --test-hooks)、テスト中に前面に出たら失敗にする。
/// マウス・システムのキーボード・クリップボードは使わない。
/// </summary>
public sealed class AppSession : IAsyncDisposable
{
    private static readonly Lazy<UIA3Automation> SharedAutomation = new(() => new UIA3Automation());

    private readonly CancellationTokenSource _monitor = new();
    private Task? _monitorTask;

    private AppSession(Process process, string profile, TestChannelClient channel, nint hwnd)
    {
        Process = process;
        AppLocator.Record(process);
        Profile = profile;
        Channel = channel;
        Hwnd = hwnd;
    }

    public Process Process { get; }

    public int Pid => Process.Id;

    /// <summary>設定フォルダ (--test-profile)。</summary>
    public string Profile { get; }

    public string RecoveryFolder => Path.Combine(Profile, "recovery");

    public string CrashFolder => Path.Combine(Profile, "crash");

    public TestChannelClient Channel { get; }

    /// <summary>メインウィンドウ。</summary>
    public nint Hwnd { get; }

    /// <summary>テスト中にこのアプリのウィンドウが前面に出たら true (作業中の利用者の入力を奪った)。</summary>
    public bool StoleForeground { get; private set; }

    public static UIA3Automation Automation => SharedAutomation.Value;

    /// <summary>メインウィンドウの UI オートメーションの要素。</summary>
    public Window Window => Automation.FromHandle(Hwnd).AsWindow();

    /// <summary>起動して、ウィンドウと命令の通り道の準備ができるまで待つ。</summary>
    public static async Task<AppSession> StartAsync(AppOptions options, string profile, string hooksPath)
    {
        Process process = Launch(options, profile, hooksPath);
        TestChannelClient? channel = await TestChannelClient.ConnectAsync(process.Id, () => process.HasExited, TimeSpan.FromSeconds(60));
        if (channel is null)
        {
            string reason = process.HasExited ? $"exited with code {process.ExitCode}" : "did not open the test channel";
            TryKill(process);
            throw new InvalidOperationException(
                $"HexEditor ({AppLocator.ExePath}) {reason}. テスト用のビルド (Debug、または -p:HexTestHooks=true) か確かめてください。");
        }

        nint hwnd = await WaitForWindowAsync(process, TimeSpan.FromSeconds(30));
        var session = new AppSession(process, profile, channel, hwnd);
        session.StartForegroundMonitor();
        if (options.WaitForEditor)
        {
            await session.WaitUntilAsync(async () => (await session.StateAsync())["hexViews"]!.GetValue<int>() > 0
                || (await session.StateAsync())["documents"]!.AsArray().Count == 0, TimeSpan.FromSeconds(30), "the hex view to load");
        }

        return session;
    }

    /// <summary>
    /// アプリ自身が起動したプロセス (再起動の後の新しいプロセスなど) に、命令の通り道とウィンドウの準備ができるまで待ってつなぐ。
    /// </summary>
    public static async Task<AppSession> AttachAsync(Process process, string profile)
    {
        TestChannelClient channel = await TestChannelClient.ConnectAsync(process.Id, () => process.HasExited, TimeSpan.FromSeconds(60))
            ?? throw new InvalidOperationException($"HexEditor (pid {process.Id}) did not open the test channel.");
        nint hwnd = await WaitForWindowAsync(process, TimeSpan.FromSeconds(30));
        var session = new AppSession(process, profile, channel, hwnd);
        session.StartForegroundMonitor();
        return session;
    }

    /// <summary>起動するだけ (転送されて終わる 2 つ目の起動など)。</summary>
    public static Process Launch(AppOptions options, string profile, string hooksPath)
    {
        // 開発用の印 (DevOptions): 起動時にウィンドウをアクティブにしない。テストでは --test-hooks でも同じ扱いにする。
        string marker = Path.Combine(Path.GetTempPath(), "HexEditor", "dev-no-activate");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, string.Empty);
        }

        File.WriteAllText(hooksPath, (options.Hooks ?? new JsonObject()).ToJsonString());
        // 標準入出力はテストの実行環境から切り離す (受け継ぐと、アプリが残っている間 dotnet test が終わらない)。
        var info = new ProcessStartInfo(AppLocator.ExePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(AppLocator.ExePath)!,
        };
        if (options.NewInstance)
        {
            info.ArgumentList.Add("--new-instance");
        }

        info.ArgumentList.Add("--test-profile");
        info.ArgumentList.Add(profile);
        info.ArgumentList.Add("--test-hooks");
        info.ArgumentList.Add(hooksPath);
        if (options.UiLanguage is { } lang)
        {
            info.ArgumentList.Add("--ui-lang");
            info.ArgumentList.Add(lang);
        }

        foreach (string arg in options.ExtraArgs)
        {
            info.ArgumentList.Add(arg);
        }

        foreach (string file in options.Files)
        {
            info.ArgumentList.Add(file);
        }

        Process process = Process.Start(info) ?? throw new InvalidOperationException("HexEditor を起動できません。");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<nint> WaitForWindowAsync(Process process, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (NativeMethods.TopLevelWindows(process.Id, "WinUIDesktopWin32WindowClass") is [var hwnd, ..])
            {
                return hwnd;
            }

            if (process.HasExited)
            {
                break;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("HexEditor のウィンドウが表示されません。");
    }

    private void StartForegroundMonitor()
    {
        CancellationToken token = _monitor.Token;
        _monitorTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                if (NativeMethods.ForegroundProcessId() == Pid)
                {
                    StoleForeground = true;
                }

                try
                {
                    await Task.Delay(50, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    // ---- 命令の通り道 ----

    public async Task<JsonObject> SendAsync(string cmd, JsonObject? args = null, TimeSpan? timeout = null)
    {
        try
        {
            return await Channel.SendAsync(cmd, args, timeout);
        }
        catch (IOException ex)
        {
            // アプリが落ちた・終わった: 終了コードとクラッシュ情報・ログの末尾を失敗の文に入れる (CI のログだけで原因が分かるように)。
            throw new IOException($"{ex.Message} (command '{cmd}'){ExitReport()}", ex);
        }
    }

    /// <summary>アプリが終わっていれば、終了コード・最新のクラッシュ情報・ログの末尾。</summary>
    public string ExitReport()
    {
        var text = new System.Text.StringBuilder();
        try
        {
            if (!Process.WaitForExit(5000))
            {
                return " The process is still running.";
            }

            text.Append($" The process exited with code 0x{Process.ExitCode:X8}.");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            text.Append(" The exit code is unknown.");
        }

        static FileInfo? Newest(string folder, string pattern) => Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles(pattern, SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        try
        {
            if (Newest(CrashFolder, "*.txt") is { } crash)
            {
                text.AppendLine().AppendLine($"--- {crash.Name}").AppendJoin(Environment.NewLine, File.ReadLines(crash.FullName).Take(60));
            }

            if (Newest(Profile, "*.log") is { } log)
            {
                text.AppendLine().AppendLine($"--- {log.Name} (last lines)").AppendJoin(Environment.NewLine, File.ReadLines(log.FullName).TakeLast(25));
            }
        }
        catch (IOException)
        {
        }

        // ネイティブのクラッシュ (0xC000027B など。アプリのクラッシュ情報は書かれない) は、Windows のイベントログの記録を示す。
        try
        {
            var query = new System.Diagnostics.Eventing.Reader.EventLogQuery("Application", System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime']) and TimeCreated[timediff(@SystemTime) <= 120000]]]");
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            for (System.Diagnostics.Eventing.Reader.EventRecord? record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    string message = record.FormatDescription() ?? string.Empty;
                    if (message.Contains($"{Pid}", StringComparison.Ordinal) || message.Contains($"0x{Pid:x}", StringComparison.OrdinalIgnoreCase))
                    {
                        text.AppendLine().AppendLine($"--- {record.ProviderName} {record.TimeCreated:O}").Append(message);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Diagnostics.Eventing.Reader.EventLogException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }

        return text.ToString();
    }

    public Task<JsonObject> StateAsync() => SendAsync("state");

    /// <summary>選択中のタブのドキュメントの状態 (カーソル・選択・長さ・Undo の件数など)。</summary>
    public async Task<JsonObject> DocumentAsync() =>
        (await StateAsync())["document"]?.AsObject() ?? throw new InvalidOperationException("No document is open.");

    /// <summary>キー 1 つ (VirtualKey の名前。例: "Right"、"PageDown"、"Z")。Hex ビューが処理しなければメニューのショートカットキー。</summary>
    public Task<JsonObject> KeyAsync(string key, bool ctrl = false, bool shift = false, bool alt = false, int count = 1) =>
        SendAsync("key", new JsonObject { ["key"] = key, ["ctrl"] = ctrl, ["shift"] = shift, ["alt"] = alt, ["count"] = count });

    /// <summary>文字を入力する (Hex 列なら 16 進の数字、テキスト列なら文字)。</summary>
    public Task<JsonObject> TypeAsync(string text) => SendAsync("text", new JsonObject { ["text"] = text });

    /// <summary>AutomationId の要素 (メニューの項目を含む) を、アプリの中で UI オートメーションの Invoke と同じ処理で押す。</summary>
    public Task<JsonObject> CommandAsync(string automationId) => SendAsync("invoke", new JsonObject { ["id"] = automationId });

    public Task<JsonObject> OpenAsync(string path) => SendAsync("open", new JsonObject { ["path"] = path });

    public Task<JsonObject> GoToAsync(long offset) => SendAsync("goto", new JsonObject { ["offset"] = offset });

    public Task<JsonObject> SelectAsync(long start, long length) => SendAsync("select", new JsonObject { ["start"] = start, ["length"] = length });

    public Task<JsonObject> RenderAsync() => SendAsync("render");

    /// <summary>長時間処理と UI スレッドの待ちが終わるまで待つ。</summary>
    public Task<JsonObject> IdleAsync() => SendAsync("idle", null, TimeSpan.FromSeconds(60));

    public async Task<byte[]> BytesAsync(long offset, long length)
    {
        JsonObject r = await SendAsync("bytes", new JsonObject { ["offset"] = offset, ["length"] = length });
        return Convert.FromHexString(r["hex"]!.GetValue<string>());
    }

    public async Task<IReadOnlyList<string>> LogAsync() =>
        (await SendAsync("log"))["lines"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    public async Task<IReadOnlyList<string>> TabNamesAsync() =>
        (await StateAsync())["documents"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()).ToList();

    /// <summary>条件が成り立つまで待つ。</summary>
    public async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        timeout = UiTest.Scaled(timeout);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (await condition())
            {
                return;
            }

            if (watch.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            if (Process.HasExited)
            {
                throw new InvalidOperationException($"HexEditor exited while waiting for {what}.{ExitReport()}");
            }

            await Task.Delay(50);
        }
    }

    // ---- UI オートメーション (フォーカスを使わないパターンだけ) ----

    /// <summary>AutomationId の要素を探す。見つからなければ null。</summary>
    public AutomationElement? Find(string automationId)
    {
        try
        {
            return Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // ダイアログを開いている・閉じている途中は UI オートメーションの木が変わり、E_UNEXPECTED などで失敗することがある
            // (遅い CI のランナー)。まだ見つからないとして、呼び出し側が待ち直す。
            return null;
        }
    }

    /// <summary>AutomationId の要素が現れるまで待つ。</summary>
    public async Task<AutomationElement> WaitForAsync(string automationId, TimeSpan? timeout = null)
    {
        AutomationElement? found = null;
        await WaitUntilAsync(() => Task.FromResult((found = Find(automationId)) is not null), timeout ?? TimeSpan.FromSeconds(15),
            $"element '{automationId}'");
        return found!;
    }

    /// <summary>InvokePattern で押す (マウスを動かさない)。</summary>
    public async Task UiaInvokeAsync(string automationId) => (await WaitForAsync(automationId)).Patterns.Invoke.Pattern.Invoke();

    /// <summary>ValuePattern で入力欄に値を入れる (キーボードを使わない)。</summary>
    public async Task UiaSetValueAsync(string automationId, string value)
    {
        (await WaitForAsync(automationId)).Patterns.Value.Pattern.SetValue(value);

        // TextChanged は後から届くため、UI スレッドの待ちがなくなるまで待つ。
        await IdleAsync();
    }

    /// <summary>要素の名前 (TextBlock では表示している文字列)。</summary>
    public async Task<string> UiaNameAsync(string automationId) => NameOf(await WaitForAsync(automationId));

    /// <summary>
    /// 表示中の要素の名前を待たずに読む。要素がなければ (空のステータスバーの項目など、表示されないもの) 空文字列。
    /// </summary>
    public string TextOrEmpty(string automationId) => Find(automationId) is { } e ? NameOf(e) : string.Empty;

    /// <summary>通知 (NotificationCenter の InfoBar。AutomationId "Notification") の文字列をまとめて取る。</summary>
    public string NotificationsText() =>
        string.Join('\n', Window.FindAllDescendants(cf => cf.ByAutomationId("Notification")).Select(AllText));

    /// <summary>要素の名前。名前を持たない要素では空文字列。</summary>
    public static string NameOf(AutomationElement element) => element.Properties.Name.ValueOrDefault ?? string.Empty;

    /// <summary>InfoBar・ダイアログの中の文字列をまとめて取る。</summary>
    public static string AllText(AutomationElement element) =>
        string.Join("\n", new[] { element }.Concat(element.FindAllDescendants()).Select(NameOf).Where(s => s.Length > 0));

    public static bool IsToggled(AutomationElement element) => element.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On;

    /// <summary>ウィンドウの画像 (前面に出さずに取る)。</summary>
    public WindowImage Screenshot() => WindowImage.Capture(Hwnd);

    // ---- プロセス ----

    /// <summary>プロセスの終了を待つ。終了コードを返す。</summary>
    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await Process.WaitForExitAsync(cts.Token);
        return Process.ExitCode;
    }

    /// <summary>TerminateProcess で強制終了する (終了の処理は行われない)。</summary>
    public void Kill()
    {
        TryKill(Process);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _monitor.CancelAsync();
        if (_monitorTask is not null)
        {
            await _monitorTask;
        }

        Kill();
        Channel.Dispose();

        Process.Dispose();
        _monitor.Dispose();
    }
}
