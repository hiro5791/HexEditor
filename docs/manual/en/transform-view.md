# Transform view

A transform view shows the data through a transformation, such as XOR with a key, without changing the data. Use it to read and edit data that is hidden by a simple transformation, without decoding the file first.

## XOR view {#xor-view}

1. Select **Data > Transform view > XOR view...**.
2. Type the **Key** in hex, such as `5A` or `5A 3C` (1 to 256 bytes).
3. If the key does not start at offset 0, type the **Key start offset**.
4. Press <kbd>Enter</kbd> or select **OK**.

With a key of several bytes, the byte at offset *o* is shown XORed with key byte number (*o* − key start) mod key length.

## Defining a transform {#define}

**Data > Transform view > Transform view settings...** opens a dialog where you can chain up to eight operations. They are applied from the top.

| Operation | Notes |
| --- | --- |
| **XOR** | With a byte key, or a number of the element size |
| **Add**, **Subtract** | Wrap around at the element size |
| **NOT** | Inverts all bits |
| **Rotate left**, **Rotate right** | By a number of bits |
| **Byte swap (16, 32, 64 bit)**, **Reverse bit order**, **Swap nibbles** | Reorder bytes or bits |

You can set the element size, byte order and operand increment as for [data operations](data-operations.md), the **Range** (the whole document by default) and the **Key start**. The preview shows 16 bytes at the cursor before and after. Give a **Name** and select **Save with this name** to reuse the transform in other documents. The transform in use is remembered for the file and restored when you open it again.

Plugins can add their own transforms.

## Working in a transform view {#working}

While a transform view is active:

- The top-left corner of the hex view shows a badge such as "Transform view: XOR 5A", and the status bar shows the same text. The editor has a dashed frame.
- What you type, paste or fill is treated as the displayed value. It is transformed back before it is written to the data.
- You cannot insert or delete bytes, because that would move the data against the key.
- Search, the data inspector, hashes, statistics, structure templates, disassembly and YARA scans work on the displayed data, and show "Transform view: ..." to say so. Exported results record the transform too. Printing, decompressing and decrypting also use the displayed data. In the data inspector, turn on **Interpret original data** to read the bytes before the transform.
- Saving, creating and applying patches, and splitting, joining and interleaving use the original data. A patch that changes the length cannot be applied while the view is active.
- Copy puts the displayed data on the clipboard. To copy the original data instead, change **Copy in a transform view** in the editing settings.
- The file is not changed, and turning the view on or off is not an edit in the undo history.

If a transform from a plugin cannot be reversed, the view is read-only. Clear the transform view to edit.

To leave the view, select **Data > Transform view > Clear transform view**.

## Writing the transform to the data {#apply-to-data}

**Data > Transform view > Apply this transform to the data...** writes the transformation into the data. For a single operation, the [data operations](data-operations.md) dialog opens with the same operation, key and range. When you select **Run**, the transform view is cleared and the operation is applied. For chained operations, you are asked to confirm instead.
