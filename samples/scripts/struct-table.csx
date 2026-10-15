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
const int RecordSize = 8;
var doc = Doc ?? throw new InvalidOperationException("Open a document first.");
long start = (long)(double)Script.Args["start"]!;
int count = (int)(double)Script.Args["count"]!;

var rows = new List<IReadOnlyDictionary<string, object?>>();
for (int i = 0; i < count; i++)
{
    long offset = start + (long)i * RecordSize;
    if (offset + RecordSize > doc.Length)
    {
        break;
    }

    long id = (long)doc.ReadInt(offset, "u16", "le");
    long flags = (long)doc.ReadInt(offset + 2, "u16", "le");
    long value = (long)doc.ReadInt(offset + 4, "u32", "le");
    rows.Add(new Dictionary<string, object?> { ["start"] = offset, ["length"] = (long)RecordSize, ["id"] = id, ["flags"] = $"0x{flags:X4}", ["value"] = value });
    Console.WriteLine($"#{i}: id={id} flags=0x{flags:X4} value={value}");
}
// Rows with start and length jump to the offset when clicked.
Ui.ShowResults("Records", rows, ["start", "id", "flags", "value"]);
