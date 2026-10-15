// ==HexEditorScript==
// @name         Patch many files
// @name:ja      複数ファイルへの一括パッチ
// @description  Replaces a byte pattern in every file of a folder (an example for `hexed run`)
// @description:ja  フォルダ内のファイルにパッチを当てる (コマンドラインの run の例)
// @version      1.0.0
// @author       HexEditor
// @api          1.0
// @param        find hex "DE AD BE EF" Pattern to find (hex bytes)
// @param        replace hex "00 00 00 00" Replacement (hex bytes)
// ==/HexEditorScript==

// Headless: hexed run batch-patch.js --trust --arg folder=C:\files --arg "find=DE AD" --arg "replace=BE EF"
const folder = await ui.pickFolder();
if (folder) {
    let patched = 0;
    for (const path of fs.list(folder)) {
        if (fs.stat(path).isFolder) {
            continue;
        }
        const target = await app.open(path, { background: true });
        const count = await target.replaceAll(script.args.find, script.args.replace);
        if (count > 0) {
            await target.save();
            patched++;
        }
        console.log(`${path}: ${count} replacement(s)`);
        await target.close({ discard: true });
    }
    console.log(`${patched} file(s) patched`);
}
