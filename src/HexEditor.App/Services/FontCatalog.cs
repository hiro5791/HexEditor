using System.Runtime.InteropServices;

namespace HexEditor.App.Services;

/// <summary>
/// Hex 表示のフォントの一覧 (UI-29 の仕様 2)。DirectWrite のシステムのフォントコレクションから、ファミリー名と等幅かどうか
/// (<c>IDWriteFont1::IsMonospacedFont</c>) を取る。設定画面のフォントの一覧はこれを使う。
/// </summary>
public static class FontCatalog
{
    /// <summary>既定のフォントと、ない場合の代わり (UI-29 の仕様 2)。</summary>
    public const string PreferredFont = "Cascadia Mono";

    public const string FallbackFont = "Consolas";

    private static IReadOnlyList<FontFamilyInfo>? _cache;

    /// <summary>フォントのファミリー (名前は英語名、なければ最初の名前)。</summary>
    public readonly record struct FontFamilyInfo(string Name, bool Monospaced);

    /// <summary>
    /// テスト用: 入っていないことにするフォント (Cascadia Mono がない環境の模擬。TC-UI-29-02)。
    /// </summary>
    public static HashSet<string> HiddenForTest { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static Task? _warmUp;

    /// <summary>一覧を作り終えたか (作り終えるまでは <see cref="Resolve"/> は XAML のフォントの代替に任せる)。</summary>
    public static bool IsReady => _cache is not null;

    /// <summary>
    /// 一覧をバックグラウンドで作る (フォントの列挙は数百 ms かかるため、UI スレッドで待たない)。作り終えたら <paramref name="ready"/> を呼ぶ
    /// (スレッドプールから)。
    /// </summary>
    public static void WarmUp(Action ready)
    {
        _warmUp ??= Task.Run(() => Families());
        _warmUp.ContinueWith(_ => ready(), TaskScheduler.Default);
    }

    /// <summary>システムのフォントのファミリー。DirectWrite が使えなければ空。</summary>
    public static IReadOnlyList<FontFamilyInfo> Families()
    {
        if (_cache is null)
        {
            List<FontFamilyInfo> list;
            try
            {
                list = Enumerate();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or EntryPointNotFoundException or DllNotFoundException)
            {
                AppLog.Warning($"FontCatalog: DirectWrite のフォントを列挙できません ({ex.Message})");
                list = [];
            }

            _cache = list;
        }

        return [.. _cache.Where(f => !HiddenForTest.Contains(f.Name))];
    }

    /// <summary>設定画面の一覧 (UI-29 の仕様 2): 既定では等幅フォントだけ、<paramref name="all"/> なら全フォント。</summary>
    public static IReadOnlyList<string> ListForSettings(bool all) =>
        [.. Families().Where(f => all || f.Monospaced).Select(f => f.Name).Order(StringComparer.CurrentCultureIgnoreCase)];

    public static bool IsInstalled(string name) =>
        Families().Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 描画に使うフォント (UI-29 の仕様 2 と「エラー」): 設定のフォントがなければ Cascadia Mono、それもなければ Consolas。
    /// <paramref name="missing"/> には、設定したのに見つからないフォント名を返す (設定画面に「… が見つかりません」と出す)。
    /// </summary>
    public static string Resolve(string? configured, out string? missing)
    {
        missing = null;
        if (!IsReady || Families().Count == 0)
        {
            // 列挙できない環境では、XAML のフォントの代替 (カンマ区切り) に任せる。
            return string.IsNullOrWhiteSpace(configured) ? PreferredFont + ", " + FallbackFont : configured + ", " + FallbackFont;
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (IsInstalled(configured))
            {
                return configured;
            }

            missing = configured;
        }

        return IsInstalled(PreferredFont) ? PreferredFont : FallbackFont;
    }

    private static List<FontFamilyInfo> Enumerate()
    {
        Guid iid = typeof(IDWriteFactory).GUID;
        Marshal.ThrowExceptionForHR(DWriteCreateFactory(0, ref iid, out object factoryObject));
        var factory = (IDWriteFactory)factoryObject;
        Marshal.ThrowExceptionForHR(factory.GetSystemFontCollection(out IDWriteFontCollection collection, false));
        uint count = collection.GetFontFamilyCount();
        var result = new List<FontFamilyInfo>((int)count);
        for (uint i = 0; i < count; i++)
        {
            if (collection.GetFontFamily(i, out IDWriteFontFamily family) < 0)
            {
                continue;
            }

            string? name = FamilyName(family);
            if (name is null)
            {
                continue;
            }

            bool mono = false;
            if (family.GetFirstMatchingFont(400, 5, 0, out IDWriteFont font) >= 0 && font is IDWriteFont1 font1)
            {
                mono = font1.IsMonospacedFont();
            }

            if (!result.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new FontFamilyInfo(name, mono));
            }
        }

        return result;
    }

    private static string? FamilyName(IDWriteFontFamily family)
    {
        if (family.GetFamilyNames(out IDWriteLocalizedStrings names) < 0)
        {
            return null;
        }

        uint index = 0;
        if (names.FindLocaleName("en-us", out uint found, out bool exists) >= 0 && exists)
        {
            index = found;
        }

        if (names.GetStringLength(index, out uint length) < 0)
        {
            return null;
        }

        // 文字列は UTF-16 のバッファで受け取る (char[] の既定の変換に頼らない)。
        IntPtr buffer = Marshal.AllocHGlobal((int)(length + 1) * 2);
        try
        {
            return names.GetString(index, buffer, length + 1) >= 0 ? Marshal.PtrToStringUni(buffer, (int)length) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("dwrite.dll")]
    private static extern int DWriteCreateFactory(int factoryType, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    // ---- DirectWrite のインターフェイス (使う方法までの vtable の順に並べる) ----

    [ComImport]
    [Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFactory
    {
        [PreserveSig]
        int GetSystemFontCollection(out IDWriteFontCollection collection, [MarshalAs(UnmanagedType.Bool)] bool checkForUpdates);
    }

    [ComImport]
    [Guid("a84cee02-3eea-4eee-a827-87c1a02a0fcc")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontCollection
    {
        [PreserveSig]
        uint GetFontFamilyCount();

        [PreserveSig]
        int GetFontFamily(uint index, out IDWriteFontFamily family);
    }

    [ComImport]
    [Guid("da20d8ef-812a-4c43-9802-62ec4abd7add")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontFamily
    {
        // IDWriteFontList
        [PreserveSig]
        int GetFontCollection(out IntPtr collection);

        [PreserveSig]
        uint GetFontCount();

        [PreserveSig]
        int GetFont(uint index, out IntPtr font);

        // IDWriteFontFamily
        [PreserveSig]
        int GetFamilyNames(out IDWriteLocalizedStrings names);

        [PreserveSig]
        int GetFirstMatchingFont(int weight, int stretch, int style, out IDWriteFont font);
    }

    [ComImport]
    [Guid("acd16696-8c14-4f5d-877e-fe3fc1d32737")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFont
    {
    }

    [ComImport]
    [Guid("acd16696-8c14-4f5d-877e-fe3fc1d32738")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFont1
    {
        // IDWriteFont (11 個)
        void GetFontFamily();

        void GetWeight();

        void GetStretch();

        void GetStyle();

        void IsSymbolFont();

        void GetFaceNames();

        void GetInformationalStrings();

        void GetSimulations();

        void GetMetrics();

        void HasCharacter();

        void CreateFontFace();

        // IDWriteFont1
        void GetMetrics1();

        void GetPanose();

        void GetUnicodeRanges();

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsMonospacedFont();
    }

    [ComImport]
    [Guid("08256209-099a-4b34-b86d-c22b110e7771")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteLocalizedStrings
    {
        [PreserveSig]
        uint GetCount();

        [PreserveSig]
        int FindLocaleName([MarshalAs(UnmanagedType.LPWStr)] string localeName, out uint index, [MarshalAs(UnmanagedType.Bool)] out bool exists);

        [PreserveSig]
        int GetLocaleNameLength(uint index, out uint length);

        [PreserveSig]
        int GetLocaleName(uint index, IntPtr buffer, uint size);

        [PreserveSig]
        int GetStringLength(uint index, out uint length);

        [PreserveSig]
        int GetString(uint index, IntPtr buffer, uint size);
    }
}
