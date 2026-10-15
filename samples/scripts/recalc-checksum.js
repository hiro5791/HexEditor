// ==HexEditorScript==
// @name         Recalculate checksum
// @name:ja      チェックサムの再計算
// @description  Calculates the CRC-32 of a range and writes it at a given position (for fixing firmware images)
// @description:ja  指定範囲の CRC-32 を計算し、指定位置に書き込む (ファームウェアの修正用)
// @version      1.0.0
// @author       HexEditor
// @api          1.0
// @param        range range "0:0x100" Range to protect (start:length)
// @param        target offset "0x100" Where to write the 4-byte checksum
// @param        endian choice "le|be" Byte order of the checksum
// ==/HexEditorScript==

const doc = app.activeDocument;
if (!doc) {
    throw new Error("Open a document first.");
}

const [result] = await hash.compute(doc, script.args.range, ["crc32"]);
const crc = parseInt(result.hex, 16);
doc.writeInt(script.args.target, "u32", crc, script.args.endian);
console.log(`CRC-32 ${result.hex.toUpperCase()} written at ${script.args.target} (${script.args.endian})`);
