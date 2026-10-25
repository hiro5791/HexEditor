using System.Text.RegularExpressions;

namespace HexEditor.Core.Tests.Engine;

/// <summary>
/// App の描画のコードの検査 (フェーズ 6 の監査)。GeometryGroup の子に GeometryGroup (模様の形 <c>ChartSupport.Pattern</c> の戻り値など) を
/// 入れると、数が多いときに WinUI の描画が stowed exception (0xC000027B) でプロセスごと落ちる。GeometryGroup の子は線・矩形・楕円・
/// PathGeometry だけにし、模様は <c>ChartSupport.AddPattern</c> で平らに足す。
/// </summary>
public sealed partial class AppDrawingSourceTests
{
    private static string AppDirectory => Path.GetDirectoryName(SourceTests.FindRepoFile("src/HexEditor.App/HexEditor.App.csproj"))!;

    private static IEnumerable<string> AppFiles(string pattern) =>
        Directory.EnumerateFiles(AppDirectory, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    [GeneratedRegex(@"(?:var|GeometryGroup\??)\s+(\w+)\s*=\s*new GeometryGroup\(\)")]
    private static partial Regex GroupDeclaration();

    [GeneratedRegex(@"(\w+)\.Children\.Add\(\s*(.+)")]
    private static partial Regex ChildAdd();

    [GeneratedRegex(@"^new (Line|Rectangle|Ellipse|Path)Geometry\b")]
    private static partial Regex PrimitiveGeometry();

    [Fact]
    public void Geometry_groups_are_never_nested()
    {
        var problems = new List<string>();
        foreach (string file in AppFiles("*.cs"))
        {
            string[] lines = File.ReadAllLines(file);
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                foreach (Match m in GroupDeclaration().Matches(line))
                {
                    groups.Add(m.Groups[1].Value);
                }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Contains("Children.Add(ChartSupport.Pattern(", StringComparison.Ordinal)
                    || line.Contains("Children.Add(new GeometryGroup", StringComparison.Ordinal))
                {
                    problems.Add($"{Path.GetFileName(file)}:{i + 1}: {line}");
                    continue;
                }

                if (ChildAdd().Match(line) is { Success: true } add && groups.Contains(add.Groups[1].Value)
                    && !PrimitiveGeometry().IsMatch(add.Groups[2].Value))
                {
                    problems.Add($"{Path.GetFileName(file)}:{i + 1}: {line}");
                }
            }
        }

        foreach (string file in AppFiles("*.xaml"))
        {
            string text = File.ReadAllText(file);
            if (Regex.IsMatch(text, @"<GeometryGroup\b(?:(?!</GeometryGroup>).)*<GeometryGroup\b", RegexOptions.Singleline))
            {
                problems.Add($"{Path.GetFileName(file)}: nested <GeometryGroup>");
            }
        }

        Assert.True(problems.Count == 0, "GeometryGroup の入れ子 (ChartSupport.AddPattern で平らに足す):\n" + string.Join("\n", problems));
    }
}
