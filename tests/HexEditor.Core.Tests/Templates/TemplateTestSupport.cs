using System.Globalization;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;

namespace HexEditor.Core.Tests.Templates;

/// <summary>テンプレートのテストの補助。</summary>
internal static class TemplateTestSupport
{
    public const string TC = "TC";

    public static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

    public static TemplateProgram Load(string template, string fileName = "test.hxt", string? directory = null, TemplateLoadOptions? options = null) =>
        TemplateLoader.LoadText(template, fileName, directory, options);

    /// <summary>テンプレートをデータに適用し、評価が終わるまで待つ。</summary>
    public static TemplateResult Run(string template, byte[] data, TemplateApplyOptions? options = null, TimeSpan? timeout = null)
    {
        TemplateProgram program = Load(template);
        return Run(program, TemplateDataSource.FromBytes(data), options, timeout);
    }

    public static TemplateResult Run(TemplateProgram program, TemplateDataSource data, TemplateApplyOptions? options = null, TimeSpan? timeout = null)
    {
        TemplateResult result = TemplateEngine.Apply(program, data, options);
        Assert.True(result.Completion.Wait(timeout ?? TimeSpan.FromSeconds(60)), "評価が終わりません。");
        return result;
    }

    /// <summary>エラーのないことを確かめる。</summary>
    public static TemplateResult RunOk(string template, byte[] data, TemplateApplyOptions? options = null)
    {
        TemplateResult r = Run(template, data, options);
        Assert.True(r.State == TemplateRunState.Completed,
            $"state {r.State}: {string.Join("; ", r.Diagnostics.Select(d => d.ToString()))}");
        Assert.DoesNotContain(r.Diagnostics, d => d.Severity == TemplateSeverity.Error);
        return r;
    }

    public static TemplateNode Node(TemplateResult r, string path) =>
        r.FindByPath(path) ?? throw new Xunit.Sdk.XunitException($"パス {path} がありません。ツリー: {Dump(r.Root)}");

    public static object? Value(TemplateResult r, string path) => Node(r, path).Value;

    public static long Long(TemplateResult r, string path) => Convert.ToInt64(Node(r, path).Value, CultureInfo.InvariantCulture);

    public static string Display(TemplateResult r, string path) => Node(r, path).GetDisplay(TemplateDisplayContext.Invariant);

    public static string Dump(TemplateNode node, int depth = 0, int max = 3)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(new string(' ', depth * 2)).Append(node.Name).Append(" : ").Append(node.TypeName).Append(" @").Append(node.Offset)
            .Append('+').Append(node.Size).Append('\n');
        if (depth < max && node.Children0 is not null)
        {
            int n = 0;
            foreach (TemplateNode c in node.Children)
            {
                if (++n > 20)
                {
                    break;
                }

                sb.Append(Dump(c, depth + 1, max));
            }
        }

        return sb.ToString();
    }
}
