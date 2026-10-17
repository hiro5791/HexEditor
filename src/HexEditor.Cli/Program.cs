using System.Runtime.InteropServices;

namespace HexEditor.Cli;

/// <summary>hexed.exe の入口 (AUTO-36)。Ctrl+C / Ctrl+Break でキャンセルし、終了コード 6 で終わる。</summary>
internal static partial class Program
{
    [LibraryImport("kernel32.dll", EntryPoint = "SetConsoleCtrlHandler")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    private static int Main(string[] args)
    {
#if HEX_TEST_HOOKS
        // テスト用: 別のプロセスのコンソールに Ctrl+C を送る (TC-AUTO-36-04)。製品版のビルドには含めない。
        if (args is ["--test-send-ctrl-c", var pid])
        {
            return CtrlCSender.Send(int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture));
        }
#endif

        // 新しいプロセスグループで起動されると Ctrl+C が無効になっている (CREATE_NEW_PROCESS_GROUP)。有効に戻す。
        _ = SetConsoleCtrlHandler(IntPtr.Zero, false);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        return CliApp.Run(args, CliHost.ForProcess(cancellation.Token, CliServices.Default));
    }
}

#if HEX_TEST_HOOKS
/// <summary>テスト用 (<c>--test-send-ctrl-c &lt;PID&gt;</c>): 指定のプロセスのコンソールに Ctrl+C を送る。</summary>
internal static partial class CtrlCSender
{
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    public static int Send(int processId)
    {
        FreeConsole();
        if (!AttachConsole((uint)processId))
        {
            return 1;
        }

        // 自分には届かないようにする。
        SetConsoleCtrlHandler(IntPtr.Zero, true);
        bool sent = GenerateConsoleCtrlEvent(0, 0);
        Thread.Sleep(200);
        FreeConsole();
        return sent ? 0 : 1;
    }
}
#endif
