# Selection and clipboard

This page explains how to select ranges of bytes and how to copy, cut and paste them. Copying and pasting is instant, even for ranges of hundreds of gigabytes.

## Selecting bytes {#select}

### With the mouse

| Action | Result |
| --- | --- |
| Drag | Selects from where you pressed to where you let go |
| <kbd>Shift</kbd>+click | Selects from the cursor to the place you click |
| Double-click | In the hex column, selects the group containing the byte (the grouping unit). In the text column, selects one byte |
| Triple-click | Selects the whole row |
| Click in the offset column | Selects the whole row. Dragging selects whole rows |

If you drag the pointer above or below the view, it scrolls automatically. You can also drag from the hex column into the text column.

### With the keyboard

Hold <kbd>Shift</kbd> while moving the cursor to extend the selection. The movement keys are the same as in [The hex view](hex-view.md#moving-the-cursor). <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>←</kbd> / <kbd>→</kbd> work by group, and <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Home</kbd> / <kbd>End</kbd> go to the start and end.

- <kbd>Ctrl</kbd>+<kbd>A</kbd> (**Edit > Select All**): selects the whole file.
- <kbd>Esc</kbd>: clears the selection. **Clear selection** in the right-click menu does too.

The position and length of the selection show in the status bar ([Status bar](hex-view.md#status-bar)). The end position is the last byte (that byte is included).

If you right-click inside a selection, the menu opens and the selection stays. If you right-click outside it, the cursor moves there.

## Selecting a range by offsets {#select-range}

Enter a start and end position to select a range.

1. Press <kbd>Ctrl</kbd>+<kbd>E</kbd> (**Edit > Select Range**).
2. Fill in two of **Start**, **End** and **Length**. The third is calculated for you. **End** is the last byte of the range, and it is included.
3. Select **Select**.

The fields accept expressions such as `0x1F00`, `4K`, `end-16` and `cur+0x100`. The decimal and hex results are shown beside the field.

- Choose how to select: **New selection** (default), **Add to the current selection** or **Extend the current selection**.
- **Go to the start after selecting** (on by default) scrolls to the beginning of the range.
- <kbd>Alt</kbd>+<kbd>↓</kbd> shows the history of what you entered.
- Selecting the selection in the status bar opens this dialog as well.

You can also shift or resize a selection, which is useful for stepping through records one by one. Use **Edit > Selection**:

- **Shift Forward** / **Shift Back**: move it by its own length.
- **Shift by N Bytes**: move it by an amount you give (negative moves back).
- **Grow / Shrink**: move the start and end separately.
- **Swap Anchor and Cursor**: move the cursor to the other end of the selection.

## Multiple selections {#multiple-selections}

You can select several separate places at once. Copy, delete, fill, data operations and so on then act on all of them.

- Add a range with <kbd>Ctrl</kbd>+drag or <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+click. <kbd>Ctrl</kbd>+click inside a selected range removes it.
- **Edit > Selection > Invert Selection** selects everything that is not selected.
- **Next Range** / **Previous Range** switch between the ranges.
- The status bar shows something like `Selection: 3 ranges, 1,024 bytes in total`.

Results from other features can become a multiple selection too. For example, the results of **Find all** can be converted to a selection with the convert button ([Find all and the results panel](search-replace.md#find-all)).

Copying several ranges joins them in order. Cut and delete remove all of them. Typing acts as several cursors, one at the start of each range.

### Multiple cursors

To make the same edit in several places at once, add cursors.

- <kbd>Alt</kbd>+click: adds a cursor where you click. Click again to remove it.
- <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>↑</kbd> / <kbd>↓</kbd>: adds a cursor in the same column of the row above or below.
- **Edit > Selection > Cursor at Each Range**: puts a cursor in each range of a multiple selection.
- <kbd>Esc</kbd>: returns to one cursor.

Typing, deleting, pasting and movement keys act on all cursors at once. One input can be undone in one step.

## Rectangular selection {#rectangular-selection}

Select a rectangle bounded by rows and columns. It is handy for picking the same field in fixed-length records.

1. Hold <kbd>Alt</kbd> and drag, or extend with <kbd>Alt</kbd>+<kbd>Shift</kbd>+arrow keys. You can also start with **Edit > Selection > Start Rectangle Selection**.
2. The status bar shows `Rectangle R rows × C bytes`.

A rectangular selection can be copied, cut, filled and used for data operations. A copied rectangle is pasted back as rows under each other. To add bytes at the left edge of each row of the rectangle, use **Edit > Selection > Insert into Rectangle**. **Convert to Multi-Selection** turns a rectangle into one range per row.

## Copy, cut and paste {#copy-and-paste}

| Action | Keys | Menu |
| --- | --- | --- |
| Copy | <kbd>Ctrl</kbd>+<kbd>C</kbd> or <kbd>Ctrl</kbd>+<kbd>Insert</kbd> | **Edit > Copy** |
| Cut | <kbd>Ctrl</kbd>+<kbd>X</kbd> or <kbd>Shift</kbd>+<kbd>Delete</kbd> | **Edit > Cut** |
| Paste | <kbd>Ctrl</kbd>+<kbd>V</kbd> or <kbd>Shift</kbd>+<kbd>Insert</kbd> | **Edit > Paste** |
| Paste overwrite | <kbd>Ctrl</kbd>+<kbd>B</kbd> | **Edit > Paste Overwrite** |

Copied bytes go to the clipboard both as binary and as text. If you copy in the hex column, the text is a hex string such as `DE AD BE EF`. If you copy in the text column, it is the characters. They are also placed in a format that other binary editors can read.

- **Paste** follows the input mode. In insert mode it pushes the data in at the cursor, and in overwrite mode it overwrites from the cursor. If there is a selection, it replaces it.
- **Paste Overwrite** overwrites whatever the mode.
- In data that cannot change length (such as disks), paste always overwrites. Anything past the end is not written.
- After pasting, the pasted range is selected (the **Select the pasted data** setting).
- Copying and pasting inside HexEditor does not duplicate the data. It only refers to the original position, so even 100 GB is instant.
- The most data HexEditor puts on the system clipboard is 64 MiB (change it with **Maximum clipboard size (MiB)**). If you copy more than that, you can paste only inside HexEditor. Other apps receive just text that states the size.
- If the clipboard has only text, it is pasted as characters in the text column, and as a hex string in the hex column. If HexEditor cannot tell, the [Pasting hex strings and other formats](#paste-formats) dialog opens.

## Copy as hex, arrays and Base64 {#copy-as}

Copy the selection as text in the format you like. It is handy when you paste into source code.

1. Select a range.
2. Press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>C</kbd> (**Edit > Copy As**).
3. Choose a format in the list on the left.
4. Change the settings on the right if you need to. Check the result in the preview below.
5. Select **Copy**.

The main formats are:

| Group | Examples |
| --- | --- |
| Hex string | `DE AD BE EF`, `DEADBEEF`, `0xDE, 0xAD`, `\xDE\xAD`, `%DE%AD` |
| Number lists | decimal, octal, binary |
| Arrays | C, C++, C#, Java, JavaScript, Python, Rust, Go, Pascal, VB.NET, assembly and more |
| Encodings | Base64, Base32, Ascii85, UUEncode, Quoted-Printable |
| Documents | HTML, RTF, Markdown, TeX |
| As displayed | As it appears on screen (offset, hex, text), or the string read in an encoding |
| Text formats | Intel HEX, Motorola S-record, JSON |
| Position | Offset or range notation |

- For each format you can set bytes per row, upper or lower case, variable name, line ending and indentation.
- The last format and settings become the starting point next time. To copy in the last format without opening the dialog, use **Edit > Copy As Last Format**.
- With several ranges selected, you can choose **join them** or **separate by range**.

## Pasting hex strings and other formats {#paste-formats}

Choose how the text on the clipboard should be read, then paste.

1. Press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>V</kbd> (**Edit > Paste Special**).
2. The list on the left shows the formats the clipboard can be read as, most likely first. When you choose one, the result bytes and length appear on the right.
3. Choose **Insert** or **Overwrite** and paste.

Formats it can read include hex strings (with spaces, commas, `0x` or `\x`), array notation (`{ 0xDE, 0xAD }`), lists of decimal, octal or binary numbers, strings with C escapes, Base64, Base32, Ascii85, UUEncode, Intel HEX, S-record, JSON, display dumps, and plain text. Files copied in Explorer can be pasted as file contents.

- When several candidates look equally likely, the format you used last is preferred (the **Prefer the last paste format** setting).
- Text up to 512 MiB can be read.
- To paste in the most likely format without asking, turn on **Paste detected formats without asking**.

Dropping text on the window opens this dialog too.

## Clipboard history and user clipboards {#clipboard-history}

Besides the system clipboard, there are nine numbered clipboards. They are useful for keeping content you use repeatedly.

- **Edit > User Clipboards > Copy To** puts the selection into one of 1 to 9. **Paste From** pastes from it. The system clipboard does not change.
- The last 20 things you copied or cut inside HexEditor are kept as **clipboard history**. Bring one back with **Edit > User Clipboards > Paste from Clipboard History**.
- The panel at **View > Panels > Clipboard** shows the list. Each item shows its size, the first 16 bytes and where it came from. It has **Paste**, **Overwrite paste**, **Clear** and **Name** buttons.

By default, user clipboards are cleared when you quit. To keep them, turn on **Keep user clipboards after exit** in the settings. You can change how many history items are kept with **Clipboard history items**.

## Dragging a selection {#drag-and-drop}

You can move selected bytes with the mouse.

- Drag from inside the selection and drop it somewhere else. The original range is removed and the bytes are inserted where you drop them (move).
- Hold <kbd>Ctrl</kbd> while dropping to keep the original and insert a copy (copy).
- Drop onto another tab to copy into that tab's document. If you wait half a second over a tab's header, that tab comes to the front.
- Drag to another app to give it the same data as copy.
- Press <kbd>Esc</kbd> while dragging to cancel.

You cannot insert into a document that cannot change length. In overwrite mode, dropping with <kbd>Shift</kbd> held overwrites. You cannot drop inside the selection itself.

## Saving and loading selections {#save-selection}

### Remember a selection

You can give a selection (including multiple ranges and rectangles) a name and store it with the document.

- **Edit > Selection > Save Selection**: enter a name and save. You can store up to 100 per document.
- **Edit > Selection > Load Saved Selection**: choose from the list. You can replace the current selection or add to it.

Saved selections do not move when you insert or delete data. Unlike bookmarks, their positions are not corrected automatically.

### Write to a file

- **Edit > Selection > Export Selection** and **Import Selection**: write the list of ranges as CSV (`start,length` columns) and read it back.
- **File > Save selection as binary**: saves the selected bytes as they are to another file.
- **File > Open selection in a new tab**: opens the selected range in a new tab. **Linked view** stays connected to the original, and edits go back to it. **As a copy** makes an independent untitled document.
