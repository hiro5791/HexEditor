# Search and replace

This page explains how to search for byte sequences, text and numbers, how to use find all and replace, and how to search many files. Searches run in the background. You can watch their progress and cancel them. Files of hundreds of gigabytes can be searched.

## The find bar {#find-bar}

The find bar is a strip that appears over the editor. It is not a dialog, so you can keep editing while it is open.

1. Press <kbd>Ctrl</kbd>+<kbd>F</kbd> (**Search > Find**). If the selection is 1 to 256 bytes, its contents go into the search box.
2. Choose the **Search type** and type in the search box.
3. Press <kbd>Enter</kbd> for the next match, or <kbd>Shift</kbd>+<kbd>Enter</kbd> for the previous one.

| Action | Keys |
| --- | --- |
| Find next | <kbd>Enter</kbd> or <kbd>F3</kbd> |
| Find previous | <kbd>Shift</kbd>+<kbd>Enter</kbd> or <kbd>Shift</kbd>+<kbd>F3</kbd> |
| Find all | <kbd>Alt</kbd>+<kbd>Enter</kbd> |
| Browse search history | <kbd>↑</kbd> <kbd>↓</kbd> |
| Cancel a running search; otherwise close the bar | <kbd>Esc</kbd> |

- The match is selected and shown near the middle of the screen.
- All matches in view are highlighted. To the right of the search box you see the number of matches and the current one. For ranges over 1 GiB, matches are counted only when you select **Count matches**.
- Even with the bar closed, <kbd>F3</kbd> and <kbd>Shift</kbd>+<kbd>F3</kbd> search with the last conditions.
- Next to the search box, the start of the search term as bytes is shown. For example, searching for the text `AB` in UTF-16 shows `41 00 42 00`.
- If the term is not valid (for example hex with an odd number of digits), the box turns red and says why.
- With **Search as you type** (on by default), the search starts again from the position where you opened the bar after each keystroke. If you want <kbd>Esc</kbd> to return you to that position when closing, turn on **Esc returns to the start position**.
- If the search reaches data it cannot read, you can choose **Skip and continue** or **Stop searching**. To always do the same thing, use the **When data can't be read while searching** setting.

The search conditions are remembered per window, so they stay when you switch tabs.

## Searching for text and hex bytes {#text-and-hex}

### Hex byte sequences

Set the **Search type** to **Hex** and type bytes such as `DE AD BE EF`. Spaces, commas, `0x` and `\x` are read as separators, so `DEADBEEF` and `0xDE,0xAD` work as well. Use an even number of digits.

A search term can be from 1 byte up to 1 MiB.

### Text

Set the **Search type** to **Text** and type the string to find. It is converted to bytes in the encoding chosen under **Encoding**, and then searched.

- The default **Encoding** is **Match the displayed encoding**. You can choose any encoding, such as UTF-8 or UTF-16 LE / BE.
- Choose **Multiple encodings** to search up to 8 encodings at the same time. It is useful when you do not know the encoding. The default combination is ASCII, UTF-8, UTF-16LE and UTF-16BE. You can save combinations under a name.
- **Match case** is off by default. Full-width letters are treated as the same in upper and lower case, but full-width and half-width, and hiragana and katakana, are kept different.
- **Whole words only** finds only matches whose neighbours are not letters or digits.
- Turn on **Use escapes (\\n, \\xHH)** to write `\n`, `\t`, `\0`, `\xHH` and `\uHHHH`.
- **Align to characters** limits UTF-16 / UTF-32 matches to offsets that are multiples of 2 / 4.

If the string has a character that the chosen encoding cannot represent, the search shows the character and gives an error.

## Searching for numbers {#numbers}

Set the **Search type** to **Integer** or **Floating point** to turn a number into bytes and search for it. You can enter expressions such as `0x1F`, `-1` and `4K`.

### Integers

- **Integer size**: 8, 16, 24, 32 (default), 48 or 64 bit.
- **Sign**: **Signed**, **Unsigned** or **Signed or unsigned** (default).
- **Byte order**: **Little endian**, **Big endian** or **Both**. The default follows the status bar.
- To search a range of values, write `1000..2000` (both ends included). Turn on **Exclude the range** to match values outside the range.

### Floating point

- **Floating-point format**: half (16 bit), float (32 bit, default) or double (64 bit).
- **Tolerance**: **Exact** (default), **Absolute tolerance**, **Relative tolerance** or **ULP**. Use it to find values that are a little off because of calculation. Enter a relative tolerance as a ratio, such as `0.001`.
- `1.5e-3`, `inf`, `-inf` and `nan` are accepted. The decimal point is always `.`.

### Bit masks and position conditions

- In a **Hex** search, turn on **Bit mask** to compare only the bits you choose. With **Bit pattern (0 1 x)**, write something like `1xx0 0001` (`x` means ignore). With **Value and mask**, enter a value and a mask in hex.
- **Position condition** limits matches to those whose position satisfies "offset mod x = y". There are buttons for **×2**, **×4**, **×8**, **×16** and **Sector start**. It works with every search type.

## Wildcards and regular expressions {#wildcards}

### Hex wildcards

In a **Hex** search term you can write:

- `??`: any one byte (for example `DE AD ?? EF`)
- `?`: any one nibble (4 bits). `A?` is a byte whose upper half is A.
- `*`: any run of 0 or more bytes, up to the **Maximum match length** setting (4,096 bytes by default).

Wildcards at the very start and end are ignored. A term that consists only of wildcards is not allowed.

### Regular expressions

- **Regular expression (text)**: searches the characters read from the data in an encoding. The syntax is close to ECMAScript (JavaScript), with the flags `i` (ignore case), `m` and `s`.
- **Regular expression (bytes)**: treats each byte as one character. Write bytes as `\xHH`, and ranges such as `[\x00-\x1F]` are allowed. `.` matches any byte.

If the syntax is wrong, the position and reason are shown. If you use constructs that need backtracking (backreferences, lookahead, lookbehind), ranges that take too long may be skipped. Matches of length 0 are not reported.

### Find what is not the pattern

In a **Hex** search, turn on **Find what is not the pattern** to find where the data stops repeating the pattern. You can skip areas filled with `00` or `FF` and find where the data starts.

## Search options and range {#options}

| Option | Meaning |
| --- | --- |
| **Direction** | **Forward** (default) or **Backward**. It applies to <kbd>Enter</kbd>. <kbd>F3</kbd> is always forward and <kbd>Shift</kbd>+<kbd>F3</kbd> always backward |
| **Wrap around** | If nothing is found up to the end, continue from the start (on by default) |
| **Search in** | **Whole document** (default), **Selection**, **Offset range** or **All open documents** |
| **Include overlapping matches** | Also counts overlapping matches, like `AA` inside `AAAA` (off by default) |

For **Offset range**, enter a **Start** and an **End, inclusive**. Expressions such as `0x100` and `end-0x10` work. Matches that cross the edge of the range are not found. While **Selection** is chosen, the range is outlined faintly.

Call up search history by pressing <kbd>↑</kbd> <kbd>↓</kbd> in the search box or using the drop-down. The history keeps 50 entries. **Search > Clear Search History** erases it.

## Find all and the results panel {#find-all}

Press <kbd>Alt</kbd>+<kbd>Enter</kbd> or select **Find all** on the find bar to find every match in the range and list them in the **Search results** panel at the bottom.

- The list shows a number, the offset, the length, the matched data (hex and text) and the data before and after. Results appear as they are found, even while the search is running.
- Select a row (click or <kbd>Enter</kbd>) to go to that match and select it. <kbd>↑</kbd> <kbd>↓</kbd> move through the rows.
- You can narrow the results with **Filter**, and sort them with the column headers.
- While there are results, <kbd>F3</kbd> and <kbd>Shift</kbd>+<kbd>F3</kbd> go to the next and previous result in the list.
- Matches are also outlined in the editor, and marked on the minimap and scroll bar.
- The limit is 1,000,000 matches. When it is reached the search stops and the heading tells you.

The **Convert** button lets you use the results in other ways. The target is **Selected rows** or **All rows**.

- **To Selection**: makes the matched ranges a multiple selection ([Multiple selections](selection-clipboard.md#multiple-selections)).
- **To bookmarks**: adds a bookmark for each match ([Bookmarks and annotations](bookmarks.md)).
- **Export as CSV** / **Export as JSON**: writes to a file.
- **Copy data as hex** / **Copy data as text**: puts the data on the clipboard.

With the **Show each Find all in a new tab** setting, each search puts its results in a separate tab.

## Replace {#replace}

<kbd>Ctrl</kbd>+<kbd>H</kbd> (**Search > Replace**) adds a **Replace with** box to the find bar.

1. Type what to find in the search box and the replacement in the replace box. Write the replacement in the form of the search type (hex for Hex, characters for Text, a number for Integer).
2. **Replace** replaces the current match and moves to the next. **Skip** moves on without replacing.
3. To replace everything at once, select **Replace all**.

- One replacement is one undo step. **Replace all** is one undo step too.
- A `??` in a hex replacement keeps the original byte at that position.
- In regular expressions, you can use `$1`, `$<name>`, `$&` (the whole match) and `$$` in the replacement.
- An empty replacement deletes the match.
- You cannot replace in a read-only document.
- If there are more than 1,000,000 matches, you are asked to confirm before replacing.

### When the lengths differ

Choose what happens when the replacement is not the same length as the match with **If the lengths differ**.

| Choice | What happens |
| --- | --- |
| **Change the length** | Inserts or deletes, so the file length changes. This is the default for files |
| **Pad and keep the length** | If shorter, fills the rest with **Padding**. If longer, it is an error. This is the default for disks and similar |
| **Overwrite following bytes** | If longer, overwrites the bytes after the match. If shorter, leaves the rest as it is |

For **Padding**, choose **Pad with 00** (default), **Pad with FF**, **Pad with spaces** or **Pad with bytes** (1 to 16 bytes in hex).

## Searching in multiple files {#multiple-files}

You can search and replace across the files in a folder at once.

1. Press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F</kbd> (**Search > Search in files**). The **Search in files** panel opens on the left.
2. Enter what to look for and its type. To use the find bar's conditions as they are, select **Use the find bar's search**.
3. Add folders under **Targets**. The default is the current document's folder.
4. Narrow the search if you want:
   - **Include subfolders** (on by default)
   - **Include** mask (for example `*.bin;*.dat`) and **Exclude** mask (for example `.git;node_modules`)
   - **Min size** and **Max size**
   - **Include hidden and system files**, **Follow symbolic links and junctions**
   - **Search open documents (including unsaved edits)** (on by default)
5. Select **Search**.

Results are grouped by file. Double-click a row to open that file and go to the match. Files that could not be read are listed under **Skipped files** with the reason.

To replace in files, switch the panel to **Replace** at the top.

1. Search, then check the results. You can clear the check box of rows or files you do not want to replace.
2. Enter the replacement and choose what to do when lengths differ.
3. Select **Replace**. In the confirmation, select **Replace and save**.

The files are changed directly and cannot be undone. That is why **Make a backup (.bak) before replacing** is on by default. Open documents are replaced in the editor instead of on disk (you can undo them until you save). Files changed after the search, and read-only files, are skipped.

## Extracting strings {#strings}

This lists every place where readable characters run together, like the `strings` command.

1. Select **Search > Extract strings**. The **Strings** panel opens at the bottom.
2. Set your options:
   - Minimum length (4 characters by default)
   - **Encodings** (ASCII, UTF-8, UTF-16LE, UTF-16BE and more; you can choose several)
   - **Characters**: **Printable** or **Printable and line breaks**
   - The range (whole document, selection or offset range)
3. Select **Extract**.

The list shows the offset, bytes, characters, encoding and string. You can filter by words or regular expressions. Select a row to go there. As with find all results, you can convert them to a selection or bookmarks, and export to CSV / JSON / text.

## Searching for template values {#template-values}

After you apply a structure template, you can search the result for fields by name, type and value. In the search box of the template results panel, type a condition such as `name:*size* value>0x1000`. The results appear in the **Search results** panel, and selecting one moves to that place. For details, see [Structure templates](templates.md).
