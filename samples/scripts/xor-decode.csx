// ==HexEditorScript==
// @name         XOR decode
// @name:ja      XOR デコード
// @description  XORs the selection (or the whole file) with the given key
// @description:ja  選択範囲 (または全体) を指定したキーで XOR する
// @version      1.0.0
// @author       HexEditor
// @api          1.0
// @param        key hex "5A" Key (hex bytes, repeated)
// @param        whole checkbox false Apply to the whole file
// ==/HexEditorScript==

// The bulk API runs on the data in chunks, so this works on files of any size
// and takes about as long as the Data > Data operation menu command.
var doc = Doc ?? throw new InvalidOperationException("Open a document first.");
bool useWhole = (bool)Script.Args["whole"]! || doc.Selection.IsEmpty;
var range = useWhole ? new ByteRange(0, doc.Length) : new ByteRange(doc.Selection.Start, doc.Selection.Length);
string key = Convert.ToHexString((byte[])Script.Args["key"]!);

await doc.TransformAsync(range, "xor", key);
Console.WriteLine($"XOR {(useWhole ? "whole file" : "selection")}: {range.Length} bytes");
