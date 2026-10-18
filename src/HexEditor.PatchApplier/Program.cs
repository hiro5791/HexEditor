using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using HexEditor.Core.Patches;
using HexEditor.Platform.Localization;

namespace HexEditor.PatchApplier;

/// <summary>
/// 自己適用型パッチの適用プログラム (EDIT-44 の仕様 2): 対象ファイルの選択 (既定のファイル名はパッチ作成時に指定)、変換前の確認 (EDIT-43 の 3)、
/// バックアップの作成 (既定オン)、適用、結果の表示を行う。適用はストリームで行い、ファイルサイズに上限を設けない。一時ファイルに書いてから
/// 置き換えるため、途中で失敗しても対象ファイルは変わらない。
/// </summary>
internal static partial class Program
{
    /// <summary>終了コード: 0 適用した、1 キャンセル・不一致で適用しなかった、2 エラー。</summary>
    [STAThread]
    private static int Main()
    {
        Strings strings = Strings.Default;
        try
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("process path");
            using var self = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (SelfPatchContainer.Find(self) is not { } payload)
            {
                Native.Message(strings.Title, strings["NoPatch"], Native.MB_ICONERROR | Native.MB_OK);
                return 2;
            }

            using var zip = new ZipArchive(new SubStream(self, payload.Offset, payload.Length), ZipArchiveMode.Read);
            strings = Strings.Load(zip);
            HexPatchManifest manifest = HexPatchManifest.Read(zip);
            return Run(Path.GetDirectoryName(exe)!, zip, manifest, strings);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            Native.Message(strings.Title, string.Format(strings["Failed"], ex.Message), Native.MB_ICONERROR | Native.MB_OK);
            return 2;
        }
    }

    private static int Run(string directory, ZipArchive zip, HexPatchManifest manifest, Strings strings)
    {
        string intro = manifest.Message is { Length: > 0 } m ? m + "\n\n" + strings["Intro"] : strings["Intro"];
        if (Native.Message(strings.Title, intro, Native.MB_ICONINFORMATION | Native.MB_OKCANCEL) != Native.IDOK)
        {
            return 1;
        }

        // 1. 対象ファイルの選択。
        string? target = Native.OpenFile(strings["SelectTitle"], strings["AllFiles"], directory, manifest.DefaultFileName);
        if (target is null)
        {
            return 1;
        }

        // 2. 変換前の確認 (長さと部分的な SHA-256。全体の SHA-256 があればそれも)。一致しなければ警告し、既定のボタンは「いいえ」。
        SourceIdentity actual;
        using (var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            actual = SourceIdentity.Compute(input, full: manifest.Source.FullSha256 is not null);
        }

        if (!actual.Matches(manifest.Source)
            && Native.Message(strings.Title, string.Format(strings["Mismatch"], Path.GetFileName(target)),
                Native.MB_ICONWARNING | Native.MB_YESNO | Native.MB_DEFBUTTON2) != Native.IDYES)
        {
            return 1;
        }

        // 3. バックアップ (既定オン)。
        string backup = BackupPath(target);
        int answer = Native.Message(strings.Title, string.Format(strings["Backup"], Path.GetFileName(backup)),
            Native.MB_ICONQUESTION | Native.MB_YESNOCANCEL);
        if (answer == Native.IDCANCEL)
        {
            return 1;
        }

        bool makeBackup = answer == Native.IDYES;

        // 4. 適用: 同じフォルダの一時ファイルに書き、変換後の識別情報を確かめてから置き換える。
        string temp = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            using (Stream data = zip.GetEntry(HexPatchManifest.DataEntry)!.Open())
            {
                manifest.ApplyStreaming(input, data, output);
                output.Flush(flushToDisk: true);
                if (!SourceIdentity.Compute(output, full: manifest.Target.FullSha256 is not null).Matches(manifest.Target))
                {
                    throw new IOException(strings["ResultMismatch"]);
                }
            }

            File.Replace(temp, target, makeBackup ? backup : null, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HexPatchFormatException)
        {
            TryDelete(temp);
            Native.Message(strings.Title, string.Format(strings["Failed"], ex.Message), Native.MB_ICONERROR | Native.MB_OK);
            return 2;
        }

        // 5. 結果の表示。
        string done = string.Format(strings["Success"], Path.GetFileName(target));
        if (makeBackup)
        {
            done += "\n" + string.Format(strings["BackupDone"], Path.GetFileName(backup));
        }

        Native.Message(strings.Title, done, Native.MB_ICONINFORMATION | Native.MB_OK);
        return 0;
    }

    /// <summary>バックアップのファイル名 (<c>名前.bak</c>。あれば <c>名前.bak2</c> …)。</summary>
    private static string BackupPath(string target)
    {
        string path = target + ".bak";
        for (int i = 2; File.Exists(path); i++)
        {
            path = target + ".bak" + i;
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// 表示する文字列。パッチを作ったアプリが、23 言語の文字列 (アプリのリソースの <c>PatchApplier_*</c>) をパッチの中の <c>applier-strings.json</c> に入れる
/// (EDIT-44 の仕様 4)。Windows の表示言語の優先順位で選び、なければ英語 (ここに持つ既定の文)。
/// </summary>
internal sealed class Strings(IReadOnlyDictionary<string, string> values)
{
    public const string Entry = "applier-strings.json";

    public static Strings Default { get; } = new(new Dictionary<string, string>
    {
        ["Title"] = "Apply patch",
        ["NoPatch"] = "This program does not contain a patch.",
        ["Intro"] = "This program applies a patch to a file. Select the file to patch.",
        ["SelectTitle"] = "Select the file to patch",
        ["AllFiles"] = "All files",
        ["Mismatch"] = "This patch may be for a different file: {0} does not match the original recorded in the patch.\n\nApply the patch anyway?",
        ["Backup"] = "Create a backup of the original file ({0}) before applying the patch?",
        ["Success"] = "The patch was applied to {0}.",
        ["BackupDone"] = "The original file was saved as {0}.",
        ["Failed"] = "The patch could not be applied: {0}\n\nThe file was not changed.",
        ["ResultMismatch"] = "The patched data does not match the result recorded in the patch.",
    });

    public string Title => this["Title"];

    public string this[string key] => values.TryGetValue(key, out string? v) && v.Length > 0 ? v : Default.Get(key);

    private string Get(string key) => values.TryGetValue(key, out string? v) ? v : key;

    /// <summary>パッチの中の文字列から、Windows の表示言語に合うものを選ぶ。</summary>
    public static Strings Load(ZipArchive zip)
    {
        if (zip.GetEntry(Entry) is not { } entry)
        {
            return Default;
        }

        try
        {
            using Stream s = entry.Open();
            using JsonDocument doc = JsonDocument.Parse(s);
            string language = DisplayLanguages.Resolve(Native.PreferredLanguages());
            foreach (string tag in new[] { language, "en" })
            {
                if (doc.RootElement.TryGetProperty(tag, out JsonElement table) && table.ValueKind == JsonValueKind.Object)
                {
                    var values = new Dictionary<string, string>();
                    foreach (JsonProperty p in table.EnumerateObject())
                    {
                        values[p.Name] = p.Value.GetString() ?? string.Empty;
                    }

                    return new Strings(values);
                }
            }
        }
        catch (JsonException)
        {
        }

        return Default;
    }
}

/// <summary>Windows の標準のダイアログ (メッセージボックスとファイルを開くダイアログ)。</summary>
internal static unsafe partial class Native
{
    public const uint MB_OK = 0x0, MB_OKCANCEL = 0x1, MB_YESNOCANCEL = 0x3, MB_YESNO = 0x4;
    public const uint MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONWARNING = 0x30, MB_ICONINFORMATION = 0x40;
    public const uint MB_DEFBUTTON2 = 0x100;
    public const int IDOK = 1, IDCANCEL = 2, IDYES = 6;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint hwnd, string text, string caption, uint type);

    [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOpenFileName(OpenFileName* ofn);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserPreferredUILanguages(uint flags, out uint count, char* buffer, ref uint size);

    public static int Message(string caption, string text, uint type) => MessageBox(0, text, caption, type);

    /// <summary>ファイルを開くダイアログ。キャンセルなら null。</summary>
    public static string? OpenFile(string title, string allFiles, string directory, string? defaultName)
    {
        const int Max = 32768;
        char[] file = new char[Max];
        if (defaultName is { Length: > 0 } name && name.Length < 260)
        {
            name.CopyTo(0, file, 0, name.Length);
        }

        string filter = allFiles + "\0*.*\0\0";
        fixed (char* f = file)
        fixed (char* flt = filter)
        fixed (char* t = title)
        fixed (char* dir = directory)
        {
            var ofn = new OpenFileName
            {
                lStructSize = sizeof(OpenFileName),
                lpstrFilter = flt,
                lpstrFile = f,
                nMaxFile = Max,
                lpstrInitialDir = dir,
                lpstrTitle = t,
                Flags = 0x00001000 | 0x00000800 | 0x00080000 | 0x02000000 | 0x00000008, // FILEMUSTEXIST | PATHMUSTEXIST | EXPLORER | DONTADDTORECENT | NOCHANGEDIR
            };
            if (!GetOpenFileName(&ofn))
            {
                return null;
            }
        }

        int end = Array.IndexOf(file, '\0');
        return new string(file, 0, end < 0 ? file.Length : end);
    }

    /// <summary>Windows の表示言語の優先順位 (BCP-47)。</summary>
    public static IReadOnlyList<string> PreferredLanguages()
    {
        const uint MUI_LANGUAGE_NAME = 0x8;
        uint size = 0;
        if (!GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out _, null, ref size) || size == 0)
        {
            return ["en"];
        }

        char[] buffer = new char[size];
        fixed (char* b = buffer)
        {
            if (!GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, out _, b, ref size))
            {
                return ["en"];
            }
        }

        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileName
    {
        public int lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public char* lpstrFilter;
        public char* lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public char* lpstrFile;
        public int nMaxFile;
        public char* lpstrFileTitle;
        public int nMaxFileTitle;
        public char* lpstrInitialDir;
        public char* lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public char* lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public char* lpTemplateName;
        public nint pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }
}
