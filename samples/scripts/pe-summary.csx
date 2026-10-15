// ==HexEditorScript==
// @name         PE header summary
// @name:ja      PE ヘッダの要約
// @description  Applies the PE template and prints the main values to the output panel
// @description:ja  テンプレートを適用し、主要な値を出力パネルに出す
// @version      1.0.0
// @author       HexEditor
// @api          1.0
// ==/HexEditorScript==

var doc = Doc ?? throw new InvalidOperationException("Open a document first.");

// Show = false keeps the template panel as it is; the result tree is still available.
var pe = await doc.ApplyTemplateAsync("pe", new ApplyTemplateOptions { Show = false });
string Hex(string path) => "0x" + Convert.ToInt64(pe.Get(path)!.Value).ToString("X");

Console.WriteLine("e_lfanew: " + Hex("header.e_lfanew"));
Console.WriteLine("Machine: " + Hex("nt.FileHeader.Machine"));
Console.WriteLine("Sections: " + pe.Get("nt.FileHeader.NumberOfSections")!.Value);
Console.WriteLine("Characteristics: " + Hex("nt.FileHeader.Characteristics"));
