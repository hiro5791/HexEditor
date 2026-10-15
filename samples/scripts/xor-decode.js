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
const doc = app.activeDocument;
if (!doc) {
    throw new Error("Open a document first.");
}

const useWhole = script.args.whole || doc.selection.isEmpty;
const range = useWhole ? { start: 0, length: doc.length } : { start: doc.selection.start, length: doc.selection.length };

await doc.transform(range, "xor", script.args.key);
console.log(`XOR ${useWhole ? "whole file" : "selection"}: ${range.length} bytes`);
