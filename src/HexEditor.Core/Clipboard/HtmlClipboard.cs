using System.Globalization;
using System.Text;

namespace HexEditor.Core.Clipboard;

/// <summary>
/// クリップボードの HTML 形式 (<c>CF_HTML</c>、形式名 "HTML Format") を作る (EDIT-25 の HTML)。ヘッダの位置は UTF-8 のバイト数で数える。
/// </summary>
public static class HtmlClipboard
{
    private const string HeaderTemplate = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
    private const string Prefix = "<html><body>\r\n<!--StartFragment-->";
    private const string Suffix = "<!--EndFragment-->\r\n</body></html>";

    /// <summary><paramref name="fragment"/> (HTML の断片) を CF_HTML の文字列にする。</summary>
    public static string Wrap(string fragment)
    {
        int headerLength = string.Format(CultureInfo.InvariantCulture, HeaderTemplate, 0, 0, 0, 0).Length;
        int startHtml = headerLength;
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(Prefix);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(Suffix);
        return string.Format(CultureInfo.InvariantCulture, HeaderTemplate, startHtml, endHtml, startFragment, endFragment)
            + Prefix + fragment + Suffix;
    }

    /// <summary>CF_HTML のヘッダの値を読む (テスト・確認用)。</summary>
    public static IReadOnlyDictionary<string, string> ReadHeader(string cfHtml)
    {
        var values = new Dictionary<string, string>();
        foreach (string line in cfHtml.Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (line.StartsWith('<') || colon <= 0)
            {
                break;
            }

            values[line[..colon]] = line[(colon + 1)..];
        }

        return values;
    }
}
