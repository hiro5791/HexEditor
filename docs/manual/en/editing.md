# Editing

This page explains how to change, delete and insert bytes, and how to undo your changes. The original file does not change until you save. You can undo any number of changes.

## Overwrite mode and insert mode {#overwrite-and-insert}

There are two ways to type:

- **Overwrite mode**: the bytes you type replace the existing bytes. The file length does not change.
- **Insert mode**: the bytes you type are pushed in at the cursor. The file gets longer.

Press <kbd>Insert</kbd> (**Edit > Toggle Overwrite / Insert**) to switch. Selecting **Overwrite** / **Insert** in the status bar also switches it. The cursor shape changes too: a filled block for overwrite, a vertical bar for insert.

- Each tab has its own mode. The mode of a newly opened file comes from the **Input mode for newly opened files** setting (overwrite by default).
- Typing at the end of the file makes the file longer in either mode.
- Data that cannot change length, such as disks and process memory, only has overwrite mode. The status bar shows **Overwrite (fixed length)**. If you press <kbd>Insert</kbd>, a message gives the reason.
- Paste (<kbd>Ctrl</kbd>+<kbd>V</kbd>) follows the mode too. To overwrite regardless of the mode, use <kbd>Ctrl</kbd>+<kbd>B</kbd> (**Edit > Paste Overwrite**).

## Typing in the hex column and the text column {#typing}

Move the cursor to the place you want to change and just type. Press <kbd>Tab</kbd> to choose which column you type in.

### Hex column

Type `0` to `9` and `A` to `F` (lowercase works too). You type one digit at a time, upper digit first then lower digit; after two digits the cursor moves to the next byte.

- In overwrite mode, each digit replaces half of the byte.
- In insert mode, a new byte is inserted when you type the upper digit.
- Keys that are not hex digits are ignored. Full-width `０`–`９` and `Ａ`–`Ｆ` are accepted as half-width.
- If the cell format is decimal, octal or binary, type in that number system. Decimal and octal take 3 digits per byte, and binary takes 8. For 16-bit and larger integer formats and float, you cannot type directly into the cell.

### Text column

Type characters. Input methods (IME) work too. Text being composed does not change the data until you confirm it.

- Each character is written as bytes in the current encoding. In UTF-8, `あ` is 3 bytes.
- If you type a character that the current encoding cannot represent, nothing is written and a message says why. Change the encoding.
- For UTF-16 and UTF-32, only the character is written, without a BOM.
- What <kbd>Enter</kbd> does is set by **Enter in the text column** (do nothing, CR LF, LF or CR). By default it does nothing.

### When there is a selection

If you type with a selection, insert mode deletes the selection and then starts typing. For overwrite mode, **Typing over a selection in overwrite mode** lets you choose **Overwrite from the start** or **Fill the selection with 00, then overwrite**.

Places you changed are shown in a different color, as modified bytes.

### Type in several places at once

Add cursors with <kbd>Alt</kbd>+click to type the same thing in several places. <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>↑</kbd> / <kbd>↓</kbd> add a cursor in the same column of the row above or below. <kbd>Esc</kbd> returns to one cursor. See [Selection and clipboard](selection-clipboard.md#multiple-selections).

## Deleting bytes {#delete}

- **No selection**: <kbd>Delete</kbd> removes the byte at the cursor. In insert mode, <kbd>Backspace</kbd> removes the byte before the cursor.
- **With a selection**: both <kbd>Delete</kbd> and <kbd>Backspace</kbd> remove the selection. **Delete** in the right-click menu does too.

**Edit > Delete** does the same as <kbd>Delete</kbd>. It removes the selection, or the byte at the cursor if nothing is selected. If there is nothing to delete, you are told so.

Deleting makes the file shorter, and the later bytes move up.

- In overwrite mode, <kbd>Backspace</kbd> deletes nothing and only moves the cursor back by one.
- If you do not want <kbd>Delete</kbd> to change the length in overwrite mode, turn on **Delete keeps the length in overwrite mode**.
- You cannot delete in data that cannot change length, such as disks and process memory.

Cut (<kbd>Ctrl</kbd>+<kbd>X</kbd>) copies and then deletes.

## Undo and redo {#undo-redo}

You can undo your changes as many times as you like. There is no limit, and memory use does not grow with the size of the file.

| Action | Keys | Menu |
| --- | --- | --- |
| Undo | <kbd>Ctrl</kbd>+<kbd>Z</kbd> | **Edit > Undo** |
| Redo | <kbd>Ctrl</kbd>+<kbd>Y</kbd> or <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Z</kbd> | **Edit > Redo** |

- The menu and toolbar buttons show the name of the action, such as **Undo: Fill**.
- Characters typed one after another become one action. A new action starts when you move the cursor, run another command, or pause for 2 seconds. Change the pause with **Group typing for undo (seconds)**.
- An operation that changes many places in one command, such as paste, fill or replace all, is undone in one step.
- After an undo, the affected range is selected and scrolled into view.
- Moving the cursor, selecting and view settings are not undone.
- Saving does not clear the history, so you can go back to before the save. If you want the history cleared, turn on **Clear the undo history when saving**.
- If you make a new change after undoing, the redo history is discarded. To keep it as a separate branch, turn on **Use branching undo history** (see [Branching history](#branching-history)).
- Closing the document discards the history, unless you save it (see [Branching history](#branching-history)).

## History panel {#history}

The history panel lists your changes and lets you jump to any point at once. Show it with **View > Panels > History**.

- Each row shows a number, the action, the range changed, the change in length (+N / −N) and the time. The point where you saved is marked.
- The row for the current state is bold. Rows after it are actions you can redo, and are faded.
- Double-click a row, or press <kbd>Enter</kbd>, to go to the state right after that action.
- **Go to this range** selects and shows the range of that action. The state does not change.
- **Create patch from these two points...** makes a patch from the state of one row to the state of another (see [Patches](patches.md)).

## Branching history {#branching-history}

With branching history, undoing and then making a new change does not throw away the changes you undid. They stay as a separate branch, and you can go back to any of them. Turn it on with **Use branching undo history** in the editing settings.

- <kbd>Ctrl</kbd>+<kbd>Y</kbd> redoes along the branch you used last.
- To see all branches, select **Show as tree** in the history panel. Changes are listed from top to bottom in the order you made them, and each branch is indented. The current state, the saved state, named points and pinned points are shown with an icon and text.
- Double-click a point, or press <kbd>Enter</kbd>, to go to that state, even on another branch.
- Select a point to see what it changed below the tree: the range and the first 16 bytes before and after.

Right-click a point in the tree for more actions.

| Action | What it does |
| --- | --- |
| **Name...** | Gives the point a name (1 to 100 characters). Leave it empty to remove the name |
| **Pin** | Keeps the point and the path to it when old branches are cleaned up |
| **Delete branch...** | Deletes the point and everything after it on that branch. This cannot be undone. You cannot delete the current point or the points before it |
| **Create patch from these two points...** | Makes a patch between two points (select two points, or one point and the current state) |

To go to a named point, run **Go to history node...** in the command palette and choose the name.

### Saving and loading the history {#save-history}

The history is normally discarded when you close the document. To keep it, save it to a history file (`.hexhist`).

1. Run **Save history...** in the command palette, or select **Save history** in the history panel.
2. Choose the file. The dialog shows the estimated size.
3. If you want, turn on **Include the SHA-256 of the whole original file**. This takes longer for large files.
4. Select **Save**.

To load it later, open the same file and run **Load history...**. You return to the point that was current when you saved. If the file seems to have changed since the history was saved, you are asked whether to load it anyway.

If the history has named or pinned points, closing the document asks whether to save the history first.

## Fill and insert bytes {#fill-and-insert}

### Fill

**Edit > Fill** fills the selection (or a range you give) with the content you choose. The length does not change.

1. Choose the range: **Selection**, or **Specify the range** (**Start** and **Length**).
2. Choose the type of **Content**.

| Content | What you give |
| --- | --- |
| **Byte value** | One byte value (`00` by default) |
| **Hex pattern** | Bytes such as `DE AD BE EF` (up to 1 MiB) |
| **Text** | A string and its encoding. Write line breaks and tabs as `\r` `\n` `\t` |
| **Random numbers** | A minimum and maximum (0 to 255 by default). With a **Seed**, you get the same result each time |
| **Cryptographic random numbers** | Nothing (uses the system's cryptographic random generator) |
| **Counter** | Start value, increment, element size, endianness, and what to do on overflow (**Wrap around** or **Saturate**) |
| **File contents** | A file, plus a start position and length inside it |
| **Clipboard contents** | The clipboard at the time you run it |

If the pattern or text is shorter than the range, turn on **Repeat the content to fill the range** to repeat it. **Pattern starts at** lets you start the pattern at the start of the range or count from offset 0. A preview shows the first 64 bytes before and after. When the content is set, select **Fill**.

Filling with a byte value finishes instantly, even for 100 GB.

### Insert bytes

**Edit > Insert Bytes** inserts a number of bytes at the cursor.

1. Enter the **Position** (the cursor position by default).
2. Enter the **Number of bytes**.
3. Choose the content (the same kinds as Fill; `00` by default).
4. Select **Insert**.

With **Select the inserted range** (on by default), the new bytes are selected.

### Insert file contents

**Edit > Insert File Contents** inserts or overwrites with all or part of another file at the cursor. Choose the file, then set **Start in the file** and **Length**. You can also hold <kbd>Ctrl</kbd> and drop a file on the window to insert it where you drop it.

## Changing the file size {#file-size}

You can change the length of a file in two ways:

- **Edit > Change File Size**: enter the **New length** (for example `4096` or `1M`). When making it shorter, the end is cut off. When making it longer, the end is padded with the content you choose (`00` by default). The number of bytes cut off is shown. Both can be undone.
- **Edit > Truncate at Cursor**: deletes everything from the cursor to the end.

These do not work on data that cannot change length, such as disks.

## Inserting and deleting bits {#bits}

You can insert or delete in units of bits instead of bytes. The data is treated as one stream of bits, and the later bits are shifted.

1. Select **Data > Data operations**.
2. For **Category**, choose **Insert / delete bits**, then **Insert bits** or **Delete bits**.
3. Set the place with **Byte offset** and **Bit in the byte (7 = most significant)**.
4. Enter the **Number of bits**. When inserting, also choose the **Inserted bit value** (0 or 1).
5. Choose what to **Shift**: **Within the selection only** (the length stays the same, and bits that fall off the end are dropped) or **To the end of the document**.
6. Select **Run**.

If the number of bits is a multiple of 8, it is the same as inserting or deleting plain bytes. You can also run **Insert bits** from the command palette. For more, see [Data operations](data-operations.md).

## Changing the case of text {#case}

You can change the case of letters in the selection all at once. Choose from **Data > Change case**:

- **Uppercase**
- **Lowercase**
- **Swap case**

Only ASCII letters (`a` to `z`, `A` to `Z`) are changed. Other bytes are left alone, and the length does not change. If the selection has several ranges, each range is converted.
