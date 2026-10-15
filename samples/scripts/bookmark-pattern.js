// ==HexEditorScript==
// @name         Bookmark a pattern
// @name:ja      パターンにブックマーク
// @description  Adds a bookmark to every match of a hex pattern
// @description:ja  Hex パターンの全一致にブックマークを付ける
// @version      1.0.0
// @author       HexEditor
// @api          1.0
// @param        pattern hex "DE AD BE EF" Pattern (hex bytes)
// ==/HexEditorScript==

const doc = app.activeDocument;
if (!doc) {
    throw new Error("Open a document first.");
}

// findAll searches in the background and returns every match (at most one million).
const results = await doc.findAll(script.args.pattern);
const added = doc.bookmarks.addFromResults(results, "match {index}");
console.log(`Added ${added} bookmarks` + (results.truncated ? " (the search stopped at the maximum count)" : ""));
