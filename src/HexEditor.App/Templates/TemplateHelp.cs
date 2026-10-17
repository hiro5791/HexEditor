using HexEditor.App.Services;

namespace HexEditor.App.Templates;

/// <summary>
/// テンプレートのエラーのコードのヘルプ (TPL-27 の仕様 7)。説明ページはリポジトリの <c>docs/help/template-errors.md</c> の、コードと
/// 同じ見出し (<c>### HXT1004</c>) の節。
/// </summary>
internal static class TemplateHelp
{
    public const string PagePath = "docs/help/template-errors.md";

    /// <summary>標準ライブラリのリファレンス (TPL-18 の仕様 4)。</summary>
    public static Uri LibraryUrl => new(AboutInfo.RepositoryUrl + "/blob/main/" + Core.Templates.Editing.TemplateLibraryReference.HelpPage);

    public static Uri UrlOf(string code) => new(AboutInfo.RepositoryUrl + "/blob/main/" + PagePath + "#" + code.ToLowerInvariant());
}
