# Monaco Editor (同梱)

スクリプトエディタ (08 の AUTO-20) とテンプレートエディタ (07 の TPL-31) のコードエディタの部品。配布物に同梱し、インターネットから読み込まない。

| 項目 | 内容 |
| --- | --- |
| パッケージ | npm の `monaco-editor` 0.57.0 (公開 2026-09-24) |
| 入手元 | `https://registry.npmjs.org/monaco-editor/-/monaco-editor-0.57.0.tgz` |
| 確認の値 (dist.integrity) | `sha512-5BkI9KGoqrNvBGUe15/QlZq3OooZ8WLg1AxTpaqHRCP3HNpzPPZKE2EDz8M7c+VRmCeUw1Brp4cx/PWm3kI/5A==` |
| shasum (SHA-1) | `9889fb51ddc27fe90e6da0e96c237c852adc4654` |
| ライセンス | MIT (`LICENSE.txt`)。Monaco が含む第三者の部品は `ThirdPartyNotices.txt` |

## フォルダ

- `vs/`: パッケージの `min/vs` のうち使う部分だけ (変更しない)。`build/monaco/trim-monaco.py` で選ぶ。
  - 残す: ローダー、エディタ本体、TypeScript の言語サービス (JavaScript の補完・型のチェック) と worker、基本言語の登録と
    JavaScript・TypeScript・C# の字句解析、Monaco の表示言語 (`nls/lang/*.js`)。
  - 除く: ほかの基本言語、CSS・HTML・JSON の言語サービスの worker の本体、`language/` (AMD の直接読み込み用の重複)、`*.d.ts`。
- `host/`: WebView2 の中のページ (このリポジトリのコード。`index.html`・`host.js`・`host.css`・`script-env.d.ts`)。
  `hexeditor.d.ts` / `hexeditor.ja.d.ts` はビルドで `docs/api/` から写す。

## 更新の手順

1. registry の `dist.integrity` と照合してから tarball を展開する (スクリプトは実行しない)。
2. `python -I build/monaco/trim-monaco.py <展開先>/package/min/vs src/HexEditor.App/Assets/Monaco/vs` (古い `vs/` は先に消す)。
3. `LICENSE` と `ThirdPartyNotices.txt` を写し、この表と `THIRD-PARTY-NOTICES.txt` の版を直す。
4. UI テスト `CodeEditorMonacoTests` を通す。
