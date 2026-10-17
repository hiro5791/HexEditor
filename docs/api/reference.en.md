# Automation API reference

API version 1.0. Generated from HexEditor.Sdk (do not edit by hand). Names are the JavaScript names (C# uses PascalCase and the `Async` suffix for asynchronous methods).

## Globals

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

The whole application (AUTO-06). JavaScript: the global `app`. C#: the global `App`.

### App.version

`readonly version: string`

The application version.

### App.apiVersion

`readonly apiVersion: string`

The automation API version (for example `1.0`).

### App.language

`readonly language: string`

The UI language tag (for example `ja`).

### App.documents

`readonly documents: Document[]`

The open documents.

### App.activeDocument

`activeDocument: Document | null`

The active document, or null. Setting it activates that tab.

### App.commands

`readonly commands: Commands`

Runs application commands.

### App.inspector

`readonly inspector: Inspector`

The data inspector.

### App.text

`readonly text: Text`

Text encoding conversion.

### App.templates

`readonly templates: Templates`

Structure templates.

### App.hash

`readonly hash: Hash`

Hashes and checksums.

### App.clipboard

`readonly clipboard: Clipboard`

The clipboard.

### App.ui

`readonly ui: Ui`

Dialogs, progress and output.

### App.settings

`readonly settings: Settings`

Application settings.

### App.script

`readonly script: Script`

The running script.

### App.fs

`readonly fs: FileSystem`

Files (permission needed).

### App.net

`readonly net: Net`

Network (permission needed).

### App.process

`readonly process: ProcessRunner`

External programs (permission needed).

### App.plugins

`readonly plugins: Plugins`

Installed plugins and the objects they provide to scripts (AUTO-31).

### App.open

`open(path: string, options?: OpenOptions): Promise<Document>`

Opens a file. Returns at once regardless of the file size.

### App.openDisk

`openDisk(id: string, options?: OpenOptions): Promise<Document>`

Opens a disk or volume by its ID.

Permission: `disk`

### App.openProcess

`openProcess(pid: number, options?: OpenOptions): Promise<Document>`

Opens the memory of a process.

Permission: `disk`

### App.newDocument

`newDocument(options?: NewDocumentOptions): Document`

Creates a new document.

### App.on

`on(eventName: string, handler: (event: ScriptEventArgs) => void): EventSubscription`

Subscribes to an event: `documentOpened`, `documentClosed`, `activeDocumentChanged`, `beforeSave`, `afterSave` or `selectionChanged` (at most every 100 ms). A script with subscriptions keeps running until `stop` is called or the user stops it.

### App.stop

`stop(): void`

Ends the running script.

### App.exit

`exit(code: number): void`

Ends the script with an exit code for headless runs (0-255; use 64 or more for script-specific meanings).

## Bookmark

One bookmark.

### Bookmark.name

`readonly name: string`

Name.

### Bookmark.start

`readonly start: number`

Start offset.

### Bookmark.length

`readonly length: number`

Length.

### Bookmark.color

`readonly color: string`

Named color.

### Bookmark.comment

`readonly comment: string`

Comment.

### Bookmark.number

`readonly number: number | null`

The number of a numbered bookmark (1-9), or null.

## Bookmarks

The bookmarks of a document (AUTO-11). Enumerable like an array; bookmarks added by scripts are saved like the user's.

### Bookmarks.add

`add(bookmark: BookmarkSpec): Bookmark`

Adds a bookmark.

### Bookmarks.remove

`remove(bookmark: Bookmark): void`

Removes a bookmark.

### Bookmarks.clear

`clear(): void`

Removes all bookmarks.

### Bookmarks.find

`find(name: string): Bookmark | null`

Finds a bookmark by name, or null.

### Bookmarks.addFromResults

`addFromResults(results: ByteRange[], nameFormat?: string): number`

Adds one bookmark per result (at most 1,000,000). `{index}` in `nameFormat` becomes 1, 2, 3, ...

## Clipboard

The clipboard (AUTO-15).

### Clipboard.setText

`setText(text: string): void`

Puts text.

### Clipboard.setBytes

`setBytes(data: BytePattern): void`

Puts bytes (binary format and Hex text).

### Clipboard.getText

`getText(): string | null`

Reads text, or null.

Permission: `clipboard-read`

### Clipboard.getBytes

`getBytes(): Uint8Array | null`

Reads bytes, or null.

Permission: `clipboard-read`

### Clipboard.copyAs

`copyAs(document: Document, range: RangeArg, formatId: string, options?: CopyAsOptions): void`

Copies a range in a Copy As format (for example `hex`, `c-array`, `base64`).

### Clipboard.formatAs

`formatAs(document: Document, range: RangeArg, formatId: string, options?: CopyAsOptions): string`

Returns the Copy As text without using the clipboard.

## Commands

Runs application commands by ID (AUTO-06).

### Commands.execute

`execute(id: string, args?: Record<string, unknown>): Promise<unknown>`

Runs the command `id`. Commands that ask for confirmation take the answer in `args.confirm`; without it, interactive runs show the dialog and headless runs choose the safe answer (cancel).

### Commands.list

`list(): CommandInfo[]`

All commands (IDs, display names, arguments).

## Document

One open document (AUTO-07). Synchronous reads and writes are limited to 16 MB per call (AUTO-05); use `chunks` and the bulk APIs (AUTO-09) for more.

### Document.id

`readonly id: string`

Identifier unique during the session.

### Document.kind

`readonly kind: string`

`file`, `disk`, `volume`, `process` or `memory` (new).

### Document.path

`readonly path: string | null`

The path, or null for a new document.

### Document.name

`readonly name: string`

The display name.

### Document.length

`readonly length: number`

Current length in bytes.

### Document.isModified

`readonly isModified: boolean`

True when there are unsaved changes.

### Document.isReadOnly

`readonly isReadOnly: boolean`

True when the document is read-only.

### Document.canResize

`readonly canResize: boolean`

True when the length can change (false for disks).

### Document.baseAddress

`readonly baseAddress: number`

The address shown for offset 0.

### Document.sectorSize

`readonly sectorSize: number`

The sector size.

### Document.encoding

`readonly encoding: string`

The text column encoding name.

### Document.cursor

`cursor: number`

The cursor offset. Setting it moves the cursor (not recorded in the jump history; use `goTo` for that).

### Document.selection

`readonly selection: Selection`

The selection (AUTO-10).

### Document.activeColumn

`activeColumn: string`

`"hex"` or `"text"`. Setting it switches the active column.

### Document.bookmarks

`readonly bookmarks: Bookmarks`

Bookmarks (AUTO-11).

### Document.highlights

`readonly highlights: Highlights`

Temporary highlights removed when the script ends.

### Document.save

`save(options?: AsyncOptions): Promise<void>`

Saves with the same safe method as the editor.

### Document.saveAs

`saveAs(path: string, options?: AsyncOptions): Promise<void>`

Saves to another path.

### Document.close

`close(options?: CloseOptions): Promise<void>`

Closes the document.

### Document.undo

`undo(): void`

Undoes the last edit.

### Document.redo

`redo(): void`

Redoes the undone edit.

### Document.beginUndoGroup

`beginUndoGroup(name: string): void`

Starts one undo unit named `name`.

### Document.endUndoGroup

`endUndoGroup(): void`

Ends the undo unit started by `beginUndoGroup`.

### Document.goTo

`goTo(offset: OffsetArg): void`

Moves the cursor and records the jump history.

### Document.scrollTo

`scrollTo(offset: OffsetArg): void`

Scrolls the view without moving the cursor.

### Document.openRange

`openRange(range: RangeArg): Promise<Document>`

Opens the range in another tab as a read-only view.

### Document.readBytes

`readBytes(offset: OffsetArg, length: OffsetArg): Uint8Array`

Reads bytes (up to 16 MB). Bytes past the end are not returned (the result is shorter).

### Document.readByte

`readByte(offset: OffsetArg): number`

Reads one byte.

### Document.readInt

`readInt(offset: OffsetArg, type: string, endian?: string): number | bigint`

Reads an integer. `type`: `i8 u8 i16 u16 i24 u24 i32 u32 i64 u64`. `endian`: `"le"` or `"be"` (default: the document setting). JavaScript receives 64-bit values as `bigint`.

### Document.readFloat

`readFloat(offset: OffsetArg, type: string, endian?: string): number`

Reads a floating point value. `type`: `f16 f32 f64`.

### Document.readString

`readString(offset: OffsetArg, maxLength: OffsetArg, encoding?: EncodingArg | null, options?: ReadStringOptions): string`

Reads a string of at most `maxLength` bytes.

### Document.writeBytes

`writeBytes(offset: OffsetArg, data: BytePattern): void`

Overwrites bytes (the length does not change). Data past the end is appended when the document can resize, otherwise `RangeError`.

### Document.writeInt

`writeInt(offset: OffsetArg, type: string, value: number | bigint, endian?: string): void`

Writes an integer (arguments as in `readInt`).

### Document.writeFloat

`writeFloat(offset: OffsetArg, type: string, value: number, endian?: string): void`

Writes a floating point value.

### Document.writeString

`writeString(offset: OffsetArg, text: string, encoding?: EncodingArg | null): number`

Writes a string and returns the number of bytes written.

### Document.insertBytes

`insertBytes(offset: OffsetArg, data: BytePattern): void`

Inserts bytes.

### Document.delete

`delete(range: RangeArg): void`

Deletes a range.

### Document.fill

`fill(range: RangeArg, pattern: BytePattern, options?: FillOptions): void`

Fills a range with a pattern (bytes, Hex or `"random"`). Finishes within 100 ms regardless of the size.

### Document.resize

`resize(newLength: OffsetArg, fill?: BytePattern | null): void`

Changes the length (truncates or extends with `fill`).

### Document.transform

`transform(range: RangeArg, op: string, operand?: TransformOperand | null, options?: TransformOptions): Promise<void>`

Data operation over a range. `op`: `xor and or not add sub mul div shl shr rol ror neg swap reverse`.

### Document.findAll

`findAll(pattern: BytePattern, options?: FindOptions): Promise<SearchResults>`

Finds all matches (at most `options.max`).

### Document.find

`find(pattern: BytePattern, options?: FindOptions): Promise<ByteRange | null>`

Finds the first match, or null.

### Document.replaceAll

`replaceAll(pattern: BytePattern, replacement: BytePattern, options?: FindOptions): Promise<number>`

Replaces all matches and returns the count.

### Document.count

`count(pattern: BytePattern, options?: FindOptions): Promise<number>`

Counts the matches.

### Document.copyRange

`copyRange(source: RangeArg, destination: Document, destinationOffset: OffsetArg, options?: CopyRangeOptions): Promise<void>`

Copies a range to `destination` by reference (no data is duplicated).

### Document.exportRange

`exportRange(range: RangeArg, path: string, options?: ExportRangeOptions): Promise<void>`

Writes a range to a file (permission `file-write` or a path the user picked).

Permission: `file-write`

### Document.chunks

`chunks(range?: RangeArg | null, chunkSize?: number): AsyncIterable<DataChunk>`

Reads a range in chunks of `chunkSize` (default 1 MB, at most 16 MB). JavaScript: `for await (const { offset, data } of doc.chunks(range))`.

### Document.transformChunks

`transformChunks(range: RangeArg, transform: (data: Uint8Array, offset: number) => Uint8Array | number[], options?: AsyncOptions): Promise<void>`

Reads a range chunk by chunk and overwrites each chunk with what `transform` returns (same length).

### Document.histogram

`histogram(range?: RangeArg | null, options?: AsyncOptions): Promise<number[]>`

Counts each byte value 0-255.

### Document.entropy

`entropy(range?: RangeArg | null, options?: EntropyOptions): Promise<number | number[]>`

Shannon entropy in bits per byte; with `options.blockSize`, one value per block.

### Document.compare

`compare(other: Document, options?: CompareOptions): Promise<ByteRange[]>`

Compares with another document and returns the differing ranges.

### Document.strings

`strings(options?: StringsOptions): Promise<FoundString[]>`

Extracts strings.

### Document.applyTemplate

`applyTemplate(nameOrPath: string, options?: ApplyTemplateOptions): Promise<TemplateNode>`

Applies a structure template (AUTO-13) and returns the root of the result tree.

## EventSubscription

A subscription returned by `on`.

### EventSubscription.off

`off(): void`

Stops receiving the event.

## FileSystem

Files (AUTO-18). Needs `file-read` / `file-write`, except for paths the user picked and reading below the script folder.

### FileSystem.readBytes

`readBytes(path: string, offset?: number, length?: number): Uint8Array`

Reads up to 16 MB of a file.

Permission: `file-read`

### FileSystem.readText

`readText(path: string, encoding?: EncodingArg | null): string`

Reads a text file.

Permission: `file-read`

### FileSystem.writeBytes

`writeBytes(path: string, data: BytePattern): void`

Writes a file.

Permission: `file-write`

### FileSystem.writeText

`writeText(path: string, text: string, encoding?: EncodingArg | null): void`

Writes a text file.

Permission: `file-write`

### FileSystem.list

`list(folder: string, pattern?: string): string[]`

Lists the entries of a folder.

Permission: `file-read`

### FileSystem.exists

`exists(path: string): boolean`

True when the file or folder exists.

Permission: `file-read`

### FileSystem.stat

`stat(path: string): FileStat`

File information.

Permission: `file-read`

### FileSystem.delete

`delete(path: string): void`

Deletes a file.

Permission: `file-write`

### FileSystem.createFolder

`createFolder(path: string): void`

Creates a folder.

Permission: `file-write`

## Hash

Hashes and checksums (AUTO-14).

### Hash.algorithms

`readonly algorithms: HashAlgorithmInfo[]`

The available algorithms.

### Hash.compute

`compute(document: Document, range?: RangeArg | null, algorithms: HashAlgorithmSpec[], options?: AsyncOptions): Promise<HashResult[]>`

Computes one or more hashes over a range of a document, reading the data once.

### Hash.computeBytes

`computeBytes(data: BytePattern, algorithm: HashAlgorithmSpec): HashResult`

Hash of bytes (synchronous, up to 16 MB).

### Hash.computeFile

`computeFile(path: string, algorithms: HashAlgorithmSpec[], options?: AsyncOptions): Promise<HashResult[]>`

Hashes a file without opening it (permission `file-read` or a path the user picked).

Permission: `file-read`

## Highlights

Temporary highlights (AUTO-07).

### Highlights.add

`add(range: RangeArg, color?: string, label?: string): void`

Highlights a range with a named color.

### Highlights.clear

`clear(): void`

Removes all highlights of this script.

## Inspector

Data inspector types and value conversion (AUTO-12).

### Inspector.types

`readonly types: InspectorTypeInfo[]`

The available types, including plugin types.

### Inspector.decode

`decode(document: Document, offset: OffsetArg, typeId: string, options?: InspectorOptions): InspectorValue`

Decodes the value at `offset` as the inspector panel shows it.

### Inspector.encode

`encode(typeId: string, valueText: string, options?: InspectorOptions): Uint8Array`

Encodes a value text as the inspector does when editing.

### Inspector.show

`show(typeId: string): void`

Shows a type in the inspector panel.

### Inspector.hide

`hide(typeId: string): void`

Hides a type in the inspector panel.

## Net

Network access of scripts (AUTO-18, permission `network`).

### Net.fetch

`fetch(url: string, options?: FetchOptions): Promise<FetchResponse>`

HTTP(S) GET or POST. The response body is limited to 100 MB.

Permission: `network`

## PluginHandle

One plugin seen from scripts.

### PluginHandle.id

`readonly id: string`

The plugin ID.

### PluginHandle.name

`readonly name: string`

The display name.

### PluginHandle.version

`readonly version: string`

The version.

### PluginHandle.enabled

`readonly enabled: boolean`

True when the plugin is loaded and running.

### PluginHandle.api

`readonly api: any | null`

The object the plugin provides to scripts (`IScriptApiProvider`), or null.

## Plugins

Installed plugins (AUTO-31, AUTO-32).

### Plugins.list

`list(): PluginInfo[]`

All plugins with their state.

### Plugins.get

`get(id: string): PluginHandle | null`

A plugin by ID, or null when it is not installed.

## ProcessRunner

External programs (AUTO-18, permission `process`).

### ProcessRunner.run

`run(path: string, args: string[], options?: ProcessOptions): Promise<ProcessResult>`

Runs a program without a shell; each element of `args` is passed as one argument.

Permission: `process`

## Script

The running script (AUTO-17).

### Script.name

`readonly name: string`

Display name.

### Script.path

`readonly path: string | null`

File path, or null (console, unsaved editor).

### Script.args

`readonly args: Record<string, unknown>`

Parameter values (AUTO-26) and, for hooks, `event`.

### Script.signal

`readonly signal: AbortSignal`

Canceled when the user stops the script.

### Script.storage

`readonly storage: ScriptStorage`

Persistent storage of this script (1 MB).

## ScriptStorage

Persistent key-value storage of one script (AUTO-17).

### ScriptStorage.get

`get(key: string): unknown`

Reads a value, or null.

### ScriptStorage.set

`set(key: string, value?: unknown): void`

Stores a JSON-compatible value.

### ScriptStorage.remove

`remove(key: string): void`

Removes a value.

## Selection

The cursor selection including multi and rectangular selections (AUTO-10).

### Selection.start

`readonly start: number`

Start of the primary range.

### Selection.end

`readonly end: number`

End of the primary range (exclusive).

### Selection.length

`readonly length: number`

Length of the primary range.

### Selection.ranges

`readonly ranges: ByteRange[]`

All selected ranges; empty when nothing is selected.

### Selection.isEmpty

`readonly isEmpty: boolean`

True when nothing is selected.

### Selection.isRectangular

`readonly isRectangular: boolean`

True for a rectangular selection.

### Selection.set

`set(range: RangeArg): void`

Selects a range.

### Selection.add

`add(range: RangeArg): void`

Adds a range to the multi-selection.

### Selection.clear

`clear(): void`

Clears the selection.

### Selection.setRectangle

`setRectangle(startOffset: OffsetArg, rows: number, columns: number): void`

Selects a rectangle.

### Selection.fromResults

`fromResults(results: ByteRange[]): void`

Turns results of a bulk API into a multi-selection (at most 1,000,000).

## Settings

Application settings (AUTO-17).

### Settings.get

`get(key: string): unknown`

Reads a setting (keys of the settings reference).

### Settings.set

`set(key: string, value?: unknown): void`

Changes a setting; takes effect at once.

Permission: `settings`

## TemplateNode

One node of a template result tree (AUTO-13).

### TemplateNode.name

`readonly name: string`

Name.

### TemplateNode.type

`readonly type: string`

Type name.

### TemplateNode.start

`readonly start: number`

Start offset.

### TemplateNode.length

`readonly length: number`

Length.

### TemplateNode.value

`value: unknown | null`

The value (number, string ...). Setting it writes the document as editing in the template tree does (undoable).

### TemplateNode.displayValue

`readonly displayValue: string`

The value formatted by the template.

### TemplateNode.children

`readonly children: TemplateNode[]`

The child nodes.

### TemplateNode.get

`get(path: string): TemplateNode | null`

Finds a descendant by a dotted path (for example `"header.e_lfanew"`), or null.

### TemplateNode.toJSON

`toJSON(): unknown`

The node as JSON (the same form as the command line `template` output).

## Templates

Structure templates (AUTO-13).

### Templates.list

`list(): TemplateInfo[]`

The available templates.

### Templates.detect

`detect(document: Document): TemplateInfo[]`

Templates that match the document by the same rules as automatic application.

### Templates.registerExtension

`registerExtension(name: string, fn: (...args: any[]) => any, options?: TemplateExtensionOptions): EventSubscription`

Registers a function that templates can call (template extension, TPL-19). The script stays resident while it is registered; `off` or the end of the script removes it.

## Text

Text encoding conversion (AUTO-12).

### Text.encodings

`readonly encodings: EncodingInfo[]`

All encodings the application supports.

### Text.encode

`encode(text: string, encoding: EncodingArg): Uint8Array`

Encodes a string.

### Text.decode

`decode(bytes: BytePattern, encoding: EncodingArg, options?: DecodeOptions): string`

Decodes bytes.

## Ui

Dialogs, progress and output (AUTO-16).

### Ui.progress

`readonly progress: UiProgress`

Progress in the operation center.

### Ui.message

`message(text: string, options?: MessageOptions): Promise<void>`

Shows a message (InfoBar by default, a dialog with `modal`).

### Ui.confirm

`confirm(text: string, buttons: string[]): Promise<number>`

Asks the user to choose a button; returns its index, or -1 for Esc.

### Ui.prompt

`prompt(label: string, options?: PromptOptions): Promise<unknown>`

Asks for one value; null when canceled.

### Ui.form

`form(fields: FormField[]): Promise<Record<string, unknown> | null>`

Shows a form; returns the values by field name, or null when canceled.

### Ui.pickFile

`pickFile(options?: PickOptions): Promise<string>`

Lets the user pick a file to read; the path is readable during this run without permission.

### Ui.pickFiles

`pickFiles(options?: PickOptions): Promise<string[]>`

Lets the user pick files to read.

### Ui.saveFile

`saveFile(options?: PickOptions): Promise<string>`

Lets the user pick a file to write.

### Ui.pickFolder

`pickFolder(options?: PickOptions): Promise<string>`

Lets the user pick a folder.

### Ui.status

`status(text: string): void`

Shows a text in the status bar for 5 seconds.

### Ui.showResults

`showResults(title: string, rows: Record<string, unknown>[], columns?: string[]): void`

Shows rows in the common results panel. Rows with `start` and `length` jump to the offset when clicked.

### Ui.output

`output(text: string, kind?: string): void`

Writes a line to the output panel. `kind`: `info`, `warning`, `error`.

## UiProgress

Operation center progress of the running script.

### UiProgress.report

`report(fraction: number, text?: string): void`

Reports the fraction done (0-1) and a description.

## ScriptEventArgs

The argument passed to event handlers (`on`).

### ScriptEventArgs.name

`readonly name: string`

The event name (for example `beforeSave`).

### ScriptEventArgs.document

`readonly document: Document | null`

The document the event is about, if any.

### ScriptEventArgs.cancel

`cancel(reason: string): void`

Cancels the operation (`beforeSave` only). The reason is shown to the user.

## ApplyTemplateOptions

Options of `applyTemplate`.

### ApplyTemplateOptions.offset

`offset?: OffsetArg | null`

Offset to apply the template at (default 0).

### ApplyTemplateOptions.show

`show?: boolean`

Show the result in the template panel (default true).

### ApplyTemplateOptions.signal

`signal?: AbortSignal | null`

Cancellation (JavaScript: an `AbortSignal` such as `script.signal` or `new AbortController().signal`).

### ApplyTemplateOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

Receives the progress: the fraction done (0-1) and a description.

## AsyncOptions

Options every asynchronous API accepts (AUTO-05). When omitted, the call follows the cancellation of the whole script and reports its progress to the operation center.

### AsyncOptions.signal

`signal?: AbortSignal | null`

Cancellation (JavaScript: an `AbortSignal` such as `script.signal` or `new AbortController().signal`).

### AsyncOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

Receives the progress: the fraction done (0-1) and a description.

## BookmarkSpec

A new bookmark for `add`.

### BookmarkSpec.start

`start?: OffsetArg`

Start offset.

### BookmarkSpec.length

`length?: number`

Length (default 1).

### BookmarkSpec.name

`name?: string | null`

Name.

### BookmarkSpec.color

`color?: string | null`

A named color of the bookmark palette (for example `"red"`).

### BookmarkSpec.comment

`comment?: string | null`

Comment.

## CloseOptions

Options of `close`.

### CloseOptions.discard

`discard?: boolean`

Discard unsaved changes without asking.

## CommandInfo

A command of the application.

### CommandInfo.id

`readonly id: string`

The fixed English identifier (for example `edit.fill`).

### CommandInfo.name

`readonly name: string`

The display name in the UI language.

### CommandInfo.category

`readonly category: string`

The category.

### CommandInfo.arguments

`readonly arguments: string[]`

Names of the arguments the command accepts.

## CompareOptions

Options of `compare`.

### CompareOptions.max

`max?: number`

Maximum number of difference ranges (default 1,000,000).

### CompareOptions.signal

`signal?: AbortSignal | null`

Cancellation (JavaScript: an `AbortSignal` such as `script.signal` or `new AbortController().signal`).

### CompareOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

Receives the progress: the fraction done (0-1) and a description.

## CopyAsOptions

Options of `copyAs` and `formatAs`.

### CopyAsOptions.bytesPerLine

`bytesPerLine?: number`

Bytes per line (0: the Copy As setting).

### CopyAsOptions.uppercase

`uppercase?: boolean | null`

Uppercase Hex digits (null: the Copy As setting).

## CopyRangeOptions

Options of `copyRange`.

### CopyRangeOptions.insert

`insert?: boolean`

Insert instead of overwriting.

### CopyRangeOptions.signal

`signal?: AbortSignal | null`

Cancellation (JavaScript: an `AbortSignal` such as `script.signal` or `new AbortController().signal`).

### CopyRangeOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

Receives the progress: the fraction done (0-1) and a description.

## CustomCrc

Custom CRC parameters (the Rocksoft model). `id` is always `"crc"`.

### CustomCrc.id

`id?: string`

Always `"crc"`.

### CustomCrc.width

`width?: number`

Width in bits (1-64).

### CustomCrc.poly

`poly?: number`

Polynomial.

### CustomCrc.init

`init?: number`

Initial value.

### CustomCrc.refIn

`refIn?: boolean`

Reflect input bytes.

### CustomCrc.refOut

`refOut?: boolean`

Reflect the output.

### CustomCrc.xorOut

`xorOut?: number`

Value XORed with the output.

## DataChunk

One chunk read by `chunks`.

### DataChunk.offset

`readonly offset: number`

Offset of the first byte.

### DataChunk.data

`readonly data: Uint8Array`

The bytes.

## DecodeOptions

Options of `decode`.

### DecodeOptions.invalid

`invalid?: string | null`

`"replace"` (default; invalid bytes become U+FFFD) or `"error"` (throw `ArgumentError`).

## EncodingInfo

A text encoding the application supports.

### EncodingInfo.codePage

`readonly codePage: number`

Code page number.

### EncodingInfo.name

`readonly name: string`

Name (for example `shift_jis`).

### EncodingInfo.displayName

`readonly displayName: string`

Display name.

## EntropyOptions

Options of `entropy`.

### EntropyOptions.blockSize

`blockSize?: number`

When set, returns one value per block of this size.

### EntropyOptions.signal

`signal?: AbortSignal | null`

Cancellation (JavaScript: an `AbortSignal` such as `script.signal` or `new AbortController().signal`).

### EntropyOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

Receives the progress: the fraction done (0-1) and a description.

## ExportRangeOptions

Options of `exportRange`.

### ExportRangeOptions.overwrite

`overwrite?: boolean`

Overwrite an existing file (default false).

### ExportRangeOptions.format

`format?: string | null`

Export format ID, the same as `hexed convert --to` (for example `ihex`, `srec`, `base64`, `hextext`, `c`; default `bin`).

### ExportRangeOptions.formatOptions

`formatOptions?: Record<string, string> | null`

Options of the format, by the names of the export dialog (for example `recordLength`, `startAddress`, `lineLength`).

### ExportRangeOptions.signal

`signal?: AbortSignal | null`

Cancellation (JavaScript: an `AbortSignal` such as `script.signal` or `new AbortController().signal`).

### ExportRangeOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

Receives the progress: the fraction done (0-1) and a description.

## FetchOptions

Options of `fetch`.

### FetchOptions.method

`method?: string | null`

`"GET"` (default) or `"POST"`.

### FetchOptions.headers

`headers?: Record<string, string> | null`

Request headers.

### FetchOptions.body

`body?: BytePattern | null`

Request body (POST).

### FetchOptions.bodyText

`bodyText?: string | null`

Request body as text (POST).

### FetchOptions.responseType

`responseType?: string | null`

`"text"` (default) or `"bytes"`.

### FetchOptions.signal

`signal?: AbortSignal | null`

Cancellation (JavaScript: an `AbortSignal` such as `script.signal` or `new AbortController().signal`).

### FetchOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

Receives the progress: the fraction done (0-1) and a description.

## FetchResponse

The response of `fetch`.

### FetchResponse.status

`readonly status: number`

HTTP status code.

### FetchResponse.headers

`readonly headers: Record<string, string>`

Response headers.

### FetchResponse.body

`readonly body: Uint8Array | string`

The body (`Uint8Array` or string, by `responseType`).

## FileStat

File information.

### FileStat.size

`readonly size: number`

Size in bytes (0 for folders).

### FileStat.modified

`readonly modified: string`

Last write time (ISO 8601, UTC).

### FileStat.isFolder

`readonly isFolder: boolean`

True for folders.

## FillOptions

Options of `fill`.

### FillOptions.insert

`insert?: boolean`

Insert instead of overwriting.

## FindOptions

Options of the search APIs.

### FindOptions.type

`type?: string | null`

How a string pattern is read: `"hex"` (default; wildcards allowed), `"text"`, `"regex"`, `"int"` or `"float"`. Byte patterns are searched as they are.

### FindOptions.encoding

`encoding?: EncodingArg | null`

Text encoding for `text` and `regex`.

### FindOptions.caseSensitive

`caseSensitive?: boolean`

Match case (default false).

### FindOptions.range

`range?: RangeArg | null`

Search only this range.

### FindOptions.alignment

`alignment?: number`

Only matches whose start is a multiple of this value.

### FindOptions.max

`max?: number`

Maximum number of results (default 1,000,000).

### FindOptions.from

`from?: OffsetArg | null`

Start offset of `find`.

### FindOptions.backward

`backward?: boolean`

Search backward (`find`).

### FindOptions.size

`size?: number`

Integer or float size in bytes for `int` / `float` (default 4).

### FindOptions.endian

`endian?: string | null`

`"le"` or `"be"` for `int` / `float`.

### FindOptions.signal

`signal?: AbortSignal | null`

Cancellation (JavaScript: an `AbortSignal` such as `script.signal` or `new AbortController().signal`).

### FindOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

Receives the progress: the fraction done (0-1) and a description.

## FormField

One field of `form`.

### FormField.name

`name?: string`

The key of the value in the result.

### FormField.label

`label?: string | null`

The label shown to the user.

### FormField.type

`type?: string | null`

`"text"` (default), `"number"` (input expressions), `"offset"`, `"range"`, `"hex"`, `"choice"` and, in forms, `"checkbox"`.

### FormField.default

`default?: unknown | null`

The default value.

### FormField.choices

`choices?: string[] | null`

The choices of a `choice` field.

## FoundString

A string found by `strings`.

### FoundString.start

`readonly start: number`

Start offset.

### FoundString.length

`readonly length: number`

Length in bytes.

### FoundString.text

`readonly text: string`

The text.

### FoundString.encoding

`readonly encoding: string`

The encoding name.

## HashAlgorithmInfo

A hash algorithm.

### HashAlgorithmInfo.id

`readonly id: string`

Algorithm ID (for example `sha256`).

### HashAlgorithmInfo.name

`readonly name: string`

Display name.

### HashAlgorithmInfo.bits

`readonly bits: number`

Output size in bits.

## HashResult

A hash result.

### HashResult.algorithm

`readonly algorithm: string`

Algorithm ID.

### HashResult.hex

`readonly hex: string`

The value as lowercase Hex.

### HashResult.bytes

`readonly bytes: Uint8Array`

The value as bytes.

## InspectorOptions

Options of the inspector APIs.

### InspectorOptions.endian

`endian?: string | null`

`"le"` or `"be"` (default: the inspector setting).

## InspectorTypeInfo

A data inspector type.

### InspectorTypeInfo.id

`readonly id: string`

Type ID.

### InspectorTypeInfo.name

`readonly name: string`

Display name.

### InspectorTypeInfo.size

`readonly size: number`

Size in bytes (0 when variable).

## InspectorValue

A decoded inspector value.

### InspectorValue.text

`readonly text: string`

The text shown in the inspector panel.

### InspectorValue.value

`readonly value: number | bigint | string | boolean | null | null`

The raw value (number, bigint, string or boolean; null when it cannot be decoded).

## MessageOptions

Options of `message`.

### MessageOptions.kind

`kind?: string | null`

`"info"` (default), `"warning"` or `"error"`.

### MessageOptions.modal

`modal?: boolean`

Show a dialog instead of an InfoBar.

## NewDocumentOptions

Options of `newDocument`.

### NewDocumentOptions.length

`length?: number`

Initial length in bytes.

### NewDocumentOptions.fill

`fill?: BytePattern | null`

Pattern for the initial content (default zero bytes).

## OpenOptions

Options of `open`.

### OpenOptions.readOnly

`readOnly?: boolean`

Open read-only.

### OpenOptions.offset

`offset?: OffsetArg | null`

Move the cursor to this offset after opening.

### OpenOptions.select

`select?: RangeArg | null`

Select this range after opening.

### OpenOptions.encoding

`encoding?: EncodingArg | null`

The text column encoding.

### OpenOptions.background

`background?: boolean`

Do not activate the new tab.

## PickOptions

Options of the file pickers.

### PickOptions.filters

`filters?: string[] | null`

File type filters such as `"*.bin"`.

### PickOptions.suggestedName

`suggestedName?: string | null`

Suggested file name (save dialog).

### PickOptions.title

`title?: string | null`

The dialog title.

## PluginInfo

A plugin and its state.

### PluginInfo.id

`readonly id: string`

Plugin ID.

### PluginInfo.name

`readonly name: string`

Display name.

### PluginInfo.version

`readonly version: string`

Version.

### PluginInfo.state

`readonly state: string`

`enabled`, `disabled`, `failed`, `suspended` or `notLoaded`.

### PluginInfo.reason

`readonly reason: string | null`

Why it is not enabled, or null.

## ProcessOptions

Options of `run`.

### ProcessOptions.timeout

`timeout?: number`

Time limit in seconds (default 60).

### ProcessOptions.workingDirectory

`workingDirectory?: string | null`

The working folder.

### ProcessOptions.input

`input?: string | null`

Text written to the standard input.

### ProcessOptions.signal

`signal?: AbortSignal | null`

Cancellation (JavaScript: an `AbortSignal` such as `script.signal` or `new AbortController().signal`).

### ProcessOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

Receives the progress: the fraction done (0-1) and a description.

## ProcessResult

The result of `run`.

### ProcessResult.exitCode

`readonly exitCode: number`

Exit code.

### ProcessResult.stdout

`readonly stdout: string`

Standard output.

### ProcessResult.stderr

`readonly stderr: string`

Standard error.

## PromptOptions

Options of `prompt` and one field of `form`.

### PromptOptions.type

`type?: string | null`

`"text"` (default), `"number"` (input expressions), `"offset"`, `"range"`, `"hex"`, `"choice"` and, in forms, `"checkbox"`.

### PromptOptions.default

`default?: unknown | null`

The default value.

### PromptOptions.choices

`choices?: string[] | null`

The choices of a `choice` field.

## ReadStringOptions

Options of `readString`.

### ReadStringOptions.stopAtNull

`stopAtNull?: boolean`

Stop at the first NUL character (default true).

## SearchResults

The results of a bulk search: the matched ranges in order. `truncated` is true when the search stopped at the maximum count.

### SearchResults.truncated

`readonly truncated: boolean`

True when the results were cut at the maximum count.

### SearchResults.count

`readonly count: number`

The number of results.

## StringsOptions

Options of `strings`.

### StringsOptions.minLength

`minLength?: number`

Minimum length in characters (default 4).

### StringsOptions.encodings

`encodings?: EncodingArg[] | null`

Encodings to look for (default ASCII and UTF-16LE).

### StringsOptions.range

`range?: RangeArg | null`

Search only this range.

### StringsOptions.max

`max?: number`

Maximum number of results (default 1,000,000).

### StringsOptions.signal

`signal?: AbortSignal | null`

Cancellation (JavaScript: an `AbortSignal` such as `script.signal` or `new AbortController().signal`).

### StringsOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

Receives the progress: the fraction done (0-1) and a description.

## TemplateExtensionOptions

Options of `registerExtension`.

### TemplateExtensionOptions.point

`point?: string`

Extension point: `"format"`, `"read"`, `"write"`, `"pointerMap"`, `"section"` or `"function"` (default).

### TemplateExtensionOptions.params

`params?: string[] | null`

Parameter types: `"integer"`, `"float"`, `"bool"`, `"string"`, `"bytes"`, `"pattern"` or `"any"`.

### TemplateExtensionOptions.returns

`returns?: string`

Return type (the same names as `params`; default `"any"`).

## TemplateInfo

A structure template.

### TemplateInfo.name

`readonly name: string`

Template name.

### TemplateInfo.path

`readonly path: string`

Template file path.

### TemplateInfo.extensions

`readonly extensions: string[]`

File extensions the template is for.

## TransformOptions

Options of `transform`.

### TransformOptions.unit

`unit?: number`

Element size: 1, 2, 4 or 8 bytes (default 1).

### TransformOptions.endian

`endian?: string | null`

`"le"` or `"be"` (default `"le"`).

### TransformOptions.overflow

`overflow?: string | null`

`"wrap"` (default) or `"saturate"`.

### TransformOptions.signal

`signal?: AbortSignal | null`

Cancellation (JavaScript: an `AbortSignal` such as `script.signal` or `new AbortController().signal`).

### TransformOptions.progress

`progress?: (fraction: number, text?: string) => void | null`

Receives the progress: the fraction done (0-1) and a description.

## BytePattern

A byte sequence argument: bytes (JavaScript: `Uint8Array` or an array of numbers) or a Hex string (for example `"DE AD"`). Some arguments also accept the word `"random"`.

`type BytePattern = Uint8Array | number[] | string`

## ByteRange

A byte range: start offset and length. The end (`end`) is exclusive.

### ByteRange.start

`readonly start: number`

Start offset.

### ByteRange.length

`readonly length: number`

Length in bytes.

### ByteRange.end

`readonly end: number`

The offset just after the range.

## EncodingArg

A text encoding argument: a code page number (for example `932`) or a name (for example `"shift_jis"`, `"utf-16le"`).

`type EncodingArg = number | string`

## HashAlgorithmSpec

A hash algorithm: an ID (for example `"sha256"`) or a custom CRC (`CustomCrc`).

`type HashAlgorithmSpec = string | CustomCrc`

## OffsetArg

A numeric argument (offset, length): either a number or a text in the input expression syntax (for example `"end-0x10"`).

`type OffsetArg = number | string`

## RangeArg

A range argument: either a `ByteRange` or a text in the input expression syntax (for example `"sel"`, `"0x100:0x20"` (start:length) or `"0x100..0x120"` (start..end, end exclusive)).

`type RangeArg = ByteRange | string`

## TransformOperand

The operand of a data operation: a number (JavaScript: `number` or `bigint`) or a repeating key as a Hex string (JavaScript: a `Uint8Array`, such as the value of a `hex` script parameter, is the same key).

`type TransformOperand = number | bigint | string | Uint8Array`

## Errors

- `ArgumentError`: An argument is invalid.
- `CanceledError`: The operation was canceled.
- `IoError`: Reading or writing failed.
- `LimitError`: A limit (size, count, time, memory) was exceeded.
- `PermissionError`: The script has no permission for the operation.
- `RangeError`: An offset or range is out of bounds.
- `ReadOnlyError`: The document is read-only or cannot change its length.
