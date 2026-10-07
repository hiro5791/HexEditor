using System.Diagnostics;
using System.Runtime.InteropServices;
using HexEditor.ManualTests;
using HexEditor.TestData;

namespace ManualTestRunner;

/// <summary>
/// 手動テストの準備 (テスト方針 8.2): テストデータの生成、テスト用の設定フォルダの作成、HexEditor の起動。
/// 起動する HexEditor は、環境変数 HEXEDITOR_EXE、開発用のビルド (src/HexEditor.App/bin)、実行エイリアス (hexeditor.exe) の順に探す。
/// </summary>
public static class Preparer
{
    public static string DataFolder => Path.Combine(RunStore.Folder, "testdata");

    /// <summary>準備を行い、起動した HexEditor のプロセスを返す。生成できなかったテストデータは <paramref name="missing"/> に入れる。</summary>
    public static Process? Prepare(TestCase testCase, out IReadOnlyList<string> missing)
    {
        var notGenerated = new List<string>();
        var files = new List<string>();
        foreach (string id in testCase.TestData)
        {
            try
            {
                files.Add(TestDataCatalog.Generate(id, DataFolder));
            }
            catch (ArgumentException)
            {
                // まだ生成ツールに定義のないテストデータ (領域ごとのもの) は手で用意する。
                notGenerated.Add(id);
            }
        }

        missing = notGenerated;
        string profile = Path.Combine(RunStore.Folder, "profiles", testCase.Id);
        if (Directory.Exists(profile))
        {
            Directory.Delete(profile, recursive: true);
        }

        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(FindHexEditor()) { UseShellExecute = false };
        start.ArgumentList.Add("--new-instance");
        start.ArgumentList.Add("--test-profile");
        start.ArgumentList.Add(profile);
        foreach (string file in files.Take(1))
        {
            start.ArgumentList.Add(file);
        }

        try
        {
            return Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string FindHexEditor()
    {
        if (Environment.GetEnvironmentVariable("HEXEDITOR_EXE") is { Length: > 0 } configured && File.Exists(configured))
        {
            return configured;
        }

        // このツールのビルド先 (tools/ManualTestRunner/bin/...) からリポジトリのルートを探し、開発用のビルドを使う。
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string bin = Path.Combine(dir.FullName, "src", "HexEditor.App", "bin");
            if (Directory.Exists(bin))
            {
                FileInfo? newest = new DirectoryInfo(bin).EnumerateFiles("HexEditor.exe", SearchOption.AllDirectories)
                    .Where(f => !f.DirectoryName!.EndsWith("AppX", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (newest is not null)
                {
                    return newest.FullName;
                }
            }
        }

        return "hexeditor.exe";
    }

    /// <summary>HexEditor のウィンドウを前面に出さずに撮る (不合格の記録に添付する)。</summary>
    public static string? CaptureWindow(Process? process, string id)
    {
        if (process is null || process.HasExited || process.MainWindowHandle == 0)
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
