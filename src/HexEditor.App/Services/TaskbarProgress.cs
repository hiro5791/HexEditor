using System.Runtime.InteropServices;

namespace HexEditor.App.Services;

/// <summary>
/// 長時間処理の OS への表示 (ENG-09 の仕様 8・9): タスクバーのボタンの進捗、完了時の点滅、実行中のスリープの抑止。
/// </summary>
public sealed class TaskbarProgress
{
    private readonly nint _hwnd;
    private ITaskbarList3? _taskbar;
    private bool _showing;
    private bool _keepingAwake;

    public TaskbarProgress(nint hwnd)
    {
        _hwnd = hwnd;
        try
        {
            _taskbar = (ITaskbarList3)new TaskbarInstance();
            _taskbar.HrInit();
        }
        catch (COMException)
        {
            _taskbar = null;
        }
    }

    /// <summary>進捗を出す (fraction が null なら不確定)。null の処理だけなら不確定の表示。</summary>
    public void Show(double? fraction)
    {
        if (_taskbar is null)
        {
            return;
        }

        _showing = true;
        if (fraction is double f)
        {
            _taskbar.SetProgressState(_hwnd, TbpFlag.Normal);
            _taskbar.SetProgressValue(_hwnd, (ulong)Math.Round(f * 1000), 1000);
        }
        else
        {
            _taskbar.SetProgressState(_hwnd, TbpFlag.Indeterminate);
        }
    }

    public void Clear()
    {
        if (_taskbar is not null && _showing)
        {
            _taskbar.SetProgressState(_hwnd, TbpFlag.NoProgress);
            _showing = false;
        }
    }

    /// <summary>ウィンドウが非アクティブのときに処理が終わったら、タスクバーのボタンを点滅させる。</summary>
    public void Flash()
    {
        var info = new FlashWInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWInfo>(),
            Hwnd = _hwnd,
            Flags = FlashwTray | FlashwTimerNoFg,
            Count = 3,
        };
        FlashWindowEx(ref info);
    }

    /// <summary>処理の実行中は OS がスリープしないように要求する (画面の消灯は妨げない)。</summary>
    public void KeepAwake(bool active)
    {
        if (active == _keepingAwake)
        {
            return;
        }

        _keepingAwake = active;
        SetThreadExecutionState(active ? EsContinuous | EsSystemRequired : EsContinuous);
    }

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint FlashwTray = 0x2;
    private const uint FlashwTimerNoFg = 0xC;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashWInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWInfo
    {
        public uint Size;
        public nint Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    private enum TbpFlag
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8,
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarInstance
    {
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();

        void AddTab(nint hwnd);

        void DeleteTab(nint hwnd);

        void ActivateTab(nint hwnd);

        void SetActiveAlt(nint hwnd);

        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        void SetProgressValue(nint hwnd, ulong completed, ulong total);

        void SetProgressState(nint hwnd, TbpFlag flags);
    }
}
