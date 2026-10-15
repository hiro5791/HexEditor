// ==HexEditorScript==
// @name         Hash list of a folder
// @name:ja      フォルダのハッシュ一覧
// @description  Calculates the hash of every file in a folder and lists them
// @description:ja  フォルダ内のファイルのハッシュを計算して一覧にする
// @version      1.0.0
// @author       HexEditor
// @api          1.0
// @param        algorithm choice "sha256|sha1|md5|crc32" Hash algorithm
// ==/HexEditorScript==

// ComputeFileAsync reads the files without opening them in the editor.
string algorithm = (string)Script.Args["algorithm"]!;
string? folder = await Ui.PickFolderAsync();
if (folder is null)
{
    App.Stop();
}
else
{
    var rows = new List<IReadOnlyDictionary<string, object?>>();
    foreach (string path in Directory.GetFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
    {
        var results = await Hash.ComputeFileAsync(path, [algorithm]);
        string name = Path.GetFileName(path);
        rows.Add(new Dictionary<string, object?> { ["name"] = name, ["hash"] = results[0].Hex });
        Console.WriteLine($"{results[0].Hex}  {name}");
    }
    Ui.ShowResults($"Hashes ({algorithm})", rows, ["name", "hash"]);
}
