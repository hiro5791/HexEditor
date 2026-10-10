# テストデータ一覧

テストデータ生成ツール (`tools/TestDataGen`、[00-test-strategy.md](00-test-strategy.md) の 7 章) が作るファイルの一覧。テストケースは ID でテストデータを指定する。

- 同じ ID からは常に同じ内容を作る。乱数を使うものは種を固定する。
- 「スパース」と書いたものはスパースファイルとして作る。見かけのサイズが大きくても、実際のディスク使用量は数 MB 以下。
- OS のツールで作るファイルシステムのイメージは、作るたびにバイト単位では同じにならない。これらは生成時に内容の一覧 (マニフェスト) を記録し、テストはマニフェストと比べる。
- 「保管」と書いたものは生成ツールでは作れない (macOS・他社製ツール・BitLocker が必要など)。一度だけ作って、テスト用の保管場所 (リポジトリの外。場所は未決定) に置き、生成ツールはそこから取得する。Linux のツールが必要なものは、生成ツールが WSL を使って作る。
- 各テストケースのファイルの末尾にある「このファイルで使うテストデータ」の表は、後でこの一覧に統合する。

## 1. 共通のテストデータ

どの領域のテストケースからも使ってよい基本のデータ。

| ID | サイズ | 内容 |
| --- | --- | --- |
| TD-EMPTY | 0 バイト | 空のファイル |
| TD-BYTES-256 | 256 バイト | 00, 01, 02, …, FF の順に 1 回ずつ |
| TD-SEQ-1M | 1 MiB | オフセットの下位 1 バイトを値とする (オフセット n の値は n mod 256) |
| TD-ZERO-1M | 1 MiB | すべて 00 |
| TD-FF-1M | 1 MiB | すべて FF |
| TD-RANDOM-16M | 16 MiB | 固定の種で作った乱数 |
| TD-MARKERS-1G | 1 GiB (スパース) | 先頭・末尾・2^20 ごとに、オフセットを ASCII の 16 桁の Hex で書いた目印 (例: `@0000000000100000`)。それ以外は 00 |
| TD-SPARSE-100G | 100 GiB (スパース) | TD-MARKERS-1G と同じ規則の目印を、先頭・末尾・2^31・2^32 の前後・1 GiB ごとに置く |
| TD-SPARSE-2T | 2 TiB (スパース) | 同じ規則の目印を、先頭・末尾・2^31・2^32・2^40 の前後に置く |
| TD-TEXT-ASCII | 4 KiB | 英文の ASCII テキスト (改行は CRLF) |
| TD-TEXT-UTF8 | 4 KiB | 23 言語の短い文を UTF-8 (BOM なし) で並べたもの。絵文字・結合文字を含む |
| TD-TEXT-UTF8-BOM | 4 KiB | TD-TEXT-UTF8 と同じ内容で BOM 付き |
| TD-TEXT-UTF16LE | 8 KiB | TD-TEXT-UTF8 と同じ内容を UTF-16 LE (BOM 付き) にしたもの |
| TD-TEXT-UTF16BE | 8 KiB | 同じく UTF-16 BE (BOM 付き) |
| TD-TEXT-SJIS | 4 KiB | 日本語の文を Shift_JIS にしたもの (半角カナ・機種依存文字を含む) |
| TD-TEXT-EBCDIC | 4 KiB | 英文を EBCDIC (コードページ 037) にしたもの |
| TD-PE-X64 | 約 10 KiB | 既知の内容の小さな Windows 実行ファイル (x64)。ソースはリポジトリに置き、同じ内容になるようにビルドする (ビルドの仕組みができるまでは、生成ツールが最小の構造 (8 KiB) を直接書き出す) |
| TD-ELF-X64 | 約 10 KiB | 同じく Linux 実行ファイル (x64) (同じく当面は最小の構造 (8 KiB) を直接書き出す) |
| TD-PNG | 約 1 KiB | 16×16 の PNG 画像 |
| TD-ZIP | 約 2 KiB | テキストファイル 3 つを入れた ZIP |
| TD-IHEX | 約 3 KiB | Intel HEX (32 bit のアドレス拡張と、アドレスの飛びを含む) |
| TD-SREC | 約 3 KiB | Motorola S-record (S3 形式、アドレスの飛びを含む) |
| TD-BASE64 | 約 2 KiB | TD-RANDOM-16M の先頭 1 KiB を Base64 にしたテキスト |
| TD-READONLY | 1 KiB | 読み取り専用属性を付けた TD-SEQ-1M の先頭 1 KiB |
| TD-VHDX-MBR | 64 MiB | MBR のパーティション 1 つ (NTFS) を持つ仮想ディスク |
| TD-VHDX-GPT | 64 MiB | GPT のパーティション 2 つ (NTFS と FAT32) を持つ仮想ディスク |

## 2. 領域ごとのテストデータ

領域ごとのテストデータは、各テストケースのファイルの末尾「このファイルで使うテストデータ」で定義する。ID の接頭辞で定義の場所が分かる。共通のテストデータと合わせて約 440 件。

| 接頭辞 | 定義の場所 |
| --- | --- |
| TD-ENG- | [cases/01-engine-and-sources.md](cases/01-engine-and-sources.md) |
| TD-VIEW- | [cases/02-view-and-navigation.md](cases/02-view-and-navigation.md) |
| TD-EDIT- | [cases/03-editing.md](cases/03-editing.md) |
| TD-FIND- | [cases/04-search.md](cases/04-search.md) |
| TD-INSP- | [cases/05-inspector-and-annotations.md](cases/05-inspector-and-annotations.md) |
| TD-ANA- | [cases/06-analysis.md](cases/06-analysis.md) |
| TD-TPL- | [cases/07-templates.md](cases/07-templates.md) |
| TD-AUTO- | [cases/08-automation.md](cases/08-automation.md) |
| TD-UI- | [cases/09-ui-and-settings.md](cases/09-ui-and-settings.md) |
| TD-PKG- | [cases/10-packaging.md](cases/10-packaging.md) |
| TD-TOOL- | [cases/11-tools.md](cases/11-tools.md) |
| TD-FOR- | [cases/12-forensics.md](cases/12-forensics.md) |

- 別の領域のテストデータを使ってよい (例: 04 が TD-EDIT-SAMPLE を使う)。定義は上の表の場所に 1 つだけ置く。
- テストデータ生成ツールは、この文書と各ファイルの表をすべて読み込んで生成する。
