# Statistics and analysis

The statistics panel shows which values a document, or part of it, contains and how often. It helps you tell whether data is text, compressed or encrypted, and what kind of repetition it has. File type detection is described here too.

Open the statistics panel with **Analysis > Statistics**. You can also use **View > Panels > Statistics** or run **Analysis: Show statistics** from the command palette.

You can also select bytes, right-click and choose **Statistics**. This item is available only when something is selected, and the selection becomes the **Range**.

In the upper part of the panel you choose the range and the settings. Below are the tabs: **Histogram**, **Descriptive**, **Entropy**, **Digram**, **Patterns** and **Classification**.

- **Range**: choose **Selection**, **Multiple selection**, **Whole document** or **Specify range**. When you specify a range, enter **Start** and **Length or end**.
- **Type**: how to read each value. Bytes (`u8`) are the default. You can also choose 16- to 64-bit integers and the floating-point types `f32` and `f64`. For types of two bytes or more you can also set **Big-endian** and **Stride** (how many bytes to step between values).
- **Recalculate automatically**: when on, the statistics are recalculated 0.3 seconds after the range changes. If the range is larger than 64 MB, they are not recalculated automatically, and the **Recalculate** button is highlighted.
- **Recalculate** and **Cancel**: start or stop the calculation.

The calculation runs in the background with a single pass over the data. The window does not freeze even for large files. Partial results appear as they are calculated. If you cancel, the results so far stay on screen with "Stopped (xx% done)". If some ranges cannot be read, they are left out and the number of unreadable ranges is shown. After you edit the document, a note says the results are for the content before editing.

Every chart has **Show as table**, which shows the same values in a table.

## Byte histogram and entropy {#histogram}

### Histogram tab {#histogram-tab}

The **Histogram** tab draws a bar chart of how many times each value occurs. For bytes there are 256 bars.

- The vertical axis shows counts or percentages. Turn on **Logarithmic scale** to see rare values more easily.
- For types other than bytes, set the number of **Bins** (2 to 65,536) and the range. With **Automatic range** on, the range runs from the minimum to the maximum. Values outside the range are counted in the "below the range" and "above the range" boxes. 16-bit types can also use **Per value (65,536)**.
- NaN and ±∞ of floating-point types are not put into bins. They are counted separately.
- The table shows the value, count, percentage and cumulative percentage. For bytes there is also a character column. **Sort** lets you choose value order or count order.
- For bytes, the shares of `0x00`, `0xFF`, printable ASCII, whitespace, other control characters and `0x80` to `0xFF` are also shown.
- Double-click a bar or a table row to go to the next place where that value occurs. Right-click and select **Find all occurrences of this value** to search for it.
- **Copy table** copies the table as tab-separated text. **Export** writes CSV or JSON.

### Descriptive statistics {#descriptive}

The **Descriptive** tab lists these values. Point at an item name to see its definition.

- **Elements**, **Sum**, **Minimum**, **Maximum**, **Range**, **Mean**
- **Population variance**, **Sample variance**, **Population standard deviation**, **Sample standard deviation**
- **Median**, **First quartile**, **Third quartile**, **Mode**
- **Skewness**, **Kurtosis**
- **Chi-square** (bytes only; a measure of how evenly values are spread)
- **Serial correlation** (how adjacent values relate)
- **Monte Carlo π** (bytes only; a measure of randomness)

For types larger than 16 bits with more than 16 million elements, the median and quartiles are approximate and start with `≈`, and the mode is not calculated. Items that cannot be calculated show "—", and a tooltip gives the reason. **Copy value** and **Copy all** copy the results.

### Entropy {#entropy}

The **Entropy** tab shows how varied the data is. For bytes it is between 0 and 8 bits per byte. Near 0 means the same value repeats. Near 8 means it looks random.

- It shows **Entropy**, **Entropy (%)**, **Redundancy** and **Theoretical minimum size**.
- With **Estimate compression ratio** on, a part of the data (up to 64 MB) is actually compressed to estimate the ratio. The sample size is shown, such as "Sample: 64 MB / 10 GB".
- For bytes, a guide in words appears: "Mostly repetitions of the same value", "Possibly text or structured data", "Possibly general binary (executables and so on)" or "Possibly compressed or encrypted data".

The **entropy graph** divides the range into blocks and draws the entropy of each block as a line.

- **Block size** is automatic at first. You can choose from 256 bytes to 64 MB.
- You can add the **0x00 share** and the **Printable ASCII share** as extra lines.
- A **Threshold (bits)** line (7.2 at first) is drawn, and the parts above it are marked with a pattern.
- Use **Zoom in** and **Zoom out**, or hold <kbd>Ctrl</kbd> and turn the mouse wheel, to zoom horizontally. When you zoom in, the visible range is recalculated with smaller blocks.
- Click the graph to go to the start of that block. Drag to select that range. When the graph has the focus, press <kbd>←</kbd> and <kbd>→</kbd> to move between blocks and <kbd>Enter</kbd> to go there.
- **Export CSV...** writes the graph values.

Compressed or encrypted data gives a flat graph near 8.

## Digram and byte distribution {#digram}

The **Digram** tab has two charts. Switch between them with **Chart**.

- **Digram**: a 256 × 256 chart of how often each pair of consecutive bytes occurs. The horizontal axis is the first byte and the vertical axis is the next byte. The darker a cell, the more often that pair appears. Text, for example, clusters in the range of letters.
- **By position**: a chart with the offset on the horizontal axis and the byte value on the vertical axis. It shows which values are common in which part of the file.

Point at the chart to see the value, count and percentage. Double-click a digram cell to search for that pair of bytes. Click a cell in the position chart to go to the start of that interval. If the range is shorter than 2 bytes, the tab says "Specify a range of 2 bytes or more."

## Pattern statistics {#pattern-statistics}

The **Patterns** tab finds frequent byte sequences and the period of repetition. Set the options, then select **Calculate**.

- **Length (n)**: the length of the sequences, 2 to 16 bytes. 4 at first.
- **Top**: how many to list, 10 to 10,000. 100 at first.
- **Minimum count**: only sequences that occur at least this many times are listed. 2 at first.
- **Offset mod ... Equals**: a position condition. For example, "Offset mod 4 equals 0" counts only positions that are multiples of 4.
- **Exclude all 0x00 / all 0xFF**: on at first.

Use **Results** to switch between two views.

- **Frequent byte sequences**: rank, hex, text, count, percentage and first position. Double-click a row to go to the first occurrence. Right-click and select **Find all** to find every occurrence.
- **Periods**: an estimate of how many bytes it takes for the data to repeat. For a file made of fixed-length records, the record length ranks high. Right-click and select **Show records of this length** to alternate the background color for every record of that length, or **Infer a template with this length** to start [template inference](template-inference.md).

Even for large files, the working memory is at most 256 MB. The counts are exact. If you cancel, the partial results are discarded.

## Detecting encrypted, compressed and known data {#detection}

### Detecting encrypted or compressed data {#classify}

The **Classification** tab (or **Analysis > Detect Encrypted/Compressed Data**) divides the range into blocks and judges the kind of each block.

| Class | Roughly means |
| --- | --- |
| **Zero-filled** / **Constant** | Almost one value only |
| **Text** | Mostly characters |
| **Binary** | Executables, data and so on |
| **Compressed** | High entropy, looks compressed |
| **Encrypted or random** | Very high entropy and evenly spread values |
| **Unreadable** | Blocks that could not be read |

**Block size** is 4 KB at first, and you can choose from 512 bytes to 1 MB. Select **Classify** and the runs of the same class are merged into sections in a list. The list shows the start, length, class, mean entropy and confidence (**High**, **Medium**, **Low**). The band above shows the share of each class with colors and patterns.

Signatures of known compression formats such as zlib, gzip, zstd, xz, bzip2, LZ4, 7z and ZIP are also searched, and the places found are added to the list.

This is an estimate based on statistics. The panel always says "This is an estimate based on statistics and can be wrong." Do not trust it blindly.

To see the classification together with format structures, embedded files and repeating records in one overview, use the [File map](file-map.md).

### Detecting the file type {#file-type}

The type of a file can be found from its magic number and similar clues.

- **Analysis > Detect File Type** opens the **File Type** panel. Select **Detect** to list candidates, most likely first. Each shows the format name, MIME type, extensions and confidence.
- **Detect from cursor** treats the cursor position as the start of the data. Use it to look at data embedded inside a file.
- **Find embedded file types** looks for signatures of known formats at every position in the range, and lists the places it finds.
- If the extension does not match the content (for example a `.jpg` that is really a PNG), the panel says "The extension does not match the content."
- If nothing matches, it shows "Unknown format (data)" or something like "Text (UTF-8)".

When you open a file, its format is detected automatically from the first and last 64 KB, and the name of the most likely format appears in the status bar. Click the name to open the panel.
