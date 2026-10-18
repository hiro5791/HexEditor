# Settings and keyboard shortcuts

This page explains the settings screen, where settings are stored, how to change keyboard shortcuts, and the command palette. Changes take effect immediately. There is no OK or Apply button.

## The settings screen {#settings-screen}

Press <kbd>Ctrl</kbd>+<kbd>,</kbd> (**Tools > Settings**) to open the settings as a tab. You can also use **Settings: Open settings** in the command palette.

Categories are on the left and settings on the right.

| Category | Main contents |
| --- | --- |
| **General** | Startup behavior, windows, recent files |
| **Appearance** | Theme, window background, color scheme, font |
| **View** | Hex view display, zoom, encoding |
| **Editing** | Input mode, paste, undo |
| **Search** | Search defaults and limits |
| **Files and saving** | Backups, recovery data, outside changes |
| **Keyboard** | Keyboard shortcuts |
| **Language** | Display language |
| **Accessibility** | Screen reader announcements |
| **Automation** | Scripts and plug-ins |
| **Updates** | Automatic updates and channel |
| **Privacy** | Features that use the network |
| **Explorer integration** | Right-click menu and file associations |
| **Advanced** | Data folder, settings file, reset, import / export |

- Type in **Search settings** at the top to find settings by name or description across categories.
- A setting you changed from its default has a vertical line at its left. **Reset to default** puts back just that setting.
- **More options** next to a category name also offers **Reset this category to default**.
- Settings that need a restart, such as the display language, say "Takes effect after restart." under the setting. Select **Restart now** at the top to restart. Your open tabs come back after the restart.
- If a value is not valid, a red outline and the reason are shown, and it is not saved.

In the command palette, type `#` followed by a word to find a setting and open it.

## The settings file {#settings-file}

Settings are stored in JSON files. If you like to edit settings directly or copy them to another PC, you can use these files. If you just use the app, you never need to touch them.

**Open data folder** in **Tools > Settings > Advanced**, or **Settings: Open data folder** in the command palette, opens the folder. Its location depends on the edition.

| Edition | Where settings are stored |
| --- | --- |
| Microsoft Store | The app's private storage (`%LocalAppData%\Packages\...\LocalState\`) |
| Installer | `%LocalAppData%\HexEditorData\` |
| Portable | The `Data` folder next to `HexEditor.exe` |

The main files in that folder are:

| File | Contents |
| --- | --- |
| `settings.json` | Only the settings you changed from the defaults |
| `keybindings.json` | Your shortcut changes |
| `themes\` | Your color schemes |
| `session.json` | The state of the tabs that were open |
| `recent.json` | Recent files |
| `documents\` | Per-document display settings, bookmarks and so on |

To open `settings.json`, use **Settings: Open settings file** in the command palette. It lists settings keys and values, like `"ui.theme": "dark"`. If you edit it while HexEditor is running, the change is picked up in about a second. If there is a mistake, HexEditor keeps using the previous settings and the bar at the top shows where the mistake is.

- If the settings file is broken, HexEditor starts with default settings and keeps the broken file.
- A settings file written by a newer version is read but never rewritten.

## Importing, exporting and resetting settings {#import-export-settings}

### Export and import

In **Tools > Settings > Advanced** you can write your settings to one file and read them back. Use it to move to another PC or change editions.

1. Select **Export settings** and choose what to include (settings, shortcuts, color schemes, recent files).
2. Choose the file name and place.

**Import settings** shows, for each part in the file, how many items would change. You can choose **Replace** or **Merge**. A backup of your current settings is made before importing. Edition-specific items, such as the data folder location, are not included.

### Reset

- One setting: **Reset to default**. An **Undo** button stays for 8 seconds.
- A whole category: **More options** next to the category name, then **Reset this category to default**.
- Everything: **Reset all settings** in **Tools > Settings > Advanced**. You can choose what to reset (settings, shortcuts, color schemes and so on).

When you reset everything, the current files are first copied into a `backup-<date and time>` folder. The latest 5 are kept.

## Theme, font and colors {#appearance}

Change the look in **Tools > Settings > Appearance**.

- **Theme** (also in **View > Theme**): **Use Windows setting** (default; called **Use system setting** in the View menu), **Light** or **Dark**. Windows high contrast always follows the Windows setting.
- **Window background**: **Mica** (default), **Mica Alt** or **None**. Where Mica is not available, a plain color is used automatically.
- **Color scheme**: the colors of the hex view. You can duplicate a built-in scheme to make your own. If text and background do not contrast enough, a warning appears.
- **Font**, **Font size (pt)** and **Line height**: the hex view's text. Only monospaced fonts are listed (**Show all fonts** shows all). The default is Cascadia Mono.
- **Accent color**: the color of the selection, focus rectangles and accent buttons. Choose **Windows accent color** (default) or a **Custom color**. It takes effect after a restart. It is not used in high contrast.
- With **Follow the Windows text size** on, the hex view gets larger along with the Windows "Text size" setting.

The font of menus and dialogs is the Windows standard and cannot be changed.

## File handling {#files-and-saving}

Change how files are handled in **Tools > Settings > Files and saving**.

- **File lock**: whether other apps can write to a file while it is open in HexEditor. A change applies to files you open afterwards. Files that are already open do not change.
  - **Block writes by other apps only while there are unsaved changes** (default): other apps cannot write only while the file has unsaved changes.
  - **Always block writes by other apps while open**: other apps cannot write for as long as the file is open.
  - **Don't lock (always allow other apps to write)**: other apps can always write.

For the backup and recovery settings, see [Backups and recovery](opening-files.md#backups-and-recovery).

## Automation settings {#automation-settings}

**Tools > Settings > Automation** has the settings for scripts and plugins. You can change the number of scripts that run at once, the time and memory limits, the folders to add scripts from, the hooks that run scripts automatically, and more.

- **Trust and permissions**: the list of scripts for which you chose "Always allow". **Revoke** brings back the confirmation from the next run. The organization policy is shown as read-only. See [Review and revoke "Always allow"](scripting.md#revoke-trust).
- **C# script component**: shows the installed version, and has **Add...**, **Remove** and **Clear compile cache**. See [Manage the component](scripting.md#csharp-component-manage).

## Display language {#display-language}

HexEditor can be shown in 23 languages. By default it follows the Windows display language.

1. Open **Tools > Settings > Language** (or use **Settings: Change display language** in the command palette).
2. Choose a language from the list. The first item is **Use the Windows setting**. Each language is written in its own name.
3. Select **Restart now**.

If there are unsaved changes, you are asked about them before restarting. The tabs you had open come back afterwards. If your Windows language is not in the list, English is used.

In Arabic and Persian, the screen is mirrored left to right. Inside the hex view it is always left to right.

Number and date formats follow your Windows region settings, not the display language.

If you find a mistake in a translation, see [Reporting a translation error](troubleshooting.md#report-a-translation).

## Command palette {#command-palette}

Press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>P</kbd> or <kbd>F1</kbd> (**Help > Command palette**) to open the command palette. You can find and run any command by name. It is useful when you do not know where something is in the menus.

1. Open the command palette.
2. Type part of a command name. It matches if your letters appear in order.
3. Choose with <kbd>↑</kbd> <kbd>↓</kbd> and run with <kbd>Enter</kbd>. <kbd>Esc</kbd> closes it.

Each entry shows its category, its shortcut, and the reason if it cannot be used now. When the box is empty, the commands you used recently come first.

A character at the start switches what is searched.

| Start of input | What it searches |
| --- | --- |
| `>` | Commands (<kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>P</kbd> opens in this mode) |
| nothing | Open tabs and recent files |
| `:` | Go to offset (for example `:0x1F00`) |
| `@` | Bookmarks of the current document |
| `#` | Settings |
| `?` | A list of these characters |

## Keyboard shortcuts {#keyboard-shortcuts}

The main shortcuts are below. Others appear to the right of menu items and in the command palette.

| Action | Keys |
| --- | --- |
| New / Open / Save / Save as | <kbd>Ctrl</kbd>+<kbd>N</kbd> / <kbd>Ctrl</kbd>+<kbd>O</kbd> / <kbd>Ctrl</kbd>+<kbd>S</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd> |
| Open disk / Open process | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>D</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>M</kbd> |
| Reload | <kbd>Ctrl</kbd>+<kbd>R</kbd> |
| Close tab / Reopen closed tab | <kbd>Ctrl</kbd>+<kbd>W</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>T</kbd> |
| Next / previous tab | <kbd>Ctrl</kbd>+<kbd>Tab</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Tab</kbd> |
| Undo / Redo | <kbd>Ctrl</kbd>+<kbd>Z</kbd> / <kbd>Ctrl</kbd>+<kbd>Y</kbd> |
| Cut / Copy / Paste | <kbd>Ctrl</kbd>+<kbd>X</kbd> / <kbd>Ctrl</kbd>+<kbd>C</kbd> / <kbd>Ctrl</kbd>+<kbd>V</kbd> |
| Paste overwrite | <kbd>Ctrl</kbd>+<kbd>B</kbd> |
| Copy as / Paste special | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>C</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>V</kbd> |
| Select all / Select range | <kbd>Ctrl</kbd>+<kbd>A</kbd> / <kbd>Ctrl</kbd>+<kbd>E</kbd> |
| Toggle overwrite / insert | <kbd>Insert</kbd> |
| Find / Replace | <kbd>Ctrl</kbd>+<kbd>F</kbd> / <kbd>Ctrl</kbd>+<kbd>H</kbd> |
| Find next / Find previous | <kbd>F3</kbd> / <kbd>Shift</kbd>+<kbd>F3</kbd> |
| Search in files | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F</kbd> |
| Go to offset | <kbd>Ctrl</kbd>+<kbd>G</kbd> |
| Back / Forward | <kbd>Alt</kbd>+<kbd>←</kbd> / <kbd>Alt</kbd>+<kbd>→</kbd> |
| Set or remove bookmark / next / previous | <kbd>Ctrl</kbd>+<kbd>F2</kbd> / <kbd>F2</kbd> / <kbd>Shift</kbd>+<kbd>F2</kbd> |
| Zoom in / out / back to 100% | <kbd>Ctrl</kbd>+<kbd>=</kbd> / <kbd>Ctrl</kbd>+<kbd>-</kbd> / <kbd>Ctrl</kbd>+<kbd>0</kbd> |
| Split the view | <kbd>Ctrl</kbd>+<kbd>\\</kbd> |
| Full screen | <kbd>F11</kbd> |
| Show the data inspector | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>I</kbd> |
| Move focus to the next / previous area | <kbd>F6</kbd> / <kbd>Shift</kbd>+<kbd>F6</kbd> |
| Command palette | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>P</kbd> / <kbd>F1</kbd> |
| Settings | <kbd>Ctrl</kbd>+<kbd>,</kbd> |

**Help > Keyboard shortcuts** shows all current assignments as a list.

Depending on your keyboard layout, symbol keys such as `Ctrl+=`, `Ctrl+-`, `Ctrl+\` and `Ctrl+,` may not be available. You can run those commands from the menu or the command palette.

### Change a shortcut

1. Open **Tools > Settings > Keyboard** (or **Settings: Keyboard shortcuts** in the command palette).
2. In the search box at the top, filter by command name or key (for example `ctrl+g`). **Search by key** lets you search by pressing the key.
3. Select **Edit** in the row of the command you want to change.
4. Press the new keys. Two-step combinations (for example <kbd>Ctrl</kbd>+<kbd>K</kbd> followed by <kbd>Ctrl</kbd>+<kbd>F</kbd>) are allowed. Press <kbd>Enter</kbd> to confirm.

- One command can have several shortcuts.
- If a key is already used by another command, the list shows it. Choose **Replace** to take it from that command.
- You can choose the **Scope** (global, editor, find bar, panel). A shortcut in a narrower scope wins over the same key in a broader one.
- Use **Reset** in a row, or **Reset all to default** above the table, to go back.
- Alt+F4, combinations with the Windows key, and letter or number keys without a modifier cannot be assigned.
- **Export** and **Import** write the assignments to a file and read them on another PC.

## Shortcut presets {#presets}

If you are used to another editor, you can switch the whole set of shortcuts. Choose it with **Preset** in **Tools > Settings > Keyboard**, or in **Get started** on the start page the first time you run HexEditor.

| Preset | Contents |
| --- | --- |
| **Default** | HexEditor's own |
| **HxD style** | Matches HxD's keys |
| **010 Editor style** | Matches 010 Editor's keys |
| **VS Code style** | Matches VS Code's keys |

Switching presets keeps the changes you made yourself. If one of yours clashes with the preset, you can choose to keep yours or use the preset's.

## Workspaces and projects {#workspaces}

A workspace is the tabs and panel layout of the current window, saved under a name. It is useful when you always open the same set.

- **File > Workspace > Save workspace** writes a `.hexworkspace` file.
- **File > Workspace > Open workspace** loads it. Double-clicking a `.hexworkspace` file in Explorer opens it too.

A workspace does not contain the contents of files or per-document data such as bookmarks. It stores the arrangement of tabs and panels.

## Explorer integration and file associations {#explorer}

In **Tools > Settings > Explorer integration** you set up the Explorer right-click menu and file associations.

- **Right-click menu "Open with HexEditor"**: right-click a file in Explorer to open it in HexEditor. If you select several files, they all open as tabs in one window. In Windows 11, it may be under **Show more options**.
- **File associations**: `.hexproj` and `.hexworkspace` are associated with HexEditor. `.bin`, `.dat`, `.img`, `.rom`, `.dmp`, `.raw` and `.iso` are only added to the **Open with** list, and your default app does not change. To make HexEditor the default, use **Set default apps**, which opens the Windows **Default apps** settings.

It depends on the edition:

| Edition | Right-click menu and associations |
| --- | --- |
| Microsoft Store | Registered when installed. Cannot be changed on this screen |
| Installer | Registered during installation. **Unregister** removes them |
| Portable | Not registered at first. **Register** adds them |

If you move the folder of a portable edition after registering, a bar suggests updating the registration. Select **Update**.

Before deleting the portable edition, select **Unregister from this PC** to remove the right-click menu, associations, jump list and temporary files together. Then delete the folder.

If you right-click the HexEditor icon on the taskbar, the jump list shows pinned and recent files and common actions.

## Privacy and network {#privacy}

HexEditor does not send usage data, crash information, settings or information about the files you open anywhere. There is no setting for sending them either.

HexEditor uses the internet only in these cases:

| Feature | Destination |
| --- | --- |
| Checking for and downloading updates | GitHub Releases (installer and portable editions), Microsoft Store (Store edition) |
| Downloading templates, plug-ins and similar | Their download sites, only when you ask |
| Downloading additional components | The same source as updates, only when you ask |
| Opening documentation and translation report pages | GitHub, in your browser |

In **Tools > Settings > Privacy** you can turn these off individually or all together. **Offline mode** turns off all of the features above. Connections use the Windows proxy settings.

Remote data sources and script network access connect only to destinations you specify, and only when you allow it. Offline mode does not cover them.

The first time you start HexEditor, the start page lists the features that use the network. Logs stay on this computer and never contain file contents.

## Updates {#updates}

HexEditor checks for updates 30 seconds after startup and then every 24 hours. You can check at any time with **Help > Check for updates**. A manual check always shows a result.

In **Tools > Settings > Updates** you can choose:

- **Check for updates automatically**
- **Download updates automatically** (installer edition)
- **Update channel**: **Stable** (default) or **Preview**
- **Skip this version**: choose it in the bar at the top to stop being told about that version.

Updating differs by edition:

| Edition | How updating works |
| --- | --- |
| Installer | Downloads in the background. When it is ready, the bar shows **Restart to update**. If you choose **Later**, it updates when you quit. You cannot restart while something like a save is running |
| Microsoft Store | The Microsoft Store updates it automatically. **Update in Microsoft Store** in the bar opens the Store page |
| Portable | Does not replace itself. **Open download page** in the bar opens the page for the new version. Extract the new zip and move the old folder's `Data` folder into it |

On the first start after an update, a bar says "Updated to version X". **Release notes** shows what changed. If an update fails, the previous version stays installed.
