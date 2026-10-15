using HexEditor.App.Services;

namespace HexEditor.App.Templates;

/// <summary>
/// テンプレートのエラーのコードのヘルプ (TPL-27 の仕様 7)。説明ページはリポジトリの <c>docs/help/template-errors.md</c> の、コードと
/// 同じ見出し (<c>### HXT1004</c>) の節。
/// </summary>
internal static class TemplateHelp
{
    public const string PagePath = "docs/help/template-errors.md";

    public static Uri UrlOf(string code) => new(AboutInfo.RepositoryUrl + "/blob/main/" + PagePath + "#" + code.ToLowerInvariant());
}
