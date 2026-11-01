using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HexEditor.ManualTests;

/// <summary>
/// コントラストテーマの UI テストの画面の判定。ウィンドウの画像を取り、
/// 右上の 3 つのボタンの記号が背景と区別できること、コントラストテーマでは背景がそのテーマの色で、テーマにない色の面がないことを確かめる。
/// </summary>
public static class ScreenCheck
{
    /// <summary>記号と背景の明るさの比の下限 (WCAG 2.1 の非テキストの基準)。</summary>
    public const double MinContrast = 3;

    /// <summary>
    /// ウィンドウがアクティブでないときの下限。非アクティブのウィンドウでは、Windows が記号を灰色に薄くする (ライトで 2.2〜2.6)。
    /// </summary>
    public const double MinInactiveContrast = 2;

    /// <summary>テーマにない色として数える面積 (画面全体に対する割合)。</summary>
    private const double MinForeignArea = 0.005;

    private const int ButtonWidth = 46;
    private const int ButtonHeight = 32;

    /// <summary>GetSysColor の番号。最初が背景: ウィンドウ、ウィンドウの文字、ボタンの面、選択、選択の文字、グレーの文字、ボタンの文字、リンク。</summary>
    private static readonly int[] SystemColors = [5, 8, 15, 13, 14, 17, 18, 26];
    private const int ColorTolerance = 8;
    private const int BlendSteps = 16;
    private static readonly string[] ButtonNames = ["閉じる", "最大化", "最小化"];

    /// <summary>画像 (上から下へ。1 画素は 0xRRGGBB) を判定し、問題の一覧を返す (空なら合格)。</summary>
    /// <param name="themeColors">コントラストテーマの色 (最初が背景の色)。通常のテーマでは null。</param>
    /// <param name="minContrast">記号と背景の明るさの比の下限。</param>
    public static IReadOnlyList<string> Evaluate(uint[] pixels, int width, int height, double scale, IReadOnlyList<uint>? themeColors, double minContrast = MinContrast)
    {
        var problems = new List<string>();
        uint At(int x, int y) => pixels[(Math.Clamp(y, 0, height - 1) * width) + Math.Clamp(x, 0, width - 1)];

        int half = (int)(10 * scale);
        int centerY = (int)(ButtonHeight / 2.0 * scale);
        for (int i = 0; i < ButtonNames.Length; i++)
        {
            int centerX = width - (int)((i + 0.5) * ButtonWidth * scale);
            uint background = At(centerX - half, centerY - half);
            double best = 1;
            for (int y = centerY - half; y <= centerY + half; y++)
            {
                for (int x = centerX - half; x <= centerX + half; x++)
                {
                    best = Math.Max(best, Contrast(background, At(x, y)));
                }
            }

            if (best < minContrast)
            {
                problems.Add($"「{ButtonNames[i]}」の記号が背景と区別できません (明るさの比 {best:F1})");
            }
        }

        if (themeColors is not null)
        {
            CheckThemeColors(pixels, themeColors, problems);
        }

        return problems;
    }

    /// <summary>
    /// いちばん多い色がテーマの背景の色で、まとまった面積のテーマ以外の色がない。文字の縁のなめらかにした色は、テーマの 2 色の間の色なので数えない
    /// (同じ文字が並ぶ Hex 表示では、1 色の面積が大きくなる)。
    /// </summary>
    private static void CheckThemeColors(uint[] pixels, IReadOnlyList<uint> themeColors, List<string> problems)
    {
        var counts = new Dictionary<uint, int>();
        foreach (uint pixel in pixels)
        {
            counts[pixel] = counts.GetValueOrDefault(pixel) + 1;
        }

        uint most = counts.MaxBy(c => c.Value).Key;
        if (!Close(most, themeColors[0]))
        {
            problems.Add($"背景の色が #{most:X6} です (テーマの色は #{themeColors[0]:X6})");
        }

        int area = (int)(pixels.Length * MinForeignArea);
        foreach ((uint color, int count) in counts.Where(c => c.Value >= area && !IsThemeColor(c.Key, themeColors)).OrderByDescending(c => c.Value).Take(3))
        {
            problems.Add($"テーマにない色 #{color:X6} が画面の {count * 100.0 / pixels.Length:F1}% にあります");
        }
    }

    /// <summary>
    /// 文字の輪郭のぼやけの幅 (画素)。範囲の中を横に見て、背景から文字の色へ変わるまでの途中の色の幅の中央値を返す (輪郭がなければ 0)。
    /// 拡大した画像を引き伸ばして描くと、倍率と同じくらいの幅になる。
    /// </summary>
    public static int EdgeWidth(uint[] pixels, int width, int left, int top, int right, int bottom)
    {
        var values = new List<double>();
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                values.Add(Gray(pixels[(y * width) + x]));
            }
        }

        double background = values.GroupBy(v => v).MaxBy(g => g.Count())!.Key;
        double ink = values.MaxBy(v => Math.Abs(v - background));
        double Level(int x, int y) => (Gray(pixels[(y * width) + x]) - background) / (ink - background);

        var widths = new List<int>();
        for (int y = top; y < bottom && ink != background; y++)
        {
            int start = -1;
            for (int x = left; x < right; x++)
            {
                double level = Level(x, y);
                if (level <= 0.1)
                {
                    start = x;
                }
                else if (level >= 0.9)
                {
                    if (start >= 0)
                    {
                        widths.Add(x - start - 1);
                    }

                    start = -1;
                }
            }
        }

        widths.Sort();
        return widths.Count == 0 ? 0 : widths[widths.Count / 2];
    }

    private static double Gray(uint rgb) => (((rgb >> 16) & 0xFF) + ((rgb >> 8) & 0xFF) + (rgb & 0xFF)) / 765.0;

    /// <summary>2 つの色の明るさの比 (WCAG 2.1)。</summary>
    public static double Contrast(uint a, uint b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(uint rgb)
    {
        static double Channel(uint value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel((rgb >> 16) & 0xFF)) + (0.7152 * Channel((rgb >> 8) & 0xFF)) + (0.0722 * Channel(rgb & 0xFF));
    }

    private static bool IsThemeColor(uint color, IReadOnlyList<uint> themeColors) =>
        themeColors.Any(a => Close(color, a) || themeColors.Any(b => IsBetween(color, a, b)));

    /// <summary><paramref name="color"/> が 2 色を混ぜた色か。</summary>
    private static bool IsBetween(uint color, uint a, uint b)
    {
        for (int step = 1; step < BlendSteps; step++)
        {
            uint Mix(int shift) => (uint)((((a >> shift) & 0xFF) * (BlendSteps - step)) + (((b >> shift) & 0xFF) * step)) / BlendSteps;

            if (Close(color, (Mix(16) << 16) | (Mix(8) << 8) | Mix(0)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Close(uint a, uint b)
    {
        static bool Near(uint x, uint y, int shift) => Math.Abs((int)((x >> shift) & 0xFF) - (int)((y >> shift) & 0xFF)) <= ColorTolerance;

        return Near(a, b, 16) && Near(a, b, 8) && Near(a, b, 0);
    }

    /// <summary>ウィンドウのクライアント領域を判定する。ウィンドウがなければ問題として返す。</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> Run(nint hwnd, bool highContrast)
    {
        if (hwnd == 0 || !GetClientRect(hwnd, out Rect client) || client.Right <= 0 || client.Bottom <= 0
            || !GetWindowRect(hwnd, out Rect window))
        {
            return ["HexEditor のウィンドウが見つかりません"];
        }

        var origin = default(Point);
        ClientToScreen(hwnd, ref origin);
        int fullWidth = window.Right - window.Left;
        int fullHeight = window.Bottom - window.Top;
        int[] full = new int[fullWidth * fullHeight];
        nint screen = GetDC(0);
        nint dc = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, fullWidth, fullHeight);
        nint old = SelectObject(dc, bitmap);
        PrintWindow(hwnd, dc, 2);
        SelectObject(dc, old);
        var header = new BitmapInfoHeader { Size = 40, Width = fullWidth, Height = -fullHeight, Planes = 1, BitCount = 32 };
        GetDIBits(dc, bitmap, 0, (uint)fullHeight, full, ref header, 0);
        DeleteObject(bitmap);
        DeleteDC(dc);
        ReleaseDC(0, screen);

        int width = client.Right;
        int height = client.Bottom;
        int left = origin.X - window.Left;
        int top = origin.Y - window.Top;
        uint[] pixels = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = (uint)full[((y + top) * fullWidth) + x + left] & 0xFFFFFF;
            }
        }

        uint[]? theme = highContrast ? [.. SystemColors.Select(index => ToRgb(GetSysColor(index)))] : null;
        return Evaluate(pixels, width, height, GetDpiForWindow(hwnd) / 96.0, theme, GetForegroundWindow() == hwnd ? MinContrast : MinInactiveContrast);
    }

    private static uint ToRgb(uint bgr) => ((bgr & 0xFF) << 16) | (bgr & 0xFF00) | ((bgr >> 16) & 0xFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
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
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

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
    private static extern int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, [Out] int[] bits, ref BitmapInfoHeader info, uint usage);
}
