# 翻訳

- 文字列は `src/HexEditor.App/Strings/<言語>/Resources.resw` に置く。英語 (`en`) が原文、日本語 (`ja`) は開発者が書く (00-overview 5.3)。
- 残り 21 言語は機械翻訳で作り、`translation-status.json` で状態を管理する (UI-49)。
  - `machine`: 機械翻訳のまま (人の確認待ち)
  - `reviewed`: 人が確認済み。機械翻訳で上書きしない
  - `stale`: 原文が変わったため再確認が必要
- 用語は `glossary.csv` に合わせる (UI-48)。
- 翻訳の誤りは GitHub の Issue または Pull Request で受け付ける (UI-41)。
