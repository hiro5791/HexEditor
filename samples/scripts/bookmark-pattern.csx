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

var doc = Doc ?? throw new InvalidOperationException("Open a document first.");

// FindAllAsync searches in the background and returns every match (at most one million).
var results = await doc.FindAllAsync((byte[])Script.Args["pattern"]!);
long added = doc.Bookmarks.AddFromResults(results, "match {index}");
Console.WriteLine($"Added {added} bookmarks" + (results.Truncated ? " (the search stopped at the maximum count)" : ""));
