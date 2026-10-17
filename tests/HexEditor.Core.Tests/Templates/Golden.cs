namespace HexEditor.Core.Tests.Templates;

/// <summary>
/// ゴールデンファイルのテスト (テストケースの文書の冒頭: <c>tests/fixtures/templates/&lt;テストケース ID&gt;/expected.json</c>)。
/// 期待値を作り直すときは HEXEDITOR_UPDATE_GOLDEN=1 を付けてテストを実行し、差分を確認してからコミットする。
/// </summary>
internal static class Golden
{
    public static string Folder(string id) =>
        Path.Combine(Path.GetDirectoryName(Engine.SourceTests.FindRepoFile("HexEditor.slnx"))!, "tests", "fixtures", "templates", id);

    public static void Check(string id, string actual, string fileName = "expected.json")
    {
        string path = Path.Combine(Folder(id), fileName);
        // 末尾は改行 1 つにそろえる (EditorConfig の insert_final_newline)
        actual = actual.ReplaceLineEndings("\n").TrimEnd('\n') + "\n";
        if (Environment.GetEnvironmentVariable("HEXEDITOR_UPDATE_GOLDEN") == "1" || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            if (Environment.GetEnvironmentVariable("HEXEDITOR_UPDATE_GOLDEN") != "1")
            {
                Assert.Fail($"{path} がなかったので作りました。内容を確認してください。");
            }

            return;
        }

        string expected = File.ReadAllText(path).ReplaceLineEndings("\n").TrimEnd('\n') + "\n";
        Assert.True(expected == actual, $"{path} と一致しません。\n--- 実際 ---\n{actual[..Math.Min(4000, actual.Length)]}");
    }
}
