using HexEditor.ManualBuilder;

// 利用者向けマニュアルの HTML を作る (09 の UI-59)。
//
// 使い方:
//   ManualBuilder build [--source <原稿のフォルダ>] [--out <出力先>]
//       原稿 (既定 docs/manual) から HTML を作って出力先 (既定 artifacts/manual) に書く。前の出力は消す。
//   ManualBuilder check [--source <原稿のフォルダ>]
//       作るだけで書き出さない (リンク・見出し・画像の検査)。
// どちらも、壊れたリンクなどの誤りがあれば一覧を出して終了コード 1 にする (書き出さない)。
// 例: dotnet run --project tools/ManualBuilder -c Release -- build --out artifacts/manual
if (args.Length == 0 || args[0] is not ("build" or "check"))
{
    Console.Error.WriteLine("usage: ManualBuilder build|check [--source <dir>] [--out <dir>]");
    return 2;
}

string? source = null;
string? output = null;
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--source" when i + 1 < args.Length:
            source = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            output = args[++i];
            break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 2;
    }
}

string? root = FindRepositoryRoot();
source ??= root is null ? null : Path.Combine(root, "docs", "manual");
if (source is null || !Directory.Exists(source))
{
    Console.Error.WriteLine("The manual sources were not found. Pass --source <dir>.");
    return 2;
}

ManualBuildResult result = SiteBuilder.Build(Path.GetFullPath(source));
foreach (ManualProblem problem in result.Problems)
{
    Console.Error.WriteLine("error: " + problem);
}

if (!result.Succeeded)
{
    Console.Error.WriteLine($"The manual has {result.Problems.Count} problem(s).");
    return 1;
}

int fallback = result.Pages.Count(p => p.IsFallback);
Console.WriteLine($"Manual: {result.Pages.Count} pages ({fallback} shown in the default language), {result.Files.Count} files.");
if (args[0] == "build")
{
    output ??= Path.Combine(root ?? Directory.GetCurrentDirectory(), "artifacts", "manual");
    SiteBuilder.Write(result, Path.GetFullPath(output));
    Console.WriteLine($"Written to {Path.GetFullPath(output)}");
}

return 0;

static string? FindRepositoryRoot()
{
    for (DirectoryInfo? dir = new(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "HexEditor.slnx")))
        {
            return dir.FullName;
        }
    }

    return null;
}
