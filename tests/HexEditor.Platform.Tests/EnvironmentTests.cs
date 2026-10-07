using HexEditor.Platform.Tests.Support;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>実行環境の判定と保存先 (PKG-05、PKG-06、PKG-12、PKG-13)。</summary>
public sealed class EnvironmentTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private EnvironmentContext Context(string exeFolder, Func<string, bool>? probe = null) => new()
    {
        ExeFolder = exeFolder,
        LocalAppData = _temp.Sub("LocalAppData"),
        TempPath = _temp.Sub("Temp"),
        ProbeWritable = probe ?? DataDirectory.TryPrepare,
        PackageFolders = () => (_temp.Sub("Packages", "P", "LocalState"), _temp.Sub("Packages", "P", "LocalCache")),
        PackageAppUserModelId = () => "HexEditor_abc!App",
    };

    private string Exe(params string[] parts)
    {
        string folder = _temp.Sub(parts);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    [Trait(TC, "TC-PKG-12-02")]
    public void MsixUsesPackageFolders()
    {
        var env = new MsixEnvironment(Context(Exe("WindowsApps", "HexEditor")));
        string local = _temp.Sub("Packages", "P", "LocalState");
        string cache = _temp.Sub("Packages", "P", "LocalCache");
        Assert.Equal(local, env.Locations.Settings);
        Assert.Equal(Path.Combine(local, "documents"), env.Locations.Documents);
        Assert.Equal(Path.Combine(local, "recovery"), env.Locations.Recovery);
        Assert.Equal(Path.Combine(local, "crash"), env.Locations.Crash);
        Assert.Equal(Path.Combine(local, "logs"), env.Locations.Logs);
        Assert.Equal(Path.Combine(cache, "temp"), env.Locations.Temp);
        Assert.Equal(Path.Combine(cache, "components"), env.Locations.Components);
        Assert.True(env.IsPackaged);
        Assert.Equal("HexEditor_abc!App", env.AppUserModelId);
        Assert.StartsWith("HexEditor-Msix-", env.InstanceKey);
    }

    [Fact]
    [Trait(TC, "TC-PKG-12-02")]
    public void InstallerUsesHexEditorDataUnderLocalAppData()
    {
        var env = new InstallerEnvironment(Context(Exe("LocalAppData", "HexEditor", "current")));
        string data = _temp.Sub("LocalAppData", "HexEditorData");
        Assert.Equal(data, env.Locations.Settings);
        Assert.Equal(Path.Combine(data, "documents"), env.Locations.Documents);
        Assert.Equal(Path.Combine(data, "recovery"), env.Locations.Recovery);
        Assert.Equal(Path.Combine(data, "crash"), env.Locations.Crash);
        Assert.Equal(Path.Combine(data, "logs"), env.Locations.Logs);
        Assert.Equal(Path.Combine(data, "temp"), env.Locations.Temp);
        Assert.Equal(Path.Combine(data, "components"), env.Locations.Components);
        // データはインストール先 (%LocalAppData%\HexEditor\) の下に置かない (PKG-07 の仕様 7)。
        Assert.DoesNotContain(Path.Combine(_temp.Sub("LocalAppData", "HexEditor")) + Path.DirectorySeparatorChar, env.Locations.Root + Path.DirectorySeparatorChar);
        Assert.Equal(InstallerEnvironment.VelopackAppUserModelId, env.AppUserModelId);
    }

    [Fact]
    [Trait(TC, "TC-PKG-12-02")]
    public void PortableUsesDataNextToExeAndHashedTemp()
    {
        string exe = Exe("p", "HexEditor");
        var env = new PortableEnvironment(Context(exe), string.Empty);
        string data = Path.Combine(exe, "Data");
        string hash = DataDirectory.FolderHash(exe);
        Assert.Equal(data, env.Locations.Settings);
        Assert.Equal(Path.Combine(data, "documents"), env.Locations.Documents);
        Assert.Equal(Path.Combine(data, "recovery"), env.Locations.Recovery);
        Assert.Equal(Path.Combine(data, "crash"), env.Locations.Crash);
        Assert.Equal(Path.Combine(data, "logs"), env.Locations.Logs);
        Assert.Equal(Path.Combine(data, "components"), env.Locations.Components);
        Assert.Equal(_temp.Sub("Temp", $"HexEditor-{hash}"), env.Locations.Temp);
        Assert.Equal($"HexEditor.Portable.{hash}", env.AppUserModelId);
        Assert.True(env.DeletesTempAtExit);
        // データフォルダは初回起動時に作る (PKG-05 の仕様 3)。
        Assert.True(Directory.Exists(data));
    }

    [Fact]
    [Trait(TC, "TC-PKG-12-02")]
    public void PortableMarkerDataDirectoryIsRelativeToExeFolder()
    {
        string exe = Exe("p", "HexEditor");
        var env = new PortableEnvironment(Context(exe), "DataDirectory=..\\HexData\r\n");
        Assert.Equal(_temp.Sub("p", "HexData"), env.Locations.Settings);
        Assert.Equal(_temp.Sub("p", "HexData", "recovery"), env.Locations.Recovery);
        Assert.False(Directory.Exists(Path.Combine(exe, "Data")));
    }

    [Fact]
    [Trait(TC, "TC-PKG-12-02")]
    public void PortableBuildWithoutMarkerUsesInstallerLocations()
    {
        string exe = Exe("p", "HexEditor");
        AppEnvironment env = AppEnvironment.Detect(Context(exe) with { BuildDistribution = Distribution.Portable });
        Assert.Equal(Distribution.Portable, env.Distribution);
        Assert.Equal(_temp.Sub("LocalAppData", "HexEditorData"), env.Locations.Settings);
        Assert.Contains(env.Warnings, w => w.Contains("Portable"));
    }

    [Fact]
    [Trait(TC, "TC-PKG-12-02")]
    public void DevelopmentUsesHexEditorDataDev()
    {
        var env = new DevelopmentEnvironment(Context(Exe("src", "bin")));
        Assert.Equal(_temp.Sub("LocalAppData", "HexEditorData-dev"), env.Locations.Settings);
        Assert.Equal(_temp.Sub("LocalAppData", "HexEditorData-dev", "temp"), env.Locations.Temp);
        Assert.False(env.DeletesTempAtExit);
    }

    [Theory]
    [InlineData("", "Data")]
    [InlineData("DataDirectory=", "Data")]
    [InlineData("DataDirectory=Settings", "Settings")]
    [InlineData("\uFEFFdatadirectory = \"..\\Shared Data\"", "..\\Shared Data")]
    [InlineData("# comment\nDataDirectory=Other", "Data")]
    public void ParsesPortableMarker(string marker, string expectedRelative)
    {
        string exe = Exe("m", "HexEditor");
        string expected = Path.GetFullPath(Path.Combine(exe, expectedRelative));
        Assert.Equal(expected, PortableMarker.ResolveDataDirectory(marker, exe));
    }

    [Fact]
    public void PortableMarkerAcceptsAbsolutePath()
    {
        string other = _temp.Sub("elsewhere");
        Assert.Equal(other, PortableMarker.ResolveDataDirectory($"DataDirectory={other}", Exe("m", "HexEditor")));
    }

    [Fact]
    public void DetectsDistributionFromMarkerAndVelopackLayout()
    {
        string portable = Exe("p", "HexEditor");
        File.WriteAllText(Path.Combine(portable, AppEnvironment.PortableMarkerFileName), string.Empty);
        Assert.IsType<PortableEnvironment>(AppEnvironment.Detect(Context(portable)));

        string current = Exe("LocalAppData", "HexEditor", "current");
        File.WriteAllText(_temp.Sub("LocalAppData", "HexEditor", "Update.exe"), string.Empty);
        Assert.IsType<InstallerEnvironment>(AppEnvironment.Detect(Context(current)));

        Assert.IsType<MsixEnvironment>(AppEnvironment.Detect(Context(Exe("pkg")) with { HasPackageIdentity = () => true }));
        Assert.IsType<DevelopmentEnvironment>(AppEnvironment.Detect(Context(Exe("dev"))));
    }

    [Fact]
    public void DetectionFailureFallsBackToDevelopment()
    {
        AppEnvironment env = AppEnvironment.Detect(Context(Exe("x")) with { HasPackageIdentity = () => throw new InvalidOperationException() });
        Assert.Equal(Distribution.Development, env.Distribution);
        Assert.Contains(env.Warnings, w => w.Contains("detection failed"));
    }

    [Fact]
    public void FailureCreatingDetectedEnvironmentFallsBackToDevelopment()
    {
        AppEnvironment env = AppEnvironment.Detect(Context(Exe("x")) with
        {
            HasPackageIdentity = () => true,
            PackageFolders = () => throw new UnauthorizedAccessException(),
        });
        Assert.Equal(Distribution.Development, env.Distribution);
        Assert.NotEmpty(env.Warnings);
    }

    [Fact]
    public void WarnsWhenBuildAndDetectionDisagree()
    {
        AppEnvironment env = AppEnvironment.Detect(Context(Exe("x")) with { BuildDistribution = Distribution.Installer });
        Assert.Equal(Distribution.Development, env.Distribution);
        Assert.Contains(env.Warnings, w => w.Contains("Installer"));

        AppEnvironment same = AppEnvironment.Detect(Context(Exe("y")));
        Assert.Empty(same.Warnings);
    }

    [Fact]
    public void ReadOnlyDataFolderMovesRecoveryToHashedTemp()
    {
        string exe = Exe("ro", "HexEditor");
        var env = new PortableEnvironment(Context(exe, probe: _ => false), string.Empty);
        string temp = _temp.Sub("Temp", $"HexEditor-{DataDirectory.FolderHash(exe)}");
        Assert.False(env.IsDataDirectoryWritable);
        Assert.Equal(Path.Combine(exe, "Data"), env.Locations.Settings);
        Assert.Equal(Path.Combine(temp, "recovery"), env.Locations.Recovery);
        Assert.Equal(temp, env.Locations.Temp);
        Assert.True(env.DeletesTempAtExit);

        var installer = new InstallerEnvironment(Context(Exe("LocalAppData", "HexEditor", "current"), probe: _ => false));
        Assert.True(installer.DeletesTempAtExit);
        Assert.StartsWith(_temp.Sub("Temp", "HexEditor-"), installer.Locations.Recovery);
    }

    [Fact]
    public void InstanceKeyDependsOnDistributionFolderAndRole()
    {
        string a = Exe("pa", "HexEditor");
        string b = Exe("pb", "HexEditor");
        var userA = new PortableEnvironment(Context(a), string.Empty);
        var userB = new PortableEnvironment(Context(b), string.Empty);
        var adminA = new PortableEnvironment(Context(a) with { IsElevated = true }, string.Empty);
        Assert.Equal($"HexEditor-Portable-{DataDirectory.FolderHash(a)}-User", userA.InstanceKey);
        Assert.NotEqual(userA.InstanceKey, userB.InstanceKey);
        Assert.EndsWith("-Admin", adminA.InstanceKey);
        Assert.Equal("HexEditor-Msix-User", new MsixEnvironment(Context(a)).InstanceKey);
    }

    [Fact]
    public void FolderHashIgnoresCaseAndTrailingSeparator()
    {
        Assert.Equal(DataDirectory.FolderHash(@"C:\P\HexEditor"), DataDirectory.FolderHash(@"c:\p\hexeditor\"));
        Assert.Matches("^[0-9A-F]{8}$", DataDirectory.FolderHash(@"C:\P\HexEditor"));
    }

    [Fact]
    public void VersionAndChannelComeFromInformationalVersion()
    {
        var env = new DevelopmentEnvironment(Context(Exe("v")) with { InformationalVersion = "1.3.0-preview.2+abc1234" });
        Assert.Equal("1.3.0-preview.2", env.AppVersion);
        Assert.Equal("1.3.0-preview.2+abc1234", env.InformationalVersion);
        Assert.Equal(ReleaseChannel.Preview, env.Channel);
    }

    [Fact]
    public void TestProfilePutsEverythingInOneFolder()
    {
        string profile = _temp.Sub("profile");
        var env = new InstallerEnvironment(Context(Exe("i")), profile);
        Assert.Equal(profile, env.Locations.Root);
        Assert.Equal(Path.Combine(profile, "temp"), env.Locations.Temp);
        Assert.Equal(Path.Combine(profile, "recovery"), env.Locations.Recovery);
    }
}
