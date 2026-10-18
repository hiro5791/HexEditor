using System.Runtime.InteropServices;
using HexEditor.Core.Printing;

namespace HexEditor.App.Services;

/// <summary>
/// Windows の印刷 (GDI + スプーラー) で、印刷の計画 (<see cref="PrintPlan"/>) のページをプリンタへ送る (TOOL-01 の仕様 7)。WinUI 3 の PrintManager は
/// 印刷ダイアログが OS のものになり、設定とプレビューを 1 つのダイアログに置く仕様 (TOOL-01 の仕様 1) を満たせないため、Win32 の印刷 API を使う。
/// ページの内容は PDF・プレビューと同じ <see cref="PrintPage"/> から描く。テストでは物理的なプリンタに送らない (ビルドの test-hook で PDF に代える)。
/// </summary>
internal static class GdiPrinter
{
    private const uint PrinterEnumLocal = 0x2;
    private const uint PrinterEnumConnections = 0x4;
    private const int LogPixelsX = 88;
    private const int LogPixelsY = 90;
    private const int PhysicalOffsetX = 112;
    private const int PhysicalOffsetY = 113;
    private const uint DmOrientation = 0x1;
    private const uint DmPaperSize = 0x2;
    private const uint DmCopies = 0x100;
    private const uint DmInBuffer = 8;
    private const uint DmOutBuffer = 2;
    private const int TaBaseline = 24;
    private const int Transparent = 1;
    private const int EtoClipped = 0x4;

    /// <summary>登録されているプリンタの名前 (「Microsoft Print to PDF」を含む)。</summary>
    public static IReadOnlyList<string> Names()
    {
        var names = new List<string>();
        try
        {
            EnumPrinters(PrinterEnumLocal | PrinterEnumConnections, null, 4, IntPtr.Zero, 0, out uint needed, out _);
            if (needed == 0)
            {
                return names;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (EnumPrinters(PrinterEnumLocal | PrinterEnumConnections, null, 4, buffer, needed, out _, out uint count))
                {
                    int size = Marshal.SizeOf<PrinterInfo4>();
                    for (int i = 0; i < count; i++)
                    {
                        PrinterInfo4 info = Marshal.PtrToStructure<PrinterInfo4>(buffer + i * size);
                        if (!string.IsNullOrEmpty(info.PrinterName))
                        {
                            names.Add(info.PrinterName);
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return names;
        }

        return names;
    }

    /// <summary>Windows の既定のプリンタの名前。</summary>
    public static string? DefaultName()
    {
        uint length = 0;
        GetDefaultPrinter(null, ref length);
        if (length == 0)
        {
            return null;
        }

        var name = new System.Text.StringBuilder((int)length);
        return GetDefaultPrinter(name, ref length) ? name.ToString() : null;
    }

    private static int PaperCode(PaperKind kind) => kind switch
    {
        PaperKind.Letter => 1,
        PaperKind.Legal => 5,
        PaperKind.A3 => 8,
        PaperKind.A5 => 11,
        _ => 9,
    };

    /// <summary>
    /// 印刷する。ページごとに <paramref name="progress"/> を呼ぶ (印刷したページの数)。キャンセルされたら <c>AbortDoc</c> して例外を投げる。
    /// 読めなかったバイトがあれば true を返す。プリンタが開けない・印刷に失敗した場合は Windows のエラーの内容を持つ <see cref="IOException"/>。
    /// </summary>
    public static bool Print(string printer, string documentName, int copies, PrintPlan plan, IPrintData data, PrintFields fields,
        SystemFontMetrics fonts, SystemFontCatalog catalog, Action<long>? progress, CancellationToken cancellationToken)
    {
        IntPtr devMode = BuildDevMode(printer, plan.Settings.Paper, plan.Settings.Landscape, copies);
        bool unreadable = false;
        try
        {
            IntPtr dc = CreateDC("WINSPOOL", printer, null, devMode);
            if (dc == IntPtr.Zero)
            {
                throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            try
            {
                var docInfo = new DocInfo { Size = Marshal.SizeOf<DocInfo>(), DocName = documentName };
                if (StartDoc(dc, ref docInfo) <= 0)
                {
                    throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
                }

                try
                {
                    double dpiX = GetDeviceCaps(dc, LogPixelsX);
                    double dpiY = GetDeviceCaps(dc, LogPixelsY);
                    double offsetX = GetDeviceCaps(dc, PhysicalOffsetX);
                    double offsetY = GetDeviceCaps(dc, PhysicalOffsetY);
                    var fontCache = new Dictionary<(string Family, int Height), IntPtr>();
                    try
                    {
                        for (long i = 0; i < plan.PrintedPages; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            PrintPage page = plan.Render(i, data, fields);
                            unreadable |= page.HadUnreadable;
                            if (StartPage(dc) <= 0)
                            {
                                throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
                            }

                            DrawPage(dc, page, plan, fonts, catalog, dpiX, dpiY, offsetX, offsetY, fontCache);
                            if (EndPage(dc) <= 0)
                            {
                                throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
                            }

                            progress?.Invoke(i + 1);
                        }
                    }
                    finally
                    {
                        foreach (IntPtr font in fontCache.Values)
                        {
                            DeleteObject(font);
                        }
                    }

                    EndDoc(dc);
                }
                catch
                {
                    AbortDoc(dc);
                    throw;
                }
            }
            finally
            {
                DeleteDC(dc);
            }
        }
        finally
        {
            if (devMode != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(devMode);
            }
        }

        return unreadable;
    }

    private static void DrawPage(IntPtr dc, PrintPage page, PrintPlan plan, SystemFontMetrics fonts, SystemFontCatalog catalog, double dpiX, double dpiY,
        double offsetX, double offsetY, Dictionary<(string, int), IntPtr> fontCache)
    {
        int X(double pt) => (int)Math.Round(pt * dpiX / 72.0 - offsetX);
        int Y(double pt) => (int)Math.Round(pt * dpiY / 72.0 - offsetY);
        SetBkMode(dc, Transparent);
        SetTextAlign(dc, TaBaseline);
        foreach (PrintRect rect in page.Rects)
        {
            IntPtr brush = CreateSolidBrush(Rgb(rect.Color));
            var area = new Rect { Left = X(rect.X), Top = Y(rect.Y), Right = X(rect.X + rect.Width), Bottom = Y(rect.Y + rect.Height) };
            FillRect(dc, ref area, brush);
            DeleteObject(brush);
        }

        foreach (PrintLine line in page.Lines)
        {
            IntPtr pen = CreatePen(0, Math.Max(1, (int)Math.Round(line.Thickness * dpiY / 72.0)), Rgb(line.Color));
            IntPtr old = SelectObject(dc, pen);
            MoveToEx(dc, X(line.X1), Y(line.Y1), IntPtr.Zero);
            LineTo(dc, X(line.X2), Y(line.Y2));
            SelectObject(dc, old);
            DeleteObject(pen);
        }

        int cellPx = (int)Math.Round(plan.CharWidth * dpiX / 72.0);
        foreach (PrintRun run in page.Runs)
        {
            string family = fonts.Primary.Family;
            if (run.Font == PrintFontKind.Substitute && catalog.Substitute(char.ConvertToUtf32(run.Text, 0)) is { } substitute)
            {
                family = substitute.Family;
            }

            int height = -(int)Math.Round(run.Size * dpiY / 72.0);
            if (!fontCache.TryGetValue((family, height), out IntPtr font))
            {
                font = CreateFont(height, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, family);
                fontCache[(family, height)] = font;
            }

            IntPtr previous = SelectObject(dc, font);
            SetTextColor(dc, Rgb(run.Color));

            // 等幅の列がずれないよう、主フォントの文字は 1 文字分の幅を指定して書く。
            int[]? dx = run.Font == PrintFontKind.Primary ? Enumerable.Repeat(cellPx, run.Text.Length).ToArray() : null;
            ExtTextOut(dc, X(run.X), Y(run.Baseline), 0, IntPtr.Zero, run.Text, (uint)run.Text.Length, dx);
            SelectObject(dc, previous);
        }
    }

    private static uint Rgb(PrintColor color) => (uint)(color.R | (color.G << 8) | (color.B << 16));

    /// <summary>プリンタの既定の DEVMODE に、用紙・向き・部数を設定する。失敗したら null (プリンタの既定で印刷する)。</summary>
    private static IntPtr BuildDevMode(string printer, PaperKind paper, bool landscape, int copies)
    {
        if (!OpenPrinter(printer, out IntPtr handle, IntPtr.Zero))
        {
            return IntPtr.Zero;
        }

        try
        {
            int size = DocumentProperties(IntPtr.Zero, handle, printer, IntPtr.Zero, IntPtr.Zero, 0);
            if (size <= 0)
            {
                return IntPtr.Zero;
            }

            IntPtr output = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++)
            {
                Marshal.WriteByte(output, i, 0);
            }

            if (DocumentProperties(IntPtr.Zero, handle, printer, output, IntPtr.Zero, (int)DmOutBuffer) < 0)
            {
                Marshal.FreeHGlobal(output);
                return IntPtr.Zero;
            }

            // DEVMODEW: dmFields @72、dmOrientation @76、dmPaperSize @78、dmCopies @86。
            Marshal.WriteInt32(output, 72, (int)((uint)Marshal.ReadInt32(output, 72) | DmOrientation | DmPaperSize | DmCopies));
            Marshal.WriteInt16(output, 76, (short)(landscape ? 2 : 1));
            Marshal.WriteInt16(output, 78, (short)PaperCode(paper));
            Marshal.WriteInt16(output, 86, (short)Math.Clamp(copies, 1, 999));
            if (DocumentProperties(IntPtr.Zero, handle, printer, output, output, (int)(DmInBuffer | DmOutBuffer)) < 0)
            {
                Marshal.FreeHGlobal(output);
                return IntPtr.Zero;
            }

            return output;
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo4
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string PrinterName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ServerName;

        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo
    {
        public int Size;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string DocName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Output;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? DataType;

        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr printerEnum, uint size, out uint needed, out uint returned);

    [DllImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDefaultPrinter(System.Text.StringBuilder? buffer, ref uint size);

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DocumentProperties(IntPtr window, IntPtr printer, string deviceName, IntPtr devModeOutput, IntPtr devModeInput, int mode);

    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr devMode);

    [DllImport("gdi32.dll", EntryPoint = "StartDocW", SetLastError = true)]
    private static extern int StartDoc(IntPtr dc, ref DocInfo info);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndDoc(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int AbortDoc(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StartPage(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndPage(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr dc, int index);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextAlign(IntPtr dc, uint align);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr dc, uint color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreatePen(int style, int width, uint color);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveToEx(IntPtr dc, int x, int y, IntPtr previous);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LineTo(IntPtr dc, int x, int y);

    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll", EntryPoint = "ExtTextOutW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExtTextOut(IntPtr dc, int x, int y, uint options, IntPtr rect, string text, uint count, int[]? dx);
}
