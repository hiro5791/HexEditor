// ==HexEditorScript==
// @name         PE header summary
// @name:ja      PE ヘッダの要約
// @description  Applies the PE template and prints the main values to the output panel
// @description:ja  テンプレートを適用し、主要な値を出力パネルに出す
// @version      1.0.0
// @author       HexEditor
// @api          1.0
// ==/HexEditorScript==

const doc = app.activeDocument;
if (!doc) {
    throw new Error("Open a document first.");
}

// show: false keeps the template panel as it is; the result tree is still available.
const pe = await doc.applyTemplate("pe", { show: false });
const hex = (node) => "0x" + Number(node.value).toString(16).toUpperCase();

console.log("e_lfanew: " + hex(pe.get("header.e_lfanew")));
console.log("Machine: " + hex(pe.get("nt.FileHeader.Machine")));
console.log("Sections: " + pe.get("nt.FileHeader.NumberOfSections").value);
console.log("Characteristics: " + hex(pe.get("nt.FileHeader.Characteristics")));
