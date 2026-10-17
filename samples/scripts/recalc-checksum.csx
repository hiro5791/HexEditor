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

var doc = Doc ?? throw new InvalidOperationException("Open a document first.");
var range = (ByteRange)Script.Args["range"]!;
long target = (long)(double)Script.Args["target"]!;
string endian = (string)Script.Args["endian"]!;

var results = await Hash.ComputeAsync(doc, range, ["crc32"]);
uint crc = Convert.ToUInt32(results[0].Hex, 16);
doc.WriteInt(target, "u32", crc, endian);
Console.WriteLine($"CRC-32 {results[0].Hex.ToUpperInvariant()} written at {target} ({endian})");
