using System.IO.Compression;
using HexEditor.Core.Scripting;

namespace HexEditor.Core.Plugins;

/// <summary>不正な <c>.hexplugin</c> (AUTO-32 の「エラー」: マニフェストがない、zip が壊れている、危険なパス)。追加せずに理由を表示する。</summary>
public sealed class PluginPackageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// <c>.hexplugin</c> (プラグインのフォルダを zip にしたもの。AUTO-32 の仕様 2) の検証と展開。中身は信頼できない入力として扱う:
/// <list type="bullet">
/// <item>各項目のパスを検証する (絶対パス・ドライブ・<c>..</c>・代替データストリーム・予約名・制御文字を拒否。展開先の外に出るもの (zip-slip) は展開しない)。</item>
/// <item>項目の数・展開後の大きさ・圧縮率に上限を設ける (zip 爆弾)。</item>
/// <item>マニフェストは zip の直下か、1 つだけの最上位のフォルダの直下に置く。</item>
/// </list>
/// </summary>
public static class PluginPackage
{
    public const string Extension = ".hexplugin";

    /// <summary>項目の数の上限。</summary>
    public const int MaxEntries = 10_000;

    /// <summary>展開後の合計の上限 (1 GB)。</summary>
    public const long MaxTotalBytes = 1L << 30;

    /// <summary>1 つの項目の圧縮率の上限 (これを超えるものは zip 爆弾とみなす。小さいファイルは見ない)。</summary>
    public const long MaxRatio = 200;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Windows の予約されたデバイス名 (拡張子付きも含む。例: <c>nul.txt</c>)。</summary>
    public static bool IsReservedDeviceName(string name)
    {
        string stem = name;
        int dot = stem.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            stem = stem[..dot];
        }

        return ReservedNames.Contains(stem.TrimEnd(' '));
    }

    /// <summary>
    /// 項目のパス (zip の区切りは <c>/</c>。<c>\</c> も区切りとして扱う) を検証し、相対パスの部分に分ける。安全でなければ null。
    /// </summary>
    public static IReadOnlyList<string>? SafeParts(string entryName)
    {
        if (entryName.Length == 0 || entryName.Length > 400)
        {
            return null;
        }

        string normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':', StringComparison.Ordinal))
        {
            // 絶対パス・UNC・ドライブ (C:)・代替データストリーム (a.dll:ads)。
            return null;
        }

        string[] parts = normalized.Split('/');
        var result = new List<string>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p.Length == 0)
            {
                // 末尾の / (フォルダの項目) だけを許す。
                if (i == parts.Length - 1 && result.Count > 0)
                {
                    continue;
                }

                return null;
            }

            if (p is "." or ".." || p.Any(c => c < 0x20 || c is '*' or '?' or '"' or '<' or '>' or '|') || p.EndsWith('.') || p.EndsWith(' ')
                || IsReservedDeviceName(p))
            {
                return null;
            }

            result.Add(p);
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// パッケージを検証して <paramref name="destination"/> (空のフォルダ。なければ作る) に展開し、マニフェストを返す。
    /// 失敗したら展開したものを消して <see cref="PluginPackageException"/> を投げる。
    /// </summary>
    public static PluginManifest Extract(string packagePath, string destination)
    {
        string root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        if (Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new InvalidOperationException("The destination is not empty.");
        }

        try
        {
            using ZipArchive zip = Open(packagePath);
            List<(ZipArchiveEntry Entry, IReadOnlyList<string> Parts)> files = Validate(zip);
            string? prefix = ManifestPrefix(files);
            long total = 0;
            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            foreach ((ZipArchiveEntry entry, IReadOnlyList<string> parts) in files)
            {
                IEnumerable<string> relative = prefix is null ? parts : parts.Skip(1);
                if (prefix is not null && !string.Equals(parts[0], prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string target = Path.GetFullPath(Path.Combine([root, .. relative]));
                if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    // zip-slip (検証を通っても、最終的なパスで必ず確かめる)。
                    throw new PluginPackageException(Msg.T($"展開先の外を指す項目があります: {entry.FullName}", $"An entry points outside the destination: {entry.FullName}"));
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using Stream input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[81920];
                long written = 0;
                int n;
                while ((n = input.Read(buffer)) > 0)
                {
                    written += n;
                    total += n;
                    if (written > entry.Length || total > MaxTotalBytes)
                    {
                        throw new PluginPackageException(Msg.T("展開後の大きさが上限を超えます。", "The extracted size exceeds the limit."));
                    }

                    output.Write(buffer, 0, n);
                }
            }

            return PluginManifest.Load(root);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PluginManifestException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(root);
            throw ex is PluginPackageException ? ex : new PluginPackageException(Describe(ex), ex);
        }
        catch (PluginPackageException)
        {
            TryDelete(root);
            throw;
        }
    }

    /// <summary>パッケージのマニフェストだけを読む (展開しない。追加の前の表示に使う)。</summary>
    public static PluginManifest ReadManifest(string packagePath)
    {
        try
        {
            using ZipArchive zip = Open(packagePath);
            List<(ZipArchiveEntry Entry, IReadOnlyList<string> Parts)> files = Validate(zip);
            string? prefix = ManifestPrefix(files);
            ZipArchiveEntry manifest = files.First(f => prefix is null
                ? f.Parts.Count == 1 && f.Parts[0].Equals(PluginManifest.FileName, StringComparison.OrdinalIgnoreCase)
                : f.Parts.Count == 2 && f.Parts[0].Equals(prefix, StringComparison.OrdinalIgnoreCase) && f.Parts[1].Equals(PluginManifest.FileName, StringComparison.OrdinalIgnoreCase)).Entry;
            if (manifest.Length > PluginManifest.MaxBytes)
            {
                throw new PluginPackageException(Msg.T("マニフェストが大きすぎます。", "The manifest is too large."));
            }

            using var reader = new StreamReader(manifest.Open());
            return PluginManifest.Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PluginManifestException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new PluginPackageException(Describe(ex), ex);
        }
    }

    private static ZipArchive Open(string packagePath)
    {
        try
        {
            return ZipFile.OpenRead(packagePath);
        }
        catch (InvalidDataException ex)
        {
            throw new PluginPackageException(Msg.T($"zip ファイルとして読めません (壊れているか途中で切れています): {ex.Message}", $"Not a valid zip file (corrupted or truncated): {ex.Message}"), ex);
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        PluginManifestException m => m.Message,
        InvalidDataException => Msg.T($"zip ファイルが壊れています: {ex.Message}", $"The zip file is corrupted: {ex.Message}"),
        _ => Msg.T($"パッケージを読めません: {ex.Message}", $"Cannot read the package: {ex.Message}"),
    };

    private static List<(ZipArchiveEntry Entry, IReadOnlyList<string> Parts)> Validate(ZipArchive zip)
    {
        if (zip.Entries.Count > MaxEntries)
        {
            throw new PluginPackageException(Msg.T("項目が多すぎます。", "The package has too many entries."));
        }

        var files = new List<(ZipArchiveEntry, IReadOnlyList<string>)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            IReadOnlyList<string> parts = SafeParts(entry.FullName)
                ?? throw new PluginPackageException(Msg.T($"安全でないパスの項目があります: {entry.FullName}", $"The package has an unsafe path: {entry.FullName}"));
            bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            if (directory)
            {
                continue;
            }

            if (!seen.Add(string.Join('/', parts)))
            {
                throw new PluginPackageException(Msg.T($"同じパスの項目が重複しています: {entry.FullName}", $"Duplicate entry: {entry.FullName}"));
            }

            total += entry.Length;
            if (entry.Length < 0 || total > MaxTotalBytes)
            {
                throw new PluginPackageException(Msg.T("展開後の大きさが上限を超えます。", "The extracted size exceeds the limit."));
            }

            if (entry.Length > 1024 * 1024 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxRatio)
            {
                throw new PluginPackageException(Msg.T($"圧縮率が異常な項目があります: {entry.FullName}", $"An entry has an abnormal compression ratio: {entry.FullName}"));
            }

            files.Add((entry, parts));
        }

        return files;
    }

    /// <summary>マニフェストの場所: 直下なら null、1 つだけの最上位のフォルダの直下ならそのフォルダ名。どちらでもなければ例外。</summary>
    private static string? ManifestPrefix(List<(ZipArchiveEntry Entry, IReadOnlyList<string> Parts)> files)
    {
        if (files.Any(f => f.Parts.Count == 1 && f.Parts[0].Equals(PluginManifest.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var tops = files.Select(f => f.Parts[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tops.Count == 1 && files.Any(f => f.Parts.Count == 2 && f.Parts[1].Equals(PluginManifest.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            return tops[0];
        }

        throw new PluginPackageException(Msg.T("マニフェスト (plugin.json) がありません。", "The manifest (plugin.json) is missing."));
    }

    internal static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
