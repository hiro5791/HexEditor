# Getting started

This page shows how to install HexEditor, open a file, make a change and save it.

## Requirements {#requirements}

- Windows 10 version 1809 or later, or Windows 11.
- An x64 or ARM64 PC.
- You do not need to install .NET or the Windows App SDK. Every edition of HexEditor contains them.

## Choose an edition {#choose-an-edition}

HexEditor comes in three editions. They have the same features. They differ in how you install and update them, and where they keep your settings.

| | Microsoft Store | Installer | Portable |
| --- | --- | --- | --- |
| How to get it | Microsoft Store | `Setup.exe` from GitHub Releases, or winget | `.zip` from GitHub Releases |
| Administrator rights to install | Not needed | Not needed | Not needed (no installation) |
| Updates | Microsoft Store | Automatic, inside the app | You replace the folder yourself |
| Settings are kept in | The app's own storage | `%LocalAppData%\HexEditorData\` | The `Data` folder next to `HexEditor.exe` |
| Explorer menu and file associations | Added on installation | Added on installation | Only when you add them in the settings |

If you are not sure, choose the Microsoft Store edition.

Some features need administrator rights, for example opening a physical disk. The installer and portable editions ask for permission only when you use such a feature. In the Microsoft Store edition, run the whole app as administrator to use them. See [Administrator rights](opening-files.md#administrator).

## Install HexEditor {#install}

### Microsoft Store {#install-store}

1. Open Microsoft Store and search for **HexEditor**.
2. Select **Get** (or **Install**).
3. Start HexEditor from the Start menu.

### Installer {#install-installer}

1. Download `HexEditor-<version>-x64-Setup.exe` (or `-arm64-` for an ARM64 PC) from the GitHub Releases page.
2. Run the file. It installs HexEditor for your user account and starts it. It does not ask any questions.

You can also install it with winget:

```text
winget install Hiroyura.HexEditor
```

The installer adds HexEditor to the Start menu. It does not create a desktop shortcut. To remove HexEditor, use **Settings > Apps > Installed apps** in Windows.

### Portable {#install-portable}

1. Download `HexEditor-<version>-x64-portable.zip` (or `-arm64-`) from the GitHub Releases page.
2. Extract the zip to any folder, for example a USB drive.
3. Run `HexEditor.exe` in the `HexEditor` folder.

The portable edition keeps all its data in the `Data` folder next to `HexEditor.exe`. It does not change the registry unless you add the Explorer menu or file associations in the settings. To remove it, remove those first (see `README.txt` in the folder), then delete the folder.

## First start {#first-start}

When no file is open, HexEditor shows the start page. It has buttons to open a file, create a new file, open a disk and open a process. It also lists your recent files.

On the first start, the start page also shows **Get started**. Here you can choose the display language, the theme and the keyboard shortcut preset. You can keep the default values and change them later in the settings. Select **Close** to hide it.

## Open a file {#open-a-file}

Do one of the following:

- Press <kbd>Ctrl</kbd>+<kbd>O</kbd>, or select **File > Open**, and choose the file.
- Drag the file from Explorer and drop it on the window.
- In Explorer, right-click the file and select **Open with HexEditor**.

HexEditor opens files of any size at once. It reads only the part that is on the screen, so even a file of hundreds of gigabytes opens immediately.

## Read the hex view {#read-the-hex-view}

The hex view has three columns:

- **Offset**: the position of the first byte of the row, counted from the start of the file.
- **Hex column**: the value of each byte in hexadecimal.
- **Text column**: the same bytes as characters.

Move the cursor with the arrow keys, <kbd>Page Up</kbd>, <kbd>Page Down</kbd>, <kbd>Home</kbd> and <kbd>End</kbd>. <kbd>Ctrl</kbd>+<kbd>Home</kbd> and <kbd>Ctrl</kbd>+<kbd>End</kbd> go to the start and the end of the file. <kbd>Tab</kbd> moves the cursor between the hex column and the text column.

The status bar at the bottom shows the cursor position, the selection, the edit mode and whether the file has changes.

To go to a position, press <kbd>Ctrl</kbd>+<kbd>G</kbd> and type the offset, for example `0x1F00`. To search, press <kbd>Ctrl</kbd>+<kbd>F</kbd>.

## Make a change {#make-a-change}

1. Move the cursor to the byte you want to change.
2. Type the new value. In the hex column, type hexadecimal digits (`0`–`9`, `A`–`F`). In the text column, type characters.

HexEditor starts in overwrite mode: typing replaces the existing bytes and the file size does not change. Press <kbd>Insert</kbd> to switch to insert mode, which adds new bytes instead.

Changed bytes are shown in a different color. Press <kbd>Ctrl</kbd>+<kbd>Z</kbd> to undo a change and <kbd>Ctrl</kbd>+<kbd>Y</kbd> to redo it. You can undo every change since you opened the file.

## Save {#save}

Press <kbd>Ctrl</kbd>+<kbd>S</kbd>, or select **File > Save**. To save to another file, select **File > Save As**.

HexEditor does not change the original file until you save. When it saves, it first writes to a temporary file and then replaces the original, so the original is safe even if saving fails.

## Find any command {#find-any-command}

Press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>P</kbd> (or <kbd>F1</kbd>) to open the command palette. Type part of a command name, for example `hash`, and press <kbd>Enter</kbd> to run it. The palette also shows the keyboard shortcut of each command.

## Next steps {#next-steps}

- [The hex view](hex-view.md)
- [Editing](editing.md)
- [Search and replace](search-replace.md)
- [Settings and keyboard shortcuts](settings.md)
