# Bookmarks and annotations

A bookmark is a mark you put on part of a document. You give it a name, a color and a comment, and you can come back to it quickly. This page explains bookmarks, annotations, and coloring rules.

## Adding bookmarks {#add-bookmarks}

Add a bookmark in any of these ways.

- Press <kbd>Ctrl</kbd>+<kbd>F2</kbd>.
- Select **Go > Bookmarks > Set or Remove Bookmark**.
- Right-click the hex view and select **Set bookmark**.

If there is a selection, the bookmark covers it. If not, it covers the one byte at the cursor. The name is set automatically, such as "Bookmark 1", and the color is the default color. You are not asked for anything. You can change them later.

If a bookmark already starts at the cursor, <kbd>Ctrl</kbd>+<kbd>F2</kbd> removes it. When you remove a bookmark that has a name or a comment, the notification offers **Undo**.

The range of a bookmark has a colored background in the hex view, and the offset column shows a mark at its start. Point at the range to see its name and comment.

Keep these points in mind.

- Adding a bookmark does not mark the document as modified. The file contents do not change.
- Bookmarks follow your edits. If you insert bytes before a bookmark, it moves back. If you insert bytes inside its range, it gets longer. If the whole range is deleted, its length becomes 0 and the panel shows "Range deleted". The bookmark itself stays. Undo brings the position back.
- Bookmarks are stored outside the file. They are still there when you close and reopen the file. If the file changed outside HexEditor, you are told that positions may not match, and you can choose **Restore bookmarks** or **Discard**.
- One document can have up to 1,000,000 bookmarks.

### Editing a bookmark {#edit-bookmarks}

1. Put the cursor on the bookmark.
2. Select **Go > Bookmarks > Edit Bookmark**. You can also use **Edit bookmark** in the right-click menu of the hex view, or **Edit** in the panel.
3. The editor opens.

In the editor you can change the items below. Changes are saved as you type. There is no save button. Press <kbd>Esc</kbd> to close it.

- **Name**: 1 to 256 characters.
- **Start offset** and **Length**: you can type expressions. You can also give the range by **End (inclusive)**.
- **Color**: pick one of 8 colors, or use **Custom color**.
- **Group**
- **Number**: none, or 1 to 9.
- **Comment (Markdown)**: up to 64 KB. Use **Preview** to check how it looks. If a link points to `#0x1F00` or `#bm.header`, clicking it jumps to that position. `http` and `https` links open in your browser.

If you use only letters, digits and `_` in the name, such as `header`, you can use it in expressions, as in `bm.header+8`. If several bookmarks have the same name, the one created first is used.

### Numbered bookmarks {#numbered-bookmarks}

Bookmarks numbered 1 to 9 can be set and visited with a single key.

1. Press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>3</kbd> to set number 3 at the cursor.
2. From another place, press <kbd>Ctrl</kbd>+<kbd>3</kbd> to return to number 3.

If the number was on another bookmark, it moves to the new position. If you try to go to a number you have not set, the status bar tells you. The number pad keys work too.

In the menu, use **Go > Bookmarks > Set Numbered Bookmark** and **Go to Numbered Bookmark**.

### Moving between bookmarks {#next-previous}

Press <kbd>F2</kbd> for the next bookmark and <kbd>Shift</kbd>+<kbd>F2</kbd> for the previous one. The range is selected. At the end, the search wraps around, and the status bar says you are back at the start.

## The bookmarks panel {#bookmarks-panel}

Select **View > Panels > Bookmarks** to open the panel. You can also use **Go > Bookmarks > Bookmark List**.

The list has columns such as number, color, name, start, length, group and comment. Select a column heading to sort. The list starts in start-offset order. You can show, hide and reorder columns. Type a word in the box at the top to show only the rows whose name or comment contains it.

How to use the list:

- Select a row (click or <kbd>Enter</kbd>): go to that position and select the range.
- <kbd>↑</kbd> and <kbd>↓</kbd>: move between rows. The editor scrolls along.
- <kbd>Delete</kbd>: delete the selected bookmarks. **Undo** in the notification brings them back.
- Select several rows (<kbd>Shift</kbd>+<kbd>↑</kbd>, <kbd>↓</kbd> or <kbd>Ctrl</kbd>+<kbd>A</kbd>): delete, change the color or move to a group all at once.
- Right-click: **Go to**, **Rename**, **Edit...**, **Color**, **Delete**, **Move to group**, **Select these ranges**.

Turn on **All documents** in the panel to see the bookmarks of every open document together.

To show bookmark marks on the scroll bar, turn on **Show bookmarks on the scroll bar** in the settings.

## Groups and tags {#groups}

Put bookmarks into groups to keep them organized. Groups can be nested up to 8 levels.

1. Select **New group** in the panel to create a group.
2. Drag bookmarks onto the group. You can also use **Move to group** in the right-click menu.

A group has a name, a color, and a shown or hidden state.

- If you give a group a color, bookmarks without their own color use it.
- If you hide a group, its bookmarks disappear from the hex view and <kbd>F2</kbd> skips them. Press <kbd>Space</kbd> on a group row to show or hide it.
- When you delete a group, choose **Delete the bookmarks in it too** or **Move the bookmarks to the parent group**. **Undo** in the notification brings it back.

## Typed bookmarks and position expressions {#typed-bookmarks}

If you give a bookmark a type, the value at that position appears in the **Value** column of the list. Choose it in **Type** in the bookmark editor. You can use every type of the [data inspector](inspector.md) and the types of the templates you applied. **Endianness** can be set for each bookmark. You can also change the value from the value cell in the list.

The start offset does not have to be a fixed number. You can set it with a **position expression**. For example:

| Expression | Meaning |
| --- | --- |
| `u32le(0x3C)` | The 4-byte value at offset 0x3C (the position of the PE header) |
| `bm.pe + 0x18` | 0x18 bytes after the bookmark named `pe` |
| `align(bm.data + u16le(bm.data), 4)` | A position rounded up to a 4-byte boundary |

You can use functions that read values from the document, such as `u8(x)`, `u16le(x)`, `u32le(x)` and `s64be(x)`. `align(x, n)` rounds x up to a multiple of n.

A bookmark with a position expression is recalculated shortly after you edit the document, and its position is updated. The list marks it as an "expression". If the expression has an error (a mistake in writing, a circular reference or a read out of range), the row shows the reason and the position stays at the last value that worked. A `bm.name` reference can go through up to 32 levels.

## Converting between selections and bookmarks {#selection-conversion}

You can convert between selections and bookmarks, including selections of several ranges.

- **Selection to bookmarks**: select **Edit > Selection to Bookmarks**. Each range becomes a bookmark named "Selection 1" and so on. If there are two or more ranges, a group named "Selection" followed by the date and time is also created.
- **Bookmarks to selection**: select bookmarks in the panel and choose **Select these ranges** in the right-click menu. You can also use **Go > Bookmarks > Bookmarks to Selection**. Two or more bookmarks make a multi-range selection. Bookmarks of length 0 are left out.

You can also turn the results of Find All into bookmarks. A group is created for them automatically.

## Importing and exporting bookmarks {#import-export-bookmarks}

You can save bookmarks to a file and load them into another document.

1. Select **File > Import / Export Bookmarks > Export Bookmarks**. The **Export bookmarks** button in the panel does the same.
2. To load them, select **Import Bookmarks** and choose a file.

These formats are available.

| Format | Contents |
| --- | --- |
| HexEditor bookmarks (`.hexbm.json`) | Everything (groups, numbers, types, position expressions, comments) |
| CSV | Start offset, length, name, color, group, comment. You can edit it in Excel |
| wxHexEditor tags (XML) | Range, tag text, colors. Information that does not fit is dropped |

When you import, choose **Add to the current bookmarks** or **Replace the current bookmarks**. If you type an expression such as `+0x200` in **Offset shift**, every bookmark is loaded that far away. Bookmarks that fall outside the document are not loaded. When it finishes, you are told how many were loaded and how many were skipped. If the file is not valid, the place and the reason are shown and nothing is loaded.

When numbers collide, the imported bookmark wins and the number is removed from the existing bookmark.

## Annotations {#annotations}

An annotation is a label, a color and a description that a feature puts on a byte range in the hex view. A bookmark is one kind of annotation.

The sources are:

- Bookmarks
- Find All results
- Structure templates
- Scripts and plugins
- Analysis results (such as the detection of encrypted or compressed data)

Use **View > Annotations** to control the display.

- **Show Annotations**: turn all annotations on or off.
- **Annotation Column**: shows the labels of annotations that start on each row, to the right of the text column. If a row has several, a count is added, such as "header +2".
- One item per source: hide the sources you do not want to see.

For each source you can choose how annotations are drawn (background color, border or underline) with **Annotation style: ...** in the settings.

Point at a byte that has annotations to see all the annotations that include it. To read them by keyboard alone, use **Go > Bookmarks > Show Description at Cursor**.

### Position manager {#position-manager}

The position manager is handy when you want to write long descriptions as you work. Open it with **Go > Bookmarks > Position Manager**. It shows the bookmarks in start-offset order, next to the comment editor and preview of the selected bookmark. Selecting a row moves the editor there, and moving the cursor in the editor selects the matching row. You can export the contents to a Markdown or HTML file.

## Coloring rules {#coloring-rules}

A coloring rule colors the bytes that match a condition. It changes only how they look, not the data. For example, you can dim `00` bytes or make a file header stand out.

Select **View > Coloring Rules > Coloring Rules** to open the panel.

To make a rule:

1. Select **Add rule** in the panel.
2. Set the **Name** and the **Condition**.
3. Type the value of the condition.
4. Choose **Text color**, **Background** and **Border**. **Apply to** lets you pick the hex column, the text column, or both.

These are the kinds of condition.

| Condition | Example value |
| --- | --- |
| **Byte values** | `00`, `20-7E`, `7F, 80-FF` |
| **Offset range** | Start and end as expressions (for example `0x0` to `0x3F`) |
| **Period** | Interval, position and length (for example the first 2 bytes of every 512 bytes) |
| **Hex pattern** | `50 4B 03 04` (`??` is any single byte) |
| **Text** | A string, a character encoding and case sensitivity |
| **Regular expression** | A match can be at most 256 bytes long |
| **Number** | Type, endianness, value or range (for example `0..0xFF` as uint32) |

If the condition is not written correctly, the box shows the reason.

You can also do the following.

- **Create Rule from Selection**: select 1 to 256 bytes and run it to make a rule whose hex pattern is that content.
- Presets: **Dim zeros**, **Printable ASCII**, **Control characters**, **FF** and **High bit set**. They are off at first. Turn on the ones you need.
- There are two sets of rules. **All documents** applies to every document and **This document** applies to that document only.
- When several rules match the same byte, the rule higher in the list wins. Use **Move up (higher priority)** and **Move down** to change the order. Rules of this document win over global rules.
- One set can hold up to 256 rules.

Select **View > Coloring Rules > Legend** to open the legend panel. It lists the colors of the enabled rules and their counts in the visible range. **Count in the whole document** counts every match in the document, and **Next match of ...** and **Previous match of ...** for each rule take you to the matches.

In a high-contrast theme, rules are told apart by the shape of the border (solid, dashed, dotted, double) instead of by color.
