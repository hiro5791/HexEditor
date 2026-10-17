# Go to and navigation

This page explains how to get to the place you want quickly: going to an offset, moving back and forward, moving by sector, and the position manager for reading bookmark descriptions.

## Go to an offset {#go-to-offset}

1. Press <kbd>Ctrl</kbd>+<kbd>G</kbd> (**Go > Go to Offset**). A go-to bar opens above the editor. Selecting the cursor position in the status bar opens it too.
2. Type an offset or an expression. The interpreted result appears beside the box (for example `= 0x1F00 (7,936)`).
3. Press <kbd>Enter</kbd> to go there. The bar closes and focus returns to the editor.

- <kbd>Shift</kbd>+<kbd>Enter</kbd>: go there and keep the bar open. It is handy when you look at many places in a row.
- <kbd>Esc</kbd>: close without moving.
- If the destination is outside the file, the box turns red and gives the reason.
- Your previous input is already in the box, fully selected. <kbd>↑</kbd> <kbd>↓</kbd> bring back earlier input (up to 20).
- Turn on **Select while moving** to select from the current position to the destination.

You can also go to the start or end of the file with <kbd>Ctrl</kbd>+<kbd>Home</kbd> and <kbd>Ctrl</kbd>+<kbd>End</kbd> (**Go > Start of File** / **End of File**).

In the command palette, typing `:` followed by an offset also works (for example `:0x1F00`).

After a jump, the view does not scroll if the destination row is already visible. If it is not, it scrolls so you can see it.

## How to write offsets {#offset-formats}

Every box that takes an offset, length or range accepts the same notation.

### Numbers

| Notation | Meaning | Example |
| --- | --- | --- |
| Starts with `0x` | Hexadecimal | `0x1F00` |
| Ends with `h` | Hexadecimal | `1F00h` |
| Starts with `$` | Hexadecimal | `$1F00` |
| Starts with `0b` | Binary | `0b1010` |
| Starts with `0o` | Octal | `0o777` |
| Nothing / ends with `d` | Decimal | `4096`, `4096d` |
| Contains `_` | Digit separator (ignored) | `0x1_0000` |
| Ends with `K` `M` `G` `T` | Multiple of 1024 | `4K` is 4096, `1.5G` |

### Expressions

You can use addition (`+`), subtraction (`-`), multiplication (`*`), division (`/`), remainder (`%`), bit operators (`&` `|` `^` `~` `<<` `>>`) and parentheses. These names are also available:

| Name | Meaning |
| --- | --- |
| `cur` | The cursor position |
| `start` | The start of the file (0) |
| `end` | The end of the file (the file length) |
| `sel.start` | The first byte of the selection |
| `sel.last` | The last byte of the selection |
| `sel.end` | The position just after the selection |
| `sel.len` | The length of the selection |
| `sector` | The sector size |
| `bm.name` | The position of the bookmark with that name |

Functions such as `u8`, `u16le`, `u32be` and `u64le` read the value at a position, as in `u32le(0x3C)`. They are useful for following pointers stored in a header.

### Relative and absolute

On the go-to bar you can choose **Relative to**:

- **Auto** (default): input starting with `+` or `-` is relative to the cursor; anything else is a position from the start.
- **From start**
- **From cursor**: adds the value to the cursor position (negative is allowed).
- **From end**: counts from the end of the file.

Some examples:

| Input | Destination |
| --- | --- |
| `0x1F00` | 0x1F00 from the start |
| `+0x100` | 0x100 after the current position |
| `-16` | 16 before the current position |
| `end-0x10` | 0x10 before the end |
| `sel.end` | Just after the selection |
| `u32le(0x3C)+4` | 4 more than the 32-bit value at offset 0x3C |

**Unit** lets you choose whether your number is in **Bytes** (default), **Rows** or **Sectors**. For example, with the unit Sectors, entering `100` goes to the start of sector 100.

If you have set a base address, you can switch between **By address** and **By offset**.

## Back and forward {#back-and-forward}

The places you jump away from are remembered. You can move back and forward, like in a web browser.

- <kbd>Alt</kbd>+<kbd>←</kbd> (**Go > Back**): return to the previous position.
- <kbd>Alt</kbd>+<kbd>→</kbd> (**Go > Forward**): go forward again after going back.
- **Go > History**: lists the latest 20 places with their address and the 8 bytes found there. Choose one to go to it.

These actions are recorded:

- Go to offset, and going to the start or end of the file
- Moving by sector, page or record
- Moving to the next or previous search match, and jumping from the results list
- Moving to a bookmark
- Clicking on the minimap or image view
- A mouse click that lands one screen or more away

Arrow keys, <kbd>Page Up</kbd> / <kbd>Page Down</kbd> and scrolling are not recorded. Each pane keeps up to 100 entries. If you insert or delete data, the recorded positions shift along with the data. The history is cleared when you quit.

## Moving by sectors {#sectors}

Use this to step through a disk or disk image sector by sector. The sector size is the device's value for disks, and 512 bytes for files (a value you chose when you [opened it as a disk image](opening-files.md#disk-images)). Change it in **View > Offset radix > Sector size**.

- **Go > Next Sector**: goes to the start of the next sector.
- **Go > Previous Sector**: if you are in the middle of a sector, goes to the start of that sector. If you are at the start, goes to the start of the previous one.
- **Go > Go to Sector**: opens the go-to bar with the unit **Sectors**. Type the sector number.

The first row of the sector you moved to appears at the top of the screen. For disks, the status bar shows the current sector number.

You can also show offsets in sectors. **View > Offset radix > Sector** writes them as sector number and position inside the sector, like `15:100` ([Display settings](hex-view.md#display-settings)).

To draw a line at every sector, use **View > Separator lines > Sector**. For process memory, the command palette commands **Go: Next memory region** and **Go: Previous memory region** jump to the start of the next or previous readable region.

## Position manager {#position-manager}

The position manager is a panel for reading and writing the long descriptions (comments) attached to bookmarks. It is useful when you record what you find as you analyze data.

1. Select **Go > Bookmarks > Position Manager**. A panel opens on the right.
2. Choose a bookmark in the list. The editor moves to that position.
3. Write the description in the comment box. You can also see a preview.

- The list shows the bookmarks in order of position. It uses the same data as the bookmark list, so changing a comment here updates the bookmark list too.
- When you move the cursor in the editor, the bookmark that contains the cursor is selected in the list.
- **Only with a description** narrows the list to bookmarks that have a comment.
- If you hold the pointer for half a second over a bookmark's range in the hex view, a tooltip shows its name, range and comment. From the keyboard, use **Go > Bookmarks > Show Description at Cursor**.
- You can export the contents to a Markdown or HTML file.

For how to create bookmarks, see [Bookmarks and annotations](bookmarks.md). To step through bookmarks, use <kbd>F2</kbd> (next) and <kbd>Shift</kbd>+<kbd>F2</kbd> (previous). <kbd>Ctrl</kbd>+<kbd>F2</kbd> sets or removes a bookmark at the current position.
