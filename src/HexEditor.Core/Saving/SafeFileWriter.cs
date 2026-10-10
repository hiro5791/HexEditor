using System.Security.Cryptography;

namespace HexEditor.Core.Saving;

/// <summary>
/// 内容を作りながら書くファイル (エクスポート・元の形式での保存。TOOL-04・TOOL-11) を、安全な保存 (ENG-22) と同じ手順で書く:
/// 同じフォルダの隠し属性の一時ファイルに書き、ディスクに反映してから <c>ReplaceFile</c> で置き換える (属性・ACL・作成日時・代替データストリームを
/// 引き継ぐ。仕様 1 の 8)。シンボリックリンクはリンク先を置き換える (仕様 1 の 1)。バックアップ (ENG-26) を指定すると置き換え前のファイルを残し、
/// 記録の場所を指定すると一時ファイルの名前と保存先を記録する (仕様 1 の 7)。途中で失敗・キャンセルしたら一時ファイルを消し、元のファイルは変えない。
/// </summary>
public static class SafeFileWriter
{
    /// <summary>書く。作ったバックアップ (なければ null) を返す。</summary>
    public static BackupOutcome? Write(string path, Action<Stream> write, BackupSettings? backup = null, string? markerDirectory = null)
    {
        string target = DocumentSaver.ResolveTarget(Path.GetFullPath(path));
        string folder = Path.GetDirectoryName(target)!;

        // バックアップを作れるかを先に確かめる。作れなければ書き始めない (ENG-26 の「エラー」)。
        string? backupPath = null;
        if (backup is not null && File.Exists(target))
        {
            string first = Backup.PathFor(target, backup);
            backupPath = Backup.Prepare(target, backup, null, Backup.SameVolume(target, first) ? 0 : new FileInfo(target).Length);
        }

        string temp = Path.Combine(folder, $".{Path.GetFileName(target)}.~hex{RandomNumberGenerator.GetHexString(8, lowercase: true)}.tmp");
        SaveTempMarker marker = SaveTempMarker.Record(markerDirectory, temp, target);
        BackupOutcome? outcome = null;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan))
            {
                File.SetAttributes(temp, File.GetAttributes(temp) | FileAttributes.Hidden);
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.SetAttributes(temp, File.GetAttributes(temp) & ~FileAttributes.Hidden);
            if (File.Exists(target))
            {
                if (backupPath is not null && Backup.SameVolume(target, backupPath))
                {
                    // 置き換え前のファイルの名前を変えてバックアップにする (コピーしない)。
                    string renamedTo = backupPath;
                    TimeSpan time = Backup.Measure(() =>
                    {
                        Backup.Rotate(target, backup!);
                        File.Replace(temp, target, renamedTo, ignoreMetadataErrors: true);
                    });
                    outcome = new BackupOutcome(renamedTo, time);
                }
                else
                {
                    if (backupPath is not null)
                    {
                        string copyTo = backupPath;
                        TimeSpan time = Backup.Measure(() => Backup.CreateByCopy(target, backup!));
                        outcome = new BackupOutcome(copyTo, time);
                    }

                    File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
            }
            else
            {
                File.Move(temp, target);
            }
        }
        catch
        {
            if (DocumentSaver.TryDelete(temp))
            {
                marker.Dispose();
            }
            else
            {
                marker.Keep();
            }

            throw;
        }

        marker.Dispose();
        return outcome;
    }
}
