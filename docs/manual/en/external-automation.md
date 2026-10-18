# External automation

Other programs on your PC, such as PowerShell, Python or a test tool, can operate the running HexEditor. They use the same functions as scripts, sent as JSON-RPC 2.0 messages over a named pipe.

## Turn it on {#turn-on}

External automation is off by default.

1. Select **Tools > Settings**, then **Automation**.
2. Turn on **Allow external automation (JSON-RPC over a named pipe)**.

While it is on, the status bar shows an icon with the name "External automation: on". Select the icon to open this setting. The section **External automation** shows the name of the pipe, where the PowerShell module is, and the programs you allowed.

Only programs that run under your account on this PC can connect. Connections from other users and from the network are refused.

## Allow a program {#allow-a-program}

The first time a program connects, HexEditor asks whether to allow it. The dialog shows the path of the program and, if it has one, its digital signature.

- **Allow this time**: the program can work until it disconnects. It is asked again next time.
- **Always allow**: the program is remembered. It is asked again if the program file changes.
- **Don't allow**: the connection is closed.

An allowed program can read and edit the open documents, open and save files, and use the other automation functions. Opening disks or process memory still asks each time. If HexEditor runs as administrator, it asks for every connection.

To remove a program you allowed, select **Revoke** next to it in **Settings > Automation > External automation**.

## Use the PowerShell module {#powershell}

HexEditor includes the PowerShell module **HexEditor.Automation**. It works in Windows PowerShell 5.1 and PowerShell 7. The folder is shown in **Settings > Automation > External automation**.

```powershell
Import-Module 'C:\Program Files\HexEditor\Automation\HexEditor.Automation'
Get-HexEditorDocument
$doc = Open-HexEditorFile -Path C:\data\sample.bin
Read-HexEditorBytes -Document $doc.id -Offset 0x10 -Length 16
Invoke-HexEditorMethod -Method 'document.goTo' -Params @($doc.id, 'end-0x10')
```

Use the path of your installation. The commands connect automatically. Use **Invoke-HexEditorMethod** for any method.

## Write your own client {#protocol}

- The pipe is `\\.\pipe\HexEditor.<your user SID>.rpc`.
- Send one JSON-RPC 2.0 message per line, in UTF-8, and read one message per line. A message can be up to 64 MB.
- Method names are the names of the scripting API: `object.member`, for example `app.open`, `app.documents` or `document.readBytes`. Properties are read with the same name, and set by giving `value`.
- Methods of a document take the document `id` as the parameter `document`, or as the first parameter in an array. Other objects, such as a template node, are given as a `handle`.
- Bytes are Base64 text. Where a method takes a byte pattern, you can also give `{"hex": "DE AD"}`.
- Offsets and ranges accept the same expressions as in scripts, for example `"end-0x10"` or `"0x100:0x20"`.
- Subscribe to events with `app.on`. Events arrive as `event` notifications.
- Long operations report progress as `$/progress` notifications. Send the notification `$/cancel` with the `id` of a request to cancel it.
- Changes made by a program can be undone like changes made by scripts. While a program is editing a document, the document shows "Operated by an external program" and a **Disconnect** button.

Example (Python):

```python
import json
pipe = open(r"\\.\pipe\HexEditor.S-1-5-21-...-1001.rpc", "r+b", buffering=0)
pipe.write((json.dumps({"jsonrpc": "2.0", "id": 1, "method": "app.documents"}) + "\n").encode())
print(pipe.read(65536))
```

The full list of methods, with their parameters and the error codes, is the file `Automation\jsonrpc-methods.json` in the HexEditor folder. Errors use these codes:

| Code | Meaning |
| --- | --- |
| -32001 | The permission is not granted |
| -32002 | An argument is not valid |
| -32003 | Out of range |
| -32004 | The document is read-only |
| -32005 | Reading or writing failed |
| -32006 | Canceled |
| -32007 | A limit was exceeded (for example, a message larger than 64 MB) |
| -32010 | The user did not allow the connection |
| -32601 | The method does not exist, or cannot be used from another program |
| -32602 | The parameters are not valid |
