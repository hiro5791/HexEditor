# Converters and reference tools

These tools turn a value into other forms and back. Each one opens as a panel from the **Tools** menu, works without an open document, and can read from or write at the cursor of the active document.

## Base and type converter {#converter}

Select **Tools > Base and type converter**. Type a value in any field, and the other fields update.

- **Bytes (hex)** is the base of all conversions. It accepts the same forms as the search bar, such as `DE AD BE EF` or `0xDE,0xAD`.
- **Hexadecimal**, **Decimal (unsigned)**, **Decimal (signed)**, **Octal** and **Binary** show the bytes as a whole number. This works for 1 to 8 bytes.
- **half**, **float** and **double** show the bytes as an IEEE 754 number when there are 2, 4 or 8 bytes.
- **ASCII** shows bytes outside the printable range as `\xNN`.
- **UTF-8**, **UTF-16 LE / BE**, **UTF-32 LE / BE** and **Text in the code page** show the bytes as text. Bytes that are not valid in the encoding show a replacement character and a warning. Choose the code page in **Code page**. Shift_JIS and the EBCDIC pages are available.
- **Base64** shows the bytes in Base64.

When you type text in a text field, HexEditor converts it with that encoding and shows the bytes. If a character cannot be converted, the field gets a red border and nothing changes.

**Byte order** switches how the number fields read the bytes. The bytes do not change.

The buttons **Read from cursor**, **Write at cursor** and **Send to search bar** work with up to 64 bytes. Reading uses the selection if there is one. Writing can be undone with <kbd>Ctrl</kbd>+<kbd>Z</kbd>.

## Floating-point visualizer {#float}

Select **Tools > Floating-point visualizer**, or right-click a float or double row in the data inspector and select **Open in floating-point visualizer**.

1. Choose the **Format**: half, bfloat16, float, double or x87 extended.
2. Enter a **Decimal value** such as `0.1`, `inf` or `nan`, or **Bytes (hex)**. HexEditor rounds a decimal value to the nearest value the format can hold and shows the rounding error.
3. The bit layout shows the **Sign**, **Exponent** and **Mantissa** parts with labels above them, so you can tell them apart without colors. Click a bit to flip it.

Below the bits you see the exponent as bits, as a stored number and as a real exponent. You also see the class (normal, subnormal, zero, infinity, NaN with its payload), the shortest decimal form that gives the same bits, the exact decimal value, the value in hexadecimal floating-point form such as `0x1.921fb6p+1`, the next larger and smaller values, and the ULP. When you change the format, the same value is shown in the new format with the rounding error.

## Character table {#chars}

Select **Tools > Character table**. Choose a character encoding. The table shows the 256 byte values of the encoding, 16 per row. Control characters show names such as `NUL` and `LF`. Bytes with no character are crossed out.

- For encodings with two-byte characters, such as Shift_JIS, select a lead byte to open a second table for the second byte.
- Select a cell to see the byte in hex, decimal, octal and binary, the character, its Unicode code point and English name, and its UTF-8 and UTF-16 bytes. The **UTF-16 byte order** button switches between little-endian (LE) and big-endian (BE).
- Press <kbd>Enter</kbd>, double-click, or select **Insert at cursor** to type the byte at the cursor. It follows the current overwrite or insert mode and can be undone.
- Use the arrow keys to move between cells. A screen reader reads each cell as "0x41, A, LATIN CAPITAL LETTER A".
- **List view** shows one byte per row. **Find character** finds a cell by character, name or code point such as `U+3042`.

## Color picker {#color}

Select **Tools > Color picker**, or right-click a color row in the data inspector and select **Open in color picker**.

1. Pick a color, or type it as `#RRGGBB` or `#AARRGGBB`. The swatch also shows the value as text.
2. The list shows the bytes of the color in each pixel format, such as RGB888, BGRA8888 and RGB565. 16-bit formats have a button to switch between little-endian and big-endian.
3. **Read from cursor** reads the bytes at the cursor in the format you choose in **Format for read, write and search**. **Write at cursor** writes the bytes. **Find this color** sends the bytes to the search bar.

## Date and time converter {#datetime}

Select **Tools > Date and time converter**. Type a date in **Date and time (ISO 8601)**, or a number or bytes in any format row, and the other rows update. The formats are Unix time (32 and 64 bit; seconds, milliseconds, microseconds and nanoseconds), FILETIME, DOS date and time, OLE Automation date, HFS+, APFS, Java, .NET ticks, GPS time and Mac absolute time. They are the same as the date types in the data inspector.

- Choose the **Time zone**: UTC, local time or any other time zone. The date is shown in your regional format and in ISO 8601.
- **Now** fills in the current time.
- A value that a format cannot hold, such as a DOS date before 1980, shows "Can not be represented".
- **Read from cursor** and **Write at cursor** use the format and byte order you choose.
