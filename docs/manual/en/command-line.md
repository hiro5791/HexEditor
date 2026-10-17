# Command line (hexed)

This page shows how to use HexEditor from the command line. You can open a file at a given position, compute hashes, compare two files, run scripts without a window, and more. It is handy for batch files, PowerShell and build steps.

## Opening files from the command line {#open-from-the-command-line}

HexEditor has two executable files:

- `HexEditor.exe`: the app with a window. It accepts only the arguments for opening files.
- `hexed.exe`: a small console program. It accepts every command and prints the results to the console (standard output).

Both can open files:

```text
hexed data.bin
hexed open data.bin --offset 0x1000
```

`hexed filename` is the same as `hexed open filename`. If HexEditor is already running, the file opens as a tab in that window. Relative paths are resolved from the folder where you run the command.

### How to call hexed {#how-to-call-hexed}

How you call `hexed` depends on the edition:

| Edition | How to call it |
| --- | --- |
| Microsoft Store | Call `hexed` from any folder |
| Installer | The installer adds the app folder to your user PATH, so you can call `hexed`. You can turn this off in the settings |
| Portable | The PATH is not changed. Call `hexed.exe` in the extracted folder by its full path, or add the folder to your PATH yourself |

### Options for opening {#open-options}

| Option | What it does |
| --- | --- |
| `--offset position`, `-g position` | Puts the cursor at that position (for example `0x1000`, `end-16`) |
| `--select range` | Selects the range (`start:length` or `start..end`) |
| `--readonly`, `-r` | Opens read-only |
| `--disk number-or-path` | Opens a physical disk (for example `--disk 0`, `--disk \\.\PhysicalDrive1`) |
| `--volume drive` | Opens a volume (for example `--volume C:`) |
| `--process PID-or-name` | Opens the memory of a process |
| `--encoding name` | Character encoding of the text column |
| `--template name-or-path` | Applies a template after opening |
| `--compare path1 path2` | Opens two files in the compare view |
| `--run script` | Runs a script after opening |
| `--new-window` | Opens in a new window |
| `--new-document` | Opens a new empty document |
| `--open-disk` | Shows the Open Disk dialog |
| `--new-instance` | Starts a separate process instead of using the running one |
| `--wait`, `-w` | Does not return until all the opened documents are closed (`hexed` only) |
| `--safe-mode` | Starts in safe mode ([Safe mode](plugins.md#safe-mode)) |
| `--ui-lang language` | Starts with the given display language (for example `--ui-lang ja`) |

`--template` and `--run` run after the file is opened. `--write-block` (write protection for the whole app) is not available in this version.

Disks and processes opened from the command line are read-only. If you need to write, allow writing after opening. If administrator rights are needed, the usual steps and confirmations apply. See [Administrator rights](opening-files.md#administrator).

`--wait` is useful when another tool calls HexEditor as its "editor". `hexed` does not end until you close the tab.

If you give a path that does not exist, `hexed` ends with exit code 3. If an `--offset` expression is not valid, it ends with exit code 2.

## The hexed command {#hexed}

The syntax of `hexed` is:

```text
hexed <command> [arguments] [options]
```

The commands are:

| Command | What it does |
| --- | --- |
| `open` | Opens in the app with a window |
| `hash` | Computes and verifies hashes and checksums |
| `dump` | Prints a hex dump |
| `compare` | Compares two files |
| `run` | Runs a script without a window |
| `template` | Applies a structure template and prints the result |
| `patch` | Applies a patch |
| `convert` | Converts between formats |
| `find` | Searches and prints the positions of matches |

Every command except `open` works on its own, without showing a window. HexEditor does not need to be running. Even huge files are handled a little at a time, so memory does not keep growing.

### Common options {#common-options}

| Option | What it does |
| --- | --- |
| `--help`, `-h` | Shows how to use it (`hexed <command> --help` explains that command) |
| `--version` | Shows the version |
| `--quiet`, `-q` | Prints nothing but the result |
| `--format text` or `json` | Output format (default `text`) |
| `--output path`, `-o path` | Writes the result to a file instead of the screen |
| `--lang language` | Language of the messages (for example `en`) |
| `--no-color` | Does not use colors |
| `--unregister` | Removes the registrations made on this PC (context menu, file associations, jump list, PATH, temporary folder) |

For `patch`, `-o` means the output file. `--format` also means the kind of patch for `patch`.

### Writing positions and ranges {#expressions}

Options that take an offset, a length or a range accept hexadecimal such as `0x1F00`, decimal numbers, and expressions such as `end-16`. A number without a prefix is decimal.

Write a range as `start:length` or `start..end`. The end of `start..end` is not included. `end` is the length of the file. For example, the last 32 bytes are `end-32:32`.

In PowerShell, `$` is treated as a variable. Put values that contain `$` in single quotes, for example `'$1F00'`.

### How output works {#output}

- Results (hash values, dumps, JSON and so on) go to standard output, as UTF-8. Progress, warnings and errors go to standard error. So when you redirect with `> out.txt`, only the result goes into the file.
- Messages are translated to the display language. The format of results and the JSON keys are not translated, because machines read them.
- The output of `--format json` contains `"schema": "hexeditor.command-name/1"`.
- In a terminal, the progress of a long task is shown on one line.
- Press <kbd>Ctrl</kbd>+<kbd>C</kbd> to cancel. The command ends with exit code 6 and an unfinished output file is deleted.

## Hashes and comparing {#hash-and-compare}

### Compute hashes {#hash}

```text
hexed hash file.bin
hexed hash file.bin -a sha256,md5
hexed hash *.bin --format gnu
hexed hash image.iso --range 0:1048576
```

- `-a` or `--algorithm` takes algorithm IDs separated by commas. The default is `sha256`. `--algorithm list` prints the available IDs. Even with several algorithms, the file is read only once.
- Paths can use wildcards such as `*.bin`, and can be folders. Add `--recurse` (or `-r`) to include subfolders. A path of `-` reads standard input.
- `--range` hashes only part of the file.
- `--format` chooses the shape of the output.

| Format | Output |
| --- | --- |
| `text` (default) | `algorithm  value  path` |
| `gnu` | `value  path` (the same as `sha256sum`, so it can be used as a checksum file as it is) |
| `bsd` | `SHA256 (path) = value` |
| `json` | An array with one object per file |

### Verify with a checksum file {#hash-check}

```text
hexed hash --check SHA256SUMS
```

This reads a checksum file (`.sha256`, `.md5`, `SHA256SUMS`, `.sfv` and so on), checks each file, and prints `OK`, `FAILED` or `MISSING`. The format is detected automatically. If any file does not match, the exit code is 1.

### Compare two files {#compare}

```text
hexed compare a.bin b.bin
hexed compare a.bin b.bin --brief
hexed compare a.bin b.bin --mode resync --format json
```

- The default (`--mode byte`) compares the bytes at the same positions. `--mode resync` allows for insertions and deletions in between.
- Each difference shows its kind (changed, inserted, deleted), both offsets and the length. A short difference (16 bytes or less) also shows both hex values. At the end you see the number of differences and the total bytes.
- `--brief` prints one line, same or different, and stops at the first difference. Use it to check huge files quickly.
- `--range1` and `--range2` choose the range to compare in each file.
- `--max` limits the number of differences printed (default 1,000). The rest are only counted.

The exit code is 0 if the files are the same and 1 if they differ.

## Hex dump {#dump}

```text
hexed dump file.bin
hexed dump file.bin --range 0x100:0x80 --width 16
hexed dump file.bin --style xxd
hexed dump file.bin --range end-32:32
```

This prints a hex dump of a file or a range. Memory use stays constant, even for huge files.

| Option | What it does |
| --- | --- |
| `--range range` | The range to print (default: the whole file) |
| `--width n` | Bytes per line (default 16) |
| `--group n` | Bytes per group (0 for no groups) |
| `--encoding name` | Character encoding of the text column (default ASCII) |
| `--base-address expression` | The address shown for the first byte (default 0) |
| `--style style` | The output style |
| `--squeeze` | Prints `*` for lines that repeat the previous line |

`--style` can be `default`, `xxd` (compatible with xxd), `hexdump-c` (compatible with `hexdump -C`), `plain` (hex only), `base64`, and source-code styles such as `c`, `csharp` and `python`. If the range is outside the file, the exit code is 2.

## Templates and JSON output {#templates-and-json}

```text
hexed template image.png --template png --format json
hexed template unknown.bin --auto
hexed template app.exe --template pe --fields header.e_lfanew,sections[*].name
```

This applies a [structure template](templates.md) to a file and prints the result.

- `--template name-or-path` chooses the template. `--auto` picks one with the same rules as [automatic selection](templates.md#auto-apply) when you open a file in the app. If none fits, the exit code is 1.
- `--offset expression` (or `-g`) sets where the structure starts (default 0).
- `--format` is `json`, `csv` or `text` (the default is `text`, an indented tree). The structure of `json` is the same as in [Exporting results](templates.md#export-results) in the app.
- `--fields path,...` limits the output to certain fields.
- `--depth n` limits the depth of the printed tree.

If the template has an error, a message with the line and column appears and the exit code is 4.

## Conversion and search {#convert-and-search}

### Convert formats {#convert}

```text
hexed convert firmware.hex firmware.bin
hexed convert firmware.bin firmware.hex --to ihex --record-length 32
```

This converts between the import and export formats. The input and output formats are detected from the extension and the contents. You can also set them with `--from` and `--to`.

- The formats include `bin`, `ihex`, `srec`, `base64`, `base32`, `ascii85`, `uu`, `qp`, `url`, `hextext`, `dectext`, `c`, `cpp`, `csharp`, `java`, `js`, `python`, `rust`, `go`, `pascal` and `vbnet`.
- If the checksum of an Intel HEX or S-record file is wrong, the command ends with exit code 8. Add `--ignore-checksum` to read it anyway.
- `--fill value` sets the byte that fills gaps (default `FF`). `--address-mode` sets the addressing mode.
- Options for a format have the names of the fields in the [Import and export](import-export.md) screens, in lowercase with hyphens (for example `--record-length 32`, `--bytes-per-line 16`).

### Search {#find}

```text
hexed find a.bin --hex "DE AD ?? EF"
hexed find *.log --text error --case-sensitive
hexed find data.bin --regex "ID[0-9]+" --format json
```

This searches files and prints `path  offset  length` for each match.

- `--hex` is a byte pattern. `??` is any one byte.
- `--text` is a string and `--regex` is a regular expression. `--encoding` sets the character encoding (default UTF-8).
- `--case-sensitive` tells upper and lower case apart.
- `--range` limits the range and `--max` limits the number of matches. For a folder, add `--recurse` to search inside it.

The exit code is 0 if there is a match and 1 if there is none.

### Apply a patch {#patch}

```text
hexed patch app.bin fix.ips -o app-fixed.bin
hexed patch app.bin fix.ips --in-place --verify-before sha256:ab12...
```

This applies a patch file to a file. This version can apply IPS and IPS32 patches.

- You must give either `-o path` (the original is not changed) or `--in-place` (the original is replaced). If you give neither, the exit code is 2.
- `--in-place` writes a temporary file first and then replaces the original, just like saving in the app.
- `--verify-before algorithm:value` checks the hash of the target before applying. If it differs, nothing is written and the exit code is 1. `--verify-after` checks the hash of the result.
- `--dry-run` applies nothing and prints the number of patch records and the number of bytes that would change.

A broken patch gives exit code 8.

## Running scripts {#run-scripts}

```text
hexed run xor.js a.bin --arg key=5A --save
hexed run check.js a.bin b.bin --dry-run
```

This runs a [script](scripting.md) without showing a window. If you list files, the script starts with them open. The first file is the active document.

| Option | What it does |
| --- | --- |
| `--arg name=value` | Passes a parameter value (can be repeated). It appears in `script.args` |
| `--trust` | Runs an untrusted script without asking |
| `--allow permission,...` | Permissions to grant (for example `file-read`) |
| `--save` | Saves every changed document when the script ends |
| `--save-as path` | Saves the first document to that path (the original is not changed) |
| `--dry-run` | Saves nothing and prints only a summary of the changes |
| `--timeout seconds` | Maximum run time of the whole script |
| `--readonly`, `-r` | Opens the files read-only |

By default, only documents for which the script calls `doc.save()` are saved. If changes are left unsaved at the end, a warning appears.

There is no window, so a trust confirmation dialog cannot be shown. The script runs only if either of these is true:

- You chose **Always allow** for that script in the app.
- You gave `--trust` and the needed `--allow` on the command line.

Otherwise it ends with exit code 5.

`console.log` writes to standard output. `console.warn`, `console.error` and `ui.message` write to standard error.

A script can set its own exit code by calling `app.exit(code)` (0 to 255). Codes 2 to 10 overlap with the meanings on this page. Use 64 or more for your own meanings.

To run C# scripts, you need the [C# script component](scripting.md#csharp-component). If it is missing, the exit code is 3.

## Exit codes {#exit-codes}

The exit codes mean the same for every command.

| Code | Meaning |
| --- | --- |
| 0 | Success (`compare`: same, `find`: found, `hash --check`: all match) |
| 1 | The command worked, but the answer is "no" (`compare`: different, `find`: not found, `hash --check`: some do not match, `patch`: verification failed, `template --auto`: nothing fits) |
| 2 | Wrong arguments (an unknown option, a bad expression and so on) |
| 3 | Input or output error (file missing, cannot read, cannot write, not enough space) |
| 4 | Script or template error (syntax error, exception while running) |
| 5 | Not permitted (an untrusted script, access denied, administrator rights needed) |
| 6 | Canceled (<kbd>Ctrl</kbd>+<kbd>C</kbd>) |
| 7 | A limit was exceeded (script run time or memory, `--timeout`) |
| 8 | Format error (a wrong Intel HEX checksum, a broken patch and so on) |
| 10 | Internal error (an unexpected problem; the details are in the log file named in the message) |

In a batch file, check `%ERRORLEVEL%`. In PowerShell, check `$LASTEXITCODE`.

```text
hexed compare a.bin b.bin --brief
if %ERRORLEVEL% == 0 echo same
```
