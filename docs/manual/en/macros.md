# Macros

This page shows how to record your actions and play them back as often as you like. It is useful when you need to repeat the same edit dozens of times. You do not need to program.

A macro is saved as a JavaScript script. If you need to, you can fine-tune it later in the [script editor](scripting.md#script-editor).

## Recording a macro {#record}

1. Move the cursor to the state where you want to start recording.
2. Press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>R</kbd>. You can also select **Tools > Macro > Start / stop recording**, or run **Macro: Start / stop recording** from the command palette.
3. In the **Record a macro** dialog, choose how positions picked with the mouse are recorded, then select **Start**.
   - **Relative to the cursor** (default): positions are recorded as the distance from the cursor where you started. Choose this if you want to play the macro somewhere else.
   - **Absolute offsets**: the offsets themselves are recorded. Choose this if you always fix the same place.
   - Select **Don't ask again (use this choice)** to skip this dialog next time.
4. Do the actions you want to record.
5. Press <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>R</kbd> again to stop. You can also select **Recording** in the status bar.

While recording, the status bar shows a red circle, the word "Recording" and the number of recorded actions.

### What is recorded {#what-is-recorded}

A macro records what your actions mean, not the keys you pressed. So it works the same way if you change the display language.

| Action | What is recorded |
| --- | --- |
| Typing hex or text | Typing in a row is joined into one write |
| Moving the cursor (arrows, <kbd>Page Up</kbd>, <kbd>Page Down</kbd>, <kbd>Home</kbd>, <kbd>End</kbd>) | The move and how many times. Repeated identical moves are joined |
| Clicking and selecting with the mouse | Follows the position setting |
| Actions that use a dialog (fill, data operations, replace and so on) | The values you confirmed in the dialog. No dialog appears when it is played |
| Search | The search conditions |
| Opening and saving files | The file path |
| Switching tabs | A relative choice such as "next tab" |

### What cannot be recorded {#unrecordable}

These actions cannot be recorded. The status bar briefly shows "Cannot record this operation: name", and the action is not recorded.

- Changing the selection with the keyboard
- Delete
- Typing at multiple cursors
- Interacting with other apps, and actions while a script runs

Select with the mouse, or use an action such as **Go > Go to Offset**, to record where you moved.

### Saving the recording {#save-the-recording}

When you stop, the **Save the macro** dialog appears.

1. Enter a **Name**.
2. Choose the **Folder**.
3. If you want, enter a **Shortcut** (for example <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>F9</kbd>). It is optional.
4. Select **Save**.

Even if you select **Don't save**, the recording stays available as the "Last macro" until you quit HexEditor.

If you stop without doing anything, "Nothing was recorded." appears and the save dialog does not.

## Playing a macro {#play}

### Play once {#play-once}

Select **Tools > Macro > Play last macro**, or run **Macro: Play last macro** from the command palette. The macro you recorded (or played) most recently runs once.

You can run a saved macro in any of these ways:

- In the **Script list**, choose it under **Macros** and select **Run**. See [The script list](scripting.md#script-list).
- It appears in the command palette as **Script: name**.
- Use the key you gave it when saving, or with **Assign shortcut** in the script list.

A macro you recorded and saved yourself runs without a trust confirmation. But if you change it later in an external editor, you are asked, just like for any other script.

### Play repeatedly {#play-repeatedly}

1. Select **Tools > Macro > Play repeatedly**. You can also run **Macro: Play repeatedly** from the command palette.
2. Choose the **Macro**.
3. Choose how to repeat:
   - **Number of times**: enter a number from 1 to 1,000,000 in **Times**.
   - **Until the end of the file**: stops when the cursor reaches the end or cannot move.
   - **For each search result**: puts the cursor at each result position and runs the macro once.
   - **Within the selection**: stops when the cursor leaves the selection.
4. Select **Play**.

A repeated play is one [undo](editing.md#undo-redo) step as a whole. Even after 10,000 repetitions, one <kbd>Ctrl</kbd>+<kbd>Z</kbd> undoes all of them.

Progress appears in the process center, with a count and a **Stop** button. The window does not freeze, even for a long run.

If an action fails during play (for example, a search finds nothing, or a position is out of range), the play stops there. A message tells you at which repetition and which action it stopped, for example "The macro stopped at repetition 3, operation "Find"".

Tip: if you played with the wrong count, <kbd>Ctrl</kbd>+<kbd>Z</kbd> undoes all of it as long as you have not saved. For important files, check the result before you save.

## Saving a macro as a script {#save-as-script}

A saved macro is a `.js` script file. It is placed in the macro folder and listed under **Macros** in the script list.

Select the macro in the **Script list** and select **Edit** to open it in the script editor. You can:

- Change the recorded values, for example the bytes it writes.
- Reorder, add or remove actions.
- Add loops and conditions.

The recording has a comment for every action. For example:

```javascript
// ==HexEditorScript==
// @name  Macro 2026-10-07 14:03
// @api   1.0
// ==/HexEditorScript==
let doc = app.activeDocument;
// Go to offset
doc.goTo(doc.cursor + 0x10);
// Fill
await app.commands.execute("edit.fill", { range: "sel", pattern: "00" });
```

To learn how to write scripts, see [Scripts](scripting.md).
