using System.Text.Json;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Saving;

/// <summary>異常終了で残った安全な保存の一時ファイル (ENG-22 の仕様 7)。</summary>
/// <param name="MarkerPath">記録のファイル (復旧用フォルダ)。</param>
/// <param name="TempPath">残った一時ファイル。</param>
/// <param name="TargetPath">保存先 (一時ファイルで置き換えるはずだったファイル)。</param>
public sealed record LeftoverTempFile(string MarkerPath, string TempPath, string TargetPath);

/// <summary>
/// 安全な保存 (ENG-22) の一時ファイルの名前と保存先の記録 (仕様 7、ENG-27 の仕様 7)。一時ファイルを作る前に復旧用フォルダに書き、置き換え・
/// 中止の後に消す。保存の間は記録のファイルを開いたままにする (他のインスタンスの起動時の確認で、使用中と分かる)。異常終了で記録が残った
/// 場合は、次回起動時に <see cref="Find"/> で見つけ、復旧の画面で一時ファイルの削除を提案する。
/// </summary>
public sealed partial class SaveTempMarker : IDisposable
{
    private const string FilePattern = "savetemp-*.json";

    private readonly string? _path;
    private FileStream? _stream;

    private SaveTempMarker(string? path, FileStream? stream)
    {
        _path = path;
        _stream = stream;
    }

    /// <summary>記録のファイル (記録できなかった場合は null)。</summary>
    public string? Path => _path;

    /// <summary>
    /// 記録する。<paramref name="directory"/> が null、または書けない場合は何も記録しない (保存は続ける。残った一時ファイルは片付けられない
    /// だけ)。
    /// </summary>
    public static SaveTempMarker Record(string? directory, string tempPath, string targetPath)
    {
        if (directory is null)
        {
            return new SaveTempMarker(null, null);
        }

        string path = System.IO.Path.Combine(directory, $"savetemp-{Guid.NewGuid():N}.json");
        FileStream? stream = null;
        try
        {
            Directory.CreateDirectory(directory);
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
            JsonSerializer.Serialize(stream, new MarkerData { TempPath = tempPath, TargetPath = targetPath });
            stream.Flush(flushToDisk: true);
            return new SaveTempMarker(path, stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            TryDelete(path);
            return new SaveTempMarker(null, null);
        }
    }

    /// <summary>一時ファイルが片付いた (置き換えた・削除した): 記録を消す。</summary>
    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        if (_path is not null)
        {
            TryDelete(_path);
        }
    }

    /// <summary>一時ファイルを消せなかった: 記録を残し (次回起動時に削除を提案する)、開いているハンドルだけを閉じる。</summary>
    public void Keep()
    {
        _stream?.Dispose();
        _stream = null;
    }

    /// <summary>
    /// <paramref name="directory"/> に残った記録のうち、一時ファイルがまだあるもの (起動時の復旧の画面に出す)。一時ファイルがもうない
    /// 記録・読めない記録は消す。保存中の記録 (他のインスタンスが開いている) は対象にしない。
    /// </summary>
    public static IReadOnlyList<LeftoverTempFile> Find(string directory)
    {
        var found = new List<LeftoverTempFile>();
        if (!Directory.Exists(directory))
        {
            return found;
        }

        foreach (string marker in Directory.GetFiles(directory, FilePattern))
        {
            MarkerData? data;
            try
            {
                // 保存中の記録は書き込みを共有せずには開けない (使用中)。
                using var stream = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                data = JsonSerializer.Deserialize<MarkerData>(stream);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (JsonException)
            {
                data = null;
            }

            if (data is { TempPath: { } temp, TargetPath: { } target } && IsSaveTempName(temp, target) && File.Exists(temp))
            {
                found.Add(new LeftoverTempFile(marker, temp, target));
            }
            else
            {
                TryDelete(marker);
            }
        }

        return found;
    }

    /// <summary>残った一時ファイルと記録を消す (復旧の画面の「削除」)。消せなければ例外 (記録は残す)。</summary>
    public static void Delete(LeftoverTempFile leftover)
    {
        if (File.Exists(leftover.TempPath))
        {
            File.SetAttributes(leftover.TempPath, FileAttributes.Normal);
            File.Delete(leftover.TempPath);
        }

        File.Delete(leftover.MarkerPath);
    }

    /// <summary>
    /// 安全な保存の一時ファイルの名前 (保存先と同じフォルダの <c>.&lt;元の名前&gt;.~hex&lt;8 桁の 16 進&gt;.tmp</c>) か。記録が書き換えられて
    /// いても、関係のないファイルを消さないために確かめる。
    /// </summary>
    public static bool IsSaveTempName(string tempPath, string targetPath)
    {
        string? folder = System.IO.Path.GetDirectoryName(tempPath);
        string? targetFolder = System.IO.Path.GetDirectoryName(targetPath);
        string name = System.IO.Path.GetFileName(tempPath);
        string prefix = "." + System.IO.Path.GetFileName(targetPath) + ".~hex";
        return folder is not null && string.Equals(folder, targetFolder, StringComparison.OrdinalIgnoreCase)
            && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && TempSuffix().IsMatch(name[prefix.Length..]);
    }

    [GeneratedRegex("^[0-9a-f]{8}\\.tmp$", RegexOptions.IgnoreCase)]
    private static partial Regex TempSuffix();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class MarkerData
    {
        public string? TempPath { get; set; }

        public string? TargetPath { get; set; }
    }
}
