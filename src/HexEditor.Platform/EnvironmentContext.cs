using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HexEditor.Platform;

/// <summary>
/// 実行環境の判定に使う外部の値 (PKG-12 の仕様 5: 単体テストで差し替えられるようにする)。
/// <see cref="ForCurrentProcess"/> が実際の値を集める。テストではフォルダや判定の結果を直接与える。
/// </summary>
public sealed record EnvironmentContext
{
    /// <summary>exe のフォルダ (末尾の区切りなし)。</summary>
    public required string ExeFolder { get; init; }

    /// <summary>%LocalAppData%。</summary>
    public required string LocalAppData { get; init; }

    /// <summary>%TEMP%。</summary>
    public required string TempPath { get; init; }

    public bool IsElevated { get; init; }

    public Architecture ProcessArchitecture { get; init; } = RuntimeInformation.ProcessArchitecture;

    /// <summary>アプリの InformationalVersion (PKG-28)。</summary>
    public string InformationalVersion { get; init; } = SemanticVersion.Local.Informational;

    /// <summary>ビルド時の HexDistro (PKG-10)。指定なしのビルド (開発用) は null。</summary>
    public Distribution? BuildDistribution { get; init; }

    /// <summary>パッケージ ID があるか (GetCurrentPackageFullName が成功するか。PKG-12 の仕様 1 の 1)。</summary>
    public Func<bool> HasPackageIdentity { get; init; } = () => false;

    /// <summary>MSIX 版の保存先 (ApplicationData の LocalFolder と LocalCacheFolder)。MSIX 版のときだけ呼ぶ。</summary>
    public Func<(string LocalFolder, string LocalCacheFolder)> PackageFolders { get; init; } =
        () => throw new InvalidOperationException("No package identity.");

    /// <summary>MSIX 版の AppUserModelID (パッケージファミリ名 + "!App")。MSIX 版のときだけ呼ぶ。</summary>
    public Func<string> PackageAppUserModelId { get; init; } = () => throw new InvalidOperationException("No package identity.");

    /// <summary>データフォルダに書き込めるか確かめる (既定: <see cref="DataDirectory.TryPrepare"/>)。</summary>
    public Func<string, bool> ProbeWritable { get; init; } = DataDirectory.TryPrepare;

    /// <summary>このプロセスの実際の値。<paramref name="entryAssembly"/> はアプリの exe のアセンブリ (版とビルドの配布形態を読む)。</summary>
    public static EnvironmentContext ForCurrentProcess(Assembly entryAssembly)
    {
        string? informational = entryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string? built = entryAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "HexDistro")?.Value;
        return new EnvironmentContext
        {
            ExeFolder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            TempPath = Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
            IsElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            InformationalVersion = informational ?? SemanticVersion.Local.Informational,
            BuildDistribution = Enum.TryParse(built, ignoreCase: true, out Distribution d) ? d : null,
            HasPackageIdentity = PackageIdentity.HasIdentity,
            PackageFolders = PackageIdentity.Folders,
            PackageAppUserModelId = PackageIdentity.AppUserModelId,
        };
    }
}
