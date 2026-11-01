using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HexEditor.UITests.Infrastructure;

/// <summary>Windows のコントラストテーマをテストの間だけ切り替える。Dispose で元に戻す。</summary>
public sealed class SystemContrastTheme : IDisposable
{
    private const uint GetHighContrast = 0x42;
    private const uint SetHighContrast = 0x43;
    private const uint On = 1;
    private const uint UpdateAndBroadcast = 3;

    /// <summary>テーマのファイルの名前と、Windows が受け付ける名前 (表示言語ごとに違う)。</summary>
    private static readonly Dictionary<string, string[]> Schemes = new()
    {
        ["hc1"] = ["ハイコントラスト #1", "High Contrast #1"],
        ["hc2"] = ["ハイコントラスト #2", "High Contrast #2"],
        ["hcwhite"] = ["ハイコントラスト 白", "High Contrast White"],
        ["hcblack"] = ["ハイコントラスト 黒", "High Contrast Black"],
    };

    private readonly bool _wasOn;
    private readonly string? _scheme;

    private SystemContrastTheme()
    {
        HighContrast current = Current();
        _wasOn = (current.Flags & On) != 0;
        _scheme = Marshal.PtrToStringUni(current.Scheme);
    }

    /// <summary>テーマを有効にする。切り替わるまで待つ。</summary>
    public static async Task<SystemContrastTheme> ApplyAsync(string file)
    {
        var theme = new SystemContrastTheme();
        foreach (string name in Schemes[file])
        {
            Set(name, on: true);
            for (int i = 0; i < 50; i++)
            {
                if (CurrentThemeFile().EndsWith(file + ".theme", StringComparison.OrdinalIgnoreCase))
                {
                    // アプリの配色が変わるまでの時間。
                    await Task.Delay(1000);
                    return theme;
                }

                await Task.Delay(100);
            }
        }

        theme.Dispose();
        throw new InvalidOperationException($"The contrast theme {file} could not be applied (now {CurrentThemeFile()}).");
    }

    public void Dispose() => Set(_scheme, _wasOn);

    private static string CurrentThemeFile() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes", "CurrentTheme", null) as string ?? string.Empty;

    private static HighContrast Current()
    {
        var value = new HighContrast { Size = Marshal.SizeOf<HighContrast>() };
        if (!SystemParametersInfo(GetHighContrast, (uint)value.Size, ref value, 0))
        {
            throw new InvalidOperationException("The contrast theme could not be read.");
        }

        return value;
    }

    private static void Set(string? scheme, bool on)
    {
        nint text = Marshal.StringToHGlobalUni(scheme);
        try
        {
            var value = new HighContrast { Size = Marshal.SizeOf<HighContrast>(), Flags = on ? On : 0, Scheme = text };
            if (!SystemParametersInfo(SetHighContrast, (uint)value.Size, ref value, UpdateAndBroadcast))
            {
                throw new InvalidOperationException($"The contrast theme {scheme} could not be set.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        public int Size;
        public uint Flags;
        public nint Scheme;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint size, ref HighContrast value, uint flags);
}
