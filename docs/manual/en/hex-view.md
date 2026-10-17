# The hex view

This page explains the parts of the hex view and how to change how it looks. Most display settings are in the **View** menu. Changes apply immediately.

## Offset, hex and text columns {#columns}

The hex view has three columns, from left to right:

- **Offset column**: the position of the first byte in the row.
- **Hex column**: the value of each byte, by default as two hexadecimal digits.
- **Text column**: the same bytes shown as characters.

The view never loads the whole file. It draws only the rows you can see. Even for a file with billions of rows, you can move anywhere with the scroll bar at the right edge. While you drag the scroll thumb, the address of the top row appears next to it.

Data that has not been read yet shows as `··` in the hex column. Data that cannot be read, such as a bad sector, shows as `??` on a hatched background. Point at it to see the reason.

### Show or hide columns

Use **View > Columns**:

- **Offset column**, **Hex column**, **Text column**: show or hide each column. At least one of the hex and text columns always stays visible.
- **Show hex column only**, **Show text column only**, **Show all columns**: switch several at once.
- **Column header**: show a header with the position inside the row (0, 1, 2, …). Selecting the header of the offset column switches the offset radix.
- **Highlight current row**: change the background of the row with the cursor.

### Bytes per row and grouping

- **View > Bytes per row**: choose 8, 16 (default), 32, 64, **Auto** or **Custom**. **Auto** fits the window width. **Custom** accepts 1 to 4,096.
- **View > Grouping**: add a gap after every 1 (default), 2, 4, 8 or 16 bytes. Turn on **Extra space every 8 bytes** for a wider gap every 8 bytes.
- Bytes per row should be a multiple of the grouping.

The cursor and selection do not move when you change these.

### Showing values as numbers

**View > Columns > Cell format** changes how the hex column looks. **Hex** (default), **Decimal (unsigned)**, **Decimal (signed)**, **Octal** and **Binary** work on single bytes. **16-bit integer**, **32-bit integer**, **64-bit integer**, **float** and **double** show 2 to 8 bytes as one value. The data does not change.

How values of 2 or more bytes are read is decided by **Endianness** (**Little endian** or **Big endian**). Selecting **LE** / **BE** in the status bar also switches it. **Show bytes in groups reversed** shows each group in reverse order without changing the data.

### Showing offsets

**View > Offset radix** sets how the offset column is written: **Hexadecimal** (default), **Decimal**, **Octal** or **Sector**. Sector shows "sector number:position in the sector", which is handy for disks and disk images.

- **Set base address** shows offsets plus a fixed value as an "address" (it matches automatically for process memory).
- **Shift row start** moves where rows break. Use it to line up data whose structure is offset.
- **Set cursor as reference point** shows offsets relative to the current position in the status bar. **Clear reference point** undoes it.

### Modified bytes and 00

- **Highlight modified bytes** (on by default): overwritten bytes are shown in a red text color with an underline, and inserted bytes in green with a double underline. Saving clears them.
- **Dim 00 bytes** (on by default): bytes with value 0 are shown faintly.
- **Show hex in lowercase**: shows `A` to `F` in lowercase.
- **Alternate background per group**: makes vertical columns easier to follow.

## Moving the cursor {#moving-the-cursor}

You can move the cursor with the keyboard or the mouse.

| Action | Keys |
| --- | --- |
| One byte left or right, one row up or down | <kbd>←</kbd> <kbd>→</kbd> <kbd>↑</kbd> <kbd>↓</kbd> |
| Start or end of the row | <kbd>Home</kbd> <kbd>End</kbd> |
| One screen up or down | <kbd>Page Up</kbd> <kbd>Page Down</kbd> |
| Start or end of the file | <kbd>Ctrl</kbd>+<kbd>Home</kbd> <kbd>Ctrl</kbd>+<kbd>End</kbd> |
| Previous or next group | <kbd>Ctrl</kbd>+<kbd>←</kbd> <kbd>Ctrl</kbd>+<kbd>→</kbd> |
| Scroll one row without moving the cursor | <kbd>Ctrl</kbd>+<kbd>↑</kbd> <kbd>Ctrl</kbd>+<kbd>↓</kbd> |
| Switch between hex and text columns | <kbd>Tab</kbd> / <kbd>Shift</kbd>+<kbd>Tab</kbd> |

- If you click in the hex column, the cursor goes to the digit you clicked, either the upper or the lower one.
- With several text columns, <kbd>Tab</kbd> goes hex column, text column, and so on. Because <kbd>Tab</kbd> switches columns, press <kbd>F6</kbd> to leave the hex view.
- The mouse wheel scrolls by the number of rows set in Windows. <kbd>Shift</kbd>+wheel scrolls sideways.
- Turn on the **Move by nibble with arrow keys** setting to make <kbd>←</kbd> <kbd>→</kbd> move by 4 bits in the hex column.
- You can also use **Go > Start of File** and **End of File**.

To move to a specific place, see [Go to and navigation](go-to.md).

If you hold the pointer on a byte for half a second, a tooltip shows the offset (hex and decimal), the value, the character and any bookmark.

## Status bar {#status-bar}

The status bar at the bottom of the window shows the current state.

| Item | Example | When you select it |
| --- | --- | --- |
| Cursor position | `Offset: 0x00001F00` | Go to offset (<kbd>Ctrl</kbd>+<kbd>G</kbd>) |
| Cursor value | `Value: 4F (79)` | Opens the data inspector |
| Selection | `Selection: 0x1F00–0x1FFF (length 0x100 = 256)` | Select range (<kbd>Ctrl</kbd>+<kbd>E</kbd>) |
| Active column | `Hex` / `Text` | Switches column |
| Text encoding | `UTF-8` | Choose an encoding |
| Endianness | `LE` / `BE` | Switches |
| Input mode | `Overwrite` / `Insert` / `Read-only` | Toggles overwrite and insert (<kbd>Insert</kbd>) |
| Modified | `● Modified` | |
| File size | `Size: 1.50 GB (1,610,612,736 bytes)` | Copies the exact size |
| Zoom | `Hex 125%` / `Screen 110%` | Resets to 100% |

The end of a selection is its last byte (that byte is included). The bar also shows the number of running operations with their progress (select it to open the processing center) and the number of notifications (select it to open the history).

Right-click the status bar to show or hide each item. **View > Panels > Status bar** hides the whole bar.

## Display settings {#display-settings}

Display settings are saved for each document. When you reopen the same file, it looks the way you left it.

- **View > View settings > Save as default**: makes the current display settings the default for newly opened documents. Items that belong to one document, such as the base address and reference point, are not included.
- **View > View settings > Reset to default**: removes this document's settings and returns to the defaults.
- **View > Presets**: save a set of settings under a name and apply it later. If you give it file extensions, it can apply automatically when you open such a file.

### Separators, records and pages

- **View > Separator lines**: draws a horizontal line and a label at every sector, page (4,096 bytes) or custom length.
- **View > Show one page at a time**: shows one section per screen.
- **View > Records**: shows fixed-length records with alternating background colors. In **Record settings** you set the record length (16 by default), where the first record starts, **One record per row** and **Record numbers in offset column**. **Go > Next record** and **Previous record** move to the neighbouring record at the same position.

## Character encodings {#encodings}

The text column shows characters using the encoding chosen in **View > Text encoding**. The default is **ASCII**, and bytes that cannot be shown appear as `.`. You can also change it by selecting the encoding in the status bar.

- The common ones, **ASCII**, **ANSI**, **UTF-8**, **UTF-16 LE / BE**, **UTF-32 LE / BE** and **Shift_JIS**, are listed first. **More encodings…** opens a list of Windows code pages, EBCDIC, ISO-8859 and more, which you can filter by name or number. The 5 you used most recently appear at the top of the menu.
- Each document has its own encoding. Changing it does not move the cursor or selection.
- The text column always uses one cell per byte. A multi-byte character is drawn in the cell of its first byte, and the following cells stay blank. Full-width characters are drawn two cells wide.
- **Decode UTF-16 from odd offsets** shifts the UTF-16 boundaries by one byte. You can also set the **UTF-32 start position**.
- Invalid byte sequences are shown byte by byte.
- **View > Text encoding > Load character table file** lets you use a `.tbl` table such as `41=A` as an encoding. It is useful for analyzing game ROMs.

### Several text columns with different encodings

**View > Columns > Add text column** puts up to 5 text columns side by side, each with its own encoding. Use it, for example, to compare UTF-16 read from different offsets. Arrange the columns with **Move text column left**, **Move text column right**, **Remove text column** and **Text column encoding**.

## Colors and byte themes {#colors}

**View > Byte theme** colors text by byte value.

- **None** (default)
- **By kind**: separate colors for 00, FF, readable ASCII, whitespace, control characters, 80 to FE and so on.
- **Gradient**: maps values 0 to 255 to a continuous color band that is friendly to color-blind users.
- **Custom**: load a JSON file with a text and background color for each value.

Byte themes are not used when Windows high contrast is on.

Choose the colors of the whole hex view with **Color scheme** in **Tools > Settings > Appearance**. You can duplicate a built-in scheme to make your own. The font and size are in **Appearance** too. The hex view only uses monospaced fonts (Cascadia Mono by default, or Consolas if it is missing).

When colors overlap, the one in front is visible. From front to back the order is:

1. Cursor
2. Selection
3. Current search match, other search matches
4. Modified bytes (the underline is always drawn)
5. Bookmarks
6. Annotations, structure coloring, coloring rules
7. Differences
8. Byte theme
9. Dimmed 00
10. Current row, records, alternating columns

Meaning is never carried by color alone: changes also get underlines and search matches also get outlines.

## Zoom and full screen {#zoom}

There are two kinds of zoom. The range is 50 to 400% in 14 steps.

| Kind | How | What changes |
| --- | --- | --- |
| Hex zoom | <kbd>Ctrl</kbd>+<kbd>=</kbd> (zoom in), <kbd>Ctrl</kbd>+<kbd>-</kbd> (zoom out), <kbd>Ctrl</kbd>+<kbd>0</kbd> (back to 100%), <kbd>Ctrl</kbd>+wheel, or **View > Zoom** | Only the data text |
| Screen zoom | **View > Zoom > Zoom in entire screen**, **Zoom out entire screen**, **Reset screen zoom to 100%** | Everything, including menus and panels |

Depending on your keyboard layout, <kbd>=</kbd> and <kbd>-</kbd> may not be available. Use the menu or the command palette then.

When the zoom is not 100%, the status bar shows it. Selecting it returns to 100%. In **Tools > Settings > View**, **Hex zoom applies to** lets you choose **All tabs** or **Current tab only**.

<kbd>F11</kbd> (**View > Full screen**) makes the window full screen. Press <kbd>F11</kbd> again, or <kbd>Esc</kbd> in the editor, to return. In full screen, pointing at the top edge of the screen brings back the title bar and tabs.

## Tabs and windows {#tabs-and-windows}

Each document opens in its own tab.

- A tab shows an icon for its kind, its name and its state. `●` means unsaved changes, a lock means read-only, and a pin means pinned. When several files have the same name, the folder name is added. The tooltip shows the path and size.
- <kbd>Ctrl</kbd>+<kbd>Tab</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Tab</kbd> switch tabs in most recently used order. <kbd>Ctrl</kbd>+<kbd>Page Down</kbd> / <kbd>Ctrl</kbd>+<kbd>Page Up</kbd> switch in tab order.
- <kbd>Ctrl</kbd>+<kbd>W</kbd> closes a tab, and so does a middle click. The **+** button at the right end creates a new document.
- Drag tabs to reorder them. Right-click and select **Pin** to fix a tab at the left; it is then skipped by **Close other tabs** and similar commands. The right-click menu also has **Copy path**, **Show in Explorer** and more.
- Drag a tab out of the tab strip to move it to a new window, or drop it on another window's tab strip. Unsaved edits, undo history and selection move with it. **Move to new window** in the right-click menu does the same.
- <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>N</kbd> (**File > New window**) opens another window.

HexEditor runs as a single app. When you open a file from Explorer or the command line, it opens as a tab in the window that is already running.

## Split view and synchronized views {#split-view}

### See two places in one document

<kbd>Ctrl</kbd>+<kbd>\\</kbd> (**View > Split**) divides a tab into two panes. You can choose **Split top and bottom**, **Split left and right** and **Remove split**. It is handy for editing while looking at two distant places.

- Each pane has its own cursor, selection and scroll position. Edits show up in both at once.
- Menus and shortcuts act on the pane you clicked last. <kbd>F6</kbd> moves between panes.
- Turn on **Synchronize pane scrolling** to make one pane's scrolling move the other.

To show the same document in another tab, use **View > Open in new view**. The tab is named like `data.bin : 2`. You can move it to another window too. Edits and undo history are shared.

### Side by side with another file

**View > Show side by side** shows another open document to the right. **View > Synchronized scrolling** chooses how they move together.

- **Off**
- **Same offset** (the default when you place them side by side): scrolls so the same offsets line up.
- **Keep the difference**: keeps the difference in offsets from when you placed them.
- **Follow the comparison**: when you have comparison results, scrolls to matching positions, taking insertions and deletions into account.

**Highlight differences** marks bytes that differ at the same offset. While synchronized, the status bar shows **Sync**. For details about comparing, see [Comparing files](compare.md).

## Minimap {#minimap}

The minimap shows the nature of the whole file (or the area around the view) as a tall strip. Turn it on with **View > Minimap > Show minimap**.

- **What it shows**: **Entropy** (default; low is blue, high is red), **Byte kinds**, **Byte value**, **Non-zero bytes**, **Byte theme (around only)**.
- **Range**: **Whole document** or **Around the view** (one pixel row per row of the view). **Calculate exactly** spends more time to compute exactly.
- Click the strip to jump there. Drag the frame that marks the visible range to scroll.
- The edge of the strip has marks for the cursor, selection, search matches, bookmarks and modified bytes.

Areas with high entropy (red) are often compressed or encrypted data.

## Byte image view {#visualizer}

This draws bytes as pixels in an image. It lets you check images, textures and patterns in data by eye. Show the panel with **View > Panels > Image view**.

1. Choose the **Pixel format** (1-bit black and white up to 8-bit gray, RGB565, RGB, RGBA and more).
2. Enter the **Width (pixels)** (256 by default).
3. If you want, choose the **Start position**. By default it follows the cursor.

Click a pixel to move the hex view's cursor there. The hex view's cursor and selection are shown as frames on the image. **Save as image** in the panel's menu saves a PNG.
