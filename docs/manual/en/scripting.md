# Scripts

This page shows how to automate HexEditor with JavaScript and C# scripts. With a script you can repeat the same edit, process many files, or run your own analysis with one click.

If you only want to record your actions and play them back, [Macros](macros.md) are enough and you do not need to write a script.

## JavaScript and C# {#languages}

You can write scripts in two languages. The file extension decides the language.

| Language | Extension | Notes |
| --- | --- | --- |
| JavaScript | `.js`, `.mjs` | Works out of the box. It runs in a safe environment that can use only the features it is allowed, and it has limits on time and memory. |
| C# | `.csx` | You must add the C# script component first. It is fast, but the code can do anything on your PC. |

Both languages offer the same features (the API). Only the naming differs: `doc.readBytes` in JavaScript, `Doc.ReadBytes` in C#.

We recommend JavaScript to start with. Choose C# when you need speed or want to use .NET libraries.

## Running a script {#run-a-script}

### Start from a sample {#samples}

Choose a bundled sample from **Tools > Scripts > Samples**. There are samples such as XOR decode, a PE header summary, a hash list of a folder and a list of high-entropy regions. Open a copy of a sample to edit it freely.

### Run a file {#run-a-file}

1. Make the target document active.
2. Select **Tools > Scripts > Run script** and choose the script file.

After the first run, press <kbd>F5</kbd> (or select **Tools > Scripts > Run last script**) to run the same script again. When the script editor has the focus, <kbd>F5</kbd> runs the script in that editor.

You can also run scripts from the script list. See [The script list](#script-list).

### Parameters {#parameters}

If a script declares parameters, an input dialog appears before it runs. The values you entered last time are the defaults next time.

### While a script runs {#while-running}

- A script runs on its own thread. The window does not freeze and you can edit other documents.
- A document that a script is writing to cannot be edited until the script ends. A bar at the top says "The script is running" and has a **Stop** button.
- Progress and elapsed time are shown in the process center.
- Up to 4 scripts run at the same time by default. The others wait. Change this with **Scripts running at the same time** in **Settings > Automation**.
- All edits made by a script during one run form one [undo](editing.md#undo-redo) step. Press <kbd>Ctrl</kbd>+<kbd>Z</kbd> once to undo everything the script changed. If a script ends with an error, the changes it made so far stay, and you can undo them in the same way.

### Stopping a script {#stop}

Select **Stop** in the process center or in the bar at the top of the document. **Tools > Scripts > Stop all scripts** stops every running script. If a JavaScript script has not ended 3 seconds after you stop it, it is cut off.

A C# script cannot be cut off. If it is in a loop that does not check for the stop request, it may not stop. In that case, restart HexEditor. Unsaved edits are written to the recovery data first.

### Limits {#limits}

JavaScript has limits so that a runaway script cannot take the app down.

| Limit | Default | Range |
| --- | --- | --- |
| Run time | 60 seconds | 1 second to unlimited (0) |
| Memory | 512 MB | 16 MB to 4 GB |
| Recursion depth | 1,000 | 100 to 10,000 |

Time spent waiting for a bulk operation, and waiting for your input, does not count as run time. When the time limit is reached, the script is paused and you can choose **Continue (60 more seconds)** or **Stop**. Change the limits in **Settings > Automation**.

## The script editor {#script-editor}

Use the script editor to write scripts. It opens as a tab next to your documents.

- Create a new script: **Tools > Scripts > New script** (JavaScript) or **New C# script**.
- Edit an existing one: select **Edit** in the [script list](#script-list).

### Editor features {#editor-features}

- Syntax highlighting that follows the theme.
- Completion. Type `doc.` to see the available methods with descriptions. C# completion needs the C# script component.
- Error display. When you stop typing, the script is checked and errors get a wavy underline. They are also listed under **Problems** below the editor. Select one to jump to its line.
- Go to line (<kbd>Ctrl</kbd>+<kbd>G</kbd>).
- Folding, bracket matching, toggling comments (<kbd>Ctrl</kbd>+<kbd>/</kbd>), formatting (<kbd>Shift</kbd>+<kbd>Alt</kbd>+<kbd>F</kbd>), and find and replace.
- Save with <kbd>Ctrl</kbd>+<kbd>S</kbd>.

On a PC without the WebView2 runtime, the script opens in a basic editor without highlighting or completion, and a bar at the top tells you. You can still edit and run scripts.

The toolbar has **Run**, **Debug**, **Stop**, **Pause** and the **Target document** choice. Use **Target document** to pick the document the script works on. <kbd>F5</kbd> runs the contents of the editor as they are, even if you have not saved them.

A script file larger than 10 MB cannot be opened in the editor. Edit it with an external editor.

### The script list {#script-list}

Select **Tools > Scripts > Script list** (or **View > Panels > Script list**) to open the list panel.

It shows **Scripts**, **Macros** and **Samples**, and the contents of any folders you added. Use the box at the top to filter.

| Action | What it does |
| --- | --- |
| **Run** | <kbd>Enter</kbd> or a double-click also runs it |
| **Edit** | Opens it in the script editor. For a sample, this is **Open a copy to edit** |
| **New** | Creates a new JavaScript or C# script |
| **Rename**, **Delete (move to the recycle bin)**, **Show in folder** | Manage the file |
| **Assign shortcut** | Gives the script a key |
| **Run automatically** | Registers it as a [script that runs on events](#event-scripts) |

Scripts also appear in the command palette as **Script: name**. Give one a shortcut to run it with a key.

To add a folder to the list, use **Additional script folders** in **Settings > Automation**. This is handy for a folder your team shares. To open the scripts folder, select **Tools > Scripts > Open scripts folder**. When you put a file in a folder, the list updates by itself.

### Metadata {#metadata}

Comments at the top of a script can declare its name, description, parameters and more.

```javascript
// ==HexEditorScript==
// @name         XOR decode
// @name:ja      XOR デコード
// @description  Selection XOR with a key
// @param        key hex "5A" Key
// @param        whole checkbox false Whole file
// @permission   file-read
// ==/HexEditorScript==
```

- `@name` and `@description`: the name and description shown in the list. With a language such as `@name:ja`, the one that fits the display language is used.
- `@param name type default label`: a parameter. When you declare one, an input dialog appears automatically before the script runs. The values are in `script.args`.
- `@permission`: a permission the script needs. See [Trust and permissions](#permissions).
- `@on`: a candidate for automatic run. You register it yourself.

A script without metadata also runs.

### The output panel {#output-panel}

The output of `console.log` and similar calls, and errors, appear in the **Output** panel. Open it with **View > Panels > Output**. A setting in **Settings > Automation** can open it automatically when a script writes output.

- Use **Output source** at the top to switch between scripts and plugins.
- Each line shows the time, the kind (information, warning, error) and the text. Warnings and errors are marked with an icon as well as a color.
- Select a line number in an error to open that line in the script editor.
- You can **Clear**, **Copy all**, **Save to file**, **Wrap lines** and filter.

## The script API {#script-api}

Scripts use objects like these (every object and method is in the [Automation API reference](api-reference.md), which you can also open with **Help > Scripting API reference**):

| Object | What it does |
| --- | --- |
| `app` | The list of open documents, opening files, running app commands, receiving events |
| `doc` | The active document: read and write, search, save, cursor and selection, bookmarks, undo |
| `ui` | Messages, input dialogs, progress display, output |
| `hash` | Computing hashes |
| `clipboard` | Reading and writing the clipboard |
| `app.templates` | Applying templates and getting results |
| `settings`, `script` | Settings, storage for the script, parameters (`script.args`) |
| `fs`, `net`, `process` | Files, network, external programs (permission needed) |

A small example. It prints the first 16 bytes of the selection to the output panel:

```javascript
const doc = app.activeDocument;
const bytes = doc.readBytes(doc.selection.start, 16);
console.log(bytes);
```

### Tips for large data {#bulk}

Looping over bytes one by one is slow on huge files. For work that grows with the amount of data, such as XOR on a range, search, hashing or copying, use the bulk functions of the API. They work a little at a time, however large the file is, and support progress and cancel.

- Small reads and writes (up to 16 MB at a time) return at once.
- Wait for slow operations with `await`. Example: `await doc.transform(range, "xor", key)`.
- You cannot read a range larger than 16 MB in one call. Read it in parts.

### Running app commands {#commands}

`app.commands.execute("edit.selectAll")` runs any command from the menus or the command palette by its ID. IDs do not change with the display language, so your script keeps working if you change the language.

### Events {#events}

`app.on("afterSave", handler)` receives events. The events are `documentOpened`, `documentClosed`, `activeDocumentChanged`, `beforeSave`, `afterSave` and `selectionChanged`. A `beforeSave` handler can cancel the save.

A script that receives events stays resident until you stop it. Resident scripts show **resident** in the script list.

## Trust and permissions {#permissions}

Depending on how it is used, a script can damage important files or send information out. So a confirmation dialog appears before a script runs for the first time.

The dialog appears for:

- A script that runs for the first time
- A script whose contents changed since you last confirmed it

It does not appear for bundled samples, scripts you saved yourself in the script editor, macros you recorded, and input in the console. A script that you changed in an external editor asks again after each change.

The dialog shows the name, the author and a description such as:

- **JavaScript**: "This script can read and write the open documents", plus a list of any other permissions it asks for.
- **C#**: a warning that "This code can do anything on your PC with your rights". C# scripts do not run in a sandbox.
- A warning, if the file came from the internet.

The buttons are:

- **Run once**: runs it this time. You are asked again next time.
- **Always allow**: from now on, the same script runs without asking.
- **Don't run**.

Run only scripts from authors you trust.

### Kinds of permission for JavaScript {#permission-kinds}

A JavaScript script can read and write the open documents without any permission. It needs a permission to do the following:

| Permission | What it allows |
| --- | --- |
| `file-read` | Reading any file |
| `file-write` | Creating, writing and deleting any file |
| `network` | Network access |
| `process` | Running external programs |
| `disk` | Opening disks, volumes and process memory |
| `clipboard-read` | Reading the clipboard |
| `settings` | Changing app settings |

If a script calls a feature that needs a permission it did not declare with `@permission`, a prompt with **Allow** and **Don't allow** appears at that moment.

When HexEditor runs as administrator, you are asked every time a script runs, even if you chose "Always allow". Administrator rights affect the whole PC.

If your organization has turned scripts off in the settings file, the script menus are disabled.

### Review and revoke "Always allow" {#revoke-trust}

Scripts for which you chose "Always allow" are listed in **Trust and permissions** under **Settings > Automation**. The list shows the name, the permissions and the date you allowed it.

1. Open **Settings > Automation** and find **Trust and permissions**.
2. Select **Revoke** for the script you want to remove.

A revoked script asks again the next time it runs. If no script is always allowed, the list says "No scripts are always allowed."

If your organization has set the script policy, the policy is shown in **Trust and permissions** as read-only. You cannot change it here. Plugin trust is not in this list. It is managed in the plugin manager ([Plugin permissions and trust](plugins.md#plugin-permissions)).

## The script console {#console}

The **Console** lets you try things one line at a time without creating a script file. Open it with **Tools > Scripts > Console** (or **View > Panels > Console**).

1. Choose JavaScript or C# in **Language** at the top.
2. Type an expression or statement in the input box and press <kbd>Enter</kbd>. Press <kbd>Shift</kbd>+<kbd>Enter</kbd> for a new line.
3. The result appears in the history. Byte arrays are shown as a hex dump.

Tips:

- `doc` points to the active document at that moment.
- In JavaScript, variables you define stay for the next input. **Reset session** starts a new session. In C#, variables are not carried over to the next input.
- Press <kbd>↑</kbd> and <kbd>↓</kbd> to go through earlier inputs. The history is kept after a restart.
- Writes from the console form one [undo](editing.md#undo-redo) step per input.
- A long-running input can be stopped from the process center.

## Scripts that run on events {#event-scripts}

You can run scripts automatically when HexEditor starts, when a file is opened, before or after a save, and so on.

Register them under **Automatic run (hooks)** in **Settings > Automation**. You can also right-click a script in the script list and select **Run automatically**. Only scripts you add here run. A script cannot register itself.

| When | Runs | Default time limit |
| --- | --- | --- |
| Startup | After the first window is ready to use | 60 seconds |
| File opened | After the start of the document is shown | 10 seconds |
| Before save | Just before saving | 5 seconds |
| After save | After saving is done | 10 seconds |
| Before close | Just before a document closes | 5 seconds |

- You can register several scripts for one trigger. They run in the order listed.
- With a **Condition** (**Extension**, **Path pattern**, **Leading bytes** or **Data source**), the script runs only for matching files.
- A before-save script can cancel the save by calling `script.args.event.cancel(reason)`.
- A script that exceeds its time limit or fails is stopped, and a notice appears at the top. If a before-save script fails, you can choose **Save** or **Don't save**.
- A script that fails 3 times in a row for the same trigger is disabled automatically.
- In safe mode, no automatic scripts run. See [Plugins](plugins.md#safe-mode).

## Adding the C# script component {#csharp-component}

C# scripts use a compiler called Roslyn. Roslyn is large, so it is not part of HexEditor. You add it as an extra component the first time you use C# scripts.

1. Select **Tools > Scripts > Add C# script component**. When you run a `.csx` script for the first time, a dialog also offers to add it.
2. A confirmation shows the size (about 20 MB), where it is saved and where it is downloaded from. Check it and add the component.
3. When it is done, `.csx` scripts can run.

The component version is paired with the app version. When you update the app, the component is updated too.

On a PC that is offline or cannot download, you can add it by hand:

1. On another PC, download the component zip from the releases page.
2. Extract the contents of the zip into `components\csharp\<version>\` in the HexEditor data folder.

If you have turned off network use (**Settings > Privacy**), nothing is downloaded. Use the manual way.

### Manage the component {#csharp-component-manage}

To check and manage the component you added, use **C# script component** under **Settings > Automation**.

- When it is installed, "Installed version" is shown. If the installed version is different from this version of HexEditor, you are told so. Add the component for this version.
- When it is not installed, it says "Not installed. C# scripts need this component."
- **Add...** adds the component. It does the same as **Tools > Scripts > Add C# script component**.
- **Remove** removes the component. C# scripts cannot run until you add it again.
- **Clear compile cache** deletes the compiled results of C# scripts. They are compiled again the next time they run.

## Debugging JavaScript {#debugging-javascript}

You can step through JavaScript scripts in the script editor.

1. In the script editor, click the margin to the left of a line, or press <kbd>F9</kbd>, to set a breakpoint.
2. Select **Debug** on the toolbar. Even without a breakpoint, pressing <kbd>F10</kbd> or <kbd>F11</kbd> starts the script stopped at the first line.
3. The script stops at the breakpoint.

While it is stopped, the **Debug** tab shows the **Call stack**, **Variables** and **Watch**. Variables are grouped as local, closure, global and so on, and objects can be expanded. Add expressions with **Add a watch expression** and their values are checked each time it stops. To try an expression at the place where the script is stopped, type it in the [console](#console).

| Action | Key |
| --- | --- |
| Continue | <kbd>F5</kbd> |
| Stop | <kbd>Shift</kbd>+<kbd>F5</kbd> |
| Set / clear a breakpoint | <kbd>F9</kbd> |
| Step over | <kbd>F10</kbd> |
| Step into | <kbd>F11</kbd> |
| Step out | <kbd>Shift</kbd>+<kbd>F11</kbd> |

These keys work while the script editor has the focus. <kbd>F11</kbd> steps into instead of switching to full screen. You can also use the toolbar or the command palette.

A breakpoint can have a condition (it stops only when the expression is true) and a hit count. A disabled breakpoint is an empty circle, and a conditional one is a circle with "=" inside.

Choose when to stop on exceptions with **Break on exceptions while debugging** in **Settings > Automation**. The time spent stopped does not count toward the run time limit. While stopped, the document the script is writing to still cannot be edited.

Breakpoints are remembered for each script.

## Debugging C# scripts {#debugging-csharp-scripts}

C# scripts cannot be stepped through in the app. In the script editor, <kbd>F9</kbd>, <kbd>F10</kbd>, <kbd>F11</kbd> and <kbd>Shift</kbd>+<kbd>F11</kbd> do nothing for a C# script. <kbd>F5</kbd> runs the script and <kbd>Shift</kbd>+<kbd>F5</kbd> stops it.

To debug a C# script, attach an external debugger to HexEditor:

1. Open the script in the script editor.
2. On the toolbar, select **Debug**. HexEditor compiles the script with debug information and shows the process ID.
3. In Visual Studio or Visual Studio Code (with the C# extension), attach to the process with that ID.
4. Set breakpoints in the `.csx` file. To stop at a fixed place, call `System.Diagnostics.Debugger.Break()` in the script.
5. When the debugger is attached, the script starts. To start without a debugger, select **Run without attaching**.

`Console.WriteLine` and `Ui.Output` write to the output panel. The stack trace of an exception shows the line numbers of the `.csx` file.
