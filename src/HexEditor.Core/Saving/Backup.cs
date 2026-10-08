using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Saving;

/// <summary>バックアップの設定 (ENG-26)。</summary>
public sealed record BackupSettings
{
    public const string EnabledKey = "save.backup.enabled";
    public const string LocationKey = "save.backup.location";
    public const string FolderKey = "save.backup.folder";
    public const string GenerationsKey = "save.backup.generations";

    /// <summary>その場保存でコピーの前に確かめる大きさ (1 GiB。仕様 5)。</summary>
    public const long CopyConfirmBytes = 1L << 30;

    public const int MaxGenerations = 10;

    /// <summary>置き場所。null なら元のファイルと同じフォルダ (既定)。</summary>
    public string? Folder { get; init; }

    /// <summary>世代数 (1〜10。仕様 3)。</summary>
    public int Generations { get; init; } = 1;
}

/// <summary>バックアップを作れないため保存を始めなかった (ENG-26 の「エラー」)。UI は「バックアップなしで保存」を付けて示す。</summary>
public sealed class BackupFailedException(string reason, Exception? inner = null)
    : IOException($"バックアップを作れないため保存を中止しました ({reason})", inner)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// バックアップファイル (ENG-26): 名前は <c>&lt;元の名前&gt;.bak</c>、2 世代目から <c>.bak2</c>、<c>.bak3</c>…。指定したフォルダに置く場合は、
/// 名前が衝突しないように元のパスのハッシュの短縮形 (8 桁) を付ける (<c>&lt;元の名前&gt;.&lt;ハッシュ&gt;.bak</c>)。
/// </summary>
public static class Backup
{
    /// <summary>1 世代目のバックアップのパス。</summary>
    public static string PathFor(string target, BackupSettings settings, int generation = 1)
    {
        string full = Path.GetFullPath(target);
        string first = settings.Folder is null
            ? full + ".bak"
            : Path.Combine(settings.Folder, $"{Path.GetFileName(full)}.{PathHash(full)}.bak");
        return generation <= 1 ? first : first + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>パスのハッシュの短縮形 (SHA-256 の先頭 8 桁。大文字・小文字を区別しない)。</summary>
    public static string PathHash(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())), 0, 4).ToLowerInvariant();

    /// <summary>
    /// 世代を 1 つずらして、1 世代目の名前を空ける (仕様 3: 古いものから削除する)。<c>.bak</c> → <c>.bak2</c> → … とし、世代数を超える
    /// ものは消す。置き場所のフォルダがなければ作る。できなければ <see cref="BackupFailedException"/> (保存を始めない)。
    /// </summary>
    public static string Rotate(string target, BackupSettings settings)
    {
        int generations = Math.Clamp(settings.Generations, 1, BackupSettings.MaxGenerations);
        try
        {
            string first = PathFor(target, settings);
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            DeleteIfExists(PathFor(target, settings, generations));
            for (int g = generations - 1; g >= 1; g--)
            {
                string from = PathFor(target, settings, g);
                if (File.Exists(from))
                {
                    File.Move(from, PathFor(target, settings, g + 1));
                }
            }

            return first;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupFailedException(ex.Message, ex);
        }
    }

    /// <summary>同じボリュームにあるか (名前の変更でバックアップにできるか。仕様 4)。</summary>
    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ファイル全体をコピーしてバックアップにする (その場保存・別のボリュームへの置き場所。仕様 5)。キャンセルできる。失敗したら
    /// 途中のファイルを消して <see cref="BackupFailedException"/>。
    /// </summary>
    public static void Copy(string source, string backup, LongRunningOperation? operation = null)
    {
        string temp = backup + ".tmp";

        // コピーの間は、コピーするバイト数を全体として進捗を示す (ENG-26 の「巨大ファイル・長時間処理」)。終わったら元に戻す。
        long? previousTotal = operation?.TotalBytes;
        long previousProcessed = operation?.ProcessedBytes ?? 0;
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.SequentialScan))
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 0))
            {
                output.SetLength(input.Length);
                operation?.SetTotal(input.Length);
                operation?.Report(0);
                byte[] buffer = new byte[4 * 1024 * 1024];
                long copied = 0;
                int n;
                while ((n = input.Read(buffer)) > 0)
                {
                    operation?.CancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, n);
                    copied += n;
                    operation?.Report(copied);
                }

                output.Flush(flushToDisk: true);
            }

            File.Move(temp, backup, overwrite: true);
            operation?.SetTotal(previousTotal);
            operation?.Report(previousProcessed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteIfExists(temp);
            throw new BackupFailedException(ex.Message, ex);
        }
        catch (OperationCanceledException)
        {
            DeleteIfExists(temp);
            throw;
        }
    }

    /// <summary>名前の変更でバックアップにするのにかかった時間を測る (TC-ENG-26-02)。</summary>
    internal static TimeSpan Measure(Action action)
    {
        long start = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(start);
    }

    private static void DeleteIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        // 保存前の Undo 履歴が古いバックアップを開いていることがある (削除を許す共有)。名前を変えてから消し、名前をすぐに空ける。
        string doomed = $"{path}.~del{RandomNumberGenerator.GetHexString(8, lowercase: true)}";
        try
        {
            File.Move(path, doomed);
            File.Delete(doomed);
        }
        catch (IOException)
        {
            File.Delete(path);
        }
    }
}
