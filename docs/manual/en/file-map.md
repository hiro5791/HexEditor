# File map

A file map shows what is where in a document: the structure of a known format, embedded files, compressed or encrypted data, text, zero padding and repeating records. This page explains how to create a file map and how to work with it.

## Create a file map {#create}

The file map is made only when you ask for it. It is not made when you open a file.

1. Open the document.
2. Select **Analysis > File Map**. You can also use **Analysis: Create file map** in the command palette, or right-click the minimap and select **Create file map**.
3. The **File Map** panel opens and the map is built in three stages:
   - **Overview (samples)**: a quick estimate from small samples spread over the document. It appears within a few seconds, even for very large files.
   - **Full scan**: the whole document is read once. The scanned part replaces the estimate as the scan goes on.
   - **Structure analysis**: templates find the structure of the file format and the exact extent of embedded files.

The progress is shown at the top of the panel and in the processing center. Select **Cancel** to stop. What was already scanned is kept, and the rest stays as the estimate from samples.

If the tab uses a transform view, the map is made from the transformed data, and the panel says so.

The file map is an estimate based on signatures and statistics. The panel always says "This map is an estimate based on signatures and statistics, and it can be wrong."

## Read the band {#band}

The band at the top of the panel shows the whole document from left to right. Offsets grow to the right, also in right-to-left languages.

- The upper lane shows the **structure**: the parts of the file format (for example `header` and `sections` of an executable) and embedded files.
- The lower lane shows the **content**: compressed, encrypted or random, text, zero, padding, repeating records, binary and unreadable data. Every byte belongs to one kind in this lane.
- Each kind has its own color and its own pattern, so you can tell the kinds apart without color. The legend under the band shows the patterns and how many bytes each kind has.
- Parts that are still estimated from samples are drawn lighter, with dots.
- A frame shows the part shown in the hex view, and a vertical line shows the cursor.

To zoom, hold <kbd>Ctrl</kbd> and turn the mouse wheel, or use the zoom buttons. Point at a region to see its kind, label, range, confidence and the first sentence of the evidence.

## Move around with the band {#navigate}

- Click the band to move the cursor there. Press <kbd>Alt</kbd>+<kbd>Left</kbd> in the hex view to go back.
- Double-click a region to select its range.
- Press <kbd>Tab</kbd> to move the focus to the band. The region at the cursor is selected. Press <kbd>Left</kbd> and <kbd>Right</kbd> to move between regions, and <kbd>Up</kbd> and <kbd>Down</kbd> to switch lanes. Press <kbd>Enter</kbd> to select the range of the region. Press <kbd>+</kbd> and <kbd>-</kbd> to zoom.
- Press <kbd>Shift</kbd>+<kbd>F10</kbd> or the Application key to open the menu of the region.
- Select **Show as table** to move the focus to the list of regions.

## The list and the details {#list}

The list shows every region with its start, end, length, kind, label, confidence and stage. Select a column header to sort, and use **Show kind** to show one kind only. Selecting a row selects the same region in the band, and the other way round.

- **Listed rows to bookmarks** adds a bookmark for each listed row.
- **Listed rows to multi-selection** selects all listed rows at once.

The details next to the list show the evidence of the selected region: the numbers it is based on, such as the entropy and p-value, the shift and share of matching bytes for records, or the template that parsed an embedded file. Each region has a confidence: **High**, **Medium** or **Low**.

## Work with a region {#actions}

Right-click a region in the band or the list, or use the buttons in the details. Only the actions that fit the kind of region are shown.

| Action | Shown for | What it does |
| --- | --- | --- |
| **Select range** | All regions | Selects the region |
| **Add bookmark** | All regions | Adds a bookmark named after the label, with the kind and evidence as the comment |
| **Apply template "…"** | Format structure, embedded files with a template | Applies the template at the start of the region, without the apply dialog |
| **Open in new tab** | Embedded files, compressed data, text, repeating records | Opens the range in a new tab |
| **Decompress from here...** | Compressed data of a known format | Opens the decompress dialog with the range and format filled in |
| **Infer template...** | Repeating records | Opens template inference with the range and record length filled in |
| **Extract strings...** | Text | Opens string extraction for the range |
| **Save as file...** | Embedded files | Saves the range as a file |

## Templates for embedded files {#templates}

When the map finds embedded files that a template can parse, a message asks whether to apply the templates. Select **Apply** to apply them, or **Show list** to see the embedded files. You can also use **Analysis: Apply templates to found files** in the command palette.

A document can have up to 32 template applications. If there are more embedded files, the message says how many were not applied.

Templates are never applied automatically. To turn the message off, set **File map: templates for embedded files** to **Do nothing** in the advanced settings.

## After editing {#edit}

If you edit the document after making the map, the panel says "The results are for the content before editing." Select **Rebuild** to make the map again for the current content.

The map is kept until you close the document. It is not saved with the document. To keep it, export it.

## Export the map {#export}

Select **Export** and choose a format:

- **JSON**: the document, the stages, every region with its offset, length, kind, lane, label, confidence, evidence, stage, template and parent, and the bytes for each kind.
- **CSV**: one region per line, UTF-8 with a byte order mark, so that spreadsheets show the text correctly.
- **Text report**: a readable summary with the bytes for each kind and one line per region.

The words for kind, lane, confidence and stage are always in English. Labels and evidence are in the display language.

## Large files {#large-files}

The overview reads at most about 16 MB, so it appears quickly even for files of hundreds of gigabytes. The full scan reads the file once from start to end, and the memory it uses does not grow with the size of the file. The hex view stays responsive while the map is being made.

The minimap can show the content lane of the map next to its marks. Click it to open the panel at that place. You can turn this mark on or off in the menu of the minimap.

Related pages: [Statistics and analysis](statistics.md), [Templates](templates.md).
