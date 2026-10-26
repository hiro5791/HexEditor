using System.Diagnostics;
using System.Runtime.InteropServices;
using HexEditor.ManualTests;

namespace ManualTestRunner;

/// <summary>準備ボタンの結果。</summary>
public sealed record PrepareOutcome(Process? HexEditor, SetupResult? Setup, IReadOnlyList<string> Done, IReadOnlyList<string> Problems)
{
    /// <summary>HexEditor を起動した (管理者として起動したときは、プロセスが取れないことがある)。</summary>
    public bool Launched { get; init; }
}

/// <summary>
/// 手動テストの準備 (テスト方針 8.2): テストデータとテスト用の設定フォルダを作り (<see cref="CaseSetup"/>)、テストケースの「起動」の種類の
/// HexEditor を起動する。ポータブル版・インストーラ版を作る処理と、仮想ディスクを作る・外す処理 (管理者の確認が出る) もここに置く。
/// 管理者の確認が出る処理は、利用者がボタンを押したときだけ行う。
/// </summary>
public static class Preparer
{
    public static string DataFolder => Path.Combine(RunStore.Folder, "testdata");

    public static string LogsFolder => Path.Combine(RunStore.Folder, "logs");

    /// <summary>ツールが展開したポータブル版のフォルダ。</summary>
    public static string PortableFolder => Path.Combine(RunStore.Folder, "portable");

    public static string PortableExe => Path.Combine(PortableFolder, "HexEditor", "HexEditor.exe");

    /// <summary>インストーラ版 (Velopack) のインストール先。</summary>
    public static string InstallerExe => Fake
        ? Path.Combine(RunStore.Folder, "fake-installer", "HexEditor.exe")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HexEditor", "current", "HexEditor.exe");

    /// <summary>
    /// ツール自身の UI テスト用の模擬: 環境変数 HEXEDITOR_MANUALTESTS_FAKE=1 (と HEXEDITOR_MANUALTESTS_DIR) があれば、仮想ディスク・ポータブル版・
    /// インストーラ版・HexEditor の起動を、記録のフォルダの中に置く空のファイルで模擬する (管理者の確認もインストールも起動もしない)。
    /// HEXEDITOR_MANUALTESTS_FAKE_FAIL に Portable / Installer / Vhd を書くと、その段階を失敗させる。
    /// </summary>
    public static bool Fake =>
        Environment.GetEnvironmentVariable("HEXEDITOR_MANUALTESTS_FAKE") == "1"
        && Environment.GetEnvironmentVariable("HEXEDITOR_MANUALTESTS_DIR") is { Length: > 0 };

    private static bool FakeFails(string what) => Environment.GetEnvironmentVariable("HEXEDITOR_MANUALTESTS_FAKE_FAIL") == what;

    /// <summary>New-ManualTestVhd.ps1 が仮想ディスクを置くフォルダ。</summary>
    public static string VhdFolder => Fake
        ? Path.Combine(RunStore.Folder, "fake-vhd")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "HexEditorManualTestData");

    public static string VhdLog => Path.Combine(LogsFolder, "virtual-disks.log");

    public static bool VirtualDisksExist => Directory.Exists(VhdFolder) && Directory.EnumerateFiles(VhdFolder, "TD-VHDX-*.vhdx").Any();

    /// <summary>
    /// 仮想ディスクが接続されている (と見てよい): ファイルがあり、最後の「仮想ディスクを作る」のログが「接続した」で、そのログが PC を起動した後に
    /// 書かれた (再起動すると接続が外れるため)。ディスクそのものには触れない。
    /// </summary>
    public static bool VirtualDisksAttached
    {
        get
        {
            try
            {
                if (!VirtualDisksExist || !File.Exists(VhdLog))
                {
                    return false;
                }

                DateTime boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
                return File.GetLastWriteTime(VhdLog) > boot && File.ReadAllText(VhdLog).Contains("RESULT: attached", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public static string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>このツールが管理者として動いている (起動する HexEditor も管理者になる)。</summary>
    public static bool ToolIsElevated
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>リポジトリのルート (docs/test/cases がある場所)。</summary>
    public static string RepoRoot { get; set; } = string.Empty;

    /// <summary>起動の種類の表示名。</summary>
    public static string LaunchLabel(LaunchKind kind) => kind switch
    {
        LaunchKind.DevAdmin => "開発版の HexEditor を管理者として",
        LaunchKind.Portable => "ポータブル版の HexEditor を",
        LaunchKind.PortableAdmin => "ポータブル版の HexEditor を管理者として",
        LaunchKind.Installer => "インストーラ版の HexEditor を",
        _ => "開発版の HexEditor を",
    };

    /// <summary>準備を行う。起動に要るもの (ポータブル版など) がなければ、起動せずに理由を返す。</summary>
    public static PrepareOutcome Prepare(TestCase testCase, CasePrep prep)
    {
        var done = new List<string>();
        var problems = new List<string>();
        SetupResult setup;
        try
        {
            setup = CaseSetup.Run(testCase, prep, DataFolder, Path.Combine(RunStore.Folder, "profiles", testCase.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new PrepareOutcome(null, null, done, [$"準備に失敗しました: {ex.Message}"]);
        }

        done.AddRange(setup.Done);
        foreach (string id in setup.Missing.Where(id => !(id.StartsWith("TD-VHDX", StringComparison.Ordinal) && VirtualDisksAttached)))
        {
            problems.Add(id.StartsWith("TD-VHDX", StringComparison.Ordinal)
                ? $"{id} は「仮想ディスクを作る」で用意します。"
                : $"{id} は準備ボタンでは作れません (「詳しい説明」の「用意のしかた」を見てください)。");
        }

        if (prep.AdminCommand is { } fakeCommand && Fake)
        {
            done.Add($"(模擬) 管理者の PowerShell で「{fakeCommand}」を実行したことにした");
        }
        else if (prep.AdminCommand is { } command)
        {
            try
            {
                StartElevatedPowerShell(command);
                done.Add($"管理者の PowerShell で「{command}」を実行した (別のウィンドウ)");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                problems.Add($"管理者の確認で「いいえ」が押されたため、「{command}」を実行できませんでした。もう一度「準備」を押してください。");
            }
        }

        if (prep.Launch == LaunchKind.None)
        {
            done.Add("このテストケースでは HexEditor を起動しない (別の PC などで行う)");
            return new PrepareOutcome(null, setup, done, problems);
        }

        if (Fake)
        {
            done.Add($"(模擬) {LaunchLabel(prep.Launch)}起動したことにした: {CaseSetup.JoinArguments(setup.Arguments)}");
            return new PrepareOutcome(null, setup, done, problems) { Launched = true };
        }

        string? exe = ExeFor(prep.Launch);
        if (exe is null)
        {
            problems.Add(prep.Launch switch
            {
                LaunchKind.Portable or LaunchKind.PortableAdmin => "ポータブル版がまだありません。",
                LaunchKind.Installer => "インストーラ版がまだインストールされていません。",
                _ => "開発版の HexEditor が見つかりません (src/HexEditor.App を Debug でビルドしてください。環境変数 HEXEDITOR_EXE でも指定できます)。",
            });
            return new PrepareOutcome(null, setup, done, problems);
        }

        if (setup.HooksPath is not null && prep.Launch is not (LaunchKind.Dev or LaunchKind.DevAdmin))
        {
            problems.Add("このテストケースのテスト用の設定 (--test-hooks) は開発版でだけ効きます。");
        }

        try
        {
            Process? process = Launch(exe, setup.Arguments, prep.IsAdmin);
            done.Add($"{LaunchLabel(prep.Launch)}起動した: {exe} {CaseSetup.JoinArguments(setup.Arguments)}");
            return new PrepareOutcome(process, setup, done, problems) { Launched = true };
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            problems.Add(prep.IsAdmin
                ? "管理者の確認で「いいえ」が押されたため、HexEditor を起動できませんでした。もう一度「準備」を押してください。"
                : $"HexEditor を起動できませんでした: {ex.Message}");
            return new PrepareOutcome(null, setup, done, problems);
        }
    }

    /// <summary>起動の種類の HexEditor.exe (なければ null)。</summary>
    public static string? ExeFor(LaunchKind kind) => kind switch
    {
        LaunchKind.Portable or LaunchKind.PortableAdmin => File.Exists(PortableExe) ? PortableExe : null,
        LaunchKind.Installer => File.Exists(InstallerExe) ? InstallerExe : null,
        LaunchKind.None => null,
        _ => FindDevBuild(),
    };

    private static Process? Launch(string exe, IReadOnlyList<string> arguments, bool admin)
    {
        if (admin)
        {
            // 管理者として起動する (Windows が確認を出す)。
            return Process.Start(new ProcessStartInfo(exe, CaseSetup.JoinArguments(arguments)) { UseShellExecute = true, Verb = "runas" });
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        return Process.Start(start);
    }

    private static void StartElevatedPowerShell(string command)
    {
        string script = $"Set-Location -LiteralPath '{RepoRoot.Replace("'", "''")}'; {command}";
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoExit -NoProfile -Command \"{script.Replace("\"", "\\\"")}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
        });
    }

    /// <summary>
    /// 開発用のビルド。環境変数 HEXEDITOR_EXE、src/HexEditor.App/bin の Debug (この PC のアーキテクチャのもの)、その他のビルドの順に探す。
    /// </summary>
    private static string? FindDevBuild()
    {
        if (Environment.GetEnvironmentVariable("HEXEDITOR_EXE") is { Length: > 0 } configured && File.Exists(configured))
        {
            return configured;
        }

        string bin = Path.Combine(RepoRoot, "src", "HexEditor.App", "bin");
        if (!Directory.Exists(bin))
        {
            return null;
        }

        string rid = "win-" + Arch;
        List<FileInfo> builds = new DirectoryInfo(bin).EnumerateFiles("HexEditor.exe", SearchOption.AllDirectories)
            .Where(f => !f.DirectoryName!.EndsWith("AppX", StringComparison.OrdinalIgnoreCase)
                && f.DirectoryName.Contains(rid, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        // Debug にはテスト用の仕組み (--test-hooks) が入っている。
        return (builds.FirstOrDefault(f => f.FullName.Contains(@"\Debug\", StringComparison.OrdinalIgnoreCase)) ?? builds.FirstOrDefault())?.FullName;
    }

    // ---- 仮想ディスク ----

    /// <summary>
    /// build/tests/New-ManualTestVhd.ps1 を管理者として実行し (Windows が確認を出す)、終わるまで待って、ログの内容を返す。
    /// 確認で「いいえ」が押されたら null。
    /// </summary>
    public static async Task<string?> RunVirtualDiskScriptAsync(bool dismount)
    {
        Directory.CreateDirectory(LogsFolder);
        if (File.Exists(VhdLog))
        {
            File.Delete(VhdLog);
        }

        if (Fake)
        {
            await Task.Delay(800);
            string fake = FakeFails("Vhd") ? "ERROR: (模擬) 仮想ディスクを作れませんでした"
                : dismount ? "RESULT: removed" : "DISK TD-VHDX-MBR 90 (模擬)\nDISK TD-VHDX-GPT 91 (模擬)\nRESULT: attached";
            if (!dismount && !FakeFails("Vhd"))
            {
                Directory.CreateDirectory(VhdFolder);
                File.WriteAllBytes(Path.Combine(VhdFolder, "TD-VHDX-MBR.vhdx"), []);
            }
            else if (dismount && Directory.Exists(VhdFolder))
            {
                Directory.Delete(VhdFolder, recursive: true);
            }

            await File.WriteAllTextAsync(VhdLog, fake);
            return fake;
        }

        string script = Path.Combine(RepoRoot, "build", "tests", "New-ManualTestVhd.ps1");
        string arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -LogPath \"{VhdLog}\"" + (dismount ? " -Dismount" : string.Empty);
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo("powershell.exe", arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }

        if (process is not null)
        {
            await process.WaitForExitAsync();
        }

        return File.Exists(VhdLog) ? await File.ReadAllTextAsync(VhdLog) : "ログがありません (スクリプトが動きませんでした)。";
    }

    /// <summary>
    /// 手順の文の TD-VHDX-… に、この PC でのディスクの番号を書き足す (HexEditor の一覧には「ディスク 3 (Msft Virtual Disk, …)」と出て、
    /// TD-VHDX-… の名前は出ないため)。仮想ディスクのログがなければ、そのまま返す。
    /// </summary>
    public static string WithDiskNumbers(string text)
    {
        if (!text.Contains("TD-VHDX-", StringComparison.Ordinal) || !File.Exists(VhdLog))
        {
            return text;
        }

        string log;
        try
        {
            log = File.ReadAllText(VhdLog);
        }
        catch (IOException)
        {
            return text;
        }

        return WithDiskNumbers(text, log);
    }

    /// <summary><see cref="WithDiskNumbers(string)"/> の本体 (ログの中身を受け取る)。</summary>
    public static string WithDiskNumbers(string text, string log)
    {
        if (!log.Contains("RESULT: attached", StringComparison.Ordinal))
        {
            return text;
        }

        foreach (string line in log.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(' ', 4);
            if (parts.Length >= 3 && parts[0] == "DISK" && int.TryParse(parts[2], out int number))
            {
                // 後ろに - や英大文字が続くものは別の名前なので当てない。
                text = System.Text.RegularExpressions.Regex.Replace(
                    text, System.Text.RegularExpressions.Regex.Escape(parts[1]) + "(?![-A-Z])", $"{parts[1]} (一覧の「ディスク {number}」)");
            }
        }

        return text;
    }

    /// <summary>仮想ディスクのログを、利用者向けの文に直す。</summary>
    public static string DescribeVirtualDiskLog(string log)
    {
        var lines = new List<string>();
        foreach (string line in log.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("DISK ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ', 4);
                lines.Add($"{parts[1]}: ディスク {parts[2]} {(parts.Length > 3 ? parts[3] : string.Empty)}");
            }
            else if (line.StartsWith("ERROR:", StringComparison.Ordinal))
            {
                lines.Add("失敗しました: " + line["ERROR:".Length..].Trim());
            }
            else if (line == "RESULT: removed")
            {
                lines.Add("仮想ディスクを外し、ファイルを消しました。");
            }
        }

        if (log.Contains("RESULT: attached", StringComparison.Ordinal))
        {
            lines.Insert(0, "仮想ディスクを接続しました (HexEditor では「Msft Virtual Disk」と表示されます)。");
        }

        if (log.Contains("RESULT: removed", StringComparison.Ordinal) && !lines.Contains("仮想ディスクを外し、ファイルを消しました。"))
        {
            lines.Add("仮想ディスクを外し、ファイルを消しました。");
        }

        return lines.Count == 0 ? log.Trim() : string.Join(Environment.NewLine, lines);
    }

    // ---- ポータブル版・インストーラ版を作る ----

    /// <summary>
    /// build/publish.ps1 でポータブル版かインストーラ版を作る (数分かかる)。ポータブル版は展開し、インストーラ版はインストールする。
    /// 進み具合は <paramref name="progress"/> に 1 行ずつ渡す。成功したら null、失敗したら理由を返す。
    /// </summary>
    public static async Task<string?> BuildDistributionAsync(string distro, IProgress<string> progress)
    {
        Directory.CreateDirectory(LogsFolder);
        if (Fake)
        {
            foreach (string line in new[] { "(模擬) 作っています 1/2", "(模擬) 作っています 2/2" })
            {
                progress.Report(line);
                await Task.Delay(400);
            }

            if (FakeFails(distro))
            {
                return $"(模擬) {distro} を作れませんでした (終了コード 1)。";
            }

            string exe = distro == "Portable" ? PortableExe : InstallerExe;
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            await File.WriteAllBytesAsync(exe, []);
            return null;
        }

        string log = Path.Combine(LogsFolder, $"publish-{distro.ToLowerInvariant()}.log");
        string script = Path.Combine(RepoRoot, "build", "publish.ps1");
        string shell = FindOnPath("pwsh.exe") ?? "powershell.exe";
        // 出力を UTF-8 にそろえて読む (既定のコードページで読むと、dotnet の日本語のメッセージが文字化けする)。
        string command = $"[Console]::OutputEncoding = [Text.Encoding]::UTF8; & '{script.Replace("'", "''")}' -Distro {distro} -Arch {Arch}";
        var start = new ProcessStartInfo(shell, $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = RepoRoot,
        };
        using var writer = new StreamWriter(log, append: false);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell を起動できません。");
        object gate = new();
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (gate)
            {
                writer.WriteLine(line);
            }

            progress.Report(line.Length > 160 ? line[..160] + "…" : line);
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            return $"作れませんでした (終了コード {process.ExitCode})。ログ: {log}";
        }

        string output = Path.Combine(RepoRoot, "artifacts", distro, Arch);
        if (distro == "Portable")
        {
            string? zip = Directory.EnumerateFiles(output, "*-portable.zip").FirstOrDefault();
            if (zip is null)
            {
                return $"zip が見つかりません ({output})。ログ: {log}";
            }

            progress.Report("展開しています...");
            await Task.Run(() =>
            {
                if (Directory.Exists(PortableFolder))
                {
                    Directory.Delete(PortableFolder, recursive: true);
                }

                System.IO.Compression.ZipFile.ExtractToDirectory(zip, PortableFolder);
            });
            return File.Exists(PortableExe) ? null : $"展開した中に HexEditor.exe がありません ({PortableFolder})。";
        }

        // インストーラ版: できた Setup.exe でこのユーザーにインストールする。
        string? setup = Directory.EnumerateFiles(output, $"HexEditor-*-{Arch}-Setup.exe").FirstOrDefault()
            ?? Directory.EnumerateFiles(output, "*Setup.exe").FirstOrDefault();
        if (setup is null)
        {
            return $"Setup.exe が見つかりません ({output})。ログ: {log}";
        }

        progress.Report("インストールしています...");
        using Process? installer = Process.Start(new ProcessStartInfo(setup, "--silent") { UseShellExecute = false });
        if (installer is not null)
        {
            await installer.WaitForExitAsync();
        }

        return File.Exists(InstallerExe) ? null : $"インストールが終わりましたが、{InstallerExe} がありません。";
    }

    /// <summary>ポータブル版・インストーラ版の zip / Setup.exe の場所 (別の PC に持っていくときのため)。</summary>
    public static string ArtifactsFolder(string distro) => Path.Combine(RepoRoot, "artifacts", distro, Arch);

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim(), exe))
            .FirstOrDefault(File.Exists);

    // ---- 不合格のときのスクリーンショット ----

    /// <summary>HexEditor のウィンドウを前面に出さずに撮る (不合格の記録に添付する)。</summary>
    public static string? CaptureWindow(Process? process, string id)
    {
        try
        {
            if (process is null || process.HasExited || process.MainWindowHandle == 0)
            {
                return null;
            }
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (!GetWindowRect(process.MainWindowHandle, out Rect r) || r.Right <= r.Left || r.Bottom <= r.Top)
        {
            return null;
        }

        string folder = Path.Combine(RunStore.Folder, "screenshots");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{id}-{DateTime.Now:yyyyMMdd-HHmmss}.bmp");
        int width = r.Right - r.Left;
        int height = r.Bottom - r.Top;
        nint screen = GetDC(0);
        nint dc = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, width, height);
        nint old = SelectObject(dc, bitmap);
        PrintWindow(process.MainWindowHandle, dc, 2);
        SaveBitmap(dc, bitmap, width, height, path);
        SelectObject(dc, old);
        DeleteObject(bitmap);
        DeleteDC(dc);
        ReleaseDC(0, screen);
        return path;
    }

    private static void SaveBitmap(nint dc, nint bitmap, int width, int height, string path)
    {
        var header = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        byte[] pixels = new byte[width * height * 4];
        GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4D42);
        writer.Write(14 + 40 + pixels.Length);
        writer.Write(0);
        writer.Write(14 + 40);
        writer.Write(40);
        writer.Write(width);
        writer.Write(-height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(pixels.Length);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(pixels);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hWnd, nint hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
}
