// ==HexEditorScript==
// @name         Save the selection as a file
// @name:ja      選択範囲を別ファイルに保存
// @description  Writes the selected range to a new file
// @description:ja  選択範囲をファイルに書き出す
// @version      1.0.0
// @author       HexEditor
// @api          1.0
// ==/HexEditorScript==

const doc = app.activeDocument;
if (!doc) {
    throw new Error("Open a document first.");
}
if (doc.selection.isEmpty) {
    throw new Error("Select a range first.");
}

const path = await ui.saveFile({ suggestedName: `${doc.name}.part.bin` });
if (path) {
    // exportRange streams the data, so the selection can be larger than the memory.
    await doc.exportRange({ start: doc.selection.start, length: doc.selection.length }, path, { overwrite: true });
    console.log(`Saved ${doc.selection.length} bytes to ${path}`);
}
