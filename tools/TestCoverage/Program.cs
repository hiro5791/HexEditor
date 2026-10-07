using HexEditor.ManualTests;

// テストの網羅の一覧 (test-coverage.md) を作る (テスト方針 6.2、10 章の 4)。
//
// 使い方: TestCoverage [--results <trx のフォルダ>] [--output <出力先>] [--enforce-phase <N>]
//   --results       テストの結果 (*.trx) のフォルダ。失敗したテストケースの一覧に使う。
//   --output        出力先 (既定 docs/test/test-coverage.md)。
//   --enforce-phase フェーズ N までに、テストケースのない受け入れ基準・自動テストのない「自動」のテストケース・
//                   失敗したテストケースがあれば終了コード 1 にする (CI で使う)。
string? results = null;
string? output = null;
int? enforce = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--results":
            results = args[++i];
            break;
        case "--output":
            output = args[++i];
            break;
        case "--enforce-phase":
            enforce = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            break;
        default:
            Console.Error.WriteLine($"不明な引数: {args[i]}");
            return 2;
    }
}

string root = FindRepositoryRoot();
Coverage coverage = Coverage.Load(root, results);
output ??= Path.Combine(root, "docs", "test", "test-coverage.md");
File.WriteAllText(output, coverage.ToMarkdown("作り方: `dotnet run --project tools/TestCoverage` (CI はテストの結果を `--results` で渡す)。"),
    new System.Text.UTF8Encoding(false));
Console.WriteLine($"書きました: {output}");

if (enforce is int phase)
{
    var problems = coverage.UncoveredCriteria.Where(c => c.Feature.Phase <= phase).Select(c => $"テストケースのない受け入れ基準: {c.Feature.Id} の {c.Number}")
        .Concat(coverage.AutomatedWithoutTest.Where(c => c.Phase >= 0 && c.Phase <= phase).Select(c => $"自動テストのない「自動」のテストケース: {c.Id}"))
        .Concat(coverage.Cases.Where(c => c.Phase >= 0 && c.Phase <= phase && coverage.Failed.Contains(c.Id)).Select(c => $"失敗したテストケース: {c.Id}"))
        .ToList();
    foreach (string problem in problems)
    {
        Console.Error.WriteLine(problem);
    }

    return problems.Count == 0 ? 0 : 1;
}

return 0;

static string FindRepositoryRoot()
{
    for (DirectoryInfo? dir = new(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "HexEditor.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new InvalidOperationException("リポジトリのフォルダ (HexEditor.slnx のあるフォルダ) が見つかりません。");
}
