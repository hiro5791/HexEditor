using HexEditor.TestData;

// テストデータ生成ツール (テスト方針 7.1)。
//   TestDataGen list            ID と説明の一覧
//   TestDataGen all <出力先>    すべて生成する
//   TestDataGen <ID> <出力先>   1 つだけ生成する
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("使い方: TestDataGen list | all <出力先> | <ID> <出力先>");
    return 0;
}

if (args[0] == "list")
{
    foreach (TestDataItem item in TestDataCatalog.All)
    {
        Console.WriteLine($"{item.Id,-16} {item.Length,20:N0}  {item.Description}");
    }

    return 0;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("出力先のフォルダを指定してください。");
    return 2;
}

IEnumerable<string> ids = args[0] == "all" ? TestDataCatalog.All.Select(i => i.Id) : [args[0]];
try
{
    foreach (string id in ids)
    {
        Console.WriteLine(TestDataCatalog.Generate(id, args[1]));
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

return 0;
