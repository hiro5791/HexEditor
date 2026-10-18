# Split, join and interleave files

Cut a large file into pieces, put pieces back together, or spread the bytes of one file over several files and back. These tools are in **Tools > File tools**. They read and write in a stream, so they work with files of any size and use a fixed amount of memory.

## Split a file {#split}

1. Select **Tools > File tools > Split file...**, or run **File tools: Split** from the command palette.
2. Choose the input: the active document, including its unsaved changes, or a file.
3. Choose how to split:
   - **Size of each file**: for example `4G`, `1.44M` or `0x10000`. The last file holds what remains.
   - **Number of files**: 2 to 10,000. The files are made equal in size, rounded up. **Make sizes a multiple of this many bytes** rounds the size up to a multiple of a number you give, for example 512.
   - **Offsets**: type offsets, one per line, or tick **Also use the bookmark positions of the document**.
4. Choose the **Output folder** (empty means the folder of the input) and the **File name format**. The default `{name}.{index:000}` gives `big.bin.001`, `big.bin.002` and so on. You can also use `{base}` (name without extension), `{ext}` (extension with the dot), `{index}`, `{index0}` and `{offset}` (start offset in hex).
5. Optionally create a command file that joins the pieces with `copy /b`, and a checksum file with the hash of the original and of every piece.
6. Check the summary under the options: the number of files, the first and last file name and the total size. Then select **Split**.

If files with the same names exist in the output folder, you are asked before they are replaced. HexEditor checks the free space of the output volume first and does not start if it is not enough.

The work appears in the processing center and you can cancel it. If you cancel or an error occurs, the unfinished file is deleted. You are asked whether to keep the files that were finished.

## Join files {#join}

1. Select **Tools > File tools > Join files...**.
2. Add the files with **Add...** or by dropping them on the list. If you add a numbered file such as `p.bin.001`, HexEditor asks whether to add the rest of the numbered files too (`.002`, `.003` and so on).
3. Put the files in order with **Move up**, **Move down** or by dragging. **Sort by name** and **Natural sort** (numbers compared as numbers, so `file10` comes after `file9`) sort the list.
4. Choose whether to leave nothing between the files or to **start every file at a multiple of N bytes**, filled with a value (`00` by default).
5. Choose **Open the result as a new document** (the default) or **Save the result to a file**, then select **Join**.

A new document is made without copying any data: it points to the original files, so even many large files open at once. The data is written only when you save. If you save to a file, the result is written in a stream and the job appears in the processing center. Files that cannot be read are listed and nothing is joined.

## Deinterleave and interleave {#interleave}

Interleaving takes one unit of bytes from each input in turn. For example, two 8-bit ROM chips that together make a 16-bit bus hold the even and odd bytes of one image.

**Deinterleave** (**Tools > File tools > Deinterleave...**) splits one file into N files. Choose the number of outputs (2 to 16) and the unit (1, 2, 4 or 8 bytes, or any number). With 2 outputs and a unit of 1, `00 11 22 33` becomes `00 22` and `11 33`. The default output names are `name.1.ext`, `name.2.ext`; use `{index0}` for numbers that start at 0. If the input is not a multiple of "number × unit", the end is filled with a value (`FF` by default) or the leftover bytes stay in the last output.

**Interleave** (**Tools > File tools > Interleave...**) does the reverse: add 2 to 16 files in order, choose the unit, and choose what to do if the lengths differ: fill the shorter ones with a value (`FF` by default), or fail. The result opens as a new document or is saved to a file. Deinterleaving a file and then interleaving the outputs with the same unit and order gives back the original.
