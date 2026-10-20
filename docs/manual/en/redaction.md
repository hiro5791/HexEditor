# Redaction

Before you hand a copy of a disk image to someone else, you can black out the contents of selected files while keeping the rest of the file system as it is.

## Redact files {#redact}

1. Open a copy of the disk image and show its file system (see [file systems](file-systems.md)). The tab must not be read-only.
2. In the file system browser, select one or more files.
3. Right-click and select **Redact file contents**. You can also select **Forensics > File system > Redact file contents**, or use **Forensics: Redact file contents** in the command palette.
4. Choose the pattern: **00 (default)**, **Random**, or **Hex bytes**.
5. To hide the file names too, select **Also replace the names with "_" of the same length**. It is off by default.
6. Check where the redaction log is written. By default, `redaction-log.csv` is written next to the image.
7. Select **Redact**.

HexEditor overwrites the data fragments and the slack of each file. Small NTFS files stored inside the MFT are overwritten there. Compressed files have their compressed fragments overwritten. The size, the dates, and the location of the fragments do not change.

Folders, deleted items, file system metadata, and files whose fragment information is broken are not redacted. HexEditor lists them with the reason.

## Save the redacted image {#save}

The redaction is an edit: nothing is written until you save, and you can undo it with <kbd>Ctrl</kbd>+<kbd>Z</kbd>.

1. Select **File > Save As**.
2. Save the image under a new name.

If you save to the original image or disk instead, HexEditor asks you to confirm, because this cannot be undone.

## The redaction log {#log}

`redaction-log.csv` has one row for each range that was overwritten: the time, the image, the path, the file ID, the kind of range (`data`, `slack`, `resident`, or `name`), the offset and length in the document, and the pattern. Files that were not redacted have a row with the reason. When the file already exists, new rows are added at the end. Each redaction is also recorded in the audit log.

## Notes on names {#names}

- On NTFS, the names in the MFT and in the folder index are replaced. Because the replaced names no longer match the sort order of the index, chkdsk may rebuild the index.
- On FAT and exFAT, the checksums of the names are updated so that the volume stays readable.
- If HexEditor cannot find all the entries for a name, it leaves the name as it is and tells you.
