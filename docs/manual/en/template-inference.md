# Template inference

This page shows how to let HexEditor guess a structure template from a range of repeated records. You get a draft template that you can try at once and finish in the template editor.

Template inference works on ranges where records of the same shape follow each other, such as a table of entries or a log of fixed-size items. It finds the record length and a type for each field, and tells you how sure it is and why.

## Start the inference {#start}

1. Select the records in the hex view. Start the selection at the first byte of a record.
2. Right-click the selection and select **Infer template from selection**. You can also select **Analysis > Structure templates > Infer template from selection**, or run **Template: Infer from selection** from the command palette.

The **Template inference** panel opens and the inference starts at once. If nothing is selected, the range starts at the cursor and is 64 KB long (or up to the end of the document).

You can also start it from other places:

- In the **Statistics** panel, right-click a row of **Periods** and select **Infer a template with this length**. The panel opens with that record length.
- The file map opens the panel for a region of repeated records, with the region and its record length filled in.

## Choose the range and the record length {#inputs}

At the top of the panel you can change what is inferred. Select **Infer** (or press <kbd>Enter</kbd> in a box) to run it again.

- **Start** and **Length**: the range. You can type numbers or expressions, as in **Go to**. The range must be at least 32 bytes and hold at least 4 records.
- **Record length**: leave it empty for **Auto**, or type a length from 1 to 65,536 bytes.
- **Records**: leave it empty to use the **Whole range**, or type a number. If you give both the record length and the number of records, the range becomes "record length × records".
- **Endianness**: **Auto**, **Little** or **Big**. With **Little** or **Big**, only that byte order is examined.

When the record length is automatic, up to five candidates are listed with their score and the number of records. Select a candidate to infer again with that length. When candidates score almost the same, the shorter length is listed first, because multiples of the record length also score well.

Below the candidates you see how many records were used, the byte order, and how many bytes at the end do not fill a whole record.

If no fixed-length records are found, for example in random data, the panel says "No fixed-length records were found" and asks you to type the record length. Variable-length records, whose size depends on a length field, cannot be inferred.

The inference reads at most about 17 MB, however large the range is: the first 1 MB to find the record length, and a sample of up to 4,096 records for the fields. It runs in the background. If it takes longer than half a second, it appears in the processing center, where you can cancel it. Records that cannot be read are left out, and the panel says how many.

## Read the field table {#fields}

Each row of the table is one field: offset from the start of the record, size, name, type, kind, confidence and reason. Select a row to see the full reason and the other candidates below the table. In a narrow panel, the reason column is hidden and shown only for the selected row.

The kinds are padding, magic, constant, counter, date and time (UNIX time, 64-bit UNIX time, Windows FILETIME and MS-DOS date and time), offset, length, floating point, string (ASCII and UTF-16), flags, enumeration, integer and unknown. Each reason contains the numbers behind it, for example "In 1,000 of 1,000 records, value = 0 + 1 × record number."

Rows with low confidence have a warning sign (⚠) and the word "low". Treat them as a starting point.

Names are made from the kind and the offset, such as `counter_04` or `name_1C`. Padding has no name.

## Change the fields {#edit-fields}

- **Rename**: right-click a row (or press <kbd>Shift</kbd>+<kbd>F10</kbd>) and select **Rename**, or double-click the row. Type the name and press <kbd>Enter</kbd>. A name that is not a valid identifier, a reserved word or a name already in use gets a red frame and the reason, and is not accepted.
- **Type**: select another candidate, or any type that fits the size: integers, floating point, date types, strings, bytes or padding.
- **Merge with previous field**: joins the row with the one above it.
- **Split**: divides the field after the number of bytes you type.

After merging or splitting, the rules are applied again to the new fields. Changed rows are marked "changed". When you select **Infer again**, your names and types are kept for fields that have the same offset and size.

While the panel has the focus, press <kbd>Ctrl</kbd>+<kbd>Z</kbd> and <kbd>Ctrl</kbd>+<kbd>Y</kbd> to undo and redo changes in the table. This history is separate from the document's undo history.

## The preview in the hex view {#preview}

While the panel is open, the fields of the visible records are colored in the hex view, in the same way as an applied template, with a line at the start of each record. In high contrast mode, the fields get borders instead of colors.

- Select a row in the table to select that field in the record under the cursor (or in the first record).
- Move the cursor in the range to select the row of the field under the cursor.

The preview disappears when you close the panel or select **Apply**.

## Use the draft {#draft}

The **Draft (HXT)** box shows the template made from the table. It is updated shortly after each change. The draft starts with a comment that says it was inferred, with the document name, range, record length, number of records and date. Each field has a comment with its kind, confidence and reason, written in your display language.

- **Apply**: applies the draft to the document at the start of the range, without saving it. The result tree shows it as "(unsaved)". Select **Apply** again to replace that application with the new draft.
- **Open in editor**: opens the draft in a new, unsaved [template editor](templates.md#template-editor) tab for the same document. When you save, it is saved as `inferred-<document name>.hxt` in your templates folder, and then it appears in the template list. Later changes in the panel do not change that tab.
- **Copy**: copies the draft to the clipboard.

The panel keeps its inputs and table for each document until you close the document. They are not saved with the document.

## Limits {#limits}

Template inference does not find:

- variable-length records or type-length-value sequences,
- nested structures, bit fields or unions (fields are listed flat),
- a header before the records (the range starts at the first record),
- what an offset points to (offsets are integers shown in hex),
- meaningful names.
