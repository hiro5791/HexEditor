# Comparing folders

Folder comparison matches the files of two folders by relative path and lists which ones are the same, different, or in only one folder. It only shows the result. It does not copy or synchronize anything.

## Compare two folders {#compare}

1. Select **Tools > File tools > Compare folders...**, or run **File tools: Compare folders** from the command palette.
2. Enter the **Left folder** and the **Right folder**, or select **Browse...**.
3. Set the options.
   - **Include subfolders** is on by default.
   - **File name pattern** limits the files, for example `*.bin`. Separate several patterns with `;`. Leave it empty for all files.
   - **Compare by SHA-256 and show the values** compares files with a hash instead of byte by byte, and shows the hash values.
4. Select **Compare**.

## Read the results {#results}

Files are matched by relative path, ignoring letter case, so `SAME.BIN` and `same.bin` are the same file. A file is **Different** if its size differs, or if its bytes differ. The bytes are compared one by one, and the comparison of a file stops at the first difference. **First difference** shows its offset.

The list shows the **State**, **Relative path**, **Left size**, **Right size** and **First difference**. The states are **Same**, **Different**, **Left only**, **Right only** and **Read error**. Each state has an icon and a word. By default the list hides files that are the same. Change the filter to show them.

Results appear as soon as each file is compared, and you can cancel the comparison from the processing center. Files and folders that cannot be read are listed as **Read error**, and the comparison goes on.

To see how two files differ, select a **Different** row and press <kbd>Enter</kbd>, or double-click it. The two files open in the file comparison. See [Comparing files](compare.md).

You can sort the list by clicking a column heading, and export it as CSV or JSON.
