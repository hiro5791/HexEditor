# Printing and PDF

Print the bytes of a document as a hex dump, look at the pages first in a preview, or save the same pages as a PDF file.

## Print a document {#print}

1. Select **File > Print...**, press <kbd>Ctrl</kbd>+<kbd>P</kbd>, or run **File: Print** from the command palette.
2. Choose the settings on the left. The preview on the right changes as you change them.
3. Select **Print**.

Settings:

| Setting | What you can choose |
| --- | --- |
| **Printer** | Any printer registered in Windows, including **Microsoft Print to PDF**. The Windows default printer is chosen first. |
| **Print range** | **All**, **Selection** (only when something is selected; each part of a multi-selection starts on a new page), **Offset range** (`start:length` or `start..end`, for example `0:0x1000` or `0x100..0x200`; the end is not included), or **Pages** (for example `1-3, 8`). If a selection exists, **Selection** is chosen first. |
| **Paper**, **Orientation** | Paper size and portrait or landscape. |
| **Margins** | 0 to 50 mm. The boxes show inches if your region uses inches. |
| **Font**, **Size** | A fixed-width font, 6 to 24 pt. The default is Cascadia Mono 9 pt. |
| **Bytes per row** | 4 to 64, or **Fit to paper**, which uses the largest multiple of 8 that fits the page. If the row you chose does not fit, **Fit to paper** is used. |
| **Columns** | Offset, Hex and Text columns. The group size, offset base and text encoding are the same as on the screen. |
| **Color** | **Black and white** marks changed bytes with an underline and bookmarks and coloring rules with gray shading. **Color** prints their background colors. |
| **Header** and **Footer** | Left, center and right text. See the symbols below. |
| **Copies** | 1 to 999. |

The defaults for the header are the file name on the left and the printed range on the right. The footer shows the page number in the center and the date on the right. Select **Save as defaults** to keep your settings for next time.

The symbols you can use in the header and footer are written in braces: `{name}` (file name), `{path}` (full path), `{page}`, `{pages}`, `{date}`, `{time}`, `{range}` (offsets of the printed range), `{size}` (document size) and `{modified}` (shows "(modified)" when there are unsaved changes).

Unsaved changes are printed. HexEditor prints the content as it was when you selected **Print**, so you can keep editing while it prints. The job appears in the processing center with the page count, and you can stop it.

### Large files {#large}

The page count is calculated from the file size, so the dialog opens at once even for a file of hundreds of gigabytes. HexEditor reads only the pages it prints or shows.

- More than 1,000 pages: HexEditor asks you to confirm the number of pages. Select **Print** to go on or **Change range** to go back to the dialog.
- More than 100,000 pages cannot be printed or saved as PDF. Narrow the range.

If a part of the data cannot be read, for example because of a bad sector, those bytes are printed as `??` and a message tells you at the end.

## Print preview {#preview}

Select **File > Print preview...** or run **File: Print preview**, or just look at the right side of the print dialog. Below the page it says, for example, "12 / 3,456 pages".

- Use the buttons, the page box, or <kbd>Page Up</kbd>, <kbd>Page Down</kbd>, <kbd>Home</kbd> and <kbd>End</kbd> when the preview has focus. Type a page number in the box and press <kbd>Enter</kbd> to jump.
- Choose **Whole page**, **Fit width** or 50 to 400 percent. Select **Two pages** to see a spread.

## Save as PDF {#pdf}

1. Select **File > Export > PDF...**, run **File: Export as PDF**, or select **Save as PDF...** in the print dialog.
2. Choose the settings. The paper can be A4, Letter, A3 or Legal. Printer and copies are not used.
3. Choose where to save the file.

The PDF contains real text, not pictures, so you can search for and copy hex values. Only the characters in use are embedded, in a subset of the font. Text that your font cannot show, such as Japanese characters in the text column, is written with a substitute font. The title is the file name, and there is a bookmark for every 1,000 pages. A file is written to a temporary file first and then replaced, so a failure never leaves a half-written PDF.

**Microsoft Print to PDF** also works, but **Save as PDF...** makes smaller files and keeps the text searchable.
