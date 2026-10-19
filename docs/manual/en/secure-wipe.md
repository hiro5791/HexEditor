# Secure wipe

Overwrite a range, a whole disk, the free space of a volume, or the file slack so that the old data cannot be read back. A wipe cannot be undone, so check the target carefully before you confirm.

## Methods {#methods}

Every wipe uses the same methods as [secure delete](secure-delete.md#delete):

| Method | What is written |
| --- | --- |
| **Overwrite once with zeros** (default) | `00` once. |
| **Overwrite once with random data** | Cryptographically secure random data once. |
| **DoD 5220.22-M (3 passes: 00, FF, random)** | `00`, `FF`, then random data. |
| **Custom** | 1 to 35 passes. Type one pass per line: hex bytes such as `A5 5A`, or `random`. |

The processing center shows which pass is running, the progress of the pass, and the time left for all passes.

## Erase a selected range {#range}

1. Open a file, a disk image, a disk, or a volume, and select the range to erase.
2. Select **Forensics > Wipe > Erase selection**. You can also right-click the selection and select **Securely erase selection**, or use **Forensics: Securely erase selection** in the command palette.
3. Check the start and the length, choose a method, and select **Erase**.
4. The confirmation shows the target (for a disk: its number, model, serial number, size and volumes), the range (start LBA, number of sectors and bytes), the method and the number of passes, and an estimate of the time. On a disk, the range is widened to whole sectors, and the confirmation says so.
5. Select **Erase N bytes** to start, or **Cancel**. **Cancel** is the default button, so pressing <kbd>Enter</kbd> alone does not erase anything.

**Read back and verify at the end** is on by default. After the last pass, HexEditor reads the range back and checks that it matches what was written. The result lists any ranges that do not match.

The data is written directly to the file or the device, not as an edit, so you cannot undo it with <kbd>Ctrl</kbd>+<kbd>Z</kbd>. After the wipe, open tabs of the same file or device are read again.

If the document has unsaved edits, HexEditor asks you to **Save** or **Discard** them first.

## Erase a whole disk {#disk}

1. Open the whole physical disk in a tab.
2. Select **Forensics > Wipe > Erase disk**.
3. Choose a method and select **Erase**.
4. Type the name of the disk, for example `Disk 2`, in the confirmation. **Erase Disk 2** stays unavailable until the name matches.

Before writing, HexEditor locks every volume on the range. If a volume cannot be locked because a program has files open, HexEditor asks whether to dismount it. HexEditor never erases a disk that Windows is using (the system disk, the boot disk, or a disk with a page file).

## Wipe free space {#free-space}

Overwrite the unallocated space of a volume, where the contents of deleted files remain.

1. Select **Forensics > Wipe > Wipe free space**. You can also use **Forensics: Wipe free space** in the command palette.
2. Choose how to wipe:
   - **Through Windows (a mounted volume)**: choose the volume. HexEditor creates a temporary folder on the volume, fills the free space with files that contain the pattern, and deletes them at the end. On NTFS it also creates many small files to overwrite data left in free MFT records. The volume is not locked, so you can keep working, but the volume has no free space while this runs.
   - **Direct writing (the file system in this tab)**: first show the file system of the tab (see [file systems](file-systems.md)). HexEditor finds the unallocated clusters from the allocation information and overwrites them directly. NTFS, FAT12, FAT16, FAT32 and exFAT are supported.
3. Choose a method and select **Erase**.
4. Check the volume, the amount of free space, the method and the estimate, and select **Wipe free space**.

When you cancel the method through Windows, HexEditor always deletes the temporary files. If a temporary file cannot be deleted, the result shows its path so that you can delete it yourself.

Direct writing can damage files if the allocation information of the file system is wrong. HexEditor checks the file system first. If the volume is marked dirty (for example, after Windows was not shut down properly) or other inconsistencies are found, it does not write and asks you to repair the volume with chkdsk first. Save or discard the edits of the tab before you write directly.

## Wipe file slack {#slack}

File slack is the space between the end of a file and the end of its last cluster. It can contain data from earlier files.

1. Show the file system of the tab.
2. Select **Forensics > Wipe > Wipe slack**, or **Forensics: Wipe file slack** in the command palette.
3. Choose a method and select **Erase**. The confirmation shows the number of files and the amount of slack.
4. Select **Wipe slack**.

The contents of the files do not change. Compressed, sparse and resident files (small files stored inside the MFT on NTFS) are skipped; the result shows how many files were skipped.

## Limits {#limits}

- On SSDs and flash media, wear leveling may keep the original data elsewhere even after it is overwritten. HexEditor does not run ATA Secure Erase, NVMe Format or Sanitize.
- Wiping is not available in write-protect mode, and data sources in the evidence list cannot be wiped. The menu items are unavailable and show the reason.
- Each wipe, refused wipe, and cancelled wipe is recorded in the audit log.
