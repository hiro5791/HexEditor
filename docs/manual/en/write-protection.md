# Write protection

Stop HexEditor from changing the evidence you examine. This page explains write-protect mode and the Windows setting that blocks writing to USB storage.

## Write-protect mode {#mode}

Write-protect mode stops this app from writing to anything it has open. Turn it on before you open evidence.

1. Select **Forensics > Write protection mode**, or run **Forensics: Toggle write protection mode** from the command palette.
2. A lock and the words **Write protection** appear at the left end of the status bar. The window title shows **[Write-protected]**, and each tab shows a lock.

While the mode is on:

- Every document is read-only, and you cannot turn read-only off. Typing, deleting, pasting, filling and data operations are not available.
- Disks, volumes and process memory that were open for writing are reopened for reading only. New disks and files are opened for reading only.
- **Save** is not available. If you had unsaved changes, they are kept, and you can use **Save as** to save them to another place that is not evidence.
- Secure delete and creating or deleting alternate data streams are not available, and their tooltip says that write-protect mode is on. Wiping, cloning, restoring sectors and writing partition tables are not available either.
- The elevated helper that reads disks also refuses every write, lock and dismount, even if a request reached it by mistake.

HexEditor still writes its own data: settings, recovery data, temporary files, the audit log, and the images and reports you create. Those outputs cannot be placed on the evidence.

Write-protect mode stops only this app. Windows and other apps can still write, for example when Windows mounts a volume. The first time you turn the mode on, a notice reminds you of this. Use a hardware write blocker, or [OS USB write protection](#usb) as well.

The mode stays on after you restart HexEditor. To turn it on every time HexEditor starts, turn on **Always turn on write protection mode at startup** in **Settings > Forensics**. You can also start HexEditor with `--write-block`; the mode is on before the first document opens.

## Turn the mode off {#turn-off}

1. Select **Forensics > Write protection mode** again, or select the item in the status bar.
2. The dialog shows how many evidence documents are open and their names.
3. Select **Turn off write protection**, or **Cancel** to keep the mode on.

Turning the mode on and off is recorded in the audit log with the time and your Windows user name. When no case is open, it is recorded in `forensics-audit.log` in the settings folder.

## Allow writing to one document {#exception}

You can keep the mode on and still edit one file, such as your notes.

1. Right-click the tab of the file and select **Allow writing to this document**.
2. Type the name of the file shown in the dialog. The button stays unavailable until the name matches.
3. Select **Allow writing to** and the file name.

The tab shows an open lock, and you can edit and save that file until you close it. The exception and its end are recorded in the audit log. You cannot allow writing to a source that is registered as evidence, or to a disk, volume or process.

## OS USB write protection {#usb}

Windows can refuse to write to USB storage devices. This also stops other apps and Windows itself, which write-protect mode cannot do.

1. Select **Forensics > OS USB write protection**, or run **Forensics: Toggle OS USB write protection**.
2. Read the dialog and select **Change the setting**. Administrator rights are required: Windows asks for permission, unless HexEditor is already running as administrator.
3. Connect the USB storage device after you change the setting. Devices that are already connected are not affected until you reconnect them.

The setting protects only USB storage. Internal SATA and NVMe disks are not protected.

The menu item is ticked while the setting is on. To turn it off, select the item again. The setting returns to the state it had before you turned it on. If the value did not exist before, it is removed.

If you exit HexEditor while the setting is on, HexEditor asks you to choose **Turn off and exit** or **Exit and keep it on**. Each change is recorded in the audit log.

If the setting cannot be changed, for example because it is managed by Group Policy, the reason is shown and nothing is changed.
