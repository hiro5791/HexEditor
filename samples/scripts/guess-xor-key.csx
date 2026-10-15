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
var doc = Doc ?? throw new InvalidOperationException("Open a document first.");
RangeArg? range = doc.Selection.IsEmpty ? null : new ByteRange(doc.Selection.Start, doc.Selection.Length);
long[] counts = await doc.HistogramAsync(range);
int plain = ((byte[])Script.Args["plain"]!)[0];

var ranked = counts
    .Select((count, value) => (value, count))
    .OrderByDescending(x => x.count)
    .Take(3)
    .ToList();

foreach (var (value, count) in ranked)
{
    Console.WriteLine($"key {value ^ plain:X2}: byte {value:X2} occurs {count} times");
}
Console.WriteLine($"Most likely key: {ranked[0].value ^ plain:X2}");
