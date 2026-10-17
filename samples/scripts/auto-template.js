// ==HexEditorScript==
// @name         Apply a template when a file is opened
// @name:ja      開いたときにテンプレートを自動適用
// @description  Hook example: detects the template by the magic bytes and applies it
// @description:ja  フック (AUTO-25) の例。マジックで判定してテンプレートを適用する
// @version      1.0.0
// @author       HexEditor
// @api          1.0
// @on           open
// @param        once checkbox false Only handle the documents that are open now, then exit
// ==/HexEditorScript==

// Registered as an "open" hook, the script runs once per opened file and receives the
// document in script.args.event. Run from the script list, it handles the open documents
// and then stays resident to handle files opened later (stop it from the process center).
async function apply(document) {
    const found = templates.detect(document);
    if (found.length === 0) {
        console.log(`${document.name}: no template matches`);
        return;
    }
    await document.applyTemplate(found[0].name, { show: false });
    console.log(`${document.name}: applied ${found[0].name}`);
}

const event = script.args.event;
if (event && event.document) {
    await apply(event.document);
} else {
    for (const document of app.documents) {
        await apply(document);
    }
    if (!script.args.once) {
        app.on("documentOpened", (e) => apply(e.document));
    }
}
