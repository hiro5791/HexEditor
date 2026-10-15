# 自動化 API リファレンス

API バージョン 1.0。HexEditor.Sdk から生成する (手で編集しない)。JavaScript の名前を示す (C# では先頭を大文字にし、非同期のメソッドには `Async` が付く)。

## グローバル変数

- `app`: `App`
- `doc`: `Document | null`
- `ui`: `Ui`
- `hash`: `Hash`
- `clipboard`: `Clipboard`
- `settings`: `Settings`
- `script`: `Script`
- `inspector`: `Inspector`
- `text`: `Text`
- `templates`: `Templates`
- `fs`: `FileSystem`
- `net`: `Net`
- `process`: `ProcessRunner`

## App

アプリ全体 (AUTO-06)。JavaScript ではグローバルの `app`、C# では `App`。

### App.version

`readonly version: string`

アプリのバージョン。

### App.apiVersion

`readonly apiVersion: string`

自動化 API のバージョン (例: `1.0`)。

### App.language

`readonly language: string`

UI の表示言語のタグ (例: `ja`)。

### App.documents

`readonly documents: Document[]`

開いているドキュメントの一覧。

### App.activeDocument

`activeDocument: Document | null`

アクティブなドキュメント。ない場合は null。代入するとそのタブをアクティブにする。

### App.commands

`readonly commands: Commands`

アプリのコマンドの実行。

### App.inspector

`readonly inspector: Inspector`

データインスペクタ。

### App.text

`readonly text: Text`

文字コードの変換。

### App.templates

`readonly templates: Templates`

構造体テンプレート。

### App.hash

`readonly hash: Hash`

ハッシュとチェックサム。

### App.clipboard

`readonly clipboard: Clipboard`

クリップボード。

### App.ui

`readonly ui: Ui`

ダイアログ・進捗・出力。

### App.settings

`readonly settings: Settings`

アプリの設定。

### App.script

`readonly script: Script`

実行中のスクリプト。

### App.fs

`readonly fs: FileSystem`

ファイル (許可が必要)。

### App.net

`readonly net: Net`

ネットワーク (許可が必要)。

### App.process

`readonly process: ProcessRunner`

外部プログラム (許可が必要)。

### App.open

`open(path: string, options?: OpenOptions): Promise<Document>`

ファイルを開く。サイズに関係なくすぐに戻る。

### App.openDisk

`openDisk(id: string, options?: OpenOptions): Promise<Document>`

ディスク・ボリュームを ID で開く。

許可: `disk`

### App.openProcess

`openProcess(pid: number, options?: OpenOptions): Promise<Document>`

プロセスのメモリを開く。

許可: `disk`

### App.newDocument

`newDocument(options?: NewDocumentOptions): Document`

新規ドキュメントを作る。

### App.on

`on(eventName: string, handler: (event: ScriptEventArgs) => void): EventSubscription`

イベントを購読する: `documentOpened`・`documentClosed`・`activeDocumentChanged`・`beforeSave`・`afterSave`・`selectionChanged` (100 ms ごとにまとめる)。購読したスクリプトは `stop` を呼ぶか停止するまで常駐する。

### App.stop

`stop(): void`

実行中のスクリプト自身を終了する。

### App.exit

`exit(code: number): void`

スクリプトを終え、ヘッドレス実行の終了コードを決める (0〜255。スクリプト独自の意味には 64 以上を使う)。

## Bookmark

1 つのブックマーク。

### Bookmark.name

`readonly name: string`

名前。

### Bookmark.start

`readonly start: number`

開始オフセット。

### Bookmark.length

`readonly length: number`

長さ。

### Bookmark.color

`readonly color: string`

色の名前。

### Bookmark.comment

`readonly comment: string`

コメント。

### Bookmark.number

`readonly number: number | null`

番号付きブックマークの番号 (1〜9)。なければ null。

## Bookmarks

ドキュメントのブックマーク (AUTO-11)。配列のように列挙できる。スクリプトが追加したものも利用者のものと同じく保存される。

### Bookmarks.add

`add(bookmark: BookmarkSpec): Bookmark`

ブックマークを追加する。

### Bookmarks.remove

`remove(bookmark: Bookmark): void`

ブックマークを削除する。

### Bookmarks.clear

`clear(): void`

すべて削除する。

### Bookmarks.find

`find(name: string): Bookmark | null`

名前でブックマークを探す。なければ null。

### Bookmarks.addFromResults

`addFromResults(results: ByteRange[], nameFormat?: string): number`

結果ごとにブックマークを追加する (100 万件まで)。`nameFormat` の `{index}` は 1 から始まる連番。

## Clipboard

クリップボード (AUTO-15)。

### Clipboard.setText

`setText(text: string): void`

文字列を置く。

### Clipboard.setBytes

`setBytes(data: BytePattern): void`

バイト列を置く (バイナリ形式と Hex 文字列の両方)。

### Clipboard.getText

`getText(): string | null`

文字列を読む。なければ null。

許可: `clipboard-read`

### Clipboard.getBytes

`getBytes(): Uint8Array | null`

バイト列を読む。なければ null。

許可: `clipboard-read`

### Clipboard.copyAs

`copyAs(document: Document, range: RangeArg, formatId: string, options?: CopyAsOptions): void`

Copy As と同じ形式で書き出す (例: `hex`、`c-array`、`base64`)。

### Clipboard.formatAs

`formatAs(document: Document, range: RangeArg, formatId: string, options?: CopyAsOptions): string`

Copy As と同じ形式の文字列を、クリップボードに置かずに返す。

## Commands

アプリのコマンドを ID で実行する (AUTO-06)。

### Commands.execute

`execute(id: string, args?: Record<string, unknown>): Promise<unknown>`

コマンド `id` を実行する。確認を求めるコマンドは `args.confirm` で答えを渡せる。渡さない場合、対話的な実行ではダイアログを出し、ヘッドレス実行では安全側 (キャンセル) を選ぶ。

### Commands.list

`list(): CommandInfo[]`

コマンドの一覧 (ID・表示名・引数)。

## Document

開いている 1 つのドキュメント (AUTO-07)。同期の読み書きは 1 回 16 MB まで (AUTO-05)。それより大きい量は `chunks` と一括処理 API (AUTO-09) を使う。

### Document.id

`readonly id: string`

セッション中で一意な識別子。

### Document.kind

`readonly kind: string`

`file`・`disk`・`volume`・`process`・`memory` (新規)。

### Document.path

`readonly path: string | null`

パス。新規ドキュメントでは null。

### Document.name

`readonly name: string`

表示名。

### Document.length

`readonly length: number`

現在の長さ (バイト)。

### Document.isModified

`readonly isModified: boolean`

未保存の変更があるか。

### Document.isReadOnly

`readonly isReadOnly: boolean`

読み取り専用か。

### Document.canResize

`readonly canResize: boolean`

長さを変えられるか (ディスクなどは false)。

### Document.baseAddress

`readonly baseAddress: number`

オフセット 0 に表示するアドレス。

### Document.sectorSize

`readonly sectorSize: number`

セクタサイズ。

### Document.encoding

`readonly encoding: string`

テキスト列の文字コードの名前。

### Document.cursor

`cursor: number`

カーソルのオフセット。代入するとカーソルを移動する (ジャンプ履歴には記録しない。記録する場合は `goTo`)。

### Document.selection

`readonly selection: Selection`

選択範囲 (AUTO-10)。

### Document.activeColumn

`activeColumn: string`

`"hex"` か `"text"`。代入で切り替える。

### Document.bookmarks

`readonly bookmarks: Bookmarks`

ブックマーク (AUTO-11)。

### Document.highlights

`readonly highlights: Highlights`

スクリプトの終了で消える一時的な強調表示。

### Document.save

`save(options?: AsyncOptions): Promise<void>`

エディタと同じ安全な方式で保存する。

### Document.saveAs

`saveAs(path: string, options?: AsyncOptions): Promise<void>`

名前を付けて保存する。

### Document.close

`close(options?: CloseOptions): Promise<void>`

閉じる。

### Document.undo

`undo(): void`

直前の編集を元に戻す。

### Document.redo

`redo(): void`

元に戻した編集をやり直す。

### Document.beginUndoGroup

`beginUndoGroup(name: string): void`

`name` という 1 つの Undo 単位を始める。

### Document.endUndoGroup

`endUndoGroup(): void`

`beginUndoGroup` で始めた単位を終える。

### Document.goTo

`goTo(offset: OffsetArg): void`

カーソルを移動し、ジャンプ履歴に記録する。

### Document.scrollTo

`scrollTo(offset: OffsetArg): void`

カーソルを動かさずに表示位置だけを変える。

### Document.openRange

`openRange(range: RangeArg): Promise<Document>`

範囲を別のタブで開く (読み取り専用のビュー)。

### Document.readBytes

`readBytes(offset: OffsetArg, length: OffsetArg): Uint8Array`

バイト列を読む (16 MB まで)。末尾を越える部分は返さない (返す長さが短くなる)。

### Document.readByte

`readByte(offset: OffsetArg): number`

1 バイト読む。

### Document.readInt

`readInt(offset: OffsetArg, type: string, endian?: string): number | bigint`

整数として読む。`type`: `i8 u8 i16 u16 i24 u24 i32 u32 i64 u64`。`endian`: `"le"` / `"be"` (省略時はドキュメントの既定)。64 bit の値は JavaScript では `bigint`。

### Document.readFloat

`readFloat(offset: OffsetArg, type: string, endian?: string): number`

浮動小数点として読む。`type`: `f16 f32 f64`。

### Document.readString

`readString(offset: OffsetArg, maxLength: OffsetArg, encoding?: EncodingArg | null, options?: ReadStringOptions): string`

最大 `maxLength` バイトを文字列として読む。

### Document.writeBytes

`writeBytes(offset: OffsetArg, data: BytePattern): void`

上書きする (長さは変わらない)。末尾を越える部分は、長さを変えられるドキュメントなら追加し、変えられないなら `RangeError`。

### Document.writeInt

`writeInt(offset: OffsetArg, type: string, value: number | bigint, endian?: string): void`

整数を書く (引数は `readInt` と対応)。

### Document.writeFloat

`writeFloat(offset: OffsetArg, type: string, value: number, endian?: string): void`

浮動小数点の値を書く。

### Document.writeString

`writeString(offset: OffsetArg, text: string, encoding?: EncodingArg | null): number`

文字列を書き、書いたバイト数を返す。

### Document.insertBytes

`insertBytes(offset: OffsetArg, data: BytePattern): void`

挿入する。

### Document.delete

`delete(range: RangeArg): void`

範囲を削除する。

### Document.fill

`fill(range: RangeArg, pattern: BytePattern, options?: FillOptions): void`

パターン (バイト列・Hex・`"random"`) で塗りつぶす。データ量に関係なく 100 ms 以内に終わる。

### Document.resize

`resize(newLength: OffsetArg, fill?: BytePattern | null): void`

長さを変える (切り詰め・`fill` で延長)。

### Document.transform

`transform(range: RangeArg, op: string, operand?: TransformOperand | null, options?: TransformOptions): Promise<void>`

範囲のデータ演算。`op`: `xor and or not add sub mul div shl shr rol ror neg swap reverse`。

### Document.findAll

`findAll(pattern: BytePattern, options?: FindOptions): Promise<SearchResults>`

すべて検索する (最大 `options.max` 件)。

### Document.find

`find(pattern: BytePattern, options?: FindOptions): Promise<ByteRange | null>`

最初の 1 件を探す。見つからなければ null。

### Document.replaceAll

`replaceAll(pattern: BytePattern, replacement: BytePattern, options?: FindOptions): Promise<number>`

すべて置換し、置換した件数を返す。

### Document.count

`count(pattern: BytePattern, options?: FindOptions): Promise<number>`

一致の件数だけを数える。

### Document.copyRange

`copyRange(source: RangeArg, destination: Document, destinationOffset: OffsetArg, options?: CopyRangeOptions): Promise<void>`

範囲を `destination` にコピーする。データエンジンの参照で行い、実データを複製しない。

### Document.exportRange

`exportRange(range: RangeArg, path: string, options?: ExportRangeOptions): Promise<void>`

範囲をファイルに書き出す (許可 `file-write` または利用者が選んだパス)。

許可: `file-write`

### Document.chunks

`chunks(range?: RangeArg | null, chunkSize?: number): AsyncIterable<DataChunk>`

範囲を `chunkSize` (既定 1 MB、最大 16 MB) ごとに読む。JavaScript では `for await (const { offset, data } of doc.chunks(range))`。

### Document.transformChunks

`transformChunks(range: RangeArg, transform: (data: Uint8Array, offset: number) => Uint8Array | number[], options?: AsyncOptions): Promise<void>`

範囲をチャンクごとに読み、`transform` が返したバイト列 (同じ長さ) で上書きする。

### Document.histogram

`histogram(range?: RangeArg | null, options?: AsyncOptions): Promise<number[]>`

バイト値 0〜255 の出現数 (長さ 256 の配列)。

### Document.entropy

`entropy(range?: RangeArg | null, options?: EntropyOptions): Promise<number | number[]>`

エントロピー (ビット / バイト)。`options.blockSize` を指定するとブロックごとの配列。

### Document.compare

`compare(other: Document, options?: CompareOptions): Promise<ByteRange[]>`

別のドキュメントと比較し、差分の範囲の配列を返す。

### Document.strings

`strings(options?: StringsOptions): Promise<FoundString[]>`

文字列を抽出する。

### Document.applyTemplate

`applyTemplate(nameOrPath: string, options?: ApplyTemplateOptions): Promise<TemplateNode>`

構造体テンプレートを適用し (AUTO-13)、解析結果のツリーの根を返す。

## EventSubscription

`on` が返す購読。

### EventSubscription.off

`off(): void`

イベントの受け取りをやめる。

## FileSystem

ファイル (AUTO-18)。許可 `file-read` / `file-write` が必要。利用者が選んだパスと、スクリプトのフォルダ以下の読み取りは許可なしで使える。

### FileSystem.readBytes

`readBytes(path: string, offset?: number, length?: number): Uint8Array`

ファイルを読む (1 回 16 MB まで)。

許可: `file-read`

### FileSystem.readText

`readText(path: string, encoding?: EncodingArg | null): string`

テキストファイルを読む。

許可: `file-read`

### FileSystem.writeBytes

`writeBytes(path: string, data: BytePattern): void`

ファイルに書く。

許可: `file-write`

### FileSystem.writeText

`writeText(path: string, text: string, encoding?: EncodingArg | null): void`

テキストファイルに書く。

許可: `file-write`

### FileSystem.list

`list(folder: string, pattern?: string): string[]`

フォルダの中を一覧する。

許可: `file-read`

### FileSystem.exists

`exists(path: string): boolean`

ファイル・フォルダがあるか。

許可: `file-read`

### FileSystem.stat

`stat(path: string): FileStat`

ファイルの情報。

許可: `file-read`

### FileSystem.delete

`delete(path: string): void`

ファイルを削除する。

許可: `file-write`

### FileSystem.createFolder

`createFolder(path: string): void`

フォルダを作る。

許可: `file-write`

## Hash

ハッシュとチェックサム (AUTO-14)。

### Hash.algorithms

`readonly algorithms: HashAlgorithmInfo[]`

使えるアルゴリズムの一覧。

### Hash.compute

`compute(document: Document, range?: RangeArg | null, algorithms: HashAlgorithmSpec[], options?: AsyncOptions): Promise<HashResult[]>`

ドキュメントの範囲のハッシュを計算する。複数のアルゴリズムを指定するとデータを 1 回だけ読んで同時に計算する。

### Hash.computeBytes

`computeBytes(data: BytePattern, algorithm: HashAlgorithmSpec): HashResult`

バイト列のハッシュ (同期、16 MB まで)。

### Hash.computeFile

`computeFile(path: string, algorithms: HashAlgorithmSpec[], options?: AsyncOptions): Promise<HashResult[]>`

ファイルを開かずに計算する (許可 `file-read` または利用者が選んだパス)。

許可: `file-read`

## Highlights

一時的な強調表示 (AUTO-07)。

### Highlights.add

`add(range: RangeArg, color?: string, label?: string): void`

範囲を名前付きの色で強調表示する。

### Highlights.clear

`clear(): void`

このスクリプトの強調表示をすべて消す。

## Inspector

データインスペクタの型と値の変換 (AUTO-12)。

### Inspector.types

`readonly types: InspectorTypeInfo[]`

使える型の一覧 (プラグインの型を含む)。

### Inspector.decode

`decode(document: Document, offset: OffsetArg, typeId: string, options?: InspectorOptions): InspectorValue`

`offset` の値をインスペクタパネルと同じく解釈する。

### Inspector.encode

`encode(typeId: string, valueText: string, options?: InspectorOptions): Uint8Array`

値の文字列からバイト列を作る (インスペクタで書き換える操作と同じ解釈)。

### Inspector.show

`show(typeId: string): void`

インスペクタパネルに型を表示する。

### Inspector.hide

`hide(typeId: string): void`

インスペクタパネルの型を隠す。

## Net

スクリプトの通信 (AUTO-18、許可 `network`)。

### Net.fetch

`fetch(url: string, options?: FetchOptions): Promise<FetchResponse>`

HTTPS / HTTP の GET・POST。応答の本文は 100 MB まで。

許可: `network`

## ProcessRunner

外部プログラム (AUTO-18、許可 `process`)。

### ProcessRunner.run

`run(path: string, args: string[], options?: ProcessOptions): Promise<ProcessResult>`

シェルを経由せずにプログラムを実行する。`args` の各要素がそのまま 1 つの引数になる。

許可: `process`

## Script

実行中のスクリプト (AUTO-17)。

### Script.name

`readonly name: string`

表示名。

### Script.path

`readonly path: string | null`

ファイルのパス。コンソール・未保存のエディタでは null。

### Script.args

`readonly args: Record<string, unknown>`

パラメータの値 (AUTO-26)。フックでは `event` も入る。

### Script.signal

`readonly signal: AbortSignal`

利用者がスクリプトを止めるとキャンセル状態になる。

### Script.storage

`readonly storage: ScriptStorage`

このスクリプトの永続的な保存領域 (1 MB)。

## ScriptStorage

スクリプトごとの永続的な保存領域 (AUTO-17)。

### ScriptStorage.get

`get(key: string): unknown`

値を読む。なければ null。

### ScriptStorage.set

`set(key: string, value?: unknown): void`

JSON にできる値を保存する。

### ScriptStorage.remove

`remove(key: string): void`

値を削除する。

## Selection

選択範囲 (マルチ選択・矩形選択を含む。AUTO-10)。

### Selection.start

`readonly start: number`

主選択範囲の開始。

### Selection.end

`readonly end: number`

主選択範囲の終了 (含まない)。

### Selection.length

`readonly length: number`

主選択範囲の長さ。

### Selection.ranges

`readonly ranges: ByteRange[]`

すべての選択範囲。選択がなければ空。

### Selection.isEmpty

`readonly isEmpty: boolean`

選択がないか。

### Selection.isRectangular

`readonly isRectangular: boolean`

矩形選択か。

### Selection.set

`set(range: RangeArg): void`

範囲を選択する。

### Selection.add

`add(range: RangeArg): void`

マルチ選択に範囲を追加する。

### Selection.clear

`clear(): void`

選択を解除する。

### Selection.setRectangle

`setRectangle(startOffset: OffsetArg, rows: number, columns: number): void`

矩形選択する。

### Selection.fromResults

`fromResults(results: ByteRange[]): void`

一括処理の結果をマルチ選択にする (100 万件まで)。

## Settings

アプリの設定 (AUTO-17)。

### Settings.get

`get(key: string): unknown`

設定値を読む (キーは設定のキー)。

### Settings.set

`set(key: string, value?: unknown): void`

設定を変える。その場で反映する。

許可: `settings`

## TemplateNode

テンプレートの解析結果のノード (AUTO-13)。

### TemplateNode.name

`readonly name: string`

名前。

### TemplateNode.type

`readonly type: string`

型の名前。

### TemplateNode.start

`readonly start: number`

開始オフセット。

### TemplateNode.length

`readonly length: number`

長さ。

### TemplateNode.value

`value: unknown | null`

値 (数値・文字列など)。代入するとテンプレートのツリーから編集した場合と同じくドキュメントを書き換える (Undo できる)。

### TemplateNode.displayValue

`readonly displayValue: string`

テンプレートの書式を適用した値の文字列。

### TemplateNode.children

`readonly children: TemplateNode[]`

子のノード。

### TemplateNode.get

`get(path: string): TemplateNode | null`

ドット区切りのパスで子孫を探す (例: `"header.e_lfanew"`)。なければ null。

### TemplateNode.toJSON

`toJSON(): unknown`

JSON にした内容 (コマンドラインの `template` の出力と同じ形)。

## Templates

構造体テンプレート (AUTO-13)。

### Templates.list

`list(): TemplateInfo[]`

使えるテンプレートの一覧。

### Templates.detect

`detect(document: Document): TemplateInfo[]`

自動適用と同じ判定で、合うテンプレートの候補を返す。

### Templates.registerExtension

`registerExtension(name: string, fn: (...args: any[]) => any, options?: TemplateExtensionOptions): EventSubscription`

テンプレートから呼べる関数 (テンプレート拡張。TPL-19) を登録する。登録している間スクリプトは常駐し、 `off` かスクリプトの終了で登録を外す。

## Text

文字コードの変換 (AUTO-12)。

### Text.encodings

`readonly encodings: EncodingInfo[]`

使える文字コードの一覧。

### Text.encode

`encode(text: string, encoding: EncodingArg): Uint8Array`

文字列をバイト列にする。

### Text.decode

`decode(bytes: BytePattern, encoding: EncodingArg, options?: DecodeOptions): string`

バイト列を文字列にする。

## Ui

ダイアログ・進捗・出力 (AUTO-16)。

### Ui.progress

`readonly progress: UiProgress`

処理センターの進捗。

### Ui.message

`message(text: string, options?: MessageOptions): Promise<void>`

メッセージを表示する (既定は InfoBar、`modal` ならダイアログ)。

### Ui.confirm

`confirm(text: string, buttons: string[]): Promise<number>`

ボタンを選ばせ、その添字を返す。Esc は -1。

### Ui.prompt

`prompt(label: string, options?: PromptOptions): Promise<unknown>`

1 つの値を入力させる。キャンセルは null。

### Ui.form

`form(fields: FormField[]): Promise<Record<string, unknown> | null>`

複数の項目の入力ダイアログ。項目名ごとの値のオブジェクトを返す。キャンセルは null。

### Ui.pickFile

`pickFile(options?: PickOptions): Promise<string>`

読むファイルを選ばせる。選んだパスはこの実行中に限り許可なしで読める。

### Ui.pickFiles

`pickFiles(options?: PickOptions): Promise<string[]>`

読むファイルを複数選ばせる。

### Ui.saveFile

`saveFile(options?: PickOptions): Promise<string>`

書き込むファイルを選ばせる。

### Ui.pickFolder

`pickFolder(options?: PickOptions): Promise<string>`

フォルダを選ばせる。

### Ui.status

`status(text: string): void`

ステータスバーに 5 秒間文字列を表示する。

### Ui.showResults

`showResults(title: string, rows: Record<string, unknown>[], columns?: string[]): void`

共通の結果一覧パネルに出す。`start` と `length` のある行はクリックでジャンプできる。

### Ui.output

`output(text: string, kind?: string): void`

出力パネルに 1 行書く。`kind`: `info`・`warning`・`error`。

## UiProgress

実行中のスクリプトの処理センターの進捗。

### UiProgress.report

`report(fraction: number, text?: string): void`

終わった割合 (0〜1) と説明を報告する。

## ScriptEventArgs

イベントのハンドラに渡す引数 (`on`)。

### ScriptEventArgs.name

`readonly name: string`

イベントの名前 (例: `beforeSave`)。

### ScriptEventArgs.document

`readonly document: Document | null`

イベントの対象のドキュメント (あれば)。

### ScriptEventArgs.cancel

`cancel(reason: string): void`

操作を中止する (`beforeSave` のみ)。理由は利用者に表示する。

## ApplyTemplateOptions

`applyTemplate` のオプション。

### ApplyTemplateOptions.offset

`offset?: OffsetArg | null`

テンプレートを適用するオフセット (既定 0)。

### ApplyTemplateOptions.show

`show?: boolean`

テンプレートパネルに結果を表示する (既定 true)。

### ApplyTemplateOptions.signal

`signal?: AbortSignal | null`

キャンセル (JavaScript では `script.signal` や `new AbortController().signal` などの `AbortSignal`)。

### ApplyTemplateOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

進捗を受け取る関数。終わった割合 (0〜1) と説明。

## AsyncOptions

非同期 API が共通で受け取るオプション (AUTO-05)。省略した場合はスクリプト全体のキャンセルに従い、進捗を処理センターに出す。

### AsyncOptions.signal

`signal?: AbortSignal | null`

キャンセル (JavaScript では `script.signal` や `new AbortController().signal` などの `AbortSignal`)。

### AsyncOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

進捗を受け取る関数。終わった割合 (0〜1) と説明。

## BookmarkSpec

`add` に渡す新しいブックマーク。

### BookmarkSpec.start

`start?: OffsetArg`

開始オフセット。

### BookmarkSpec.length

`length?: number`

長さ (既定 1)。

### BookmarkSpec.name

`name?: string | null`

名前。

### BookmarkSpec.color

`color?: string | null`

ブックマークの色パレットの名前 (例: `"red"`)。

### BookmarkSpec.comment

`comment?: string | null`

コメント。

## CloseOptions

`close` のオプション。

### CloseOptions.discard

`discard?: boolean`

未保存の変更を確認なしで破棄する。

## CommandInfo

アプリのコマンド。

### CommandInfo.id

`readonly id: string`

固定の英語の識別子 (例: `edit.fill`)。

### CommandInfo.name

`readonly name: string`

表示言語での表示名。

### CommandInfo.category

`readonly category: string`

分類。

### CommandInfo.arguments

`readonly arguments: string[]`

受け付ける引数の名前。

## CompareOptions

`compare` のオプション。

### CompareOptions.max

`max?: number`

差分の範囲の最大件数 (既定 1,000,000)。

### CompareOptions.signal

`signal?: AbortSignal | null`

キャンセル (JavaScript では `script.signal` や `new AbortController().signal` などの `AbortSignal`)。

### CompareOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

進捗を受け取る関数。終わった割合 (0〜1) と説明。

## CopyAsOptions

Copy As のオプション。

### CopyAsOptions.bytesPerLine

`bytesPerLine?: number`

1 行のバイト数 (0 は Copy As の設定)。

### CopyAsOptions.uppercase

`uppercase?: boolean | null`

Hex を大文字にする (null は Copy As の設定)。

## CopyRangeOptions

`copyRange` のオプション。

### CopyRangeOptions.insert

`insert?: boolean`

上書きでなく挿入する。

### CopyRangeOptions.signal

`signal?: AbortSignal | null`

キャンセル (JavaScript では `script.signal` や `new AbortController().signal` などの `AbortSignal`)。

### CopyRangeOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

進捗を受け取る関数。終わった割合 (0〜1) と説明。

## CustomCrc

カスタム CRC のパラメータ (Rocksoft のモデル)。`id` は常に `"crc"`。

### CustomCrc.id

`id?: string`

常に `"crc"`。

### CustomCrc.width

`width?: number`

ビット幅 (1〜64)。

### CustomCrc.poly

`poly?: number`

多項式。

### CustomCrc.init

`init?: number`

初期値。

### CustomCrc.refIn

`refIn?: boolean`

入力のビットを反転する。

### CustomCrc.refOut

`refOut?: boolean`

出力のビットを反転する。

### CustomCrc.xorOut

`xorOut?: number`

出力に XOR する値。

## DataChunk

`chunks` が読んだ 1 つのチャンク。

### DataChunk.offset

`readonly offset: number`

先頭のバイトのオフセット。

### DataChunk.data

`readonly data: Uint8Array`

バイト列。

## DecodeOptions

`decode` のオプション。

### DecodeOptions.invalid

`invalid?: string | null`

`"replace"` (既定。不正なバイトを置換文字にする) か `"error"` (`ArgumentError` を投げる)。

## EncodingInfo

アプリが対応する文字コード。

### EncodingInfo.codePage

`readonly codePage: number`

コードページ番号。

### EncodingInfo.name

`readonly name: string`

名前 (例: `shift_jis`)。

### EncodingInfo.displayName

`readonly displayName: string`

表示名。

## EntropyOptions

`entropy` のオプション。

### EntropyOptions.blockSize

`blockSize?: number`

指定するとこの大きさのブロックごとの値の配列を返す。

### EntropyOptions.signal

`signal?: AbortSignal | null`

キャンセル (JavaScript では `script.signal` や `new AbortController().signal` などの `AbortSignal`)。

### EntropyOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

進捗を受け取る関数。終わった割合 (0〜1) と説明。

## ExportRangeOptions

`exportRange` のオプション。

### ExportRangeOptions.overwrite

`overwrite?: boolean`

既存のファイルを上書きする (既定 false)。

### ExportRangeOptions.format

`format?: string | null`

エクスポートの形式の ID。`hexed convert --to` と同じ (例: `ihex`、`srec`、`base64`、`hextext`、`c`。既定 `bin`)。

### ExportRangeOptions.formatOptions

`formatOptions?: Record<string, string> | null`

形式の設定。エクスポートのダイアログの項目名で指定する (例: `recordLength`、`startAddress`、`lineLength`)。

### ExportRangeOptions.signal

`signal?: AbortSignal | null`

キャンセル (JavaScript では `script.signal` や `new AbortController().signal` などの `AbortSignal`)。

### ExportRangeOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

進捗を受け取る関数。終わった割合 (0〜1) と説明。

## FetchOptions

`fetch` のオプション。

### FetchOptions.method

`method?: string | null`

`"GET"` (既定) か `"POST"`。

### FetchOptions.headers

`headers?: Record<string, string> | null`

要求のヘッダー。

### FetchOptions.body

`body?: BytePattern | null`

要求の本文 (POST)。

### FetchOptions.bodyText

`bodyText?: string | null`

要求の本文の文字列 (POST)。

### FetchOptions.responseType

`responseType?: string | null`

`"text"` (既定) か `"bytes"`。

### FetchOptions.signal

`signal?: AbortSignal | null`

キャンセル (JavaScript では `script.signal` や `new AbortController().signal` などの `AbortSignal`)。

### FetchOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

進捗を受け取る関数。終わった割合 (0〜1) と説明。

## FetchResponse

`fetch` の応答。

### FetchResponse.status

`readonly status: number`

HTTP の状態コード。

### FetchResponse.headers

`readonly headers: Record<string, string>`

応答のヘッダー。

### FetchResponse.body

`readonly body: Uint8Array | string`

本文 (`responseType` により `Uint8Array` か文字列)。

## FileStat

ファイルの情報。

### FileStat.size

`readonly size: number`

サイズ (バイト。フォルダは 0)。

### FileStat.modified

`readonly modified: string`

最終更新日時 (ISO 8601、UTC)。

### FileStat.isFolder

`readonly isFolder: boolean`

フォルダか。

## FillOptions

`fill` のオプション。

### FillOptions.insert

`insert?: boolean`

上書きでなく挿入する。

## FindOptions

検索の API のオプション。

### FindOptions.type

`type?: string | null`

文字列のパターンの解釈: `"hex"` (既定。ワイルドカード可)、`"text"`、`"regex"`、`"int"`、`"float"`。バイト列のパターンはそのまま探す。

### FindOptions.encoding

`encoding?: EncodingArg | null`

`text` と `regex` の文字コード。

### FindOptions.caseSensitive

`caseSensitive?: boolean`

大文字と小文字を区別する (既定 false)。

### FindOptions.range

`range?: RangeArg | null`

この範囲だけを検索する。

### FindOptions.alignment

`alignment?: number`

開始がこの値の倍数の一致だけ。

### FindOptions.max

`max?: number`

結果の最大件数 (既定 1,000,000)。

### FindOptions.from

`from?: OffsetArg | null`

`find` の開始位置。

### FindOptions.backward

`backward?: boolean`

後ろ向きに検索する (`find`)。

### FindOptions.size

`size?: number`

`int` / `float` の値のサイズ (既定 4)。

### FindOptions.endian

`endian?: string | null`

`int` / `float` のエンディアン。

### FindOptions.signal

`signal?: AbortSignal | null`

キャンセル (JavaScript では `script.signal` や `new AbortController().signal` などの `AbortSignal`)。

### FindOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

進捗を受け取る関数。終わった割合 (0〜1) と説明。

## FormField

`form` の 1 つの項目。

### FormField.name

`name?: string`

結果のオブジェクトでの値のキー。

### FormField.label

`label?: string | null`

利用者に見せるラベル。

### FormField.type

`type?: string | null`

`"text"` (既定)、`"number"` (入力式)、`"offset"`、`"range"`、`"hex"`、`"choice"`。フォームでは `"checkbox"` も使える。

### FormField.default

`default?: unknown | null`

既定値。

### FormField.choices

`choices?: string[] | null`

`choice` の選択肢。

## FoundString

`strings` が見つけた文字列。

### FoundString.start

`readonly start: number`

開始オフセット。

### FoundString.length

`readonly length: number`

長さ (バイト)。

### FoundString.text

`readonly text: string`

文字列。

### FoundString.encoding

`readonly encoding: string`

文字コードの名前。

## HashAlgorithmInfo

ハッシュのアルゴリズム。

### HashAlgorithmInfo.id

`readonly id: string`

アルゴリズムの ID (例: `sha256`)。

### HashAlgorithmInfo.name

`readonly name: string`

表示名。

### HashAlgorithmInfo.bits

`readonly bits: number`

出力のビット数。

## HashResult

ハッシュの結果。

### HashResult.algorithm

`readonly algorithm: string`

アルゴリズムの ID。

### HashResult.hex

`readonly hex: string`

値 (小文字の Hex)。

### HashResult.bytes

`readonly bytes: Uint8Array`

値 (バイト列)。

## InspectorOptions

インスペクタの API のオプション。

### InspectorOptions.endian

`endian?: string | null`

`"le"` か `"be"` (既定はインスペクタの設定)。

## InspectorTypeInfo

データインスペクタの型。

### InspectorTypeInfo.id

`readonly id: string`

型の ID。

### InspectorTypeInfo.name

`readonly name: string`

表示名。

### InspectorTypeInfo.size

`readonly size: number`

サイズ (バイト。可変長は 0)。

## InspectorValue

インスペクタで解釈した値。

### InspectorValue.text

`readonly text: string`

インスペクタパネルに表示する文字列。

### InspectorValue.value

`readonly value: number | bigint | string | boolean | null | null`

生の値 (数値・bigint・文字列・真偽値。解釈できなければ null)。

## MessageOptions

`message` のオプション。

### MessageOptions.kind

`kind?: string | null`

`"info"` (既定)・`"warning"`・`"error"`。

### MessageOptions.modal

`modal?: boolean`

InfoBar でなくダイアログで表示する。

## NewDocumentOptions

`newDocument` のオプション。

### NewDocumentOptions.length

`length?: number`

初期の長さ (バイト)。

### NewDocumentOptions.fill

`fill?: BytePattern | null`

初期内容のパターン (既定は 0)。

## OpenOptions

`open` のオプション。

### OpenOptions.readOnly

`readOnly?: boolean`

読み取り専用で開く。

### OpenOptions.offset

`offset?: OffsetArg | null`

開いた後にカーソルを置くオフセット。

### OpenOptions.select

`select?: RangeArg | null`

開いた後に選択する範囲。

### OpenOptions.encoding

`encoding?: EncodingArg | null`

テキスト列の文字コード。

### OpenOptions.background

`background?: boolean`

タブをアクティブにしない。

## PickOptions

ファイル選択のオプション。

### PickOptions.filters

`filters?: string[] | null`

`"*.bin"` などの種類の絞り込み。

### PickOptions.suggestedName

`suggestedName?: string | null`

提案するファイル名 (保存のダイアログ)。

### PickOptions.title

`title?: string | null`

ダイアログのタイトル。

## ProcessOptions

`run` のオプション。

### ProcessOptions.timeout

`timeout?: number`

制限時間 (秒、既定 60)。

### ProcessOptions.workingDirectory

`workingDirectory?: string | null`

作業フォルダ。

### ProcessOptions.input

`input?: string | null`

標準入力に書く文字列。

### ProcessOptions.signal

`signal?: AbortSignal | null`

キャンセル (JavaScript では `script.signal` や `new AbortController().signal` などの `AbortSignal`)。

### ProcessOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

進捗を受け取る関数。終わった割合 (0〜1) と説明。

## ProcessResult

`run` の結果。

### ProcessResult.exitCode

`readonly exitCode: number`

終了コード。

### ProcessResult.stdout

`readonly stdout: string`

標準出力。

### ProcessResult.stderr

`readonly stderr: string`

標準エラー出力。

## PromptOptions

入力欄の定義。

### PromptOptions.type

`type?: string | null`

`"text"` (既定)、`"number"` (入力式)、`"offset"`、`"range"`、`"hex"`、`"choice"`。フォームでは `"checkbox"` も使える。

### PromptOptions.default

`default?: unknown | null`

既定値。

### PromptOptions.choices

`choices?: string[] | null`

`choice` の選択肢。

## ReadStringOptions

`readString` のオプション。

### ReadStringOptions.stopAtNull

`stopAtNull?: boolean`

最初の NUL 文字で止める (既定 true)。

## SearchResults

一括の検索の結果 (一致した範囲の並び)。上限の件数で打ち切った場合は `truncated` が true。

### SearchResults.truncated

`readonly truncated: boolean`

上限の件数で打ち切ったか。

### SearchResults.count

`readonly count: number`

件数。

## StringsOptions

`strings` のオプション。

### StringsOptions.minLength

`minLength?: number`

最小の文字数 (既定 4)。

### StringsOptions.encodings

`encodings?: EncodingArg[] | null`

探す文字コード (既定 ASCII と UTF-16LE)。

### StringsOptions.range

`range?: RangeArg | null`

この範囲だけを対象にする。

### StringsOptions.max

`max?: number`

結果の最大件数 (既定 1,000,000)。

### StringsOptions.signal

`signal?: AbortSignal | null`

キャンセル (JavaScript では `script.signal` や `new AbortController().signal` などの `AbortSignal`)。

### StringsOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

進捗を受け取る関数。終わった割合 (0〜1) と説明。

## TemplateExtensionOptions

`registerExtension` のオプション。

### TemplateExtensionOptions.point

`point?: string`

拡張点: `"format"`、`"read"`、`"write"`、`"pointerMap"`、`"section"`、`"function"` (既定)。

### TemplateExtensionOptions.params

`params?: string[] | null`

引数の型: `"integer"`、`"float"`、`"bool"`、`"string"`、`"bytes"`、`"pattern"`、`"any"`。

### TemplateExtensionOptions.returns

`returns?: string`

戻り値の型 (`params` と同じ名前。既定 `"any"`)。

## TemplateInfo

構造体テンプレート。

### TemplateInfo.name

`readonly name: string`

テンプレートの名前。

### TemplateInfo.path

`readonly path: string`

テンプレートのファイルのパス。

### TemplateInfo.extensions

`readonly extensions: string[]`

対応する拡張子。

## TransformOptions

`transform` のオプション。

### TransformOptions.unit

`unit?: number`

要素のサイズ: 1・2・4・8 バイト (既定 1)。

### TransformOptions.endian

`endian?: string | null`

`"le"` か `"be"` (既定 `"le"`)。

### TransformOptions.overflow

`overflow?: string | null`

`"wrap"` (既定) か `"saturate"`。

### TransformOptions.signal

`signal?: AbortSignal | null`

キャンセル (JavaScript では `script.signal` や `new AbortController().signal` などの `AbortSignal`)。

### TransformOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

進捗を受け取る関数。終わった割合 (0〜1) と説明。

## BytePattern

バイト列の引数。バイト列 (JavaScript では `Uint8Array` か数値の配列) か Hex 文字列 (例: `"DE AD"`)。一部の引数は `"random"` も受け付ける。

`type BytePattern = Uint8Array | number[] | string`

## ByteRange

バイトの範囲 (開始と長さ)。終了 (`end`) は含まない。

### ByteRange.start

`readonly start: number`

開始オフセット。

### ByteRange.length

`readonly length: number`

長さ (バイト)。

### ByteRange.end

`readonly end: number`

範囲の直後のオフセット。

## EncodingArg

文字コードの引数。コードページ番号 (例: `932`) か名前 (例: `"shift_jis"`、`"utf-16le"`)。

`type EncodingArg = number | string`

## HashAlgorithmSpec

ハッシュのアルゴリズム。ID (例: `"sha256"`) かカスタム CRC (`CustomCrc`)。

`type HashAlgorithmSpec = string | CustomCrc`

## OffsetArg

数値の引数 (オフセット・長さ)。数値か入力式の文字列 (例: `"end-0x10"`)。

`type OffsetArg = number | string`

## RangeArg

範囲の引数。`ByteRange` か入力式の文字列 (例: `"sel"`、`"0x100:0x20"` (開始:長さ)、`"0x100..0x120"` (開始..終了。終了は含まない))。

`type RangeArg = ByteRange | string`

## TransformOperand

データ演算のオペランド。数値 (JavaScript では `number` か `bigint`) か、繰り返しのキー (Hex 文字列。JavaScript では `hex` のパラメータの値などの `Uint8Array` も同じキーとして使える)。

`type TransformOperand = number | bigint | string | Uint8Array`

## 例外

- `ArgumentError`: 引数が不正。
- `CanceledError`: キャンセルされた。
- `IoError`: 読み書きに失敗した。
- `LimitError`: 上限 (量・件数・時間・メモリ) を超えた。
- `PermissionError`: 許可がない。
- `RangeError`: 範囲外。
- `ReadOnlyError`: 読み取り専用、または長さを変えられない。
