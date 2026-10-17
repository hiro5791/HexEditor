// ==HexEditorScript==
// @name         Show an array of structures
// @name:ja      構造体の配列の表示
// @description  Reads fixed-size records and shows them as a table
// @description:ja  固定長レコードを読んで表にする
// @version      1.0.0
// @author       HexEditor
// @api          1.0
// @param        start offset "0" Offset of the first record
// @param        count number 16 Number of records
// ==/HexEditorScript==

// Record layout (8 bytes, little endian): u16 id, u16 flags, u32 value.
const RECORD_SIZE = 8;
const doc = app.activeDocument;
if (!doc) {
    throw new Error("Open a document first.");
}

const rows = [];
for (let i = 0; i < script.args.count; i++) {
    const offset = script.args.start + i * RECORD_SIZE;
    if (offset + RECORD_SIZE > doc.length) {
        break;
    }
    rows.push({
        start: offset,
        length: RECORD_SIZE,
        id: doc.readInt(offset, "u16", "le"),
        flags: "0x" + doc.readInt(offset + 2, "u16", "le").toString(16).toUpperCase().padStart(4, "0"),
        value: doc.readInt(offset + 4, "u32", "le"),
    });
    console.log(`#${i}: id=${rows[i].id} flags=${rows[i].flags} value=${rows[i].value}`);
}
// Rows with start and length jump to the offset when clicked.
ui.showResults("Records", rows, ["start", "id", "flags", "value"]);
