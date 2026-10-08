using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HexEditor.ShellExtension;

/// <summary>
/// MSIX 版のパッケージの中での <see cref="ICommandHost"/>。この DLL と同じフォルダの HexEditor.exe を起動する。
/// メニューの名前はパッケージのリソース (resources.pri の <c>Shell_OpenWith</c>。従来のメニューの UI-54 と同じ文字列) を
/// Windows の言語の設定で選んで使う。読めなければ英語。
/// </summary>
internal sealed partial class PackageCommandHost : ICommandHost
{
    public const string EnglishTitle = "Open with HexEditor";
    public const string ExeName = "HexEditor.exe";

    /// <summary>リソースのキー (Strings/&lt;言語&gt;/Resources.resw)。</summary>
    public const string TitleResource = "Shell_OpenWith";

    public static PackageCommandHost Instance { get; } = new();

    private string? _title;
    private string? _exePath;

    public string ExePath => _exePath ??= Path.Combine(ModuleFolder() ?? AppContext.BaseDirectory, ExeName);

    public string Title() => _title ??= LoadTitle();

    public void Launch(IReadOnlyList<string> files)
    {
        var start = new ProcessStartInfo(ExePath) { UseShellExecute = false };
        foreach (string file in files)
        {
            start.ArgumentList.Add(file);
        }

        // 2 つ目以降の起動は単一インスタンス (UI-15) が既存のウィンドウにまとめる。
        using Process? process = Process.Start(start);
    }

    /// <summary>パッケージのリソース (<c>@{&lt;PackageFullName&gt;?ms-resource://HexEditor/Resources/Shell_OpenWith}</c>)。</summary>
    private static unsafe string LoadTitle()
    {
        try
        {
            // パッケージの外 (単体テストなど) では APPMODEL_ERROR_NO_PACKAGE になり、英語を使う。
            const int MaxName = 130;
            char* name = stackalloc char[MaxName];
            int length = MaxName;
            if (GetCurrentPackageFullName(&length, name) != 0 || length <= 1)
            {
                return EnglishTitle;
            }

            string fullName = new(name, 0, length - 1);
            const int Size = 512;
            char* buffer = stackalloc char[Size];
            int hr = SHLoadIndirectString($"@{{{fullName}?ms-resource://HexEditor/Resources/{TitleResource}}}", buffer, Size, 0);
            string text = hr >= 0 ? new string(buffer) : string.Empty;
            return string.IsNullOrWhiteSpace(text) ? EnglishTitle : text;
        }
        catch (Exception)
        {
            return EnglishTitle;
        }
    }

    /// <summary>この DLL のフォルダ (読み込まれたモジュールのパス)。取れなければ null。</summary>
    private static unsafe string? ModuleFolder()
    {
        const uint FromAddress = 0x4;
        const uint UnchangedRefCount = 0x2;
        nint address = (nint)(delegate* unmanaged<int>)&Exports.DllCanUnloadNow;
        nint module;
        if (GetModuleHandleExW(FromAddress | UnchangedRefCount, address, &module) == 0 || module == 0)
        {
            return null;
        }

        const int Size = 32768;
        char[] buffer = new char[Size];
        int length;
        fixed (char* p = buffer)
        {
            length = GetModuleFileNameW(module, p, Size);
        }

        if (length <= 0 || length >= Size)
        {
            return null;
        }

        string path = new(buffer, 0, length);
        return Path.GetFileName(path).Equals(ExplorerCommand.DllName, StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path) : null;
    }

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentPackageFullName(int* length, char* name);

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int SHLoadIndirectString(string source, char* buffer, int size, nint reserved);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetModuleHandleExW(uint flags, nint address, nint* module);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetModuleFileNameW(nint module, char* buffer, int size);
}
