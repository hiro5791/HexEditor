// ==HexEditorScript==
// @name         Export strings to CSV
// @name:ja      文字列を CSV に書き出す
// @description  Extracts the strings of the document and saves them as a CSV file
// @description:ja  文字列を抽出して CSV に保存する
// @version      1.0.0
// @author       HexEditor
// @api          1.0
// @param        minLength number 4 Minimum length (characters)
// ==/HexEditorScript==

const doc = app.activeDocument;
if (!doc) {
    throw new Error("Open a document first.");
}

const found = await doc.strings({ minLength: script.args.minLength });
if (found.length === 0) {
    ui.message("No strings were found.");
    app.stop();
}

// The file the user picks can be written without any permission.
const path = await ui.saveFile({ suggestedName: `${doc.name}.strings.csv`, filters: ["*.csv"] });
if (path) {
    const quote = (text) => `"${text.replaceAll('"', '""')}"`;
    const lines = ["offset,length,encoding,text"];
    for (const s of found) {
        lines.push(`0x${s.start.toString(16).toUpperCase()},${s.length},${s.encoding},${quote(s.text)}`);
    }
    fs.writeText(path, lines.join("\r\n") + "\r\n");
    console.log(`Saved ${found.length} strings to ${path}`);
}
