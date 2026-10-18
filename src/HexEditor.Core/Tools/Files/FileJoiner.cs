using System.Text.RegularExpressions;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Tools.Files;

/// <summary>結合の条件 (TOOL-14 の仕様 3)。</summary>
public sealed record JoinOptions
{
    /// <summary>各ファイルの開始位置をこのバイト数の倍数にそろえる。1 以下ならそろえない (既定)。</summary>
    public long AlignTo { get; init; }

    /// <summary>そろえるときの詰める値 (既定 00)。</summary>
    public byte PadValue { get; init; }
}

/// <summary>結合の 1 ファイル分: パス、長さ、結合後の開始位置、手前に入れる詰め物の長さ。</summary>
public sealed record JoinEntry(string Path, long Length, long Start, long PaddingBefore);

public sealed record JoinPlan(IReadOnlyList<JoinEntry> Entries, long TotalLength, IReadOnlyList<(string Path, string Reason)> Unreadable)
{
    public bool CanRun => Entries.Count > 0 && Unreadable.Count == 0;
}

/// <summary>ファイルの結合 (TOOL-14)。</summary>
public static partial class FileJoiner
{
    private const int BufferSize = 4 * 1024 * 1024;

    [GeneratedRegex(@"^\.(\d{2,})$")]
    private static partial Regex NumberedExtension();

    // ---- 連番のファイル (仕様 1) ----

    /// <summary>
    /// <c>.001</c> のように拡張子が連番のファイルか。そうなら、同じ名前 (拡張子を除く) で同じ桁数の連番のファイルのうち、番号がこのファイル
    /// 以降のもの (このファイルを含む。番号順) を返す。連番でなければ空。
    /// </summary>
    public static IReadOnlyList<string> FindSequence(string path)
    {
        string full = Path.GetFullPath(path);
        Match match = NumberedExtension().Match(Path.GetExtension(full));
        string? directory = Path.GetDirectoryName(full);
        if (!match.Success || directory is null || !Directory.Exists(directory))
        {
            return [];
        }

        int width = match.Groups[1].Value.Length;
        long number = long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        string stem = Path.GetFileNameWithoutExtension(full);
        var found = new List<(long Number, string Path)>();
        foreach (string candidate in Directory.EnumerateFiles(directory))
        {
            Match m = NumberedExtension().Match(Path.GetExtension(candidate));
            if (!m.Success || m.Groups[1].Value.Length != width
                || !string.Equals(Path.GetFileNameWithoutExtension(candidate), stem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long n = long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (n >= number)
            {
                found.Add((n, candidate));
            }
        }

        return [.. found.OrderBy(f => f.Number).Select(f => f.Path)];
    }

    /// <summary>自然順 (数字の並びを数値として比べる) の比較。ファイル名だけを比べる。</summary>
    public static IComparer<string> NaturalOrder { get; } = new NaturalComparer();

    private sealed class NaturalComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            int i = 0;
            int j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    int si = i;
                    int sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i]))
                    {
                        i++;
                    }

                    while (j < y.Length && char.IsAsciiDigit(y[j]))
                    {
                        j++;
                    }

                    ReadOnlySpan<char> a = x.AsSpan(si, i - si).TrimStart('0');
                    ReadOnlySpan<char> b = y.AsSpan(sj, j - sj).TrimStart('0');
                    int byLength = a.Length.CompareTo(b.Length);
                    if (byLength != 0)
                    {
                        return byLength;
                    }

                    int byValue = a.CompareTo(b, StringComparison.Ordinal);
                    if (byValue != 0)
                    {
                        return byValue;
                    }

                    // 数値が同じなら、先頭の 0 が少ない方を先にする。
                    int byZeros = (i - si).CompareTo(j - sj);
                    if (byZeros != 0)
                    {
                        return byZeros;
                    }

                    continue;
                }

                int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0)
                {
                    return c;
                }

                i++;
                j++;
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }

    /// <summary>名前順 (ファイル名の文字の順) に並べ替える。</summary>
    public static IReadOnlyList<string> SortByName(IEnumerable<string> paths) =>
        [.. paths.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.OrdinalIgnoreCase)];

    /// <summary>自然順に並べ替える。</summary>
    public static IReadOnlyList<string> SortNatural(IEnumerable<string> paths) =>
        [.. paths.OrderBy(p => Path.GetFileName(p), NaturalOrder).ThenBy(p => p, StringComparer.OrdinalIgnoreCase)];

    // ---- 計画 ----

    /// <summary>結合の計画を作る。読めないファイルは <see cref="JoinPlan.Unreadable"/> に入れる (仕様: そのファイルを示して実行しない)。</summary>
    public static JoinPlan Plan(IReadOnlyList<string> paths, JoinOptions options)
    {
        var entries = new List<JoinEntry>(paths.Count);
        var unreadable = new List<(string, string)>();
        long position = 0;
        long align = options.AlignTo;
        foreach (string path in paths)
        {
            long length;
            try
            {
                using FileStream probe = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                length = probe.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                unreadable.Add((path, ex.Message));
                continue;
            }

            long padding = 0;
            if (align > 1 && entries.Count > 0)
            {
                long rem = position % align;
                padding = rem == 0 ? 0 : align - rem;
            }

            position += padding;
            entries.Add(new JoinEntry(path, length, position, padding));
            position += length;
        }

        return new JoinPlan(entries, position, unreadable);
    }

    // ---- ファイルに保存する (仕様 3・巨大ファイル) ----

    /// <summary>結合した結果の書き出し。外部変更を検知したファイルの一覧 (結合中に元のファイルが変わった場合。ENG-19 と同じく通知する)。</summary>
    public sealed record JoinWriteResult(IReadOnlyList<string> ChangedDuringJoin);

    /// <summary>
    /// 結合した内容をファイルに書く (ストリームでコピーする。一時ファイルに書いてから置き換える)。空き容量が足りなければ書き始めない。
    /// </summary>
    public static JoinWriteResult WriteToFile(JoinPlan plan, JoinOptions options, string destination, LongRunningOperation? operation = null,
        IVolumeInfoProvider? volumes = null, CancellationToken cancellationToken = default)
    {
        if (!plan.CanRun)
        {
            throw new FileToolException(FileToolError.Unreadable, "読めないファイルがあります。");
        }

        string full = Path.GetFullPath(destination);
        string folder = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(folder);
        VolumeInfo? volume = (volumes ?? SystemVolumeInfoProvider.Instance).GetVolume(folder);
        long required = plan.TotalLength + DocumentSaver.FreeSpaceMargin;
        if (volume?.AvailableFreeSpace is long available && available < required)
        {
            throw new FileToolException(FileToolError.InsufficientSpace, "空き容量が足りません。") { Space = new SpaceShortage(volume.Name, required, available) };
        }

        operation?.SetTotal(plan.TotalLength);
        var stamps = plan.Entries.ToDictionary(e => e.Path, e => FileStamp.FromPath(e.Path));
        long done = 0;
        byte[] buffer = new byte[BufferSize];
        byte[] pad = new byte[64 * 1024];
        Array.Fill(pad, options.PadValue);
        SafeFileWriter.Write(full, output =>
        {
            foreach (JoinEntry entry in plan.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                operation?.ReportDetail(System.IO.Path.GetFileName(entry.Path));
                for (long left = entry.PaddingBefore; left > 0;)
                {
                    int n = (int)Math.Min(pad.Length, left);
                    output.Write(pad, 0, n);
                    left -= n;
                    done += n;
                }

                using var input = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
                    FileOptions.SequentialScan);
                long remaining = entry.Length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (n <= 0)
                    {
                        throw new IOException($"{entry.Path} を最後まで読めませんでした。");
                    }

                    output.Write(buffer, 0, n);
                    remaining -= n;
                    done += n;
                    operation?.Report(done);
                }
            }
        });

        var changed = new List<string>();
        foreach (JoinEntry entry in plan.Entries)
        {
            FileStamp? after = FileStamp.FromPath(entry.Path);
            if (stamps[entry.Path] is { } before && after is not null && (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc))
            {
                changed.Add(entry.Path);
            }
        }

        return new JoinWriteResult(changed);
    }

    // ---- 新しいドキュメントとして開く (仕様 4) ----

    /// <summary>
    /// 結合した内容を表す新しい (無題の) ドキュメントを作る。各ファイルを参照するピースでつなぐため、データはコピーせず、
    /// ファイルの数に比例する時間で終わる。保存したときに初めて書き出す。詰め物はパターンのピース。
    /// </summary>
    public static Document CreateDocument(JoinPlan plan, JoinOptions options, string name, DocumentOptions? documentOptions = null)
    {
        if (!plan.CanRun)
        {
            throw new FileToolException(FileToolError.Unreadable, "読めないファイルがあります。");
        }

        var doc = new Document(MemoryByteSource.CreateEmpty(name), documentOptions);
        var opened = new List<FileByteSource>();
        try
        {
            foreach (JoinEntry entry in plan.Entries)
            {
                if (entry.PaddingBefore > 0)
                {
                    doc.InsertContent(doc.Length, EditContent.Pattern([options.PadValue], entry.PaddingBefore));
                }

                if (entry.Length > 0)
                {
                    FileByteSource source = FileByteSource.Open(entry.Path);
                    opened.Add(source);
                    doc.InsertContent(doc.Length, EditContent.FromSource(source, 0, source.Length, owns: true));
                    opened.Remove(source);
                }
            }
        }
        catch
        {
            foreach (FileByteSource source in opened)
            {
                source.Dispose();
            }

            doc.Dispose();
            throw;
        }

        doc.ClearHistory();
        return doc;
    }
}
