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

    [Fact]
    public void OpenOptionsOfHexedAreParsedAndTheirValuesAreNotFiles()
    {
        // hexed open が HexEditor.exe に渡す引数 (AUTO-37)。--compare だけは値を 2 つ取る。
        CommandLine c = CommandLine.Parse([
            @"C:\w\a.bin", "-g", "end-16", "--select", "0x10:0x20", "-r", "--encoding", "cp932", "--template", "pe",
            "--compare", @"C:\w\x.bin", @"C:\w\y.bin", "--run", @"C:\w\s.js", "--disk", "0", "--volume", "C:", "--process", "notepad",
            "--write-block", "--plugin-dev", @"C:\dev\plg", "--wait-id", "abc123", "--unregister-result", @"C:\t\r.txt"]);
        Assert.Equal([@"C:\w\a.bin"], c.Files);
        Assert.Equal("end-16", c.Offset);
        Assert.Equal("0x10:0x20", c.Select);
        Assert.True(c.ReadOnly);
        Assert.Equal("cp932", c.Encoding);
        Assert.Equal("pe", c.Template);
        Assert.Equal([@"C:\w\x.bin", @"C:\w\y.bin"], c.Compare);
        Assert.Equal(@"C:\w\s.js", c.Run);
        Assert.Equal("0", c.Disk);
        Assert.Equal("C:", c.Volume);
        Assert.Equal("notepad", c.Process);
        Assert.True(c.WriteBlock);
        Assert.Equal(@"C:\dev\plg", c.PluginDev);
        Assert.Equal("abc123", c.WaitId);
        Assert.Equal(@"C:\t\r.txt", c.UnregisterResult);
        Assert.True(c.HasOpenActions);
        Assert.False(CommandLine.Parse(["a.bin"]).HasOpenActions);
    }

    [Fact]
    public void ACompareWithoutTwoFilesAndAnInvalidWaitIdAreIgnored()
    {
        CommandLine c = CommandLine.Parse(["--compare", "only-one.bin"]);
        Assert.Empty(c.Compare);
        Assert.Empty(c.Files);
        Assert.Null(CommandLine.Parse(["--wait-id", @"..\evil"]).WaitId);
        Assert.Null(CommandLine.Parse(["--wait-id", "Local\\x y"]).WaitId);
    }

    [Fact]
    public void WaitSignalNamesAreSessionLocal()
    {
        string id = WaitSignal.NewId();
        Assert.True(WaitSignal.IsValidId(id));
        Assert.StartsWith(@"Local\HexEditor.Wait.", WaitSignal.EventName(id));
        Assert.False(WaitSignal.IsValidId(string.Empty));
        Assert.False(WaitSignal.IsValidId(new string('a', 65)));
    }

    [Fact]
    public void UnregisterIsAFlagAndNotAFile()
    {
        // HexEditor.exe --unregister (08 の AUTO-36 の 9)。
        CommandLine c = CommandLine.Parse(["--unregister", "--test-profile", @"C:\p"]);
        Assert.True(c.Unregister);
        Assert.Empty(c.Files);
        Assert.Equal(@"C:\p", c.TestProfile);
        Assert.False(CommandLine.Parse(["a.bin"]).Unregister);
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
