# 標準テンプレート (TPL-34) のサンプルファイルの出所

このフォルダの `*.expected.json` は、標準テンプレート (`src/HexEditor.Core/Templates/Bundled/templates/*.hxt`) を、同じ名前のサンプルファイルに
適用し、すべてを評価して JSON (TPL-25 の形式、表示は不変の地域設定) に書き出した結果である。

## サンプルファイルの出所

**すべてのサンプルは `tools/HexEditor.TestData/StandardTemplateSamples.cs` のコードで組み立てた自作のバイト列である。**
外部からダウンロードしたファイルも、第三者のファイルも、パブリックドメインのファイルも使っていない (権利の問題がない)。
リポジトリにはサンプルのファイルそのものは置かない。テストはそのコードから同じバイト列を作る (同じ入力からは常に同じ内容になる)。

- 形式ごとのサンプルは、名前 (テンプレートのファイル名から拡張子を除いたもの) をキーにする。同じテンプレートの別のサンプルは「名前.接尾辞」をキーにする
  (例: `pe.dotnet`、`elf.mips`、`zip.zip64`、`macho.fat`、`tiff.be`、`fat.fat16`、`vhd.dynamic`、`pcap.be`)。
- 期待する JSON のファイル名は `<キー>.expected.json`。
- GPT のサンプル (`gpt`) は `TestDataCatalog.GptImage` (TD-TPL-GPT-100G と同じ内容の計算) で 4 MiB を作ったもの。
- 実行形式の中の鍵・署名・暗号に当たる値 (X.509 風の DER、署名など) は意味のないバイト列で、本物の証明書や鍵ではない。
- 日時は固定の値、GUID は固定の値 (種は使わない)。

## テストデータとしての登録

`TD-TPL-STD-SAMPLES` (フォルダ。`TestDataCatalog` に登録済み) は、すべてのサンプルを `<キー>.bin` として書き出し、一覧 `_samples.txt` を添える。

## 期待する結果の作り直し

テンプレートかサンプルを直したときは、次のようにして期待する結果を作り直し、差分を確認してからコミットする。

```powershell
$env:HEXEDITOR_UPDATE_TEMPLATE_GOLDEN = "1"
dotnet test tests/HexEditor.Core.Tests --filter "FullyQualifiedName~StandardTemplate"
Remove-Item Env:HEXEDITOR_UPDATE_TEMPLATE_GOLDEN
```

期待する結果は、エンジンが出した値をそのまま写したものではなく、サンプルを組み立てたコードの値と照らして確認したものである。
`StandardTemplateTests` の手計算の確認 (セクション名、e_machine、ncmds、ZIP の最初のファイル名、パーティションの種類、GPT の名前など) が、
サンプルを組み立てたコードの値と一致することを検査している。
