# Cases, evidence and the audit log

Keep everything about one investigation in a case: the list of evidence with its hashes, a log of what you did, and the files you export. This page also explains how times are shown.

## Create or open a case {#case}

1. Select **Forensics > Case > New case...**, or run **Forensics: New case** from the command palette.
2. Type the case number and the case name. These two are required. You can also enter the investigator, the organization and a description.
3. Choose the case folder. HexEditor puts `case.json`, `audit.log` and the folders `exports` and `reports` in it.
4. Choose the time zone of the case. It is used to read times that have no time zone, such as FAT file times (see [Times](#times)).
5. Select **Create**. If the folder cannot be written, the case is not created and the reason is shown.

Only one case can be open at a time. To open an existing case, select **Forensics > Case > Open case...** and choose its folder. If `case.json` is damaged, HexEditor shows the line of the error and does not open the case or change the file. Select **Forensics > Case > Close case** to close it. **Forensics > Case > Case properties...** changes the case information, including the time zone.

While a case is open, its name is shown in the window title and in the status bar.

When you open a case, HexEditor turns on [write-protect mode](write-protection.md), or asks you whether to turn it on. You can choose which in **Settings > Forensics**.

## Add evidence {#evidence}

1. Open the disk, volume, image or file in a tab.
2. Select **Forensics > Case > Add evidence...**.
3. Type a description. Leave **Compute hashes now** ticked to compute SHA-256 and MD5 (you can also choose SHA-1 and SHA-512).
4. Select **Add evidence**.

Each item gets a number (`E001`, `E002`, ...) and is recorded with its type, path, and for disks the model, serial number, capacity and sector size. The hash is computed on the data as it is on the disk, not on a transformed view. Hashing is a long task shown in the processing center. If you cancel it, the evidence stays in the list marked as "Not computed".

Adding a source that is already in the list is refused, and the existing number is shown.

The **Evidence** panel (**Forensics > Case > Evidence list**) shows the list. Double-click a row to open the evidence read-only. Select a row and press <kbd>Delete</kbd> to remove it from the list. Removing only takes the item off the list: the disk or file is not touched, `case.json` keeps the item marked as excluded, and the audit log records it.

Reopening the case shows the same list.

## The audit log {#audit}

The audit log records what you do while a case is open: creating and opening the case, adding and removing evidence, opening and closing documents, hash results, exporting files, carving, saving, creating reports, and changes of write-protect mode. Browsing actions such as jumping and searching are recorded only if you turn on **Also record browsing actions in the audit log** in **Settings > Forensics**.

Each line has the time (always UTC), the user, the computer, the operation, the evidence, the result and a message. Passwords, keys and recovery keys are never written to it. Each line also contains the hash of the line before it, so a change anywhere in the log can be found.

Open **Forensics > Case > Show audit log** to see the log. The list stays fast even with millions of lines. Select **Export CSV** or **Export JSON** to save it.

To check the log, select **Forensics > Case > Verify audit log**, or **Verify** in the panel. If a line was changed, deleted or inserted, the first line that does not match is shown with a red border and a sentence. Lines removed from the end of the log are found too: the app keeps the number and hash of the last line it wrote in `audit.log.head`, next to `audit.log`. That result is recorded in the log too.

If the log cannot be written, for example because the disk is full, an error bar that cannot be closed stays at the top. Operations that write data, such as saving, secure delete and carving, are turned off until you fix the problem and select **Try again**. Records made in the meantime are kept and written afterwards.

When no case is open, nothing is recorded, except that changes of write-protect mode are recorded in `forensics-audit.log` in the settings folder.

## Times {#times}

Times in forensic lists are kept in UTC and shown in ISO 8601, for example `2026-10-07T03:15:42Z` and `2026-10-07T12:15:42+09:00`. The regional date format is not used, so that results look the same everywhere.

Choose how times are shown in **Settings > Forensics > Date and time display**: UTC and local time (two columns, or the local time in parentheses in the details), UTC only, or local time only. The local time uses the time zone of the case, or the Windows time zone when no case is open.

Some formats do not store a time zone. FAT times are read as local time in the time zone of the case and marked as an estimate. If you change the time zone of the case, those times change. NTFS times are UTC. exFAT uses the offset stored with the time when there is one.

A time that is out of range, for example `FFFFFFFFFFFFFFFF`, is shown as the raw value in hexadecimal with the words "invalid time".
