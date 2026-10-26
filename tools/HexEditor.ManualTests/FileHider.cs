namespace HexEditor.ManualTests;

/// <summary>
/// 準備の「隠すファイル」(docs/test/manual-test-prepare.md): テストのあいだだけ、配布物のフォルダの中のファイルの名前を
/// <c>名前.bak</c> に変えて隠し、あとで元に戻す。何度呼んでもよく、既にあるファイルを上書きしない。
/// 失敗したときは、利用者に見せる理由を返す (成功なら null)。
/// </summary>
public static class FileHider
{
    public const string BackupSuffix = ".bak";

    public static string BackupPath(string folder, string name) => Path.Combine(folder, name + BackupSuffix);

    /// <summary>隠してある (元の名前がなく、.bak がある)。</summary>
    public static bool IsHidden(string folder, string name) =>
        !File.Exists(Path.Combine(folder, name)) && File.Exists(BackupPath(folder, name));

    /// <summary>名前を <c>名前.bak</c> に変えて隠す。もう隠してあれば何もしない。</summary>
    public static string? Hide(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        string backup = BackupPath(folder, name);
        if (File.Exists(path))
        {
            if (File.Exists(backup))
            {
                return $"{name} と {name}{BackupSuffix} の両方があるため、名前を変えません (上書きしないため)。フォルダを開いて、どちらかを消してから「もう一度」を押してください: {folder}";
            }

            try
            {
                File.Move(path, backup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"{name} の名前を変えられませんでした ({ex.Message})。このフォルダの HexEditor が動いていると変えられません。HexEditor を閉じてから「もう一度」を押してください: {folder}";
            }

            return null;
        }

        return File.Exists(backup) ? null : $"{name} が見つかりません: {folder}";
    }

    /// <summary>隠したファイルの名前を元に戻す。隠していなければ何もしない。<paramref name="restored"/> は、実際に戻したとき true。</summary>
    public static string? Restore(string folder, string name, out bool restored)
    {
        restored = false;
        string path = Path.Combine(folder, name);
        string backup = BackupPath(folder, name);
        if (!File.Exists(backup))
        {
            return null;
        }

        if (File.Exists(path))
        {
            return $"{name} と {name}{BackupSuffix} の両方があるため、戻しませんでした (上書きしないため)。フォルダを開いて確かめてください: {folder}";
        }

        try
        {
            File.Move(backup, path);
            restored = true;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{name}{BackupSuffix} の名前を {name} に戻せませんでした ({ex.Message})。フォルダを開いて、手で戻してください: {folder}";
        }
    }
}
