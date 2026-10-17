using System.Text;
using HexEditor.Core.Scripting.Library;

namespace HexEditor.Core.Scripting.Macros;

/// <summary>
/// 記録したマクロの保存 (AUTO-27 の仕様 6・7、AUTO-28 の仕様 7)。<c>&lt;設定フォルダ&gt;/macros/</c> の <c>.js</c> ファイルにし、
/// 保存する時点で「パス + 内容の SHA-256」を信頼済みの一覧に登録する (これでアプリ内で記録したマクロを見分ける。ファイルの中の印では
/// 見分けない)。保存後にアプリの外で内容が変わると、通常のスクリプトと同じく信頼の確認を出す。
/// </summary>
public static class MacroStore
{
    /// <summary>マクロのフォルダ。</summary>
    public static string FolderOf(ScriptFolders folders) => Path.Combine(folders.Settings, "macros");

    /// <summary>
    /// マクロを保存して信頼済みにする。<paramref name="folder"/> が null なら標準のマクロのフォルダ。同じ名前があれば上書きする
    /// (<paramref name="overwrite"/>)。書き込めなければ <see cref="IOException"/>・<see cref="UnauthorizedAccessException"/>。
    /// </summary>
    public static string Save(ScriptFolders folders, TrustStore trust, string name, string script, string? folder = null, bool overwrite = true)
    {
        string target = folder ?? FolderOf(folders);
        Directory.CreateDirectory(target);
        string path = Path.Combine(target, ScriptLibrary.SafeFileName(name) + ".js");
        if (!overwrite && File.Exists(path))
        {
            throw new IOException(Msg.T($"同じ名前のマクロがあります: {Path.GetFileName(path)}", $"A macro with the same name exists: {Path.GetFileName(path)}"));
        }

        string content = SetName(script, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        trust.TrustSavedScript(path, content, TrustSource.RecordedMacro);
        return path;
    }

    /// <summary>メタデータの <c>@name</c> を保存する名前に合わせる。</summary>
    public static string SetName(string script, string name)
    {
        string[] lines = script.Split('\n');
        string clean = name.Replace('\r', ' ').Replace('\n', ' ');
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].TrimStart();
            if (t.StartsWith("// @name ", StringComparison.Ordinal) || t.StartsWith("// @name\t", StringComparison.Ordinal))
            {
                lines[i] = "// @name  " + clean;
                break;
            }
        }

        return string.Join('\n', lines);
    }
}
