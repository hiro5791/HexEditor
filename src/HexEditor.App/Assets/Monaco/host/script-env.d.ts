// スクリプトの実行環境のうち、hexeditor.d.ts (AUTO-04) にないもの (スクリプトエディタの補完・型のチェック用。AUTO-20 の仕様 3)。
// DOM の型 (window・document など) はスクリプトにないので読み込まない。

/** Output to the script output panel (AUTO-24). */
declare const console: {
    log(...data: unknown[]): void;
    info(...data: unknown[]): void;
    warn(...data: unknown[]): void;
    error(...data: unknown[]): void;
    debug(...data: unknown[]): void;
};

/** Built-in module: the application object. */
declare module "hexeditor:app" {
    const app: HexEditor.App;
    export { app };
    export default app;
}

/** Built-in module: text encoding conversion. */
declare module "hexeditor:text" {
    const text: HexEditor.Text;
    export default text;
}

/** Built-in module: one function per hash algorithm (`crc32(data)` and so on) and `algorithms`. */
declare module "hexeditor:hash" {
    const hash: { readonly algorithms: readonly unknown[]; readonly [algorithm: string]: unknown };
    export = hash;
}
