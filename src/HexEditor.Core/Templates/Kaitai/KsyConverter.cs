namespace HexEditor.Core.Templates.Kaitai;

/// <summary>Kaitai Struct (.ksy) を HXT に変換する (TPL-33)。</summary>
internal static partial class KsyConverter
{
    public static string? Convert(byte[] bytes, string path, DiagnosticBag diags, out string? rootType)
    {
        rootType = null;
        diags.Error(TemplateMessages.UnsupportedKaitai, new Syntax.TemplateSourceLocation(path, 1, 1), "ksy");
        return null;
    }
}
