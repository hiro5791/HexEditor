# File carving

Find files in raw data by their headers, even when no file system lists them, and save them to a folder. Carving reads the data without changing it.

## Carve files {#run}

1. Open the disk, image or file. To limit the search, select a range.
2. Select **Forensics > Carving > Carve files...**.
3. Choose the scope: the whole document, the selection, unallocated space only, or slack space only. The last two are available when the file system has been analyzed.
4. Tick the file types to look for. Tick **Do not look for headers inside a file that was found** if you do not want files inside files.
5. Leave **Write the carved files to a folder** ticked and choose the output folder. In a case, the default is `exports/carved`. Untick it to only make the list.
6. Select **Start**.

The output folder cannot be on a disk that is under investigation (a disk in the evidence list, or the disk you are carving). HexEditor also checks the free space and the file size limit of the output file system, and refuses to start if they are not enough.

Carving is a long task shown in the processing center with progress, speed, time left and the count of each file type. You can keep working. Files are written while the scan goes on. If you cancel, the results found so far and the manifest are kept.

Files are named `type/offset.ext`, with the start offset as 16 hexadecimal digits, for example `jpeg/00000001A2B3C400.jpg`. The file `carving-manifest.csv` lists the number, type, start offset, start LBA, length, how the length was decided, MD5, SHA-256 and the output path. Ranges that could not be read are listed at the end of the manifest.

If the output disk becomes full, the scan pauses and you can free space, choose another folder, or stop.

HexEditor does not rebuild fragmented files. Only files stored in one piece are found.

## The results {#results}

The **Carving results** panel lists the files found. Select a row to jump to its start and select its range. For JPEG, PNG, GIF and BMP files, a small preview is shown (you can turn it off in **Settings > Forensics**). The list works with millions of results.

The length of each file is decided, in this order, by checking its structure, by a size value in the header, by the footer, and at last by the maximum size.

## Manage the signatures {#signatures}

Select **Forensics > Carving > Manage signatures...** to see the file type definitions. Built-in types cover JPEG, PNG, GIF, BMP, TIFF, PDF, ZIP (including DOCX, XLSX, JAR and APK), RAR, 7z, GZIP, BZIP2, MP3, MP4, AVI, WAV, SQLite, PE, ELF, OLE documents, registry hives, EVTX, LNK, RTF, HTML and XML.

- Built-in definitions cannot be edited. You can turn them off, or select **Duplicate** and edit the copy.
- **New** makes a definition. Enter the name, the extension and at least one header as hexadecimal bytes. `??` stands for any byte and `?` for any digit. Optional parts are the header offset (for example 4 for MP4), a footer, the minimum and maximum size (for example `1M`), a size value in the header, a structure check, and whether the header is searched only at the start of a sector.
- Mistakes are marked in red with an explanation, and **Save** stays off until they are fixed.
- **Try this definition** scans the selected range with the definition and shows how many were found and where the first ten are.
- **Import...** and **Export...** use a JSON file. When you import, definitions with mistakes are skipped and listed with the reason; the others are imported.

Definitions are saved in `carving-signatures.json` in the settings folder.
