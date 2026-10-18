using System.Security.Cryptography;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Tools;

/// <summary>フォルダの比較の結果の状態 (TOOL-20 の仕様 3)。</summary>
public enum FolderCompareStatus
{
    Same,
    Different,
    LeftOnly,
    RightOnly,
    ReadError,
}

/// <summary>フォルダの比較の条件 (TOOL-20 の仕様 1)。</summary>
public sealed record FolderCompareOptions
{
    /// <summary>サブフォルダを含める (既定: 含める)。</summary>
    public bool IncludeSubfolders { get; init; } = true;

    /// <summary>対象のファイル名のパターン (例: <c>*.bin</c>。<c>;</c> か <c>,</c> で複数)。空なら、すべて。</summary>
    public string Pattern { get; init; } = string.Empty;

    /// <summary>バイトごとの比較の代わりに SHA-256 で比較し、その値を一覧に出す。</summary>
    public bool UseHash { get; init; }
}

/// <summary>結果一覧の 1 行。<see cref="FirstDifference"/> はバイトごとの比較で最初に違ったオフセット (サイズが違うときやハッシュの比較では null)。</summary>
public sealed record FolderCompareRow(
    FolderCompareStatus Status,
    string RelativePath,
    string? LeftPath,
    string? RightPath,
    long? LeftSize,
    long? RightSize,
    long? FirstDifference,
    string? LeftHash = null,
    string? RightHash = null,
    string? Error = null);

/// <summary>
/// 2 つのフォルダの比較 (TOOL-20)。相対パスで対応付け (大文字・小文字は区別しない)、サイズが違えば「異なる」、同じなら内容を比べる。
/// フォルダごとに名前の順に突き合わせて、確定した行から <c>onRow</c> に渡す (メモリはフォルダ内の項目数に比例し、全体のファイル数には比例しない)。
/// </summary>
public static class FolderComparer
{
    public const int ChunkSize = 1024 * 1024;

    public static IReadOnlyList<FolderCompareRow> Compare(string left, string right, FolderCompareOptions options, Action<FolderCompareRow>? onRow = null,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        var rows = new List<FolderCompareRow>();
        string[] patterns = [.. options.Pattern.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        void Emit(FolderCompareRow row)
        {
            rows.Add(row);
            onRow?.Invoke(row);
        }

        var context = new Context(options, patterns, Emit, operation, cancellationToken);
        CompareDirectory(context, Path.GetFullPath(left), Path.GetFullPath(right), string.Empty);
        return rows;
    }

    private sealed record Context(FolderCompareOptions Options, string[] Patterns, Action<FolderCompareRow> Emit, LongRunningOperation? Operation,
        CancellationToken Token)
    {
        public long Processed;
    }

    private static bool Matches(Context c, string fileName) =>
        c.Patterns.Length == 0 || c.Patterns.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true));

    private static (List<string> Files, List<string> Directories, string? Error) List(string directory)
    {
        var files = new List<string>();
        var directories = new List<string>();
        try
        {
            foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        directories.Add(entry.Name);
                    }
                }
                else
                {
                    files.Add(entry.Name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (files, directories, ex.Message);
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        directories.Sort(StringComparer.OrdinalIgnoreCase);
        return (files, directories, null);
    }

    private static (List<string> Files, List<string> Directories, string? Error) ListOrEmpty(string directory) =>
        directory.Length == 0 ? (new List<string>(), new List<string>(), null) : List(directory);

    private static string Join(string relative, string name) => relative.Length == 0 ? name : relative + "/" + name;

    private static void CompareDirectory(Context c, string left, string right, string relative)
    {
        c.Token.ThrowIfCancellationRequested();
        var l = ListOrEmpty(left);
        var r = ListOrEmpty(right);
        if (l.Error is not null || r.Error is not null)
        {
            // 読めないフォルダは「読み取りエラー」として一覧に出し、続ける。
            c.Emit(new FolderCompareRow(FolderCompareStatus.ReadError, relative.Length == 0 ? "." : relative, left, right, null, null, null,
                Error: l.Error ?? r.Error));
        }

        MergeFiles(c, left, right, relative, l.Files, r.Files);
        if (!c.Options.IncludeSubfolders)
        {
            return;
        }

        int i = 0, j = 0;
        while (i < l.Directories.Count || j < r.Directories.Count)
        {
            int order = i >= l.Directories.Count ? 1 : j >= r.Directories.Count ? -1 : StringComparer.OrdinalIgnoreCase.Compare(l.Directories[i], r.Directories[j]);
            if (order == 0)
            {
                CompareDirectory(c, Path.Combine(left, l.Directories[i]), Path.Combine(right, r.Directories[j]), Join(relative, l.Directories[i]));
                i++;
                j++;
            }
            else if (order < 0)
            {
                CompareDirectory(c, Path.Combine(left, l.Directories[i]), string.Empty, Join(relative, l.Directories[i]));
                i++;
            }
            else
            {
                CompareDirectory(c, string.Empty, Path.Combine(right, r.Directories[j]), Join(relative, r.Directories[j]));
                j++;
            }
        }
    }

    private static void MergeFiles(Context c, string left, string right, string relative, List<string> lf, List<string> rf)
    {
        int i = 0, j = 0;
        while (i < lf.Count || j < rf.Count)
        {
            c.Token.ThrowIfCancellationRequested();
            int order = i >= lf.Count ? 1 : j >= rf.Count ? -1 : StringComparer.OrdinalIgnoreCase.Compare(lf[i], rf[j]);
            if (order == 0)
            {
                if (Matches(c, lf[i]))
                {
                    CompareFiles(c, Path.Combine(left, lf[i]), Path.Combine(right, rf[j]), Join(relative, lf[i]));
                }

                i++;
                j++;
            }
            else if (order < 0)
            {
                if (Matches(c, lf[i]))
                {
                    string path = Path.Combine(left, lf[i]);
                    c.Emit(new FolderCompareRow(FolderCompareStatus.LeftOnly, Join(relative, lf[i]), path, null, SizeOf(path), null, null));
                }

                i++;
            }
            else
            {
                if (Matches(c, rf[j]))
                {
                    string path = Path.Combine(right, rf[j]);
                    c.Emit(new FolderCompareRow(FolderCompareStatus.RightOnly, Join(relative, rf[j]), null, path, null, SizeOf(path), null));
                }

                j++;
            }
        }
    }

    private static long? SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void CompareFiles(Context c, string left, string right, string relative)
    {
        c.Operation?.ReportDetail(relative);
        long? ls = null, rs = null;
        try
        {
            ls = new FileInfo(left).Length;
            rs = new FileInfo(right).Length;
            if (ls != rs)
            {
                c.Emit(new FolderCompareRow(FolderCompareStatus.Different, relative, left, right, ls, rs, null));
                return;
            }

            if (c.Options.UseHash)
            {
                string lh = Hash(c, left);
                string rh = Hash(c, right);
                c.Emit(new FolderCompareRow(lh == rh ? FolderCompareStatus.Same : FolderCompareStatus.Different, relative, left, right, ls, rs, null, lh, rh));
                return;
            }

            long? first = FirstDifference(c, left, right, ls.Value);
            c.Emit(new FolderCompareRow(first is null ? FolderCompareStatus.Same : FolderCompareStatus.Different, relative, left, right, ls, rs, first));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            c.Emit(new FolderCompareRow(FolderCompareStatus.ReadError, relative, left, right, ls, rs, null, Error: ex.Message));
        }
    }

    private static string Hash(Context c, string path)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = Open(path);
        long length = RandomAccess.GetLength(handle);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[ChunkSize];
        for (long position = 0; position < length;)
        {
            c.Token.ThrowIfCancellationRequested();
            int read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - position)), position);
            if (read <= 0)
            {
                break;
            }

            hasher.AppendData(buffer, 0, read);
            position += read;
            Progress(c, read);
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);

    private static void Progress(Context c, long bytes)
    {
        c.Processed += bytes;
        c.Operation?.Report(c.Processed);
    }

    /// <summary>バイトごとに比べ、違いが見つかった時点で止める。最初に違うオフセット (同じなら null)。</summary>
    private static long? FirstDifference(Context c, string left, string right, long length)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle a = Open(left);
        using Microsoft.Win32.SafeHandles.SafeFileHandle b = Open(right);
        byte[] x = new byte[ChunkSize];
        byte[] y = new byte[ChunkSize];
        for (long position = 0; position < length;)
        {
            c.Token.ThrowIfCancellationRequested();
            int want = (int)Math.Min(ChunkSize, length - position);
            int ra = ReadFully(a, x.AsSpan(0, want), position);
            int rb = ReadFully(b, y.AsSpan(0, want), position);
            int common = Math.Min(ra, rb);
            int mismatch = x.AsSpan(0, common).CommonPrefixLength(y.AsSpan(0, common));
            if (mismatch < common)
            {
                return position + mismatch;
            }

            if (ra != rb)
            {
                return position + common;
            }

            if (ra == 0)
            {
                return null;
            }

            position += ra;
            Progress(c, ra * 2L);
        }

        return null;
    }

    private static int ReadFully(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Span<byte> buffer, long position)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = RandomAccess.Read(handle, buffer[total..], position + total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
