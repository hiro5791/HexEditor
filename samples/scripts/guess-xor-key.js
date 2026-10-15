// ==HexEditorScript==
// @name         Guess repeating key
// @name:ja      繰り返しキーの推定
// @description  Guesses a single-byte XOR key by frequency analysis
// @description:ja  単一バイト XOR のキーを頻度分析で推定する
// @version      1.0.0
// @author       HexEditor
// @api          1.0
// @param        plain hex "00" Most frequent byte of the plain data (00 for binaries, 20 for text)
// ==/HexEditorScript==

// Counts every byte value once (histogram), then assumes the most frequent byte of the
// encoded data was the most frequent byte of the plain data.
const doc = app.activeDocument;
if (!doc) {
    throw new Error("Open a document first.");
}

const range = doc.selection.isEmpty ? null : { start: doc.selection.start, length: doc.selection.length };
const counts = await doc.histogram(range);
const plain = script.args.plain[0];

const ranked = counts
    .map((count, value) => ({ value, count }))
    .sort((a, b) => b.count - a.count)
    .slice(0, 3);

for (const { value, count } of ranked) {
    const key = value ^ plain;
    console.log(`key ${key.toString(16).toUpperCase().padStart(2, "0")}: byte ${value.toString(16).toUpperCase().padStart(2, "0")} occurs ${count} times`);
}
console.log(`Most likely key: ${(ranked[0].value ^ plain).toString(16).toUpperCase().padStart(2, "0")}`);
