# Scripts

> TODO: Describe how to automate HexEditor with JavaScript and C# scripts.

## JavaScript and C# {#languages}

> TODO

## Running a script {#run-a-script}

> TODO

## The script editor {#script-editor}

> TODO

## The script API {#script-api}

> TODO

## Trust and permissions {#permissions}

> TODO

## The script console {#console}

> TODO

## Scripts that run on events {#event-scripts}

> TODO

## Adding the C# script component {#csharp-component}

> TODO

## Debugging JavaScript {#debugging-javascript}

> TODO

## Debugging C# scripts {#debugging-csharp-scripts}

C# scripts cannot be stepped through in the app. In the script editor, <kbd>F9</kbd>, <kbd>F10</kbd>, <kbd>F11</kbd> and <kbd>Shift</kbd>+<kbd>F11</kbd> do nothing for a C# script. <kbd>F5</kbd> runs the script and <kbd>Shift</kbd>+<kbd>F5</kbd> stops it.

To debug a C# script, attach an external debugger to HexEditor:

1. Open the script in the script editor.
2. On the toolbar, select **Debug run**. HexEditor compiles the script with debug information and shows the process ID.
3. In Visual Studio or Visual Studio Code (with the C# extension), attach to the process with that ID.
4. Set breakpoints in the `.csx` file. To stop at a fixed place, call `System.Diagnostics.Debugger.Break()` in the script.
5. When the debugger is attached, the script starts. To start without a debugger, select **Run without attaching**.

`Console.WriteLine` and `Ui.Output` write to the output panel. The stack trace of an exception shows the line numbers of the `.csx` file.
