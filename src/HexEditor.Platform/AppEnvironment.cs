using System.Runtime.InteropServices;

namespace HexEditor.Platform;

/// <summary>
/// 実行環境の実装の共通部分と判定 (PKG-12)。配布形態ごとの実装は <see cref="MsixEnvironment"/>、
/// <see cref="InstallerEnvironment"/>、<see cref="PortableEnvironment"/>、<see cref="DevelopmentEnvironment"/>。
/// 配布形態を判定するコード (パッケージ ID の取得、目印のファイル) はこのファイルの中だけに置く (PKG-01 の仕様 3)。
/// </summary>
public abstract class AppEnvironment : IAppEnvironment
{
    /// <summary>ポータブル版の目印のファイル (PKG-05 の仕様 2)。</summary>
    public const string PortableMarkerFileName = "portable.marker";

    /// <summary>インストーラ版・開発用のデータフォルダの名前 (%LocalAppData% の下)。</summary>
    public const string InstallerDataFolderName = "HexEditorData";

    public const string DevelopmentDataFolderName = "HexEditorData-dev";

    private readonly List<string> _warnings = [];

    private protected AppEnvironment(EnvironmentContext context, Distribution distribution, DataLocations locations, string? testProfile)
    {
        Context = context;
        Distribution = distribution;
        if (testProfile is not null)
        {
            // --test-profile: すべての保存先をそのフォルダに置く (テスト方針 8.3)。
            locations = new DataLocations(testProfile, Path.Combine(testProfile, "temp"), Path.Combine(testProfile, "components"));
        }

        IsDataDirectoryWritable = context.ProbeWritable(locations.Root);
        if (!IsDataDirectoryWritable)
        {
            // 書き込めない: 復旧用データと一時ファイルは %TEMP%\HexEditor-<ハッシュ>\ に置く (PKG-06 の仕様 5 の 3)。
            string fallback = HashedTempFolder(context);
            locations = locations with { Temp = fallback, Recovery = Path.Combine(fallback, "recovery") };
            _warnings.Add("The data folder is not writable; settings are kept for this session only.");
        }

        Locations = locations;
        AppVersionInfo = SemanticVersion.FromInformationalVersion(context.InformationalVersion);
        string hash = DataDirectory.FolderHash(context.ExeFolder);
        string role = IsElevated ? "Admin" : "User";
        InstanceKey = distribution == Distribution.Msix ? $"HexEditor-Msix-{role}" : $"HexEditor-{distribution}-{hash}-{role}";
    }

    protected EnvironmentContext Context { get; }

    public Distribution Distribution { get; }

    public bool IsPackaged => Distribution == Distribution.Msix;

    public bool IsElevated => Context.IsElevated;

    public Architecture ProcessArchitecture => Context.ProcessArchitecture;

    public SemanticVersion AppVersionInfo { get; }

    public string AppVersion => AppVersionInfo.SemVer;

    public string InformationalVersion => AppVersionInfo.Informational;

    public ReleaseChannel Channel => AppVersionInfo.Channel;

    public abstract string AppUserModelId { get; }

    public DataLocations Locations { get; }

    public bool IsDataDirectoryWritable { get; }

    public string InstanceKey { get; }

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>一時フォルダを終了時に消すか (ポータブル版と、データフォルダに書き込めない場合。PKG-06 の仕様 4・5 の 3)。</summary>
    public bool DeletesTempAtExit => Distribution == Distribution.Portable || !IsDataDirectoryWritable;

    /// <summary>
    /// 判定する (PKG-12 の仕様 1)。判定で例外が起きたら <see cref="Distribution.Development"/> として扱う (「エラー」)。
    /// ビルドの配布形態と判定の結果が違う場合は判定の結果を使い、警告を残す (仕様 2)。
    /// ただし <c>Portable</c> のビルドで目印のファイルがない場合は、ポータブル版としてインストーラ版と同じ保存先を使う (PKG-05 の仕様 4)。
    /// </summary>
    public static AppEnvironment Detect(EnvironmentContext context, string? testProfile = null)
    {
        var warnings = new List<string>();
        Distribution detected;
        try
        {
            detected = DetectDistribution(context);
        }
        catch (Exception ex)
        {
            detected = Distribution.Development;
            warnings.Add($"Distribution detection failed ({ex.GetType().Name}); running as Development.");
        }

        Distribution built = context.BuildDistribution ?? Distribution.Development;
        if (built != detected)
        {
            warnings.Add($"Built as {built} but detected {detected}; using {(built == Distribution.Portable && detected == Distribution.Development ? Distribution.Portable : detected)}.");
        }

        AppEnvironment environment;
        try
        {
            environment = detected switch
            {
                Distribution.Msix => new MsixEnvironment(context, testProfile),
                Distribution.Portable => new PortableEnvironment(context, ReadMarker(context.ExeFolder), testProfile),
                Distribution.Installer => new InstallerEnvironment(context, testProfile),
                _ when built == Distribution.Portable => new PortableEnvironment(context, null, testProfile),
                _ => new DevelopmentEnvironment(context, testProfile),
            };
        }
        catch (Exception ex) when (detected != Distribution.Development)
        {
            warnings.Add($"Creating the {detected} environment failed ({ex.GetType().Name}); running as Development.");
            environment = new DevelopmentEnvironment(context, testProfile);
        }

        environment._warnings.InsertRange(0, warnings);
        return environment;
    }

    /// <summary>このプロセスを判定する。</summary>
    public static AppEnvironment DetectCurrent(System.Reflection.Assembly entryAssembly, string? testProfile = null)
    {
        EnvironmentContext context;
        try
        {
            context = EnvironmentContext.ForCurrentProcess(entryAssembly);
        }
        catch (Exception)
        {
            string temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
            context = new EnvironmentContext
            {
                ExeFolder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } l ? l : temp,
                TempPath = temp,
            };
        }

        return Detect(context, testProfile);
    }

    internal static Distribution DetectDistribution(EnvironmentContext context)
    {
        if (context.HasPackageIdentity())
        {
            return Distribution.Msix;
        }

        if (File.Exists(Path.Combine(context.ExeFolder, PortableMarkerFileName)))
        {
            return Distribution.Portable;
        }

        // Velopack のインストール先: <root>\Update.exe と <root>\current\<exe>。
        DirectoryInfo? parent = Directory.GetParent(context.ExeFolder);
        if (Path.GetFileName(context.ExeFolder).Equals("current", StringComparison.OrdinalIgnoreCase)
            && parent is not null && File.Exists(Path.Combine(parent.FullName, "Update.exe")))
        {
            return Distribution.Installer;
        }

        return Distribution.Development;
    }

    /// <summary>目印のファイルの中身。読めなければ空 (既定の場所を使う)。</summary>
    private static string? ReadMarker(string exeFolder)
    {
        try
        {
            return File.ReadAllText(Path.Combine(exeFolder, PortableMarkerFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>%TEMP%\HexEditor-&lt;exe のフォルダのハッシュ 8 桁&gt; (PKG-06 の仕様 4)。</summary>
    internal static string HashedTempFolder(EnvironmentContext context) =>
        Path.Combine(context.TempPath, $"HexEditor-{DataDirectory.FolderHash(context.ExeFolder)}");

    internal static DataLocations InstallerLocations(EnvironmentContext context, string folderName)
    {
        string data = Path.Combine(context.LocalAppData, folderName);
        return new DataLocations(data, Path.Combine(data, "temp"), Path.Combine(data, "components"));
    }
}

/// <summary>MSIX 版 (Microsoft Store 版)。保存先は ApplicationData の API で取る (PKG-13 の仕様 2)。</summary>
public sealed class MsixEnvironment : AppEnvironment
{
    public MsixEnvironment(EnvironmentContext context, string? testProfile = null)
        : base(context, Distribution.Msix, LocationsFor(context), testProfile)
    {
        AppUserModelId = context.PackageAppUserModelId();
    }

    public override string AppUserModelId { get; }

    private static DataLocations LocationsFor(EnvironmentContext context)
    {
        (string local, string cache) = context.PackageFolders();
        return new DataLocations(local, Path.Combine(cache, "temp"), Path.Combine(cache, "components"));
    }
}

/// <summary>インストーラ版 (Velopack)。データは %LocalAppData%\HexEditorData\ (インストール先と分ける。PKG-07 の仕様 7)。</summary>
public sealed class InstallerEnvironment : AppEnvironment
{
    /// <summary>Velopack が設定する AppUserModelID (既定は <c>velopack.&lt;パッケージ ID&gt;</c>。ショートカットと同じ値)。</summary>
    public const string VelopackAppUserModelId = "velopack.HexEditor";

    public InstallerEnvironment(EnvironmentContext context, string? testProfile = null)
        : base(context, Distribution.Installer, InstallerLocations(context, InstallerDataFolderName), testProfile)
    {
    }

    public override string AppUserModelId => VelopackAppUserModelId;

    /// <summary>インストーラ版のデータフォルダ (フックは実行環境の判定より前に動くため、ここから取る)。</summary>
    public static string DataRoot(string localAppData) => Path.Combine(localAppData, InstallerDataFolderName);
}

/// <summary>
/// ポータブル版 (PKG-05)。データは目印のファイルの <c>DataDirectory=</c> (既定 <c>&lt;exe のフォルダ&gt;\Data</c>)、
/// 一時ファイルは %TEMP%\HexEditor-&lt;ハッシュ&gt;\ (終了時に消す)。目印のファイルがない場合 (<paramref name="marker"/> が null) は
/// インストーラ版と同じ保存先を使う (PKG-05 の仕様 4)。
/// </summary>
public sealed class PortableEnvironment : AppEnvironment
{
    public PortableEnvironment(EnvironmentContext context, string? marker, string? testProfile = null)
        : base(context, Distribution.Portable, LocationsFor(context, marker), testProfile)
    {
        HasMarker = marker is not null;
        AppUserModelId = $"HexEditor.Portable.{DataDirectory.FolderHash(context.ExeFolder)}";
    }

    public bool HasMarker { get; }

    public override string AppUserModelId { get; }

    private static DataLocations LocationsFor(EnvironmentContext context, string? marker)
    {
        string temp = HashedTempFolder(context);
        string data = marker is null
            ? Path.Combine(context.LocalAppData, InstallerDataFolderName)
            : PortableMarker.ResolveDataDirectory(marker, context.ExeFolder);
        return new DataLocations(data, temp, Path.Combine(data, "components"));
    }
}

/// <summary>開発中の実行 (PKG-12 の仕様 1 の 4)。データは %LocalAppData%\HexEditorData-dev\。登録・更新は行わない。</summary>
public sealed class DevelopmentEnvironment : AppEnvironment
{
    public DevelopmentEnvironment(EnvironmentContext context, string? testProfile = null)
        : base(context, Distribution.Development, InstallerLocations(context, DevelopmentDataFolderName), testProfile)
    {
    }

    public override string AppUserModelId => "HexEditor.Development";
}

/// <summary>ポータブル版の目印のファイルの中身 (PKG-05 の仕様 2)。</summary>
public static class PortableMarker
{
    public const string DefaultDataDirectory = "Data";

    /// <summary>
    /// 1 行目の <c>DataDirectory=&lt;パス&gt;</c> を読み、データフォルダの絶対パスを返す。相対パスは exe のフォルダから。
    /// 中身が空、または指定がなければ <c>&lt;exe のフォルダ&gt;\Data</c>。
    /// </summary>
    public static string ResolveDataDirectory(string markerText, string exeFolder)
    {
        string? first = markerText.TrimStart('﻿').Split(["\r\n", "\n"], StringSplitOptions.None).FirstOrDefault()?.Trim();
        string value = DefaultDataDirectory;
        int equals = first?.IndexOf('=') ?? -1;
        if (first is not null && equals > 0 && first[..equals].Trim().Equals("DataDirectory", StringComparison.OrdinalIgnoreCase))
        {
            string specified = first[(equals + 1)..].Trim().Trim('"');
            if (specified.Length > 0)
            {
                value = Environment.ExpandEnvironmentVariables(specified);
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(exeFolder, value)));
    }
}

/// <summary>パッケージ ID (MSIX 版) の取得。</summary>
internal static class PackageIdentity
{
    private const int AppModelErrorNoPackage = 15700;

    public static bool HasIdentity()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    public static (string LocalFolder, string LocalCacheFolder) Folders() =>
        (Windows.Storage.ApplicationData.Current.LocalFolder.Path, Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path);

    public static string AppUserModelId() => Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
