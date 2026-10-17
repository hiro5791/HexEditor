# Accessibility

You can do everything in HexEditor without a mouse. It also works with screen readers, high contrast, large text and screen zoom. This page explains how to use each of them.

## Using HexEditor with the keyboard only {#keyboard-only}

Every command can have a key. Open menus with <kbd>Alt</kbd> or <kbd>F10</kbd> and choose items with their access keys (the underlined letters). If you only know a command's name, find it in the command palette with <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>P</kbd> or <kbd>F1</kbd> ([Command palette](settings.md#command-palette)). You can change key assignments in [Keyboard shortcuts](settings.md#keyboard-shortcuts).

### Move between areas of the window

<kbd>F6</kbd> moves focus to the next area and <kbd>Shift</kbd>+<kbd>F6</kbd> to the previous one. The areas are, in order: tab strip, editor, right panel, bottom panel, left panel and status bar.

- Inside the hex view, <kbd>Tab</kbd> switches between the hex and text columns. Use <kbd>F6</kbd> to leave the hex view.
- Inside an area, <kbd>Tab</kbd> / <kbd>Shift</kbd>+<kbd>Tab</kbd> and the arrow keys move around. In the tab strip, toolbar and status bar, the arrow keys move between items.
- Split panes can be reached with <kbd>F6</kbd> too.
- On a status bar item, <kbd>Enter</kbd> does the same as clicking it.

### Everyday operations

- Open a right-click menu with <kbd>Shift</kbd>+<kbd>F10</kbd> or the Menu key.
- Dialogs and flyouts close with <kbd>Esc</kbd>, and focus returns to where it was.
- In list panels (bookmarks, search results and so on), <kbd>Enter</kbd> goes to the selected item, <kbd>Delete</kbd> deletes, <kbd>Space</kbd> toggles, and <kbd>Esc</kbd> returns focus to the editor.
- In the find bar, <kbd>Enter</kbd> finds next, <kbd>Shift</kbd>+<kbd>Enter</kbd> finds previous and <kbd>Esc</kbd> closes it.
- The place that has focus always shows a visible outline.

Operations that look like drag only have keyboard alternatives. To reorder tabs, use **Tab: Move left** and **Move right** in the command palette. To split the view, press <kbd>Ctrl</kbd>+<kbd>\\</kbd>. To move a panel, use the **Panel options** menu of the panel.

For the movement and selection keys of the hex view, see [The hex view](hex-view.md#moving-the-cursor) and [Selection and clipboard](selection-clipboard.md#select).

## Screen readers {#screen-readers}

HexEditor works with Narrator and NVDA. The hex view is read as a "hex editor", and its help text says "Tab switches between the Hex and text columns."

When you move the cursor, the position is announced.

- Example: "Offset 0x1F00, value 4A, character J, Hex column, modified, bookmark header"
- Hex digits are read one by one. `00` is read as "null", and characters that cannot be shown are read as "non-printable character".
- Switching overwrite and insert, switching columns, switching read-only, changes in the selection ("Selection 16 bytes, 0x1F00 to 0x1F0F"), long operations finishing, and messages in the bar at the top are announced too.
- While you hold a key and the cursor races along, announcements are held back and spoken once after you stop.

Choose how much is announced with **Screen reader detail** in **Tools > Settings > Accessibility**.

| Choice | What is announced |
| --- | --- |
| **Full** (default) | Offset, value, character, column and states (modified, bookmark and so on) |
| **Brief** | Only the value and character (for example "4A J") |
| **Offset only** | Only the offset |
| **None** | Nothing |

To hear the current position again, run **Read current position** from the command palette (**View > Read current position**). It also reads the selection and the file size.

The text of the hex view can be read by screen readers in the same format as the screen. Every control, including menus and panel buttons, has a name.

## High contrast and text size {#high-contrast}

### Contrast themes

When you turn on a Windows contrast theme (**Settings > Accessibility > Contrast themes**), HexEditor switches to the Windows system colors automatically. No restart is needed.

- The hex view uses only system colors, without color schemes or byte themes.
- Meaning is never given by color alone. Modified bytes get an underline, search matches an outline, bookmarks a triangle mark, the current row lines above and below, and differences a hatched background.
- **Byte theme**, which relies on color, and the window background material are not available.

For light and dark, use **View > Theme**. **Use system setting** follows Windows. For more, see [Theme, font and colors](settings.md#appearance).

### Text size

If you make text bigger in Windows **Settings > Accessibility > Text size** (100 to 225%), the text of menus and panels grows. So does the hex view. If you turn off **Follow the Windows text size**, only the hex view stays as it was. Text is designed not to be cut off or overlap.

If **Visual effects > Animation effects** is off in Windows, HexEditor stops its animations too.

## Zoom and high DPI {#zoom-and-dpi}

HexEditor has two kinds of zoom.

- **Hex zoom**: enlarges only the data text. Change it with <kbd>Ctrl</kbd>+<kbd>=</kbd>, <kbd>Ctrl</kbd>+<kbd>-</kbd>, <kbd>Ctrl</kbd>+<kbd>0</kbd> (back to 100%) or <kbd>Ctrl</kbd>+wheel.
- **Screen zoom**: enlarges everything, including menus, panels, dialogs and the status bar. Change it with **Zoom in entire screen** and similar items in **View > Zoom**.

Both range from 50 to 400%. The byte with the cursor stays on screen when you zoom. The scale appears in the status bar; selecting it returns to 100%. For more, see [Zoom and full screen](hex-view.md#zoom).

When you move the window between monitors with different scaling, or change the Windows display scale (100 to 400%), the hex view redraws itself automatically. The text stays sharp.
