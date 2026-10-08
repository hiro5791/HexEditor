namespace HexEditor.UITests.Infrastructure;

/// <summary>テストで起動する HexEditor の実行ファイルを探す。</summary>
public static class AppLocator
{
    /// <summary>実行ファイルの名前 (HexEditor.exe に変わっても見つける)。</summary>
    private static readonly string[] ExeNames = ["HexEditor.exe", "HexEditor.App.exe"];

    private static readonly Lazy<string> Exe = new(Find);

    /// <summary>
    /// 実行ファイルのパス。環境変数 HEXEDITOR_APP_EXE があればそれを使い、なければ src/HexEditor.App/bin の下で
    /// 最も新しくビルドしたものを使う (テスト用のビルドでないと、命令の通り道が開かないので起動の確認で失敗する)。
    /// </summary>
    public static string ExePath => Exe.Value;

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>HEXEDITOR_APP_EXE で配布形態のビルド (CI の ui-distro) を指定した。false なら開発中のビルド (dotnet build の出力)。</summary>
    public static bool IsConfigured => Environment.GetEnvironmentVariable("HEXEDITOR_APP_EXE") is { Length: > 0 };

    private static string Find()
    {
        if (Environment.GetEnvironmentVariable("HEXEDITOR_APP_EXE") is { Length: > 0 } configured)
        {
            return File.Exists(configured)
                ? Path.GetFullPath(configured)
                : throw new FileNotFoundException($"HEXEDITOR_APP_EXE が指すファイルがありません: {configured}");
        }

        string bin = Path.Combine(RepositoryRoot, "src", "HexEditor.App", "bin");
        string? newest = Directory.Exists(bin)
            ? ExeNames
                .SelectMany(name => Directory.EnumerateFiles(bin, name, SearchOption.AllDirectories))
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "publish" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !p.Contains("AppPackages", StringComparison.OrdinalIgnoreCase))
                .Where(HasTestHooks)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        return newest ?? throw new FileNotFoundException(
            $"テスト用のビルドの HexEditor が見つかりません。先に `dotnet build src/HexEditor.App -p:Platform=x64` でビルドしてください ({bin})。");
    }

    /// <summary>テスト用のビルド (HEX_TEST_HOOKS) か: アプリの DLL にテスト用の命令の通り道の型があるかで判定する。</summary>
    private static bool HasTestHooks(string exe)
    {
        string dll = Path.ChangeExtension(exe, ".dll");
        if (!File.Exists(dll))
        {
            dll = Path.Combine(Path.GetDirectoryName(exe)!, "HexEditor.App.dll");
        }

        if (!File.Exists(dll))
        {
            return false;
        }

        ReadOnlySpan<byte> marker = "TestChannel"u8;
        return File.ReadAllBytes(dll).AsSpan().IndexOf(marker) >= 0;
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HexEditor.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("リポジトリのルート (HexEditor.slnx のあるフォルダ) が見つかりません。");
    }
}
