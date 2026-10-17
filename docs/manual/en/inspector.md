# Data inspector

The data inspector is a panel that reads the bytes at the cursor as integers, decimals, characters, dates and more, and lists the results. You can also change a value right there. Use it when you do not know a file format, or when you want to check a number.

## Showing the inspector {#show-the-inspector}

Open or close the panel in any of these ways.

- Press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>I</kbd>.
- Select **View > Panels > Data inspector**.
- Run **View: Toggle data inspector** from the command palette.

The inspector reads from the byte at the cursor. When there is a selection, it reads from the start of the selection. To read from the cursor even when there is a selection, turn on the setting **Inspector: use the cursor when there is a selection**.

The top of the panel shows where reading starts, for example `From 0x00001F00`. When you move the cursor, the values update right away.

If fewer bytes are left before the end of the file than the type needs, the value shows "—". Point at it to see the reason, such as "Not enough data (4 bytes needed, 2 left)".

When you point at a row or select it, the bytes that row reads are highlighted in the hex column and the text column. Your selection does not change. To turn this off, turn off the setting **Highlight the inspector target**.

Rows are grouped by kind: **Integers**, **Floating point**, **Variable-length integers**, **Characters and strings**, **Date and time**, **Other** and **User-defined**. Select a group heading to collapse it. While the panel is closed, nothing is calculated.

Right-click a row for this menu.

- **Copy value**
- **Copy all rows** (as tab-separated text)
- **Edit value**
- **Follow as pointer**
- **Move as an array of this type**
- **Hide this row**

## Value types {#types}

These rows are shown at first.

| Group | Rows |
| --- | --- |
| Integers | int8, uint8, int16, uint16, int32, uint32, int64, uint64 |
| Floating point | float, double |
| Characters and strings | ANSI, UTF-8, UTF-16 |
| Date and time | Unix time (32-bit), FILETIME, DOS date and time |
| Other | Binary (8-bit), GUID (Windows) |

Other types are hidden at first. You can show them as described in [Showing, hiding and ordering rows](#customize).

- Integers: int24 / uint24, int48 / uint48, int128 / uint128.
- Floating point: half, bfloat16, long double (80 bit), Real48, and fixed-point rows (you set the bit counts).
- Variable-length integers: ULEB128, SLEB128, SQLite varint.
- Strings: NUL-terminated (ANSI / UTF-8 / UTF-16) and Pascal strings (1-, 2- or 4-byte length).
- Date and time: DOS date, DOS time, Unix time (32-bit unsigned, 64-bit, milliseconds), OLE Automation date, HFS+ date, APFS date, Mac absolute time (Cocoa), SQL Server datetime and smalldatetime, .NET DateTime, and a date written in decimal digits.
- Other: Binary (16, 32 and 64-bit), UUID (RFC 9562), and colors (RGBA8, BGRA8, RGB8, RGB565).

Bytes that cannot be read show **Invalid** with the reason, such as "continuation byte" or "incomplete sequence". UTF-8 and UTF-16 strings read up to the first 4 KB.

Dates are shown in local time or in UTC. Switch with **Show dates in UTC** on the panel. Under **Inspector: date format** in the settings, choose **Regional format** or **ISO 8601**.

## Endianness {#endianness}

For types of two bytes or more, the byte order (endianness) changes the value. For example, `01 02 03 04` is 67,305,985 as a little-endian uint32 and 16,909,060 as a big-endian uint32.

Choose the order with **Byte order** on the panel.

- **Follow the document** (default): uses the document's endianness.
- **Little endian**
- **Big endian**

If you choose little or big endian, the choice applies only inside the inspector. The status bar and the hex view keep their own endianness. HexEditor remembers the choice for each document.

You can also run **Inspector: Toggle endianness** from the command palette to switch to the opposite order.

Some types ignore the byte order. SQLite varint is always big endian. The first three fields of a GUID (Windows) are always little endian. These rows are marked "(fixed)".

You can also choose how numbers are shown.

- **Integer format**: **Decimal** (default), **Hexadecimal** (padded with zeros, such as `0x0000002A`) and **Octal**. Decimal numbers use the digit grouping of your region. To turn it off, use **Inspector: digit grouping** in the settings.
- **Floating-point format**: **Shortest exact** (default), **Exponent** and **Hex float** (`0x1.8p+1`).

## Changing values from the inspector {#edit-values}

1. Put the cursor where you want to change data.
2. Select a row in the inspector and press <kbd>Enter</kbd>. You can also double-click the value, or right-click and select **Edit value**.
3. Type the new value. Next to the box, the bytes that will be written appear in hex.
4. Press <kbd>Enter</kbd> to apply. Press <kbd>Esc</kbd> to cancel.

In an integer box you can type an expression such as `0x100*4` as well as a number. Type floating-point values like `1.5`, `1e-3`, `inf`, `nan` or `0x1.8p+1`. Type dates as `2026-10-07 12:34:56`. A GUID takes 32 hex digits, and a color looks like `#FF8000`.

Keep these points in mind.

- A change always overwrites, even in insert mode. It replaces as many bytes as the type has, and the file length does not change.
- If the value would go past the end of the document, you cannot apply it. The reason appears under the box.
- Out-of-range values and badly written input also cannot be applied. The reason appears under the box.
- Variable-length integers and strings cannot change their byte count. You can only write a value that fits the original length.
- One change is undone with one <kbd>Ctrl</kbd>+<kbd>Z</kbd>.
- The cursor does not move after a change.
- You cannot edit values in a read-only document.

## Showing, hiding and ordering rows {#customize}

You can show only the rows you need and change their order.

1. Select **Rows** on the panel.
2. Check the rows you want to see.
3. To change the order, select a row and use **Move up** or **Move down**. In the **Rows** list you can also drag rows.

You can also hide a row with **Hide this row** in its right-click menu.

There are options for some types.

- **Also show in the opposite byte order**: adds a row such as `int32 (BE)` in the opposite byte order.
- **Add a fixed-point row**: set the total bits, the fraction bits and whether it is signed, then select **Add**. Select **Remove row** to delete it.
- The date and time format.

You can pick a set of rows as a preset.

| Preset | Contents |
| --- | --- |
| **Basic** | The rows shown at first |
| **All** | Every type |
| **Date and time** | The date and time types |
| **Embedded** | Integers, fixed point, half and binary |

**Reset to default** returns to **Basic**. To keep your own set, type a name in **Preset name** and select **Save as preset**. The row layout is shared by the whole app, not per document.

## User-defined types {#user-types}

You can add your own types to the **User-defined** group of the inspector. A type with several fields appears as a row you can expand.

There are two ways to define a type.

- Define the type in a [template](templates.md) and mark it to be shown in the inspector. Put the definition files in the `inspector` folder of the settings folder. Templates you register under **Inspector templates** in the settings also work.
- Add the type with a [plugin](plugins.md). The row appears only while the plugin is enabled.

These rules apply to user-defined types.

- A type reads at most 4 KB from the start position.
- Each type has a time limit (50 ms by default). If it takes longer, the row shows **Timed out**. It runs again when the start position changes.
- An error in one type does not affect other rows. The row shows "Error: ...".
- When you save the definition file again, the rows update without restarting the app.
- You can edit a value only for types that define a conversion from text to bytes.

## Following pointers and arrays {#pointers}

### Follow as pointer {#follow-pointer}

Some numbers in a file point to another place. You can treat the value of an integer row as a position and jump there.

1. Put the cursor on the number that points to a position.
2. In the inspector, right-click that integer row.
3. Select **Follow as pointer** and choose what the number means.

| Choice | Where you go |
| --- | --- |
| **Absolute (offset)** | The value is the offset itself |
| **Absolute (address)** | The value is a displayed address. You go to the offset after the base address is subtracted |
| **Relative to the cursor** | Start position + value |
| **Relative to the end of the value** | Start position + type size + value |
| **Relative to a base...** | Base you type + value. Type the base as an expression such as `0x400000` or `bm.header`, then select **Go** |

A negative value in a signed row moves backward. If the target is outside the document, HexEditor does not move and tells you. The jump is saved in the jump history, so <kbd>Alt</kbd>+<kbd>←</kbd> takes you back.

You can also run **Inspector: Follow as pointer** from the command palette.

### Move as an array {#follow-array}

When equally sized items are lined up, you can move the cursor one item at a time.

1. Right-click the row for the item type and select **Move as an array of this type**.
2. Array controls appear on the panel. Use **Previous element** and **Next element** to move one item.
3. Type a number in **Element number** to go to that item. Negative numbers work too.
4. If the item size differs from the type size, type an expression such as `0x28` in **Element size**.
5. To finish, select **Exit array mode**, or press <kbd>Esc</kbd> in the panel.

The position where you entered array mode is element 0. When the cursor is not on an item boundary, the panel shows "(not on an element boundary)". Moves in array mode are not saved in the jump history.
