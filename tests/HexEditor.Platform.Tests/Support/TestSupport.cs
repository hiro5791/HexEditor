namespace HexEditor.Platform.Tests.Support;

/// <summary>テスト用の補助。</summary>
public static class TestSupport
{
    /// <summary>テストケース ID の Trait 名 (テスト方針 6.2)。</summary>
    public const string TC = "TC";

    /// <summary>リポジトリのルート (HexEditor.slnx のあるフォルダ)。</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string RepoFile(string relative) => Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HexEditor.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("HexEditor.slnx が見つかりません。");
    }
}

/// <summary>テストごとの一時フォルダ。破棄すると消す。</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HexEditorPlatformTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
