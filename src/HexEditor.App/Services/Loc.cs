using Microsoft.Windows.ApplicationModel.Resources;

namespace HexEditor.App.Services;

/// <summary>
/// コードから使う文字列のリソース (UI-42)。XAML の文字列は x:Uid で参照する。文字列を連結して文を組み立てず、
/// 数値を含む文は書式の文字列 ({0} など) をリソースに置く (00-overview 5.4)。
/// </summary>
public static class Loc
{
    private static readonly ResourceLoader Loader = new();

    /// <summary>
    /// 文字列を返す。翻訳がなければ英語 (MRT のフォールバック)。キー自体がない場合は <c>[キー名]</c> を返してログに記録する
    /// (UI-42 の「エラー」)。開発版では例外にして、すぐに気付けるようにする。
    /// </summary>
    public static string Get(string key)
    {
        string value = Loader.GetString(key);
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        AppLog.Warning($"Missing resource key: {key}");
#if DEBUG
        throw new KeyNotFoundException($"リソースのキー {key} がありません。");
#else
        return $"[{key}]";
#endif
    }

    /// <summary>キーがなければ null (実行時に登録されたコマンドの別名など、ないことがある文字列)。</summary>
    public static string? TryGet(string key)
    {
        string value = Loader.GetString(key);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static Dictionary<string, string>? _english;

    /// <summary>
    /// 英語の文字列 (コマンドの英語名での検索など。UI-16、UI-17 の仕様 4)。なければ null。表示言語を固定した後の MRT は
    /// 別の言語の値を返さないため、英語の .resw をアセンブリに埋め込んで読む。キーの形は Loc.Get と同じ (<c>名前/属性</c>)。
    /// </summary>
    public static string? English(string key)
    {
        if (_english is null)
        {
            var english = new Dictionary<string, string>(StringComparer.Ordinal);
            using Stream? stream = typeof(Loc).Assembly.GetManifestResourceStream("HexEditor.Strings.en.resw");
            if (stream is not null)
            {
                foreach (System.Xml.Linq.XElement data in System.Xml.Linq.XDocument.Load(stream).Root!.Elements("data"))
                {
                    english[data.Attribute("name")!.Value.Replace('.', '/')] = data.Element("value")?.Value ?? string.Empty;
                }
            }

            _english = english;
        }

        return _english.GetValueOrDefault(key.Replace('.', '/'));
    }

    public static string Format(string key, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), args);
}
