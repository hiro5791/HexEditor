# Troubleshooting

This page explains how to find out what went wrong when something does not work, and how to fix it. When you are in trouble, look here first.

## Where HexEditor keeps its data {#data-folder}

Everything HexEditor creates, such as settings, logs and recovery data, is kept in one data folder. Its location depends on the edition.

| Edition | Data folder |
| --- | --- |
| Microsoft Store | `%LocalAppData%\Packages\<package name>\LocalState\` |
| Installer | `%LocalAppData%\HexEditorData\` |
| Portable | The `Data` folder next to `HexEditor.exe` |

Open the folder with **Open data folder** in **Tools > Settings > Advanced**, or with **Settings: Open data folder** in the command palette. **Help > About HexEditor** also has a button to open it.

It contains:

| Name | Contents |
| --- | --- |
| `settings.json`, `keybindings.json` | Settings and shortcuts |
| `recovery\` | Recovery data for crashes |
| `crash\` | Information about crashes |
| `logs\` | Logs |
| `documents\` | Per-document display settings, bookmarks and so on |

If you run the portable edition from a place that cannot be written to (such as read-only media), it cannot save settings. The bar at the top tells you when that happens. Copy it to a writable place.

Data from edits in progress is kept as temporary files in the data folder (in the Windows temp folder for the portable edition). Edits that make the file bigger make the temporary files bigger. A bar warns you when free space drops below 1 GB. You can move the folder to another drive with the **Folder for temporary data** setting.

## After a crash {#crash-recovery}

Even if HexEditor stops abnormally, you can usually get your unsaved changes back. Documents with changes are saved as recovery data every minute.

1. Start HexEditor again.
2. When the **Recover unsaved changes** window appears, choose the documents you want and select **Recover**.
3. Check the recovered documents and save them, for example with **File > Save As**.

**Discard** deletes the recovery data. **Later** asks again at the next start.

- A recovered document has no undo history.
- If the original file changed in the meantime, it opens read-only. Check the contents.
- Process memory cannot be recovered.
- If a save stopped halfway, **Restore** is offered.
- Recovery data older than 30 days is deleted automatically.

If a bar says "HexEditor closed unexpectedly last time." when you start, **Open crash info** shows the file in the `crash\` folder. It contains the version, edition, exception details and recent log lines. It does not contain the contents of the files you had open. **Report a problem** lets you report it as is ([Reporting a problem](#report-a-problem)).

See also [Backups and recovery](opening-files.md#backups-and-recovery).

### Safe mode

If you hold the <kbd>Shift</kbd> key while starting, HexEditor starts in safe mode. It does not load plug-ins or scripts that run automatically, and the title shows **(Safe Mode)**. Use it when these prevent HexEditor from starting. **Restart normally** in the bar returns to normal.

## A file or disk cannot be opened {#cannot-open}

| Message or symptom | Cause and what to do |
| --- | --- |
| File not found | The file was moved or deleted. If you opened it from the recent files list, you can remove it from the list |
| You don't have permission to open this file | Check the file's permissions. If it cannot be opened even read-only, no tab is created |
| Another app is using the file | The message names the app. Close that app and open the file again |
| Folders cannot be opened | Choose a file. To search the files in a folder together, use [Searching in multiple files](search-replace.md#multiple-files) |
| A device path (starting with `\\.\`) | It cannot be opened as a file. Use **File > Open Disk** |
| Opening a disk or process says administrator rights are needed | See [Administrator rights](opening-files.md#administrator) |
| This volume is locked by BitLocker | Unlock it, then open it |
| No media is inserted | Put media in the optical drive or card reader |
| A process cannot be opened | A process protected by Windows cannot be opened, even as administrator |

Also check:

- If you cannot open files by dragging from Explorer, check whether HexEditor is running as administrator. While it is, Windows does not allow dropping. Open with <kbd>Ctrl</kbd>+<kbd>O</kbd> instead.
- If you drop 21 or more files at once, HexEditor asks before opening them. Folders cannot be opened.
- A file with the extension `.hex` or `.s19` opens decoded instead of as text. That is intended. To see it as text, set **Format** to **Binary as is** in **Open with options**.

## A file cannot be saved {#cannot-save}

Even if a save fails, the original file is unchanged. The message gives the reason.

| Reason | What to do |
| --- | --- |
| Not enough free space at the destination | Free some space, or choose **Save to another location**. **Save in place by shifting data** risks damaging the file if it stops halfway |
| No permission to write | Use **Save As** to save to a place you can write |
| "Can't be saved because ... is using it" | The message names the app that is using the file. Close it and select **Retry** |
| Over the limit of the drive's file system | On FAT32, files over 4 GB cannot be written. Save to another drive |
| An error while writing | The reason is shown. Check your USB drive or network connection |
| Contains unreadable ranges | Data with bad sectors and the like cannot be saved. The ranges are listed |
| A read-only document | **Save** is not available. Use **Allow editing** or **Save As** |
| Saving over a file open in another tab | Close that tab first |

- The document cannot be edited until the save is done. Progress is shown in the status bar and the processing center.
- If you save over a file that was changed outside HexEditor, you are asked to confirm the overwrite ([When the file changes on disk](opening-files.md#external-changes)).
- When writing to a disk, if the volume cannot be locked, you are asked whether to dismount it. You cannot write to the system volume.

## HexEditor is slow {#slow}

HexEditor is built to stay fast even with huge files. If it feels slow, check the following.

- **Running operations**: searching, saving and calculating hashes run in the background. Select the operations indicator in the status bar to open the processing center, where you can see progress and time left, and cancel.
- **Network and removable drives**: files on a network and USB drives read slowly. Opening a file reads only the start, but searching and saving depend on how fast the drive is.
- **Files that exist only in the cloud**: for files in OneDrive and similar, Windows downloads them when they are read.
- **A search with too many matches**: find all takes time when there are many results. Narrow the range or make the conditions stricter.
- **Regular expressions**: expressions that use backreferences or lookahead can take a long time.
- **Exact minimap calculation**: with **Calculate exactly** on, a huge file takes a long time. Turn it off.
- **Very many changes**: a lot of edits use memory. Saving reduces it.
- **Searching many files on a hard disk**: reducing how many files are searched at once can make it faster.

HexEditor does not use memory in proportion to the size of the file, and it never loads a whole file when you open it.

## Log files {#logs}

Logs are saved in the `logs\` folder of the data folder. They stay on this computer and are never sent out. They never contain file contents.

- The default **Log level** is **Normal**. To investigate a problem, set **Log level** in **Tools > Settings** to **Debug**. Debug logs also contain file paths, so be careful when you paste them into a report.
- Actions run by the administrator helper program (such as opening a disk) are recorded with their kind, target and result only. The data that was read or written is not recorded.
- Information from crashes is kept in `crash\`. The latest 10 are kept.

## Reporting a problem {#report-a-problem}

If you find a bug, please report it on GitHub.

1. Select **Help > Report a problem...**.
2. Your default browser opens a GitHub report page. The version, edition and OS details are already in the text.
3. Describe what you did and what happened, and send it.

The names and contents of the files you had open are not added. These details make a cause easier to find:

- What you did (the steps)
- What you expected and what actually happened
- The kind and size of the file (please do not include its contents)
- For a crash, the contents of the file in `crash\`

**Copy info** in **Help > About HexEditor** copies the version and other details. Paste it into your report. In offline mode, the address is shown before the browser opens.

## Reporting a translation error {#report-a-translation}

If text on the screen is unnatural or wrong, you can report it.

1. Select **Help > Report a translation error...**.
2. Choose the string you want to report from the list. You can search by the text shown, the English original or the resource key.
3. Select **Report**. Your browser opens a translation report page with the display language, the string key and the current translation filled in.
4. Write the translation you think is right and send it.

To report without picking a string, select **Report without choosing**. When the display language is English, the same page is used to report mistakes in the English original.

To see the key of a string, turn on **Show resource keys in tooltips** in the settings. Then point at text on the screen to see its key.

If you want to fix a translation yourself, you can also send a Pull Request that edits the resource file (`.resw`). The steps are in the description on the report page.
