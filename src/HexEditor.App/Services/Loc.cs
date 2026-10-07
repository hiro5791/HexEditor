using Microsoft.Windows.ApplicationModel.Resources;

namespace HexEditor.App.Services;

/// <summary>
/// コードから使う文字列のリソース (UI-42)。XAML の文字列は x:Uid で参照する。文字列を連結して文を組み立てず、
/// 数値を含む文は書式の文字列 ({0} など) をリソースに置く (00-overview 5.4)。
/// </summary>
public static class Loc
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string key)
    {
        string value = Loader.GetString(key);
        return string.IsNullOrEmpty(value) ? key : value;
    }

    public static string Format(string key, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), args);
}
