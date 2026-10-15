using System.Reflection;

namespace HexEditor.Core.Templates.Loading;

/// <summary>
/// テンプレートの読み込みに使うファイルの操作 (TPL-12、TPL-37)。評価器はこのインターフェースを通してだけファイルを読み、
/// 読むのはインポートとインクルードの解決だけである。テストでは読み込みの回数とアクセスを記録する実装に差し替える。
/// </summary>
public interface ITemplateFileSystem
{
    bool FileExists(string fullPath);

    byte[] ReadAllBytes(string fullPath);

    /// <summary>パスの要素がシンボリックリンク・ジャンクションの場合は最終的なリンク先を、そうでなければ null を返す (中身は読まない)。</summary>
    string? ResolveLink(string fullPath);
}

/// <summary>実際のファイルシステム。</summary>
public sealed class PhysicalTemplateFileSystem : ITemplateFileSystem
{
    public static PhysicalTemplateFileSystem Instance { get; } = new();

    public bool FileExists(string fullPath) => File.Exists(fullPath);

    public byte[] ReadAllBytes(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > TemplateLimits.MaxFileBytes)
        {
            throw new TemplateFileTooLargeException();
        }

        byte[] buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    public string? ResolveLink(string fullPath)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(fullPath) ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
            if (!info.Exists || !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? fullPath;
        }
        catch (IOException)
        {
            return fullPath;
        }
        catch (UnauthorizedAccessException)
        {
            return fullPath;
        }
    }
}

internal sealed class TemplateFileTooLargeException : Exception
{
}

/// <summary>製品に同梱の標準ライブラリ (TPL-18 の仕様 5。検索パスの (4))。</summary>
internal static class BundledTemplates
{
    public const string Root = "bundled:";

    private static readonly Lazy<Dictionary<string, byte[]>> Files = new(Load);

    private static Dictionary<string, byte[]> Load()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        Assembly asm = typeof(BundledTemplates).Assembly;
        const string prefix = "HexEditor.Templates.Bundled.";
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            // 例: HexEditor.Templates.Bundled.std.pe.hxt → std/pe.hxt
            string rest = name[prefix.Length..];
            string ext = Path.GetExtension(rest);
            string stem = rest[..^ext.Length].Replace('\\', '/').Replace('.', '/');
            using Stream s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            map[stem + ext] = ms.ToArray();
        }

        return map;
    }

    public static bool TryGet(string relativePath, out byte[] bytes) =>
        Files.Value.TryGetValue(relativePath.Replace('\\', '/'), out bytes!);

    public static IEnumerable<string> Names => Files.Value.Keys;
}
