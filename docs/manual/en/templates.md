# Structure templates

This page shows how to apply a structure template to a file to read its contents, and how to write and debug your own templates. The language itself is described on the [Template standard library](template-library.md) and [Template error codes](template-error-codes.md) pages.

A structure template is a blueprint of the shape of some bytes. For example, when you apply the PNG template to a PNG file, the signature, the header and every chunk appear as a tree of named values. In the hex column, each part gets its own color.

## Applying a template {#apply-a-template}

1. Open the document.
2. Select **Analysis > Structure templates > Apply template**. You can also run **Template: Apply** from the command palette.
3. Choose a template from the list. Type part of a name, description, extension or MIME type in **Search templates** to narrow the list. Recently used templates are at the top.
4. Change **Start offset**, **Default endianness** or **Kind** if you need to.
5. Select **Apply**.

The result tree appears in the **Structure** panel. If the panel is closed, open it with **View > Panels > Structure**.

To apply a template from the cursor position, right-click in the hex view and select **Apply template from here**. The cursor position is filled in as the **Start offset**.

### Fixed and follow {#fixed-and-follow}

There are two **Kind** choices:

- **Fixed** (default): the structure stays at the start offset. Moving the cursor does not change the result.
- **Follow cursor**: one type that you choose is read at the cursor position. The result changes every time you move the cursor. This is handy when you step through the sectors or records of a disk.

For follow mode, enter the name of a type from the template in **Type (follow)**. You can also start it with **Template: Show type at cursor** in the command palette. The result of follow mode is shown only with an outline in the hex view, not with colors. To turn a follow application into a fixed one at the current position, right-click its node and select **Convert to fixed**.

### Several applications, and removing one {#multiple-applications}

A document can have up to 32 applications. For example, you can apply PE at the start and another structure at 0x4000. Each application is one node at the top of the tree.

To remove an application, right-click its node and select **Remove application**. The tree and the colors disappear.

The list of applications is remembered with the document. When you open the document again, the same applications come back.

## The result tree {#result-tree}

The **Structure** panel shows the result as a table.

- The columns are **Name**, **Value**, **Type**, **Offset**, **Size** and **Comment**. The **Columns** button also lets you show Endian, End and Color, and change the order.
- Structures, arrays and pointers can be expanded and collapsed. Children are read only when you expand them, so even a huge structure does not freeze the window. An array of more than 1,000 elements is split into groups of 1,000.
- Type in the filter box above the tree to filter by name, value or type. To also search the parts that have not been read yet, select **Evaluate all and search**.
- The **Path** box below shows the path of the selected node, for example `chunks[3].header.width`. You can also type a path to jump to that node.
- At the bottom you see how many patterns were read and how many errors and warnings there are. Select the counts to open the [template output panel](#template-output).

Use <kbd>↑</kbd> and <kbd>↓</kbd> to move, and <kbd>→</kbd> and <kbd>←</kbd> to expand and collapse. Press <kbd>Enter</kbd> to select the range of the node in the hex view.

### Link with the hex view {#hex-link}

- When you select a node, its range is selected in the hex view. The view scrolls if needed.
- When you move the cursor in the hex view, the innermost node that contains the cursor is selected in the tree. The **Follow cursor** button turns this link on and off.
- When you point at a byte in the hex view, a tooltip shows its path, type and value.

### Coloring {#coloring}

The range of each pattern is colored in the hex view. Neighboring parts get different colors. Select **View > Structure coloring** to show or hide the colors, or run **Template: Toggle coloring** from the command palette. Change the opacity with **Coloring opacity (%)** in **Settings > Templates**.

In a high contrast theme, lines are drawn at the borders of the patterns instead of filling them.

### The right-click menu of a node {#node-menu}

Right-click a node for these actions:

- **Go to data**: selects the range in the hex view.
- **Go to pointer target**: for a pointer node, jumps to the place it points to. Press <kbd>Alt</kbd>+<kbd>←</kbd> to go back.
- **Go to definition**: opens the line in the template editor where the pattern is declared. <kbd>F12</kbd> does the same.
- **Copy value**, **Copy name and value**, **Copy path**, **Copy bytes**, **Copy offset**.
- **Copy subtree as JSON**: copies the node and everything below it as JSON. If the result is larger than 64 MB, you are asked to save it to a file instead.
- **Add to bookmarks**: makes the range a bookmark whose comment holds the name and path. See [Bookmarks](bookmarks.md).
- **Add ranges to multi-selection**: selects the ranges of several chosen nodes together.
- **Open section in new tab**: for a section node, opens the expanded data in a read-only tab. See [Sections](#sections).

If you select several nodes, the copy items copy all of them, one per line.

## Editing values from the tree {#edit-from-the-tree}

Double-click a value in the tree, or right-click it and select **Edit value**, to change the bytes. You can also run **Template: Edit value** from the command palette.

You get an input field that fits the type:

| Type | Input |
| --- | --- |
| Integer | A number (hexadecimal such as `0x1F` also works) |
| Floating point | A number, `NaN`, `Inf` or `-Inf` |
| Enumeration | A list of names. You can also choose **Enter a number** |
| Flags | A list of check boxes |
| Boolean | A check box |
| String | Text. If it is shorter than the field, you can choose **Fill the rest with zeros** |
| Date and time | A date and a time. Choose **UTC** or **Local time** |
| Color | A color picker, or `#RRGGBB` |

Press <kbd>Enter</kbd> to confirm and <kbd>Esc</kbd> to cancel. If the value is not valid, the field turns red and shows the reason.

The change overwrites only the range of that pattern. The size does not change, even in insert mode. The change is one step for [Undo](editing.md#undo-redo).

These nodes cannot be edited. A tooltip tells you why:

- Nodes in a read-only document
- Patterns marked `readonly`
- Patterns inside a section
- Computed values and error nodes

## Automatic template selection {#auto-apply}

When you open a file, HexEditor looks at its start and end and searches for a matching template. It uses the magic number, the MIME type and the extension. If only the extension matches, the template is a candidate but is not applied automatically.

Choose what happens when a template matches with **Auto-apply** in **Settings > Templates**:

- **Suggest** (default): a bar appears above the editor, for example "This file looks like PNG image. Apply the template?" It has the buttons **Apply**, **Other candidates...** and **Don't show for this format**. You can keep editing.
- **Always apply**: the best match is applied automatically.
- **Do nothing**.

If you select **Don't show for this format**, that format is not suggested again. To undo this, select **Show again** under **Suggestions turned off** in **Settings > Templates**.

No suggestion is made if the document already has applications.

To search by hand, select **Analysis > Structure templates > Find matching templates**. The **Matching templates** list shows what matched (magic, MIME or extension) and the score. Choose one to apply it.

After you edit the document, the result is evaluated again 0.5 seconds after your last edit. The expanded nodes and the selection are restored by path. If you turn off **Re-evaluate after editing** in **Settings > Templates**, this no longer happens automatically. Use **Analysis > Structure templates > Run again** instead.

## Standard templates {#standard-templates}

Templates for common formats come with the app. They are the **Standard** ones in the **Apply template** list.

| Category | Example formats |
| --- | --- |
| Executables | PE, ELF, Mach-O, Java class, DEX, WebAssembly |
| Images | PNG, JPEG, GIF, BMP, ICO, TIFF, WebP, TGA |
| Audio and video | WAV, AVI, MP3, FLAC, Ogg, MIDI, MP4, MKV |
| Archives | ZIP, GZIP, TAR, 7z, RAR, XZ, CAB |
| Documents | PDF, OLE compound file, RTF |
| Disks and file systems | MBR, GPT, FAT, exFAT, NTFS, ISO 9660, VHD, VHDX, ext |
| Others | SQLite, LNK, registry hive, PCAP, TrueType, DER |

Standard templates are read-only and cannot be saved directly. To change one, select **Duplicate and edit** in the template editor. This makes a copy in your own folder.

## Kaitai Struct files {#kaitai}

HexEditor can run Kaitai Struct definition files (`.ksy`) as they are. You do not need an external compiler. The result appears in the same tree as a normal template.

1. Select **Analysis > Structure templates > Apply Kaitai Struct**.
2. Choose the `.ksy` file and select **Apply**.

You can also pick a `.ksy` file in the template list.

To keep working on a `.ksy` file by hand, convert it into a HexEditor template (`.hxt`). Select **Analysis > Structure templates > Convert .ksy to HXT**. Parts that cannot be converted are marked with comments.

If a type uses a feature that is not supported (for example `opaque-types`), only that type becomes an error. The rest can still be read.

## Writing templates in the template editor {#template-editor}

Your own templates are text files with the extension `.hxt`. You write them in the template editor.

### Create and open {#create-and-open}

- Create a new one: **Analysis > Structure templates > New template**. It opens with a starting skeleton.
- Open an existing one: **Analysis > Structure templates > Open template**.
- Fix the template you have applied: **Analysis > Structure templates > Edit template**.

The editor opens as a tab next to your documents. An `.hxt` file you open is also shown in the template editor.

### Example {#example}

This template reads a simple format made of a 4-byte magic number and two counters:

```text
#pragma name "My format"
#pragma magic 0 "4D 59 46 4D"
#pragma extensions "myf"

struct Header {
    char magic[4];
    u16  version;
    u16  count;
};

struct Item {
    u32 id;
    u32 size;
};

Header header;
Item   items[header.count];
```

`#pragma magic` and `#pragma extensions` are the conditions for [automatic selection](#auto-apply). The patterns are read from the start of the file, from top to bottom.

### Editor features {#editor-features}

- Syntax highlighting that follows the theme.
- Syntax checking while you type. Lines with errors get a wavy underline and are also listed under **Problems**.
- Completion. Keywords, types, attributes, standard library functions and already declared members are offered (type `header.` to see what is inside `header`). Press <kbd>Ctrl</kbd>+<kbd>Space</kbd> to open the list.
- Go to line (<kbd>Ctrl</kbd>+<kbd>G</kbd>).
- Folding, bracket matching, toggling comments (<kbd>Ctrl</kbd>+<kbd>/</kbd>) and formatting (<kbd>Shift</kbd>+<kbd>Alt</kbd>+<kbd>F</kbd>).
- Go to definition (<kbd>F12</kbd>). On a type name, it opens the line that defines the type.
- Save with <kbd>Ctrl</kbd>+<kbd>S</kbd>. Applications that use the template are evaluated again. The same happens when you save the file in another editor.

On a PC without the WebView2 runtime, the template opens in a basic editor without highlighting or completion, and a bar at the top tells you. You can still edit and save templates.

### Try it without saving {#try-run}

Select **Try run** on the toolbar to apply the template to the **Target document** without saving it. The tree shows **(unsaved)**. You can choose the **Target document** on the toolbar.

### The template output panel {#template-output}

Syntax errors, errors while reading, warnings and the output of `print` appear in the **Template output** panel, in time order. Open it with **View > Panels > Template output**.

- Errors have a code such as `HXT1004`. On the line of an error, choose an item such as **Help for HXT1004** to open its description on the [Template error codes](template-error-codes.md) page.
- Double-click a line to go to **Go to source** (the line in the template) or **Go to data** (the position in the document).
- If an error happens halfway, everything read before it stays in the tree. The error node has an icon and the word "Error".

## Template debugger {#template-debugger}

When a template does not read what you expect, you can stop the evaluation and run it one statement at a time.

1. In the template editor, click the margin to the left of a line, or press <kbd>F9</kbd>, to set a breakpoint.
2. Press <kbd>F5</kbd>, or select **Start debugging / continue** on the toolbar.
3. The evaluation stops at the breakpoint.

While it is stopped, the current line is highlighted and you can see:

- **Variables**: local variables and the patterns of the structure being read
- **Watch**: the values of expressions you add with **Add watch expression**
- **Call stack**
- **Breakpoints**
- The result tree so far
- A `$` mark in the hex view, showing the current read position

| Action | Key |
| --- | --- |
| Start / continue | <kbd>F5</kbd> |
| Stop | <kbd>Shift</kbd>+<kbd>F5</kbd> |
| Set / clear a breakpoint | <kbd>F9</kbd> |
| Step over | <kbd>F10</kbd> |
| Step into | <kbd>F11</kbd> |
| Step out | <kbd>Shift</kbd>+<kbd>F11</kbd> |

These keys work while the template editor has the focus. <kbd>F11</kbd> steps into instead of switching to full screen. You can also use the toolbar or the command palette.

A breakpoint can have a condition (for example `$ >= 0x1000`) and a hit count. Right-click the result tree or the hex view and select **Break when reading here** to stop at the statement that reads that offset.

While debugging, lazy evaluation is off so that the order is easy to follow (**Evaluate in order while debugging**). Large arrays can take a long time.

The target document is read-only while you debug. It becomes editable again when you stop. If the template has syntax errors, you are asked to fix them first.

## Sections (virtual buffers) {#sections}

When a file contains compressed data, you can still place patterns on the data after it is expanded. The place that holds the expanded data is called a **section**.

In the result tree, a section appears under a heading **Section: name**. Right-click the section node and select **Open section in new tab** to see the expanded data in a read-only tab.

- Patterns inside a section cannot be edited.
- The size of expanded data has a limit. When the limit is reached, expansion stops and a warning appears.
- Data is expanded only when a pattern inside it is needed.

To create sections in a template, see `std::section` and `std::compress` in the [Template standard library](template-library.md).

## Exporting results {#export-results}

You can save the result tree as JSON or CSV.

1. Select **Export** on the toolbar of the Structure panel. You can also select **Analysis > Structure templates > Export results**.
2. Choose the **Format** and the **Target** (the whole application, or the selected subtree).
3. If needed, set **Maximum array elements**, **Maximum depth**, **Include hidden patterns** and **Write offsets as hexadecimal strings**.
4. Select **Save**.

CSV is written as UTF-8 with a BOM, so Excel shows it correctly. Even a result with a huge array is written a little at a time, so memory does not keep growing. You can cancel the export from the process center. If you cancel, the unfinished file is deleted.

To produce the same output from the command line, see [Templates and JSON output](command-line.md#templates-and-json).

## Managing templates {#manage-templates}

Select **Analysis > Structure templates > Manage templates** (or **Template: Manage** in the command palette, or **Settings > Templates**) to manage your templates.

- The list shows the name, the origin (Standard or User) and the auto-apply conditions of each template. Use **Filter by name** to find one.
- The **Enabled** and **Auto-apply** switches decide, for each template, whether it appears in the list and whether it is used for automatic selection.
- If several templates have the same name, the one found first is used and the others show "Hidden by another template with the same name."
- **Add folder** adds a folder to search for templates, for example a folder your team shares. Changes in these folders appear in the list without restarting.
- **Import** takes `.hxt` files, `.ksy` files, or a `.zip` that contains them, into your folder.
- **Export** packs the chosen templates, and the files they import, into a `.zip`. Standard templates cannot be exported. Duplicate one first.
- **New template** and **Edit** open the template editor.

### Use in the inspector {#inspector-templates}

If you add `[[inspector("Display name")]]` to a type in a template, that type can appear in the data inspector. Select **Add inspector template** in the template manager to register it. The row in the inspector changes with the value at the cursor. See [Data inspector](inspector.md#user-types). Use fixed-size types of 4 KB or less. A warning appears if you register a variable-size type.
