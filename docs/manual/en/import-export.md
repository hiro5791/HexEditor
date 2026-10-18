# Import and export

Read data from text in another format, or write data out in another format. You can handle microcontroller firmware (Intel HEX, S-record), Base64, source code arrays, dumps to paste into documents, and more. This page also explains how to save a selection to a file and how to open a selection in a new tab.

## Export formats {#export-formats}

Export writes the contents of a document to a file in another format. The original document does not change.

1. Select the range to write out. To export the whole document, you do not need a selection.
2. Select **File > Export > Export...**. For the selection only, use **File > Export > Export selection...**. You can also run **File: Export** from the command palette.
3. In **Export**, choose the range: **Whole document**, **Selection**, or **Offset range** (type the start and the end as expressions). If several ranges are selected, choose **A separate file for each range** or **Join into one file**. For separate files, use `{base}`, `{index}`, `{start}` and similar in the **File name format** so that each range gets its own name.
4. Choose the **Format**. The settings for that format appear.
5. Check the **Preview** and the estimated output size.
6. Choose **Save to** and select **Export**.

You can choose from these formats.

| Format | Use |
| --- | --- |
| **Intel HEX**, **Motorola S-record** | Microcontroller firmware and similar. Formats that carry addresses |
| **Base64**, **Base32**, **Ascii85**, **UUEncode**, **Quoted-Printable**, **URL encoding** | Turn bytes into text |
| **Hex text**, **Decimal text** | Text of hexadecimal or decimal numbers |
| **Source code** (C, C++, C#, Java, JavaScript, Python, Rust, Go, Pascal, VB.NET) | Arrays to embed in a program |
| **Dump** (text, HTML, RTF, TeX, Markdown) | A hex dump shaped like the screen, to paste into documents and reports |
| **Binary** | Writes the range as raw bytes |

The main settings are below. Each format remembers its previous values.

- **Intel HEX**: **Bytes per record** (16 at first), **Address format** (Automatic, I8HEX, I16HEX, I32HEX), **Start address**, **Execution start address**, **Uppercase hex**. You can also omit gaps (runs of the same value).
- **Motorola S-record**: bytes per record, **Address format** (Automatic, S1, S2, S3), **Header (S0)**, **Write the record count (S5/S6)**.
- **Base64 and similar**: **Characters per line** (0 means no line breaks), padding, **URL-safe**.
- **Hex text**: **Bytes per line**, **Separator**, **Prefix** (such as `0x` or `\x`), upper or lower case, **Offset at the start of each line**.
- **Source code**: **Variable name**, **Bytes per element**, **Decimal values**, values per line, **Header file with include guard** (C / C++), **Add a length constant**, **Comment with the file name, range, and date**.
- **Dump**: **Bytes per row**, **Group size**, **Offset column**, **Hex column**, **Text column**, **Offset radix**. For HTML, RTF and TeX you can turn on **Colors (modified bytes, bookmarks)**. HTML also has a **Dark color scheme**.

Text formats also let you choose the **Line break** (CRLF or LF) and the **Text encoding** (UTF-8 with or without BOM, ASCII). The defaults are CRLF and UTF-8 (no BOM).

If the output is expected to exceed 1 GB, the estimated size is shown and you are asked to confirm. Text formats are 2 to 5 times larger than the data. The export runs in the background with progress. If you cancel, no half-written file is left behind. If a file with the same name exists, you are asked whether to replace it.

## Import formats {#import-formats}

Import reads a text file in another format and turns it into bytes.

1. Select **File > Import...**. You can also run **File: Import** from the command palette. You can drag and drop a file onto the dialog too.
2. Choose the **File to import**. The format is chosen automatically from the start of the file. If it is wrong, choose the right one in **Format**.
3. Check the settings for that format.
4. Choose **Import into**:
   - **A new document** (default)
   - **Insert at the cursor**
   - **Overwrite from the cursor**
5. Check the **Preview** for the first converted bytes, the size, the address range and the number of warnings.
6. Select **Import**.

Inserting or overwriting at the cursor is undone with one <kbd>Ctrl</kbd>+<kbd>Z</kbd>.

The importable formats are the export formats, except the dumps.

- **Intel HEX** and **Motorola S-record**: the checksum of every line is checked. **Address placement** is **Lowest address at offset 0** (default) or **Address as offset**. Addresses with no data (gaps) are filled with the **Gap fill value** (`FF` at first). If you turn on **Add bookmarks to gaps**, the gaps get bookmarks. If the same address has data more than once, it is listed as an error at first. Turn on **Use the later data for duplicate addresses** to import it anyway.
- **Base64 and other encodings**: options such as **Ignore spaces and line breaks**, **Allow missing padding** and **Treat + as a space** (URL encoding). PEM header and footer lines are removed automatically.
- **Hex text**: reads numbers separated by spaces, commas, `0x`, `\x` or line breaks. Turn on **Read dumps** to take bytes out of the output of `xxd`, `hexdump -C` and similar, removing the offset and text columns.
- **Decimal text**: reads decimal numbers separated by spaces, commas, semicolons or line breaks. Set **Bytes per value**, **Signed** and **Big-endian**.
- **Source code**: takes numbers out of an array definition. It reads hex, decimal, octal, characters such as `'A'`, and strings such as `"..."`. The element size is guessed from the type name, and you can change it in **Bytes per element**.
- **Binary**: reads the file as it is.

If the format has errors (a wrong checksum, an invalid character and so on), the **Errors and warnings** list shows the line, the column and the reason. You can import ignoring the errors, or cancel. The default is to cancel.

You can also open Intel HEX, S-record and Base64 files with **File > Open**. Then the decoded bytes open as a document, and they are converted back to the original format when you save. See [Opening files](opening-files.md) for details.

## Saving a range to a file {#extract-range}

You can save only the selection as a binary file. It is handy when you want to cut a piece out of a file.

1. Select the range to save.
2. Select **File > Export > Save selection as binary...**. You can also run **File: Save selection to a file** from the command palette.
3. Choose where to save.

The bytes of the selection are written to the new file as they are. The original document does not change. If you want a different format, or want to handle several ranges, choose **Selection** in [Export formats](#export-formats).

## Opening a selection in a new tab {#open-in-new-tab}

You can open a selection in a new tab and work on it like an independent document. It is handy when you only want to edit a part of a huge file. It opens right away no matter how large the range is.

1. Select the range to open.
2. Choose how to open it from **File > Open selection in a new tab**. You can also run it from the command palette. In the bookmarks panel, right-click a bookmark and select **Open in a new tab**.

There are two ways to open it.

| Way | Meaning |
| --- | --- |
| **Linked view** | Shows the range of the parent document. Edits go to the parent. The length cannot change, so you can only overwrite. <kbd>Ctrl</kbd>+<kbd>Z</kbd> works on the same history in the parent and in the child. Saving saves the parent. The tab is named like `data.bin [0x1000–0x1FFF]`. Closing the parent tab closes the child tab. If the whole range is deleted in the parent, a message says the original range was deleted |
| **As a copy** | Creates an untitled document that holds the contents of the range. It is independent of the parent and its length can change. Save it with **Save As**. Even if the parent file changes later, the content stays as it was when you opened it |

If several ranges are selected, you are asked whether to open only the first range or one tab for each range.
