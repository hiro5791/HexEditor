# Calculator and programmer mode

The calculator works out an expression and shows the result in hexadecimal, decimal, octal and binary at the same time. It writes expressions the same way as the offset boxes, and it can use values from the open document. Programmer mode adds a bit width, a table of bits and bit operations.

## Open the calculator {#open}

Select **Tools > Calculator**, or run **Tools: Calculator** from the command palette. The calculator opens as a panel, so it does not get in the way of editing. You can use it without an open document.

## Calculate an expression {#expressions}

1. Type an expression in **Expression**, for example `0x1F00 + 10h * 2`. The result updates as you type. While the expression is not complete, the previous result is shown dimmed.
2. Press <kbd>Enter</kbd> to add the expression to the history.

Numbers, units (`4K`, `1.5G`), operators, names (`cur`, `end`, `sel.len`, `bm.header`) and read functions (`u32be(0x3C)`) work the same as in the offset boxes. Without an open document, names and read functions give an error.

The calculator also understands these extras. They only work in the calculator.

| You type | Meaning |
| --- | --- |
| `ans` | The previous result |
| `u16(x)`, `u32(x)`, `u64(x)`, `s16(x)` to `s64(x)` | Read a value at offset `x`, in the byte order of the active document |
| `min(a, b)`, `max(a, b)`, `abs(a)` | Smaller, larger and absolute value |
| `align(a, n)` | Round `a` up to a multiple of `n` |
| `popcnt(a)`, `clz(a)`, `ctz(a)` | Number of 1 bits, leading zero bits and trailing zero bits |
| `rol(a, n)`, `ror(a, n)`, `bswap(a)` | Rotate bits and reverse the byte order |
| `==`, `!=`, `<`, `<=`, `>`, `>=` | Compare. The result is 1 or 0 |

Whole numbers are calculated as 64-bit signed numbers. If the result does not fit, **Overflow** appears and the value wraps around. A number with a decimal point, such as `1.5 + 1`, is calculated as a decimal number. The decimal point is always `.`, whatever your regional settings. Dividing by zero is an error, and so is shifting by 64 bits or more.

When an expression is wrong, the box gets a red border and a message that says where the problem is.

## Read the result {#results}

The table shows the result in hexadecimal, decimal, octal, binary (in groups of four digits) and as a file size such as `1.5 GiB`. A negative result also shows its unsigned decimal value. The decimal value follows your regional settings for the thousands separator. The **Copy** button next to each row copies the value without separators.

## Use the result {#use}

- **Go to this position** moves the cursor to the result.
- **Use as selection length** selects that many bytes from the cursor.
- **Write at cursor** writes the result at the cursor. Choose the size (1, 2, 4 or 8 bytes) and the byte order first. One <kbd>Ctrl</kbd>+<kbd>Z</kbd> undoes the write.

## History {#history}

The calculator keeps the last 1,000 expressions with their results, and keeps them after you restart HexEditor. Select an entry to put its expression back in the box.

## Programmer mode {#programmer}

Select **Programmer**, or run **Tools: Calculator (programmer mode)** from the command palette.

1. Choose the **Bit width** (8, 16, 32 or 64 bits) and whether numbers are **Signed**. Results wrap at the bit width. For example, with 8 bits and signed numbers, `0x7F + 1` is `-128`.
2. The **Bits** table shows every bit, with its number below it. Click a bit, or move to it and press <kbd>Space</kbd>, to flip it. The other radixes update. Use <kbd>←</kbd> and <kbd>→</kbd> to move between bits.
3. Drag across bits, or hold <kbd>Shift</kbd> and press <kbd>←</kbd> or <kbd>→</kbd>, to select a range. The value of that bit field and an expression that picks it out, such as `(x >> 4) & 0xF`, appear below the table.
4. The buttons apply **AND**, **OR**, **XOR**, **NAND** and **NOR** with the **Operand**, and **NOT**, shifts, rotations, **Negate** and **Byte swap** to the current value.
5. **Read from cursor** loads as many bytes as the bit width from the cursor, in the byte order you choose.

Each bit has a name for screen readers, such as "Bit 31, value 1".
