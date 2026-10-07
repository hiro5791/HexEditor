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

    public static string Format(string key, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), args);
}
