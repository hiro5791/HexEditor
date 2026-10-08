# Contributing

[日本語](#日本語) | [English](#english)

## English

### Reporting a translation error

In HexEditor, choose **Help > Report a translation error**, pick the string and press **Report**. Your browser opens the
[translation form](https://github.com/hiro5791/HexEditor/issues/new?template=translation.yml) with the language, the resource key,
the current text and the version filled in. Write the correct text and submit the issue.

To see the resource key of any string, turn on **Show resource keys in tooltips** (setting `i18n.showStringKeys`) and restart HexEditor.

### Fixing a translation with a pull request

1. The strings are in `src/HexEditor.App/Strings/<language>/Resources.resw`. The folder names are listed in
   [docs/spec/00-overview.md](docs/spec/00-overview.md) (section 5.1). English (`en`) is the source text.
2. Find the key: search the `.resw` file for the key from the tooltip (for example `Menu_File.Title`), or for the English text in
   `Strings/en/Resources.resw`.
3. Change only the `<value>` of the entry. Keep placeholders such as `{0}` and `{count}` as they are, and keep the access key
   (`.AccessKey` entries) unique within its menu.
4. Use the terms of the glossary [docs/i18n/glossary.csv](docs/i18n/glossary.csv). Terms marked `do_not_translate` (for example `Hex`,
   `UTF-8`) stay in English.
5. Open a pull request. The CI checks the resources (keys, placeholders) and the glossary. A maintainer reviews the translation and marks it
   as reviewed in [docs/i18n/translation-status.json](docs/i18n/translation-status.json). Reviewed translations are never overwritten by machine
   translation.

## 日本語

### 翻訳の誤りの報告

HexEditor のヘルプ >「翻訳の誤りを報告」で文字列を選び、「報告」を押します。表示言語・リソースキー・現在の訳文・版を入力済みにした
[翻訳の報告用のフォーム](https://github.com/hiro5791/HexEditor/issues/new?template=translation.yml) がブラウザで開くので、正しい訳を書いて送ってください。

文字列のリソースキーを確かめるには、設定「ツールチップにリソースキーを表示する」(`i18n.showStringKeys`) をオンにして再起動します。

### Pull Request で翻訳を直す

1. 文字列は `src/HexEditor.App/Strings/<言語>/Resources.resw` にあります。フォルダ名は
   [docs/spec/00-overview.md](docs/spec/00-overview.md) の 5.1 の表のとおりです。英語 (`en`) が原文です。
2. キーを探す: ツールチップのキー (例: `Menu_File.Title`) で `.resw` を検索するか、`Strings/en/Resources.resw` で英語の原文を検索します。
3. その項目の `<value>` だけを直します。`{0}` や `{count}` などのプレースホルダーは変えません。アクセスキー (`.AccessKey` の項目) は同じメニューの中で重ならないようにします。
4. 用語は用語集 [docs/i18n/glossary.csv](docs/i18n/glossary.csv) に合わせます。`do_not_translate` の用語 (`Hex`、`UTF-8` など) は原文のままにします。
5. Pull Request を送ります。CI がリソース (キー、プレースホルダー) と用語集を検査します。本プロジェクトが訳を確認し、
   [docs/i18n/translation-status.json](docs/i18n/translation-status.json) で「確認済み」にします。確認済みの訳は機械翻訳で上書きされません。
