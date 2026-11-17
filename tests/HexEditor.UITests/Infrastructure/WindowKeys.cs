using System.Runtime.InteropServices;
using System.Text;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// テスト用のアプリのウィンドウに、実際のキー入力と同じ道筋 (WinUI の入力の窓 → XAML のキーのイベント) でキーを届ける。
/// SendInput は使わず (作業中の PC の入力を奪わない)、ウィンドウを前面にも出さない: キーのメッセージを入力の窓に直接送り、
/// 修飾キーの状態はアプリの UI スレッドの入力の状態 (AttachThreadInput で一時的に共有する) にだけ書き、終わったら元に戻す。
/// テスト用の命令 (<c>key</c>) はキーの振り分けを直接呼ぶので、フォーカスの場所によってキーが届かない不具合はこちらで確かめる。
/// </summary>
internal static partial class WindowKeys
{
    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101;
    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLShift = 0xA0, VkLControl = 0xA2, VkLMenu = 0xA4;

    /// <summary>修飾キーを押したまま <paramref name="key"/> (仮想キーコード) を押して離す。</summary>
    public static void Press(AppSession app, int key, bool ctrl = false, bool shift = false, bool alt = false)
    {
        nint input = FindChild(app.Hwnd, "InputSiteWindowClass");
        if (input == 0)
        {
            throw new InvalidOperationException("The WinUI input window (InputSiteWindowClass) was not found.");
        }

        uint appThread = NativeMethods.GetWindowThreadProcessId(app.Hwnd, out _);
        Exception? error = null;

        // 入力の状態の共有はスレッドごとなので、専用のスレッドで行う (テストの非同期の処理のスレッドを巻き込まない)。
        var thread = new Thread(() =>
        {
            uint self = GetCurrentThreadId();
            if (!AttachThreadInput(self, appThread, true))
            {
                error = new InvalidOperationException("AttachThreadInput failed.");
                return;
            }

            byte[] saved = new byte[256];
            try
            {
                GetKeyboardState(saved);
                byte[] state = (byte[])saved.Clone();
                var modifiers = new List<int>();
                if (ctrl)
                {
                    state[VkControl] = state[VkLControl] = 0x80;
                    modifiers.Add(VkControl);
                }

                if (shift)
                {
                    state[VkShift] = state[VkLShift] = 0x80;
                    modifiers.Add(VkShift);
                }

                if (alt)
                {
                    state[VkMenu] = state[VkLMenu] = 0x80;
                    modifiers.Add(VkMenu);
                }

                SetKeyboardState(state);
                foreach (int m in modifiers)
                {
                    Post(input, m, down: true);
                }

                Post(input, key, down: true);

                // アプリがキーを処理するまで、修飾キーの状態を保つ。
                Thread.Sleep(UiTest.Scaled(TimeSpan.FromMilliseconds(800)));
                Post(input, key, down: false);
                foreach (int m in Enumerable.Reverse(modifiers))
                {
                    Post(input, m, down: false);
                }

                Thread.Sleep(200);
            }
            finally
            {
                SetKeyboardState(saved);
                AttachThreadInput(self, appThread, false);
            }
        });
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }
    }

    private static void Post(nint hwnd, int key, bool down)
    {
        uint scan = MapVirtualKey((uint)key, 0);
        nint lParam = (nint)(1u | (scan << 16) | (down ? 0u : 0xC0000000u));
        PostMessage(hwnd, down ? WmKeyDown : WmKeyUp, key, lParam);
    }

    private static nint FindChild(nint parent, string className)
    {
        nint found = 0;
        EnumChildWindows(parent, (hwnd, _) =>
        {
            var name = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() == className)
            {
                found = hwnd;
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, NativeMethods.EnumWindowsProc callback, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static partial uint MapVirtualKey(uint code, uint mapType);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetKeyboardState([Out] byte[] state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKeyboardState(byte[] state);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
