// ==HexEditorScript==
// @name         High-entropy regions
// @name:ja      高エントロピー領域の一覧
// @description  Calculates the entropy of every block and lists the ranges above a threshold
// @description:ja  ブロックごとのエントロピーを計算し、しきい値を超える範囲を結果一覧に出す
// @version      1.0.0
// @author       HexEditor
// @api          1.0
// @param        blockSize number 4096 Block size (bytes)
// @param        threshold text 7 Entropy threshold (bits per byte, 0-8; decimals allowed)
// ==/HexEditorScript==

const doc = app.activeDocument;
if (!doc) {
    throw new Error("Open a document first.");
}

const blockSize = script.args.blockSize;
const threshold = Number(script.args.threshold);
const entropies = await doc.entropy(null, { blockSize });

// Merge neighbouring blocks above the threshold into one range.
const rows = [];
for (let i = 0; i < entropies.length; i++) {
    if (entropies[i] < threshold) {
        continue;
    }
    const start = i * blockSize;
    const end = Math.min(doc.length, start + blockSize);
    const last = rows[rows.length - 1];
    if (last && last.start + last.length === start) {
        last.length = end - last.start;
        last.entropy = Math.max(last.entropy, entropies[i]);
    } else {
        rows.push({ start, length: end - start, entropy: entropies[i] });
    }
}

for (const row of rows) {
    row.entropy = Math.round(row.entropy * 100) / 100;
    console.log(`0x${row.start.toString(16).toUpperCase()} +${row.length} entropy ${row.entropy}`);
}
console.log(`${rows.length} region(s) with entropy >= ${threshold}`);
ui.showResults("High-entropy regions", rows, ["start", "length", "entropy"]);
