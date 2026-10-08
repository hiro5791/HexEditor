# 翻訳

- 文字列は `src/HexEditor.App/Strings/<言語>/Resources.resw` に置く。英語 (`en`) が原文、日本語 (`ja`) は開発者が書く (00-overview 5.3)。
- 残り 21 言語は CI (`.github/workflows/translate.yml`、毎晩と手動) で機械翻訳し、変更を 1 つの Pull Request にまとめる (UI-49)。
- 訳文の状態は `translation-status.json` で、言語とキーごとに `"<状態>:<原文のハッシュ>"` の形で持つ。ハッシュは英語の原文 (UTF-8) の SHA-256 の先頭 16 桁 (小文字)。状態ファイルにない文字列は未翻訳として扱う。
  - `machine`: 機械翻訳のまま (人の確認待ち)。原文が変わったら機械翻訳し直す
  - `reviewed`: 人が確認済み。機械翻訳で上書きしない。原文が変わったら `stale` にする
  - `stale`: 確認した後に原文が変わった (要再確認)。訳はそのまま使い、機械翻訳で上書きしない
  - 確認した訳は、Pull Request でその値を `reviewed:<ハッシュ>` (今の原文のハッシュ) にする。
- 用語は `glossary.csv` に合わせる (UI-48)。列は `term`、`description`、`part_of_speech`、`do_not_translate` と 22 言語。`do_not_translate` が `true` の用語は全言語で原文のまま (訳語の列は空)。
- 翻訳の誤りは GitHub の Issue (`.github/ISSUE_TEMPLATE/translation.yml`) または Pull Request で受け付ける (UI-41、[CONTRIBUTING.md](../../CONTRIBUTING.md))。
- ツール (`tools/I18nTool`):

  ```powershell
  dotnet run --project tools/I18nTool -- check-glossary   # 用語集の形式 (誤りは失敗) と訳さない用語 (違反は警告)
  dotnet run --project tools/I18nTool -- check-status     # 状態ファイルの形式と .resw との食い違い (誤りは失敗)
  dotnet run --project tools/I18nTool -- translate        # 機械翻訳 (HEX_TRANSLATOR_KEY、HEX_TRANSLATOR_ENDPOINT)
  dotnet run --project tools/I18nTool -- coverage         # translation-coverage.json (ビルドでも同じものを作る)
  ```

- 言語ごとの翻訳済み・確認済みの割合 (`translation-coverage.json`) は、ビルド時に `build/TranslationCoverage.targets` が作ってアプリに同梱する。表示言語の一覧 (UI-43) とリリースノートの表 (PKG-29) に使う。
