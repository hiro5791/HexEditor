// ==HexEditorScript==
// @name         Hash list of a folder
// @name:ja      フォルダのハッシュ一覧
// @description  Calculates the hash of every file in a folder and lists them
// @description:ja  フォルダ内のファイルのハッシュを計算して一覧にする
// @version      1.0.0
// @author       HexEditor
// @api          1.0
// @param        algorithm choice "sha256|sha1|md5|crc32" Hash algorithm
// ==/HexEditorScript==

// hash.computeFile reads the files without opening them in the editor.
const folder = await ui.pickFolder();
if (!folder) {
    app.stop();
} else {
    const rows = [];
    for (const path of fs.list(folder)) {
        if (fs.stat(path).isFolder) {
            continue;
        }
        const [result] = await hash.computeFile(path, [script.args.algorithm]);
        rows.push({ name: path.split(/[\\/]/).pop(), hash: result.hex });
        console.log(`${result.hex}  ${rows[rows.length - 1].name}`);
    }
    ui.showResults(`Hashes (${script.args.algorithm})`, rows, ["name", "hash"]);
}
