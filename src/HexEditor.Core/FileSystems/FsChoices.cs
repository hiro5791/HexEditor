using System.Text;

namespace HexEditor.Core.FileSystems;

/// <summary>
/// ブラウザで切り替えられる表示の選択 1 つ (ISO 9660 の名前の種類・セッション、ブリッジ形式の UDF / ISO 9660。FOR-14 の仕様 2・3)。
/// <see cref="Options"/> は選択肢の識別子で、表示は <c>FsChoice_&lt;Key&gt;_&lt;Option&gt;</c> の訳 (なければそのまま)。
/// </summary>
public sealed record FsChoice(string Key, IReadOnlyList<string> Options, int Selected);

/// <summary>表示の選択を持つファイルシステム (ブラウザが選択欄を出す)。</summary>
public interface IFsChoices
{
    IReadOnlyList<FsChoice> Choices { get; }

    /// <summary>
    /// 選ぶ。戻り値は開き直す種類 (ブリッジ形式で UDF と ISO 9660 を切り替える場合)。null ならこのファイルシステムのままで、ブラウザは一覧を
    /// 読み直す。
    /// </summary>
    FileSystemKind? Select(string key, int index);
}

/// <summary>表示用の文字列の補助。</summary>
public static class FsText
{
    /// <summary>POSIX のモードの文字列 (<c>-rw-r--r--</c>)。</summary>
    public static string PosixMode(int mode)
    {
        char type = (mode >> 12 & 0xF) switch
        {
            0x1 => 'p', 0x2 => 'c', 0x4 => 'd', 0x6 => 'b', 0x8 => '-', 0xA => 'l', 0xC => 's', _ => '?',
        };
        var s = new StringBuilder(10).Append(type);
        const string Rwx = "rwxrwxrwx";
        for (int i = 0; i < 9; i++)
        {
            bool set = (mode & (1 << (8 - i))) != 0;
            char c = set ? Rwx[i] : '-';
            if (i == 2 && (mode & 0x800) != 0)
            {
                c = set ? 's' : 'S';
            }

            if (i == 5 && (mode & 0x400) != 0)
            {
                c = set ? 's' : 'S';
            }

            if (i == 8 && (mode & 0x200) != 0)
            {
                c = set ? 't' : 'T';
            }

            s.Append(c);
        }

        return s.ToString();
    }

    /// <summary>パスをつなぐ (区切りは <c>\</c>)。</summary>
    public static string Combine(string parent, string name) => (parent.EndsWith('\\') ? parent : parent + "\\") + name;
}
