namespace HexEditor.Platform.Tests;

/// <summary>GUI のコマンドライン (AUTO-37 のうち今の版で扱うもの) と、起動の転送で受け取る文字列 (UI-15)。</summary>
public sealed class CommandLineTests
{
    [Fact]
    public void ValuesOfOptionsAreNotFiles()
    {
        CommandLine c = CommandLine.Parse(["--ui-lang", "ja", "a.bin", "--test-profile", @"C:\p", "--offset", "0x10", "b.bin", "--new-window"]);
        Assert.Equal(["a.bin", "b.bin"], c.Files);
        Assert.Equal("ja", c.UiLanguage);
        Assert.Equal(@"C:\p", c.TestProfile);
        Assert.Equal("0x10", c.Offset);
        Assert.True(c.NewWindow);
        Assert.False(c.NewInstance);
    }

    [Fact]
    public void ParseStringFollowsWindowsQuotingAndDropsTheExe()
    {
        CommandLine c = CommandLine.ParseString("\"C:\\Program Files\\HexEditor\\HexEditor.exe\" \"C:\\My Files\\a.bin\" -g 100 --new-instance");
        Assert.Equal([@"C:\My Files\a.bin"], c.Files);
        Assert.Equal("100", c.Offset);
        Assert.True(c.NewInstance);
    }

    [Fact]
    public void EmptyCommandLineHasNoFiles()
    {
        Assert.Empty(CommandLine.ParseString(string.Empty).Files);
        Assert.Empty(CommandLine.Parse([]).Files);
    }
}

/// <summary>起動時のエラーの記録 (PKG-11 の「エラー」)。</summary>
public sealed class StartupErrorTests
{
    [Fact]
    public void WritesStartupErrorLogWithoutPaths()
    {
        using var temp = new Support.TempFolder();
        string logs = temp.Sub("logs");
        var error = new IOException(@"Could not open C:\Users\someone\secret-content.bin");
        string? path = StartupError.WriteLog(error, "environment", logs, temp.Path);
        Assert.Equal(Path.Combine(logs, StartupError.FileName), path);
        string text = File.ReadAllText(path!);
        Assert.Contains("environment", text);
        Assert.Contains("IOException", text);
        Assert.DoesNotContain("secret-content.bin", text);
        Assert.DoesNotContain(@"C:\Users", text);
    }

    [Fact]
    public void FallsBackToTempWhenTheLogsFolderCannotBeCreated()
    {
        using var temp = new Support.TempFolder();
        string blocker = temp.Sub("blocker");
        File.WriteAllText(blocker, string.Empty);
        string? path = StartupError.WriteLog(new InvalidOperationException("x"), "com", Path.Combine(blocker, "logs"), temp.Path);
        Assert.Equal(Path.Combine(temp.Path, "HexEditor", "logs", StartupError.FileName), path);
    }

    [Theory]
    [InlineData(@"open C:\data\a.bin failed", "open <path> failed")]
    [InlineData(@"share \\server\share\x.bin gone", "share <path> gone")]
    [InlineData("no paths here", "no paths here")]
    public void RedactsPaths(string input, string expected) => Assert.Equal(expected, Redaction.RedactPaths(input));
}
