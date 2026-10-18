using System.Globalization;
using System.Security.Cryptography;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Tools.Files;

/// <summary>上書きの 1 回分。<see cref="Pattern"/> が null なら暗号論的な乱数、あればそのバイト列の繰り返し。</summary>
public sealed record ShredPass(byte[]? Pattern)
{
    public bool IsRandom => Pattern is null;

    public static ShredPass Random { get; } = new((byte[]?)null);

    public static ShredPass Fill(params byte[] pattern) => new(pattern);
}

/// <summary>上書きの方式 (TOOL-17 の仕様 2。FOR-30・FOR-31 も同じ方式を使う)。</summary>
public sealed record ShredMethod(string Id, IReadOnlyList<ShredPass> Passes)
{
    public const int MaxCustomPasses = 35;

    /// <summary>0 で 1 回 (既定)。</summary>
    public static ShredMethod ZeroOnce { get; } = new("zero", [ShredPass.Fill(0x00)]);

    /// <summary>乱数で 1 回。</summary>
    public static ShredMethod RandomOnce { get; } = new("random", [ShredPass.Random]);

    /// <summary>DoD 5220.22-M (3 回): 00、FF、乱数。</summary>
    public static ShredMethod Dod { get; } = new("dod", [ShredPass.Fill(0x00), ShredPass.Fill(0xFF), ShredPass.Random]);

    public static IReadOnlyList<ShredMethod> BuiltIn { get; } = [ZeroOnce, RandomOnce, Dod];

    /// <summary>任意 (回数 1〜35 と各回のパターン)。範囲外なら null。</summary>
    public static ShredMethod? Custom(IReadOnlyList<ShredPass> passes) =>
        passes.Count is >= 1 and <= MaxCustomPasses ? new ShredMethod("custom", passes) : null;

    /// <summary>
    /// 1 回分のパターンの文字列を読む。<c>random</c> (または <c>乱数</c>) なら乱数、それ以外は Hex バイト列 (<c>A5 5A</c>、<c>0xA5,0x5A</c> など)。
    /// 読めなければ null。
    /// </summary>
    public static ShredPass? ParsePass(string text)
    {
        string t = text.Trim();
        if (t.Equals("random", StringComparison.OrdinalIgnoreCase) || t == "乱数" || t == "らんすう")
        {
            return ShredPass.Random;
        }

        var bytes = new List<byte>();
        foreach (string token in t.Split([' ', ',', '\t', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            string hex = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;
            if (hex.Length % 2 != 0 || hex.Length == 0 || !hex.All(char.IsAsciiHexDigit))
            {
                return null;
            }

            bytes.AddRange(Convert.FromHexString(hex));
        }

        return bytes.Count is >= 1 and <= 4096 ? ShredPass.Fill([.. bytes]) : null;
    }
}

/// <summary>上書き中に書き込める 1 つのファイル (またはストリーム)。</summary>
public interface IShredStream : IDisposable
{
    long Length { get; }

    void Write(long offset, ReadOnlySpan<byte> data);

    int Read(long offset, Span<byte> buffer);

    /// <summary>ディスクに書き出す (<c>FlushFileBuffers</c>)。</summary>
    void Flush();

    void Truncate();
}

/// <summary>安全な削除が使うファイルシステムの操作。テストでは仮想ディスクに差し替える。</summary>
public interface IShredFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>フォルダの中のファイルをすべて (サブフォルダを含む。リンクはたどらない)。</summary>
    IEnumerable<string> EnumerateFiles(string directory);

    /// <summary>ファイル (またはストリーム) の長さ。開かずに調べる。</summary>
    long LengthOf(string path, string? streamName = null);

    /// <summary>排他で開く。他のプログラムが開いていれば <see cref="IOException"/>。</summary>
    IShredStream OpenExclusive(string path, string? streamName = null);

    /// <summary>メイン以外の代替データストリームの名前。</summary>
    IReadOnlyList<string> NamedStreams(string path);

    /// <summary>ランダムな名前に変えて、新しいパスを返す。</summary>
    string RenameRandom(string path);

    void DeleteFile(string path);

    void DeleteEmptyDirectory(string path);
}

/// <summary>対象にできない理由 (TOOL-17 の仕様 6・7)。</summary>
public enum ShredRejection
{
    None,

    /// <summary>システムフォルダ (<c>C:\Windows</c>、<c>Program Files</c> など)。</summary>
    SystemFolder,

    /// <summary>ドライブのルートそのもの。</summary>
    DriveRoot,

    /// <summary>アプリで開いているファイル (先に閉じる)。</summary>
    OpenInApp,

    NotFound,
}

public enum ShredFailureKind
{
    InUse,
    AccessDenied,
    NotFound,
    Other,
}

public sealed record ShredFailure(string Path, ShredFailureKind Kind, string Message);

public sealed record ShredResult(int DeletedFiles, long OverwrittenBytes, IReadOnlyList<ShredFailure> Failures, bool Cancelled,
    IReadOnlyList<string> LeftCorrupted)
{
    public bool Succeeded => Failures.Count == 0 && !Cancelled;
}

/// <summary>安全な削除 (シュレッダー。TOOL-17)。</summary>
public static class SecureDelete
{
    private const int BufferSize = 1 << 20;

    /// <summary>対象にできないシステムフォルダ (環境から求める)。</summary>
    public static IReadOnlyList<string> SystemFolders()
    {
        var folders = new List<string>();
        foreach (string? p in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetEnvironmentVariable("ProgramW6432"),
        })
        {
            if (!string.IsNullOrEmpty(p))
            {
                folders.Add(Normalize(p));
            }
        }

        return [.. folders.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    /// <summary>パスが対象にできるか。<paramref name="openFiles"/> はアプリで開いているファイル。</summary>
    public static ShredRejection Check(string path, IShredFileSystem fileSystem, IEnumerable<string> openFiles, IReadOnlyList<string>? systemFolders = null)
    {
        string full = Path.GetFullPath(path);
        string trimmed = Normalize(full);
        string? root = Path.GetPathRoot(full);
        if (root is not null && string.Equals(trimmed, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return ShredRejection.DriveRoot;
        }

        foreach (string folder in systemFolders ?? SystemFolders())
        {
            if (IsSameOrUnder(trimmed, folder))
            {
                return ShredRejection.SystemFolder;
            }
        }

        bool isDirectory = fileSystem.DirectoryExists(full);
        if (!isDirectory && !fileSystem.FileExists(full))
        {
            return ShredRejection.NotFound;
        }

        foreach (string open in openFiles)
        {
            string openFull = Normalize(open);
            if (isDirectory ? IsSameOrUnder(openFull, trimmed) : string.Equals(openFull, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return ShredRejection.OpenInApp;
            }
        }

        return ShredRejection.None;
    }

    private static bool IsSameOrUnder(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>対象 (ファイルとフォルダ) を、中のファイルをすべて集めた一覧にする。重複は除く。</summary>
    public static IReadOnlyList<string> ExpandTargets(IEnumerable<string> targets, IShredFileSystem fileSystem)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string target in targets)
        {
            string full = Path.GetFullPath(target);
            if (fileSystem.DirectoryExists(full))
            {
                foreach (string file in fileSystem.EnumerateFiles(full))
                {
                    if (seen.Add(file))
                    {
                        files.Add(file);
                    }
                }
            }
            else if (seen.Add(full))
            {
                files.Add(full);
            }
        }

        return files;
    }

    /// <summary>確認ダイアログに出す合計サイズ (ストリームを含まない)。</summary>
    public static long TotalSize(IEnumerable<string> files, IShredFileSystem fileSystem)
    {
        long total = 0;
        foreach (string file in files)
        {
            try
            {
                total += fileSystem.LengthOf(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 調べられないファイルはサイズを数えない (実行時に失敗として一覧に出る)。
            }
        }

        return total;
    }

    /// <summary>
    /// 安全に削除する。ファイルごとに: 排他で開く → 方式に従って全体を上書きして各回の後にディスクへ書き出す → (任意で) 読み戻して確認 →
    /// 長さを 0 にする → ランダムな名前に変える → 削除する (ごみ箱には入れない)。代替データストリームも同じ方式で上書きする。
    /// 開けない・書けないファイルは飛ばして続け、結果の一覧に理由を残す。キャンセルしたら途中までのファイルは削除せずに残す。
    /// </summary>
    public static ShredResult Execute(IReadOnlyList<string> files, ShredMethod method, bool verifyLastPass, IShredFileSystem fileSystem,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default, IEnumerable<string>? foldersToRemove = null)
    {
        var failures = new List<ShredFailure>();
        var corrupted = new List<string>();
        int deleted = 0;
        long overwritten = 0;
        long totalBytes = 0;
        foreach (string file in files)
        {
            try
            {
                totalBytes += fileSystem.LengthOf(file) * method.Passes.Count;
                foreach (string ads in fileSystem.NamedStreams(file))
                {
                    totalBytes += fileSystem.LengthOf(file, ads) * method.Passes.Count;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 実行時に失敗として扱う。
            }
        }

        operation?.SetTotal(totalBytes);
        byte[] buffer = new byte[BufferSize];
        long done = 0;
        foreach (string file in files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ShredResult(deleted, overwritten, failures, true, corrupted);
            }

            operation?.ReportDetail(file);
            bool started = false;
            try
            {
                // 排他で開いたまま、メインのストリームを上書きして長さを 0 にする。
                using (IShredStream stream = fileSystem.OpenExclusive(file))
                {
                    started = true;
                    OverwriteStream(stream, method, verifyLastPass, buffer, ref done, ref overwritten, operation, cancellationToken);
                    stream.Truncate();
                    stream.Flush();
                }

                // 代替データストリームも同じ方式で上書きする (仕様 3 の 5)。
                foreach (string ads in fileSystem.NamedStreams(file))
                {
                    using IShredStream stream = fileSystem.OpenExclusive(file, ads);
                    OverwriteStream(stream, method, verifyLastPass, buffer, ref done, ref overwritten, operation, cancellationToken);
                    stream.Truncate();
                    stream.Flush();
                }

                string renamed = fileSystem.RenameRandom(file);
                fileSystem.DeleteFile(renamed);
                deleted++;
            }
            catch (OperationCanceledException)
            {
                // 途中まで上書きしたファイルは削除せずに残す (内容は壊れている)。
                if (started)
                {
                    corrupted.Add(file);
                }

                return new ShredResult(deleted, overwritten, failures, true, corrupted);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new ShredFailure(file, Classify(ex), ex.Message));
            }
        }

        // 空になったフォルダを、深い方から消す。
        if (foldersToRemove is not null)
        {
            foreach (string folder in foldersToRemove.OrderByDescending(f => f.Length))
            {
                try
                {
                    DeleteEmptyTree(Path.GetFullPath(folder), fileSystem);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 中にファイルが残っているフォルダは残す。
                }
            }
        }

        return new ShredResult(deleted, overwritten, failures, false, corrupted);
    }

    private static void DeleteEmptyTree(string folder, IShredFileSystem fileSystem)
    {
        if (!fileSystem.DirectoryExists(folder))
        {
            return;
        }

        foreach (string sub in Directory.Exists(folder) ? Directory.EnumerateDirectories(folder) : [])
        {
            DeleteEmptyTree(sub, fileSystem);
        }

        if (!fileSystem.EnumerateFiles(folder).Any())
        {
            fileSystem.DeleteEmptyDirectory(folder);
        }
    }

    private static ShredFailureKind Classify(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return ShredFailureKind.AccessDenied;
        }

        if (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ShredFailureKind.NotFound;
        }

        // ERROR_SHARING_VIOLATION (32)、ERROR_LOCK_VIOLATION (33)
        return (ex.HResult & 0xFFFF) is 32 or 33 ? ShredFailureKind.InUse : ShredFailureKind.Other;
    }

    private static void OverwriteStream(IShredStream stream, ShredMethod method, bool verify, byte[] buffer, ref long done, ref long overwritten,
        LongRunningOperation? operation, CancellationToken cancellationToken)
    {
        long length = stream.Length;
        for (int pass = 0; pass < method.Passes.Count; pass++)
        {
            ShredPass shred = method.Passes[pass];
            for (long position = 0; position < length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, length - position);
                Fill(shred, buffer.AsSpan(0, n), position);
                stream.Write(position, buffer.AsSpan(0, n));
                position += n;
                done += n;
                overwritten += n;
                operation?.Report(done);
            }

            stream.Flush();
        }

        if (verify && method.Passes[^1] is { IsRandom: false } last)
        {
            // 最後の上書きを読み戻して確認する (乱数の回は内容を覚えていないため確認しない)。
            byte[] expected = new byte[buffer.Length];
            for (long position = 0; position < length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, length - position);
                int got = stream.Read(position, buffer.AsSpan(0, n));
                Fill(last, expected.AsSpan(0, n), position);
                if (got != n || !buffer.AsSpan(0, n).SequenceEqual(expected.AsSpan(0, n)))
                {
                    throw new IOException("上書きの確認に失敗しました (読み戻した内容が一致しません)。");
                }

                position += n;
            }
        }
    }

    /// <summary>1 回分の内容を <paramref name="buffer"/> に作る。パターンは位置 <paramref name="position"/> からの位相で繰り返す。</summary>
    public static void Fill(ShredPass pass, Span<byte> buffer, long position)
    {
        if (pass.Pattern is not { } pattern)
        {
            RandomNumberGenerator.Fill(buffer);
            return;
        }

        if (pattern.Length == 1)
        {
            buffer.Fill(pattern[0]);
            return;
        }

        int phase = (int)(position % pattern.Length);
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = pattern[(phase + i) % pattern.Length];
        }
    }
}

/// <summary>本物のファイルシステムでの安全な削除の操作。</summary>
public sealed class WindowsShredFileSystem : IShredFileSystem
{
    public static WindowsShredFileSystem Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        });

    public long LengthOf(string path, string? streamName = null)
    {
        string full = Path.GetFullPath(path);
        if (streamName is { Length: > 0 } && OperatingSystem.IsWindows())
        {
            return AlternateStreams.List(full).FirstOrDefault(x => x.Name == streamName)?.Size ?? 0;
        }

        return new FileInfo(full).Length;
    }

    public IShredStream OpenExclusive(string path, string? streamName = null)
    {
        string full = Path.GetFullPath(path);
        try
        {
            FileAttributes attributes = File.GetAttributes(full);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(full, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 属性を変えられなくても、開くときに失敗として分かる。
        }

        string target = streamName is { Length: > 0 } ? AlternateStreams.PathOf(full, streamName) : full;
        return new WindowsShredStream(new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough));
    }

    public IReadOnlyList<string> NamedStreams(string path)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? [.. AlternateStreams.List(path).Where(s => !s.IsDefault).Select(s => s.Name)]
                : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public string RenameRandom(string path)
    {
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)!;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string name = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + ".tmp";
            string target = Path.Combine(directory, name);
            if (File.Exists(target))
            {
                continue;
            }

            File.Move(full, target);
            return target;
        }

        return full;
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteEmptyDirectory(string path) => Directory.Delete(path, recursive: false);

    private sealed class WindowsShredStream(FileStream stream) : IShredStream
    {
        public long Length => stream.Length;

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            stream.Position = offset;
            stream.Write(data);
        }

        public int Read(long offset, Span<byte> buffer)
        {
            stream.Position = offset;
            return stream.Read(buffer);
        }

        public void Flush() => stream.Flush(flushToDisk: true);

        public void Truncate() => stream.SetLength(0);

        public void Dispose() => stream.Dispose();
    }
}
