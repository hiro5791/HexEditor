# Data operations

The **Data** menu has features that rewrite the selected bytes in one go. For example: add 1 to every byte, apply an XOR key, swap the byte order, or convert the character encoding.

Every operation treats the bytes as a row of equally sized "elements" and calculates each element. The result is one edit, so one <kbd>Ctrl</kbd>+<kbd>Z</kbd> undoes it. The file length does not change. The exceptions are inserting and deleting bits (described below) and converting the character encoding, which can change the length.

## Using the data operations dialog {#dialog}

1. Select the bytes to work on. To work on the whole document, you do not need a selection.
2. Select **Data > Data operations...**.
3. Choose the **Category** and the **Operation**.
4. Set the options and check the preview below.
5. Select **Run**.

To repeat the previous operation with the same settings, select **Data > Repeat last data operation**. The dialog does not open, and it runs on the current selection. In the command palette you can run each operation by itself, such as **Data operation: Add**.

These settings are common to all operations.

| Setting | Meaning |
| --- | --- |
| **Target** | **Selection**, **Whole document** or **Range** (start and length). With several ranges selected, elements are counted in each range |
| **Element type** | **Unsigned integer**, **Signed integer** or **Floating point**. Floating point works only for arithmetic and clamping |
| **Element size (bytes)** | 1, 2, 4 or 8 for integers; 4 or 8 for floating point. 1 at first |
| **Byte order** | **Little endian** or **Big endian**. The document's setting at first |
| **Operand** | The value used by the operation. You can type `0x10` or an expression. Floating point also accepts `0.5` and `1e-3` |
| **Operand increment** | A value added to the operand after each element. 0 at first. For example, with Add, operand 0 and increment 1, the i-th element gets i added |
| **Process N elements** and **then skip M elements** | Repeats "process N, skip M". 1 and 0 at first |
| **Increment across ranges** | With several ranges selected, choose **Restart for each range** or **Continue across ranges** |

The preview shows the first 64 bytes of the target before and after, and how many bytes change. If the length of the range is not a multiple of the element size, a note says the last N bytes are too short to be an element, and they are left unchanged.

The result is applied to the document only after it is completely ready. If you cancel, or if something fails, the document does not change. Ranges over 4 MiB run in the background with progress. Large ranges need temporary free space of the same size. If there is not enough, the operation does not run and tells you how much is needed. If the settings would not change any data, you see "The data does not change."

Data operations are not available in a read-only document.

### Using the right-click menu {#context-menu}

Select bytes, right-click and choose **Data operation** to open a submenu.

- **Data operations...**: opens the data operations dialog.
- **Arithmetic**, **Bitwise**, **Shift / rotate**, **Reorder**, **Clamp** and **Insert / delete bits**: the operations of each category. Choosing one opens the dialog with that operation already selected.

Operations in **Reorder** run at once, without opening the dialog, when something is selected. **Data operation** is not available for a read-only document.

## Arithmetic and bitwise operations {#arithmetic}

### Arithmetic operations {#arithmetic-ops}

Choose the **Arithmetic** category.

| Operation | Result |
| --- | --- |
| **Add** | x + operand |
| **Subtract** | x − operand |
| **Reverse subtract** | operand − x |
| **Multiply** | x × operand |
| **Divide** | x ÷ operand (integers round toward zero) |
| **Reverse divide** | operand ÷ x (elements where x is 0 are left unchanged) |
| **Modulo (remainder)** | The remainder after dividing x (integers only) |
| **Negate** | −x (no operand) |
| **Absolute value** | \|x\| (signed integers and floating point only) |

For integers, choose what happens when the result does not fit with **On overflow**.

- **Wrap** (default): the result is cut to the element size. With an unsigned byte, `FF` plus 2 gives `01`.
- **Saturate**: the result stops at the smallest or largest value. In the same example it stays `FF`.

When it finishes, you are told how many elements overflowed. For floating point, you are told how many became NaN or ∞. You cannot divide by 0. If the increment would make the divisor 0 along the way, you are told before it runs.

### Bitwise operations {#bitwise-ops}

Choose the **Bitwise** category. **AND**, **OR**, **XOR**, **NOT**, **NAND**, **NOR** and **XNOR** are available.

For the **Operand**, choose one of these.

- **Number**: a value of the element size.
- **Byte key**: enter it as hex, text, a file or from the clipboard. It can be 1 byte to 16 MiB. Elements become 1 byte and the key repeats. **Key starts at** chooses whether the key starts at the start of the range or is counted from offset 0. **Key increment per round** adds a value to each byte of the key every time the key goes around once.

Running XOR twice with the same settings returns the original data. This is handy for examining data that was obscured with XOR.

## Shift and rotate {#shift-and-rotate}

Choose the **Shift / rotate** category.

| Operation | Meaning |
| --- | --- |
| **Shift left** | Moves bits left. New bits are 0 |
| **Shift right (logical)** | Moves bits right. New bits are 0 |
| **Shift right (arithmetic)** | Moves bits right. New bits copy the sign bit |
| **Rotate left** | Rotates bits left. Bits pushed out come back on the right |
| **Rotate right** | Rotates bits right. Bits pushed out come back on the left |

Enter the amount in **Bits**. For example, rotating the byte `81` left by 1 gives `03`, and rotating it right by 1 gives `C0`.

If you set a **Rotation increment**, the amount grows with each element. For the i-th element it is the remainder of (bits + i × increment) divided by the number of bits in the element. For example, rotate left with bits 1 and increment 1 turns `01 01 01` into `02 04 08`.

To move a whole range as one string of bits, use inserting and deleting bits (below), not shifting.

## Byte swap and reverse {#byte-swap}

Choose the **Reorder** category. It is useful for converting the byte order.

| Operation | Example |
| --- | --- |
| **Byte swap (16 bit)** | Reverses every 2 bytes. `01 02` → `02 01` |
| **Byte swap (32 bit)** | Every 4 bytes. `01 02 03 04` → `04 03 02 01` |
| **Byte swap (64 bit)** | Every 8 bytes |
| **Swap 16-bit words (within 32 bit)** | Swaps the two halves of 4 bytes. `01 02 03 04` → `03 04 01 02` |
| **Reverse byte order** | Reverses the whole range. `01 02 03` → `03 02 01` |
| **Reverse bit order (each byte)** | Reverses the bits of each byte. `01` → `80` |
| **Swap nibbles (each byte)** | Swaps the upper and lower 4 bits. `12` → `21` |

For byte swaps, the operation decides the element size. If bytes are left over, they stay unchanged and a note appears. You can also run these from the command palette, for example **Data operation: Byte swap (32 bit)**.

Reversing a whole range works on large ranges too. It runs in the background.

## Limiting to minimum and maximum values {#limits}

Choose the **Clamp** category. It keeps each element inside the range you give.

1. Enter the **Minimum** and the **Maximum**. Either one can be left out. Select **No limit** to leave that side free.
2. Elements smaller than the minimum become the minimum, and elements larger than the maximum become the maximum.

For example, with an unsigned byte, minimum `0x10` and maximum `0x80`, `00 50 FF` becomes `10 50 80`. NaN in floating point is left alone. If the minimum is larger than the maximum, it cannot run. When it finishes, you are told how many elements changed.

## Inserting and deleting bits {#bit-insert-delete}

Choose the **Insert / delete bits** category to treat the data as one string of bits and shift it bit by bit. Use it to realign data that is off by some bits.

1. In **Operation**, choose **Insert bits** or **Delete bits**.
2. Enter the **Byte offset**, the **Bit in the byte** (7 = most significant) and the **Number of bits**. For insertion, also set the **Inserted bit value** (0 or 1).
3. Choose what to **Shift**. **Within the selection only** does not change the length, and bits pushed out are dropped. **To the end of the document** changes the length.
4. Use **Bit order** to count from the most significant bit or from the least significant bit.

Everything after the position is rewritten, so if the shifted range is larger than 1 GiB, a confirmation appears. In a document whose length cannot change, use **Within the selection only**.

## Converting the character encoding {#encoding-conversion}

Reads the selected bytes as text in one encoding and rewrites them in another. For example, you can change Shift_JIS to UTF-8, or convert between ASCII and EBCDIC.

1. Select the bytes to convert.
2. Select **Data > Convert encoding...**.
3. Choose **From** and **To**. At first, **From** is the current display encoding and **To** is UTF-8.
4. Change the settings below if you need to.
5. Check the preview and select **Convert**.

| Setting | Meaning |
| --- | --- |
| **Bytes that cannot be decoded** | **Stop at the first one with an error** (default), **Replace with U+FFFD**, **Keep them unchanged** |
| **Characters the target cannot represent** | **Stop at the first one with an error** (default), **Replace with ?**, **Replace with a character** |
| **Remove the BOM at the start of the source** | On at first |
| **Add a BOM to the result** | Off at first |
| **Newline conversion** | Keep as is (default), CRLF, LF, CR |
| **Unicode normalization** | **None** (default), NFC, NFD, NFKC, NFKD |

The preview shows the first bytes before and after, and the expected length after conversion (such as "12 bytes → 18 bytes").

With some encodings the length changes. In that case the selection is replaced by the converted result. In a document whose length cannot change, it runs only if the length stays the same.

If it stops with an error, the document does not change. The offset and the character of the first problem are shown, and **Go to the offset** takes you there.

## Changing case {#case-conversion}

Change the case of letters with **Data > Change case**. Choose **Uppercase**, **Lowercase** or **Swap case**.

By default, only ASCII letters (`a` to `z` and `A` to `Z`) are changed, and the length stays the same. The "follow the encoding" method reads the bytes in the display encoding and converts them with Unicode rules. The command palette has a command for each method, such as "Swap case (ASCII letters only)".
