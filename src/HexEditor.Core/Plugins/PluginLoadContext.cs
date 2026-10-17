using System.Reflection;
using System.Runtime.Loader;

namespace HexEditor.Core.Plugins;

/// <summary>
/// プラグインごとの <see cref="AssemblyLoadContext"/> (AUTO-30 の仕様 3 の 4・5)。依存関係の分離のためのもので、セキュリティの境界ではない。
/// <list type="bullet">
/// <item>SDK (HexEditor.Sdk、HexEditor.Sdk.UI)、.NET の基本ライブラリ、Windows App SDK はアプリ側のものを共有する。プラグインのフォルダに
/// 同梱しても使わない (AUTO-29 の仕様 4)。</item>
/// <item>それ以外の依存ライブラリはプラグインのフォルダから読み込む (プラグイン同士で別のバージョンを使える)。フォルダになければアプリ側に任せる。</item>
/// <item>回収可能にしない (アンロードには対応しない。AUTO-30 の仕様 6)。</item>
/// </list>
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    /// <summary>アプリ側のものを必ず使うアセンブリ (SDK)。</summary>
    public static readonly IReadOnlySet<string> SharedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "HexEditor.Sdk", "HexEditor.Sdk.UI",
    };

    private readonly string _folder;
    private readonly AssemblyDependencyResolver? _resolver;

    public PluginLoadContext(string pluginId, string entryPath)
        : base("plugin:" + pluginId, isCollectible: false)
    {
        _folder = Path.GetDirectoryName(Path.GetFullPath(entryPath))!;
        try
        {
            _resolver = new AssemblyDependencyResolver(entryPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            // .deps.json がない・読めない場合は、フォルダの <名前>.dll を探す。
            _resolver = null;
        }
    }

    /// <summary>アプリ側のものを共有するアセンブリか (SDK、.NET の基本ライブラリ、Windows App SDK)。</summary>
    public static bool IsShared(AssemblyName name)
    {
        string n = name.Name ?? string.Empty;
        return SharedAssemblies.Contains(n) || IsFramework(n);
    }

    private static bool IsFramework(string n) =>
        n.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) || n.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
        || n.Equals("System", StringComparison.OrdinalIgnoreCase) || n.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
        || n.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase)
        || n.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase) || n.Equals("Microsoft.VisualBasic", StringComparison.OrdinalIgnoreCase)
        // Windows App SDK と WinRT の投影 (アプリと同じものを使わないと型が一致しない)。
        || n.Equals("Microsoft.WinUI", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase)
        || n.StartsWith("Microsoft.InteractiveExperiences", StringComparison.OrdinalIgnoreCase) || n.Equals("WinRT.Runtime", StringComparison.OrdinalIgnoreCase)
        || n.StartsWith("Microsoft.Web.WebView2", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Microsoft.Graphics.", StringComparison.OrdinalIgnoreCase)
        || n.Equals("Microsoft.Windows.SDK.NET", StringComparison.OrdinalIgnoreCase);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsShared(assemblyName))
        {
            // 既定のコンテキスト (アプリ) に任せる。
            return null;
        }

        string? path = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is null && assemblyName.Name is { } name && PluginManifest.IsSafeFileName(name + ".dll"))
        {
            string candidate = Path.Combine(_folder, name + ".dll");
            path = File.Exists(candidate) ? candidate : null;
        }

        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
