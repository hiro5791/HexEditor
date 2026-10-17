# Opening files, disks and processes

This page explains how to open files, disks and process memory, and how saving and recovery work. Everything opens quickly, whatever its size.

## Open a file {#open-a-file}

1. Press <kbd>Ctrl</kbd>+<kbd>O</kbd> or select **File > Open**.
2. Choose a file. If you choose several files, each one opens in its own tab.

You can also drag files from Explorer into the window, or right-click a file in Explorer and select **Open with HexEditor**. If you drop several files, they all open as tabs in one window. If you drop 21 or more files, HexEditor asks before opening them. Folders cannot be opened.

HexEditor does not load the whole file. It reads only the part on screen, so even a file over 100 GB shows its first bytes within a second. Memory use does not grow with the file size.

- If you open the same file again, HexEditor switches to the tab that is already open.
- Files of length 0 open too.
- Paths longer than 260 characters and network files such as `\\server\share` open too.
- For files that exist only in the cloud, such as OneDrive placeholders, Windows downloads the data when HexEditor reads it.

If another app is using the file and it cannot be opened, the message names that app. If the file can be read but not written, HexEditor opens it read-only and the bar at the top of the window tells you why.

To open a file with options, select **File > Open with options**. You can set:

- **Open read-only**
- **Open a range** (see [Open part of a file](#partial-open))
- **Open as a disk image** (see [Disk images](#disk-images))
- **Format** (**Automatic**, **Binary as is**, **Intel HEX**, **S-record**, **Base64**)
- **Don't let other apps write to the file**

If you change nothing and select **Open**, it works like the normal **File > Open**.

## Create a new file {#new-file}

Press <kbd>Ctrl</kbd>+<kbd>N</kbd> or select **File > New**. An empty untitled document opens in a tab. You can change its length freely. When you save it, you choose where to put it.

To create a document with a given size, select **File > New with size**.

1. Enter the **Size** (for example `4K`, `1M` or `0x1000`).
2. Enter the fill value in hex. If you change nothing, the document is filled with `00`.
3. Select **Create**.

Even a size such as `1T` is created instantly. Nothing is actually written to disk until you save.

## Recent files and sessions {#recent-files}

**File > Recent files** lists the files you opened recently. Pinned files come first, followed by the 10 most recent. **Show all** lists everything in a window with a search box. **Clear list** removes only the items that are not pinned.

- You can change how many files are kept with the **Number of recent files** setting (25 by default).
- When you reopen a file, the cursor, scroll position and selection return to where they were. You can turn this off with the **Restore the last position** setting.
- The list also appears on the start page, and in the command palette when you type without a `>`.
- Files opened with a range and disks are listed too, and reopen with the same options.

When you quit, HexEditor saves which windows and tabs were open. The next time you start it, the same tabs come back. The **On startup** setting lets you choose **Restore the previous session** (default), **Offer to restore on the start page**, or **Start with no files**. Files open starting with the visible tab.

- <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>T</kbd> (**File > Reopen closed tab**) reopens closed tabs, newest first. Changes you discarded are not brought back.
- **File > Workspace > Save workspace** saves the tabs and panel layout of the current window under a name. **Open workspace** loads it again.
- Unsaved changes are not part of the session. When you quit, HexEditor asks whether to save or discard them.
- Process memory tabs are not restored.

## Read-only mode {#read-only}

You cannot change the data of a read-only document. A document becomes read-only automatically when:

- you opened it with **File > Open read-only**,
- the file has the read-only attribute or you have no permission to write it, or
- you opened a disk or process memory (the default).

A read-only tab shows a lock icon and the status bar shows **Read-only**. If you try to edit, the bar at the top explains why.

To make it editable, select **Allow editing** in that bar, or clear the check mark at **Edit > Read-only**. Selecting the lock icon in the status bar also toggles it. For a file with the read-only attribute, the attribute is removed when you save. For disks and process memory, HexEditor asks you to confirm.

You cannot clear read-only for:

- data with nowhere to write back, such as a process snapshot, or
- a file that another app is writing to, so that HexEditor cannot get a write handle.

Even a read-only document can be refreshed. If you try to save it, **Save as** is used instead, and the original file is not written.

## Open part of a file {#partial-open}

To edit only a part of a huge file, open just a range.

1. Select **File > Open with options**.
2. Choose the file and turn on **Open a range**.
3. Enter the **Start** (for example `0x1000_0000` or `5G`).
4. Choose **Length** or **End (inclusive)** and enter the value.
5. Select **Open**.

The tab shows the range, as in `data.bin [0x10000000–0x13FFFFFF]`. Offset 0 is the start of the range, and the addresses shown match positions in the original file.

- By default the length cannot change (overwrite only). Saving writes only the bytes you changed inside the range.
- Turn on **Allow changing the length (insert and delete)** to insert and delete. Saving then rewrites the whole file.
- You can open different, non-overlapping ranges of the same file in separate tabs.

## Open Intel HEX, S-record and Base64 files {#hex-formats}

Intel HEX, Motorola S-record and Base64 text files open as decoded binary data that you can edit.

- With **Open**, files with the extensions `.hex`, `.ihx`, `.ihex` (Intel HEX), `.s19`, `.s28`, `.s37`, `.srec`, `.mot`, `.mhx` (S-record) or `.b64` (Base64) are decoded automatically.
- To choose a format yourself, set **Format** in **Open with options**, or run **Open as Intel HEX**, **Open as S-record** or **Open as Base64** from the command palette.
- To see the text itself as bytes, open the file with **Format** set to **Binary as is**.

For Intel HEX and S-record, the document runs from the lowest address to the highest. Gaps with no data show as 00. These two cannot change length (overwrite only). Base64 can change length.

Saving converts the data back to the original format. Lines with checksum or format errors are listed when you open the file. A file with errors still opens, but read-only.

## Disk images {#disk-images}

Raw disk images such as `.img`, `.dd`, `.raw` and `.iso` can be handled like a disk with sectors.

1. Select **File > Open with options**.
2. Choose the file and turn on **Open as a disk image**.
3. Choose the sector size: 512 (default), 2048, 4096, or any power of two. The default for `.iso` is 2048.

For a disk image you can show offsets as sector number and position ([Display settings](hex-view.md#display-settings)) and use **Go > Go to Sector** ([Moving by sectors](go-to.md#sectors)). The length is fixed by default. Turn on **Allow changing the length** to insert and delete as well.

You can also start it from the command palette with **File: Open as disk image**.

## Disks and volumes {#disks-and-volumes}

You can open physical disks, volumes with a drive letter, and optical drives as bytes.

1. Press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd> or select **File > Open Disk**.
2. Choose a disk or volume from the list. Disks show their model, size, bus (SATA, NVMe, USB and so on) and MBR/GPT type. Volumes show their label, file system and BitLocker state.
3. If you want, specify a range. **Open read-only** is on by default.
4. Select **Open**.

A disk document has a fixed length. It is named like **Disk 1 (model, size)** or **Volume C: (label)**.

- Unreadable areas, such as bad sectors, show as `??` for just those bytes.
- The contents of a volume can be changed by the file system. Use **File > Reload** to read again.
- A volume locked by BitLocker must be unlocked before you open it.
- If a USB drive is removed, the bar at the top of the window tells you.

### Writing to a disk

If you turn off read-only and edit, saving (<kbd>Ctrl</kbd>+<kbd>S</kbd>) writes to the disk. Before it writes, HexEditor always asks you to confirm. The message shows the target disk, how much will be written and which volumes are affected. The default button is **Cancel**.

Writing is risky. The file system can be damaged, and if the write stops halfway it may not be possible to restore the original. HexEditor saves the old contents of the places it changes, and writes them back automatically if something fails. A mounted volume is locked while it is written. You cannot write to the system volume.

## Process memory {#process-memory}

You can open the memory of a running process, with virtual addresses as offsets.

1. Press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>M</kbd> or select **File > Open Process**.
2. Choose a process from the list. In **Filter** you can type part of the name, PID or title. **Show only my processes** and **Refresh** are also available.
3. Choose what to open: **Whole address space** (default), **Choose a module**, or **Specify a range**.
4. Select **Open**.

The name looks like **notepad.exe (PID 1234)**. Unallocated areas are not read. When you open a process, a **Memory map** panel appears at the bottom with the regions and modules (if you close it, bring it back with **View > Panels > Memory Map** or with **Toggle memory map** in the command palette). Double-click a row to jump to the start of that region.

- It is read-only by default. If you allow editing, saving (<kbd>Ctrl</kbd>+<kbd>S</kbd>) writes the changed bytes into the process. HexEditor asks before the first write.
- If the target page is read-only, HexEditor asks whether to remove the protection temporarily for the write.
- Writing does not stop the process, so other threads may see a half-written value.
- If the process exits, the bar at the top tells you. What was already read stays visible.
- Changes you close without saving are not brought back.
- A process protected by Windows cannot be opened, even as administrator.

## Administrator rights {#administrator}

Opening the following needs administrator rights.

| What you open | Administrator rights |
| --- | --- |
| A process of the same user that is not elevated | Not needed |
| A volume on a removable USB drive | Not needed |
| A whole physical disk, an internal disk volume, an optical drive | Needed |
| An elevated process, a system process, another user's process | Needed |

The installer and portable editions ask for permission through the Windows prompt (UAC) only when needed, and start a small helper program. HexEditor itself keeps running with normal rights. While the status bar shows a shield icon, the helper is running.

The Microsoft Store edition does not include the helper. When you open something that needs administrator rights, a message explains what to do. Close HexEditor, right-click HexEditor in the Start menu and select **More > Run as administrator**. Selecting **Restart as administrator** in the message does the same. For the portable edition, right-click `HexEditor.exe` and select **Run as administrator**.

While the whole app runs as administrator, Windows does not allow drag and drop from Explorer. Open files with <kbd>Ctrl</kbd>+<kbd>O</kbd> instead.

## When the file changes on disk {#external-changes}

If another app changes an open file, HexEditor notices and tells you. It does not report your own saves.

- **A file you have not edited** is read again automatically. The bar at the top says "The file was changed by another app, so it was reloaded." If you turn off **Reload unmodified files automatically**, you get the same bar as for edited files instead.
- **A file you are editing** shows these buttons:
  - **Reload**: discard your changes and read the new contents.
  - **Merge**: apply your changes again on top of the new contents, at the same offsets. If the outside change altered the length, they may not apply correctly.
  - **Compare**: compare your version with the version on disk.
  - **Ignore**: keep editing. If you save, you overwrite the outside change. HexEditor asks before it does.
- **If the file is deleted or moved**, the bar tells you. The contents can still be read, so you can keep editing. Saving recreates the file at its original path.

To reload by hand, use **File > Reload** (<kbd>Ctrl</kbd>+<kbd>R</kbd>). It keeps the bytes you changed and refreshes the rest. **File > Discard changes and reload** throws away all changes. For a file that was not changed outside, you can undo even this with <kbd>Ctrl</kbd>+<kbd>Z</kbd>.

When you start editing, HexEditor stops other apps from writing to the file. When you undo all changes or save, other apps can write again. While you are not editing, other apps can read and write freely.

## Save and save as {#saving}

Press <kbd>Ctrl</kbd>+<kbd>S</kbd> (**File > Save**) to save. <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd> (**File > Save As**) writes to a different file. **File > Save all** saves every tab with changes.

HexEditor automatically picks a safe and fast way to save.

- **Safe save**: writes the whole file to a temporary file in the same folder, and replaces the original only when it is complete. If the app or PC stops halfway, the original is not damaged. File attributes and permissions are kept. A save that changes the length uses this way.
- **In-place save**: when the length does not change and the data has not moved, only the changed parts are written to the original file. The old contents are kept aside first. Disks and process memory always use this way.
- **Save while shifting data**: you can choose it when there is not enough free space. It needs almost no extra space, but if it stops halfway the file may be damaged and cannot be restored. HexEditor asks every time.

Before saving, HexEditor checks the free space at the destination. If there is not enough, you can choose **Save to another location**, **Save in place by shifting data**, or **Cancel**. On a FAT32 drive, files over 4 GB cannot be written.

Saving runs in the background. Progress and time left show in the status bar and the processing center, and you can cancel. While saving, that document cannot be edited. A save shorter than 0.5 seconds shows nothing.

If saving fails, the original file is unchanged. The message explains why.

- If there are **no changes**, nothing is written, and the modified time stays the same.
- An **untitled document**, or one whose file was deleted, uses **Save as**.
- Even after saving, you can press <kbd>Ctrl</kbd>+<kbd>Z</kbd> to go back to the state before the save.
- You cannot save over a file that is open in another tab. Close that tab first.
- If another app is using the file, you see "name can't be saved because it's being used by app name." Close that app and select **Retry**. The original file is unchanged.

If you close a tab or window without saving, HexEditor asks whether to save. When several tabs have changes, they are listed in one dialog. A tab whose save fails is not closed.

## Backups and recovery {#backups-and-recovery}

### Keep backups

If you turn on **Make a backup when saving** in the settings, each save keeps the file as it was before as `<original name>.bak`. You can also choose the **Backup location** (the same folder as the file, or a folder you choose) and **Backups to keep** (1 to 10). It is off by default. Backups are not made for disks and process memory.

### Recovery after a crash

HexEditor saves documents with changes as recovery data automatically every minute. It does not write to the original file. You can change the interval with the **Recovery data interval (minutes)** setting (0 turns it off).

After the app or PC stops abnormally, the next start shows the **Recover unsaved changes** window.

1. Choose the documents you want to recover. The list shows the name, when it was saved and how many bytes changed.
2. Select **Recover** to open the document with its changes, marked as modified. **Discard** deletes the recovery data. **Later** asks again at the next start.

A recovered document has no undo history. If the original file changed after the recovery data was made, it opens read-only, because the content may not be correct. Process memory cannot be recovered.

If a save stopped halfway (for example an in-place save), the same window offers **Restore** to put the file back the way it was before saving. Recovery data older than 30 days is deleted automatically at startup.

See also [After a crash](troubleshooting.md#crash-recovery).
