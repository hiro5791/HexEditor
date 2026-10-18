# NTFS alternate data streams

A file on an NTFS or ReFS volume can carry extra named streams besides its main data. Windows uses them for the "downloaded from the Internet" mark (`Zone.Identifier`), and some programs store properties or secrets in them. HexEditor lists, opens, edits, creates and deletes them.

## See the streams of a file {#list}

1. Open a file and select **File > Alternate data streams...**, or **Tools > File tools > Alternate data streams...**. You can also right-click the tab and select **Alternate data streams...**. Select **Choose file...** or **Choose folder...** in the panel to look at another file or a folder. A folder has streams too; you can list, create, rename, delete, export and import them, but not open them in a tab.
2. The panel lists the streams. The main stream appears as `::$DATA`. The columns are the stream name, the size, the first 16 bytes in hex and a description of well-known streams. `Zone.Identifier` is described as the mark of a file that came from the Internet.

If the file is on a volume without stream support, such as FAT32 or exFAT, the panel says "This volume does not support alternate data streams." and the buttons are off.

## Open, edit and save a stream {#open}

Select a stream and select **Open**, or double-click it. The stream opens in a new tab named like `data.bin:Zone.Identifier`, and you edit it like any other document.

- If the length does not change, only the changed bytes are written.
- If the length changes, a stream cannot be replaced like a file, so HexEditor first saves the old content in the recovery folder, rewrites the stream and puts the old content back if that fails. The usual question about the size limit of the recovery journal applies.
- Saving the main stream of a file keeps its alternate streams.
- On ReFS, streams have a size limit set by Windows. If a save would go over it, HexEditor says so and does not save.

## Create, rename, delete, export and import {#manage}

- **Create...** asks for a name and makes an empty stream, then opens it. The name cannot contain `:`, `\`, `/` or control characters; the box turns red and **Create** stays off. A name that already exists is refused.
- **Rename...** copies the stream to the new name and then deletes the old one, because Windows has no rename for streams.
- **Delete...** asks first and shows the name and size. It cannot be undone.
- **Export...** writes the content of the stream to a file. **Import...** replaces the content of the stream with a file.

A stream that is open in a tab cannot be renamed, deleted or replaced. Close the tab first. In write-protect mode you can list and open streams, but creating, renaming, deleting and importing are off.

## Be told about streams {#notice}

In **Settings**, turn on **Notify when a file has alternate data streams**. When you open a file that has streams besides the main one, a message tells you how many and has a **Show list** button. It is off by default.
