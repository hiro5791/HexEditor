using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace HexEditor.App.Hosting;

/// <summary>配布形態 (PKG-01、PKG-12)。</summary>
public enum Distribution
{
    Msix,
    Installer,
    Portable,
    Development,
}

/// <summary>データの保存先 (PKG-13)。</summary>
public sealed record DataLocations(string Root, string Temp, string Components)
{
    public string Settings => Root;

    public string Documents => Path.Combine(Root, "documents");

    public string Recovery => Path.Combine(Root, "recovery");

    public string Crash => Path.Combine(Root, "crash");

    public string Logs => Path.Combine(Root, "logs");
}

/// <summary>実行環境 (PKG-12)。配布形態ごとの違いはここにまとめ、他のコードは配布形態を直接調べない。</summary>
public interface IAppEnvironment
{
    Distribution Distribution { get; }

    /// <summary>true なら Microsoft Store 版 (PKG-04)。</summary>
    bool IsPackaged { get; }

    bool IsElevated { get; }

    string AppVersion { get; }

    DataLocations Locations { get; }

    /// <summary>単一インスタンスのキー (PKG-11 の仕様 2)。</summary>
    string InstanceKey { get; }
}

/// <summary>実行環境の判定 (PKG-12 の仕様 1)。</summary>
public sealed class AppEnvironment : IAppEnvironment
{
    private const int AppModelErrorNoPackage = 15700;

    private AppEnvironment(Distribution distribution, DataLocations locations)
    {
        Distribution = distribution;
        Locations = locations;
        string exeFolder = AppContext.BaseDirectory;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exeFolder.ToUpperInvariant())))[..8];
        string role = IsElevated ? "Admin" : "User";
        InstanceKey = distribution == Distribution.Msix ? $"HexEditor-Msix-{role}" : $"HexEditor-{distribution}-{hash}-{role}";
    }

    public Distribution Distribution { get; }

    public bool IsPackaged => Distribution == Distribution.Msix;

    public bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public string AppVersion { get; } = typeof(AppEnvironment).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public DataLocations Locations { get; }

    public string InstanceKey { get; }

    /// <summary>
    /// 判定する。<paramref name="testProfile"/> (--test-profile) が指定されていれば、すべての保存先をそのフォルダに置く
    /// (テスト方針 8.3)。
    /// </summary>
    public static AppEnvironment Detect(string? testProfile = null)
    {
        Distribution distribution = DetectDistribution();
        DataLocations locations = testProfile is not null
            ? new DataLocations(testProfile, Path.Combine(testProfile, "temp"), Path.Combine(testProfile, "components"))
            : LocationsFor(distribution);
        return new AppEnvironment(distribution, locations);
    }

    private static Distribution DetectDistribution()
    {
        uint length = 0;
        if (GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage)
        {
            return Distribution.Msix;
        }

        string exeFolder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(exeFolder, "portable.marker")))
        {
            return Distribution.Portable;
        }

        // Velopack のインストール先: <root>\Update.exe と <root>\current\<exe>。
        DirectoryInfo? parent = Directory.GetParent(exeFolder);
        if (Path.GetFileName(exeFolder).Equals("current", StringComparison.OrdinalIgnoreCase)
            && parent is not null && File.Exists(Path.Combine(parent.FullName, "Update.exe")))
        {
            return Distribution.Installer;
        }

        return Distribution.Development;
    }

    private static DataLocations LocationsFor(Distribution distribution)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        switch (distribution)
        {
            case Distribution.Msix:
                // パスを文字列で組み立てず、ApplicationData の API で取る (PKG-13 の仕様 2)。
                string local = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                string cache = Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path;
                return new DataLocations(local, Path.Combine(cache, "temp"), Path.Combine(cache, "components"));
            case Distribution.Installer:
                string data = Path.Combine(localAppData, "HexEditorData");
                return new DataLocations(data, Path.Combine(data, "temp"), Path.Combine(data, "components"));
            case Distribution.Portable:
                string exeFolder = AppContext.BaseDirectory;
                string portable = Path.Combine(exeFolder, "HexEditorData");
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exeFolder.ToUpperInvariant())))[..8];
                return new DataLocations(portable, Path.Combine(Path.GetTempPath(), $"HexEditor-{hash}"), Path.Combine(portable, "components"));
            default:
                string dev = Path.Combine(localAppData, "HexEditorData-dev");
                return new DataLocations(dev, Path.Combine(dev, "temp"), Path.Combine(dev, "components"));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
