using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Images.Scope;

/// <summary>
/// ファイルシステムの領域 (バイトの範囲。ボリュームの先頭から)。<see cref="Allocated"/> は割り当て済みの領域 (スパースイメージに含める)、
/// <see cref="Metadata"/> はファイルシステムのメタデータ (スケルトンイメージに含める。FOR-16 の「メタデータ」)。
/// </summary>
public sealed record FileSystemRegions(string FileSystem, IReadOnlyList<(long Offset, long Length)> Allocated, IReadOnlyList<(long Offset, long Length)> Metadata);

/// <summary>
/// ボリュームの割り当て済みの領域とメタデータの範囲を求める口 (FOR-23)。ファイルシステムの解析 (FOR-09〜14) の実装は、より詳しいものを
/// <see cref="FileSystemRegionProviders.Register"/> で登録できる (先に登録したものより優先する)。
/// </summary>
public interface IFileSystemRegionProvider
{
    /// <summary>ファイルシステムの名前 (NTFS、FAT32 など)。</summary>
    string Name { get; }

    /// <summary>ボリュームの先頭 (4 KiB) から、このファイルシステムかを判定する。</summary>
    bool Probe(ReadOnlySpan<byte> head);

    /// <summary>解析する。解析できなければ null (呼び出し側はボリューム全体を含める)。</summary>
    FileSystemRegions? Analyze(IByteSource volume, CancellationToken cancellationToken);
}

/// <summary>登録されたファイルシステムの領域の解析 (既定はファイルシステムの解析 FOR-09〜14 の <see cref="FileSystemSessionRegionProvider"/>)。</summary>
public static class FileSystemRegionProviders
{
    private static readonly List<IFileSystemRegionProvider> Providers = [new FileSystemSessionRegionProvider()];

    /// <summary>より詳しい解析を登録する (先頭に加える)。</summary>
    public static void Register(IFileSystemRegionProvider provider)
    {
        lock (Providers)
        {
            Providers.Insert(0, provider);
        }
    }

    /// <summary>ボリュームのファイルシステムを判定して解析する。判定できない・解析できなければ null。</summary>
    public static FileSystemRegions? Analyze(IByteSource volume, CancellationToken cancellationToken = default)
    {
        byte[] head = new byte[(int)Math.Min(4096, volume.Length)];
        if (head.Length < 512 || !volume.Read(0, head).IsComplete)
        {
            return null;
        }

        IFileSystemRegionProvider[] providers;
        lock (Providers)
        {
            providers = [.. Providers];
        }

        foreach (IFileSystemRegionProvider p in providers)
        {
            if (!p.Probe(head))
            {
                continue;
            }

            try
            {
                if (p.Analyze(volume, cancellationToken) is { } regions)
                {
                    // メタデータの値がボリュームより大きい範囲を指していても、ボリュームの中に切り詰める。
                    return regions with { Allocated = Clip(regions.Allocated, volume.Length), Metadata = Clip(regions.Metadata, volume.Length) };
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or OverflowException or IndexOutOfRangeException)
            {
                // 壊れたメタデータ: 解析できないものとして全体を含める。
            }
        }

        return null;
    }

    private static List<(long Offset, long Length)> Clip(IEnumerable<(long Offset, long Length)> ranges, long length) =>
        Merge(ranges.Where(r => r.Offset >= 0 && r.Offset < length && r.Length > 0).Select(r => (r.Offset, Math.Min(r.Length, length - r.Offset))));

    /// <summary>範囲を昇順に並べ、重なり・隣接をまとめる。</summary>
    public static List<(long Offset, long Length)> Merge(IEnumerable<(long Offset, long Length)> ranges)
    {
        var result = new List<(long Offset, long Length)>();
        foreach ((long o, long l) in ranges.Where(r => r.Length > 0).OrderBy(r => r.Offset))
        {
            if (result.Count > 0 && o <= result[^1].Offset + result[^1].Length)
            {
                (long po, long pl) = result[^1];
                result[^1] = (po, Math.Max(po + pl, o + l) - po);
            }
            else
            {
                result.Add((o, l));
            }
        }

        return result;
    }
}
