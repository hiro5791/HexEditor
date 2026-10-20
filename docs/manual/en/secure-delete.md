# Secure delete

Overwrite a file with other data and then delete it, so that the usual recovery tools cannot bring it back. Use it with care: it cannot be undone.

## Delete files securely {#delete}

1. Select **Tools > File tools > Secure delete...**, or run **File tools: Secure delete** from the command palette.
2. Add the files and folders to delete with **Add...** and **Add folder...**, or by dropping them on the list. A folder includes every file inside it.
3. Choose the overwrite method:

   | Method | What is written |
   | --- | --- |
   | **Overwrite once with zeros** (default) | `00` once. |
   | **Overwrite once with random data** | Cryptographic random bytes once. |
   | **DoD 5220.22-M (3 passes)** | `00`, then `FF`, then random bytes. |
   | **Custom** | 1 to 35 passes. Write one line per pass: hex bytes such as `A5 5A`, or `random`. If there are fewer lines than passes, the last line is repeated. |

   Tick **Read back and verify the last pass** to check the last pass after it is written (not for random data).
4. Select **Delete...**. A confirmation shows the number of files, the total size and the method. Select **Delete permanently** to go on, or **Cancel**.

For each file, HexEditor opens it for exclusive use, overwrites all of it and flushes every pass to the disk, sets the length to 0, renames it to a random name and then deletes it. The file does not go to the Recycle Bin. NTFS alternate data streams are overwritten the same way.

The work appears in the processing center. If you cancel, the file that was being overwritten is not deleted and is reported as corrupted. If a file cannot be opened or written, for example because another program uses it, it is skipped and listed with the reason when everything else is done.

## What it cannot do {#limits}

- On SSDs, USB flash drives and SD cards, wear leveling can keep the original data somewhere else even after overwriting.
- Copies can remain in volume shadow copies, backups, synced folders such as OneDrive, compressed, encrypted or deduplicated files, and copy-on-write file systems such as ReFS.
- Free space and slack space are not wiped. To wipe them, see [secure wipe](secure-wipe.md).

## What cannot be deleted {#rules}

- A file that is open in HexEditor. Close its tab first.
- System folders such as `C:\Windows` and `Program Files`, and the root of a drive.
- Anything while write-protect mode is on. The menu item is disabled and says so.
