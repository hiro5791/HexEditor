# Remote data sources

This page explains how to edit memory through a GDB server, open files over SFTP, and look at UDP datagrams as they arrive. HexEditor connects only when you ask it to, and never when it starts or restores a session.

## GDB server {#gdb-server}

You can read and write the memory of a target through a GDB server, such as `gdbserver`, QEMU, OpenOCD or a debug probe.

1. Select **File > Connect to Remote > GDB Server...**, or run **File: Connect to GDB server...** from the command palette.
2. Type the **Host** (default `localhost`) and **Port** (default `1234`), and choose the **Address width** (32 bit or 64 bit).
3. **Open read-only** is on by default. Turn it off if you want to write to the target.
4. Select **Connect**.

The offset is the target address, and the document covers the whole address space. Only the part you look at is read. Ranges the target refuses are shown as not readable. HexEditor only reads and writes memory; it does not stop or resume the target or set breakpoints.

- Saving (<kbd>Ctrl</kbd>+<kbd>S</kbd>) writes the changed bytes to the target. HexEditor asks before the first write. Running **File: Toggle immediate write** from the command palette writes every edit right away, as for process memory.
- The status bar shows the server and whether it is connected. If a request gets no answer within the time limit (5 seconds by default; **GDB server request timeout** in **Settings > Files**), or the server stops, the document becomes disconnected. Your unsaved changes stay. Select **Reconnect** in the bar, select the status bar item, or run **File: Reconnect**.
- **Reload** (<kbd>Ctrl</kbd>+<kbd>R</kbd>) reads the memory again.

## SFTP {#sftp}

You can open a file on another computer over SSH (SFTP), edit it and save it. Only the parts you look at are downloaded.

1. Select **File > Connect to Remote > SFTP...**, or run **File: Open with SFTP...** from the command palette.
2. Type the **Host**, **Port** (default `22`) and **User name**.
3. Choose the **Authentication**: **Password**, **Key file** (with its passphrase, if it has one), or **SSH agent** (the Windows OpenSSH agent or Pageant).
4. Select **Connect**.
5. Choose a file in the list and select **Open**. Double-click a folder to open it, or select **Up**.

The first time you connect to a server, HexEditor shows its host key type and SHA-256 fingerprint. Check that they match the server, then select **Trust and connect**. If the key of a server is different from the one you trusted, HexEditor warns you and does not connect unless you select **Replace the key and connect**.

- A password or passphrase is saved only if you turn on **Save the password in Windows Credential Manager**. It is never written to the settings files.
- The file is added to the recent files as `sftp://user@host:port/path`, without the password. Opening it from there shows the connection dialog again.
- The tab tooltip shows the remote path, and the status bar shows the connection.
- When you save without changing the length, only the changed ranges are written. HexEditor reminds you that if the connection drops during the save, only part of the remote file may be rewritten (turn this off with **Confirm before writing only the changed ranges over SFTP** in **Settings > Files**). Other changes are written to a temporary file in the same folder, which then replaces the file.
- HexEditor checks every 30 seconds, and when you come back to the window, whether someone else changed the remote file. If so, a bar offers **Reload**.
- If the connection drops, HexEditor tries once to reconnect. If that fails, select **Reconnect** in the bar. Your unsaved changes stay.
- Tabs restored from a session are not connected. Select **Connect** in the tab.

## Receive UDP {#udp}

You can listen on a UDP port and see the datagrams you receive, one after another. This helps when you debug a device or a network program.

1. Select **File > Connect to Remote > Receive UDP...**, or run **File: Receive UDP...** from the command palette.
2. Choose where to **Listen on** (default `127.0.0.1`; `0.0.0.0` or `::` for all addresses). For other addresses, Windows Firewall may ask for permission.
3. Type the **Port** (1 to 65535).
4. Set the **Maximum to keep** (default 64 MiB).
5. Select **Start**.

Received datagrams are added to the end of a read-only document, in the order they arrive. HexEditor only receives; it never sends anything.

- The **Packets** panel lists each datagram with its position, length, time and sender. Select a row to jump to it. Use **Pause**, **Resume** and **Clear** there. **Export CSV...** and **Export JSON...** save the list.
- If the cursor is at the end, the view follows new data (turn this off with **Follow the end while receiving UDP** in **Settings > Files**).
- The status bar shows how many datagrams and how much data were received.
- When the maximum is reached, recording stops and a bar tells you. Select **Discard old data and continue** to drop the oldest data instead. The addresses still count from the start of receiving.
- **File > Save As** saves the received data as one file. The tab keeps receiving.
- Receiving stops when you close the tab. It does not start again when you restart the app.
