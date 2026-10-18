# Checksum files

A checksum file lists files with their hash values, such as `SHA256SUMS`. You can create one for a folder, and later check that the files have not changed. The files work with other tools, for example `sha256sum -c` on Linux and `shasum -c` on macOS.

## Create a checksum file {#create}

1. Select **Tools > File tools > Create checksum file...**, or run **File tools: Create checksum file** from the command palette.
2. Add the files and folders to hash, one per line. **Add files...** and **Add folder...** open a picker. **Include subfolders** applies to folders.
3. Choose the **Algorithm**. The default is SHA-256.
4. Choose the **Format**.

   | Format | What is written | Default file name |
   | --- | --- | --- |
   | GNU | One line per file: `hash  path` | `SHA256SUMS` |
   | BSD | One line per file: `SHA256 (path) = hash` | `CHECKSUM.SHA256` |
   | One file per target | `file.sha256` next to each file, with one GNU line | `file.sha256` |
   | SFV | One line per file: `path CRC` (CRC-32 only) | `folder name.sfv` |

5. Optionally mark GNU lines as binary (`*`), choose the path separator (`/` by default, so other systems can check the file) and the line ending (LF by default). You can also change the output file name.
6. Select **Create**.

The paths are written relative to the checksum file. The file is saved as UTF-8 without a byte order mark. If a file of the same name exists, you are asked before it is replaced.

The work runs in the background and appears in the processing center with progress for each file. You can cancel it. Files that cannot be read are skipped, and a list with the reasons is shown at the end. They are not written to the checksum file.

## Verify a checksum file {#verify}

1. Select **Tools > File tools > Verify checksum file...**, or run **File tools: Verify checksum file**, and choose the checksum file. If you choose an ordinary file that has a checksum file next to it, such as `file.bin.sha256`, HexEditor offers to verify it with that file.
2. The results appear in a list as each file is checked. Each row shows the **State**, **Path**, **Expected** value, **Computed** value and **Size**. Above the list is a summary such as "Match 120, Mismatch 1, Missing 2".

The state is shown with an icon and a word as well as a color: **Match**, **Mismatch**, **Missing** and **Read error**. The comparison ignores letter case in the hash value. To open a file from the list, press <kbd>Enter</kbd> on the row, or right-click it and select **Open**.

HexEditor detects the format and algorithm from the file extension, the file name, the length of the hash values and the shape of the lines. GNU files from `sha256sum`, including lines with `*`, BSD files, `.md5`, `.sha1`, `.sha256`, `.sha512` and `.sfv` files all work. If the algorithm cannot be detected, you choose it. Lines in the wrong format are skipped, and a message gives their line numbers.

When you open a checksum file in HexEditor, a bar says "This is a checksum file." with a **Verify** button.

You can sort the list by clicking a column heading, filter it by state, and export it as CSV or JSON.
