# Comparing files

Place two documents side by side and find where they differ. Use it to compare a file before and after an update, or to see how your edits differ from the saved file.

## Starting a comparison {#start-a-comparison}

1. Select **Analysis > Compare > Compare files**. You can also run **Compare: Compare files** from the command palette.
2. The **Compare files** dialog opens. Choose the **Target** for **Left** and **Right**.
3. Choose the comparison method. See [Simple and insertion-aware comparison](#modes).
4. Select **Compare**.

For each side you can choose:

- An open document (pick it from the list)
- A file on disk (select **Choose a file...**, or type the **File path**)
- The saved content on disk of an open document (see below)
- A snapshot of process memory (**Snapshot...**; see [Comparing process memory snapshots](#process-snapshots))

You can set a **Start offset** and a **Length** for each side. At first the start is 0 and the length runs to the end. For example, you can compare from `0x200` on the left against `0` on the right. Expressions are accepted. **Swap left and right** swaps all the settings of both sides.

If something is wrong, the reason appears under the box and **Compare** is disabled. For example: the file does not exist, access is denied, or the start offset is beyond the data. If both sides are the same range of the same document, a warning appears, but you can still run it.

The comparison opens in a new tab named "left name ↔ right name". If a tab for the same pair already exists, HexEditor switches to it and compares again. You can keep editing both documents during a comparison. The comparison uses the content as it was when you started.

There are other ways to start.

- **Tab right-click menu**: on the first tab select **Select as left side for comparison**, then on the second tab select **Compare with selected left side** (or "Compare with" followed by the name of the left tab).
- **Analysis > Compare > Compare with saved content**: compares what you are editing with the same file as saved on disk. The saved content is on the left and your edits are on the right. If you have no unsaved edits, HexEditor says "Same as the saved content." and stops. It does not work for a new document that has never been saved.
- Drop two files from Explorer onto the window while holding <kbd>Shift</kbd> to open the compare dialog with those two files.

The comparison tab has buttons at the top for compare again, method, previous difference, next difference, synchronized scrolling, layout and copying differences. In the middle are the left and right hex views, and below is the list of differences. To switch between side by side and stacked, use **View > Stack comparison views**.

At the right edge of each view is a "difference map" that shows where the differences are. Click it to go there.

The status bar shows the current difference, such as "Difference 12 / 340", with the number of different bytes and the match rate.

If you edit in a comparison tab, a bar says "The content has changed. The comparison results are out of date." Select **Compare again** to compare once more. The same bar appears when a file is changed by another app.

## Simple and insertion-aware comparison {#modes}

There are two methods.

| Method | When to use it |
| --- | --- |
| **Byte-by-byte comparison** (default) | Compare versions whose length did not change. Bytes at the same position are compared |
| **Comparison that detects insertions and deletions** | Compare files where bytes were added or removed in the middle. The matching parts after that are lined up correctly |

Differences are color coded by kind. Each kind also has a mark other than color.

| Kind | Meaning |
| --- | --- |
| **Changed** | The value differs at the same position |
| **Inserted (right only)** | Bytes that exist only on the right |
| **Deleted (left only)** | Bytes that exist only on the left |
| **Unreadable** | A range that could not be read (such as a bad sector) |

When the lengths differ, the byte-by-byte method turns everything after the end of the shorter side into a "left only" or "right only" difference.

The insertion-aware method gives an approximate result, not always the shortest list of edits. The summary says "Detects insertions and deletions (approximate)".

Change the options with **Options** in the dialog or in the comparison tab, then select **Compare again**. Your last settings become the starting values next time.

| Option | Meaning |
| --- | --- |
| **Comparison unit** | 1, 2, 4 or 8 bytes. If any byte in a unit differs, the whole unit is a difference |
| **Merge differences separated by at most (bytes)** | If the matching bytes between two differences are no more than this, they become one difference. 0 to 65,536 |
| **Resynchronization window (bytes)** | For the insertion-aware method, how far to look for a match. 256 bytes to 16 MB. 64 KB at first |
| **Minimum match length (bytes)** | For the insertion-aware method, the shortest run that counts as a match. 1 to 4,096. 8 at first |

Large files are fine. The whole file is never loaded into memory, so memory use does not grow with file size. The comparison runs in the background with a progress display. Differences are shown as soon as they are found. If you cancel, the results so far are kept.

## Moving between differences {#differences}

Move between differences in any of these ways.

- <kbd>Alt</kbd>+<kbd>F5</kbd>: next difference.
- <kbd>Shift</kbd>+<kbd>Alt</kbd>+<kbd>F5</kbd>: previous difference.
- **Go > Next difference** and **Go > Previous difference**.
- **Next difference** and **Previous difference** buttons in the toolbar of the comparison tab.
- Select a row in the list of differences.

When you move, the range of the difference is selected on both sides. From the last difference, going forward returns to the first, and a message says "Returned to the first difference." If there are no differences, a message says "There are no differences." You can move between the differences found so far while a comparison is still running. The move is saved in the jump history, so <kbd>Alt</kbd>+<kbd>←</kbd> takes you back.

In an ordinary tab, if a comparison that includes that document is open, the same keys move between its differences.

The **Differences** panel at the bottom lists the differences. Open or close it with **View > Panels > Differences**. The list shows the number, kind, left and right offsets and lengths, and the first bytes of each side.

- Filter by kind. Use **Min. length** to hide short differences.
- Use **Sort** for number order, longest first or shortest first.
- Use **Export** to write CSV, JSON or a text report. You can also export only the selected rows to CSV. **Compare: Save comparison report** in the command palette saves a report with the summary and all differences.
- Turn selected differences into a **multiple selection** or **bookmarks**. You choose which document, left or right, gets them.

Beside the list is the **Distribution of differences** graph. It divides the whole file into intervals and shows the share of different bytes in each as a bar. Click a bar to go to the first difference in that interval. **Show as table** switches to a table of numbers. The summary shows the method, the compared bytes, the number of differences, the number of different bytes, the match rate and the time taken.

### Copying differences to make both sides match {#merge}

You can copy the contents of a difference from one side to the other so that both match.

- **Copy difference to the right** and **Copy difference to the left**: copy the current difference, or the differences selected in the list.
- **Copy all differences to the right** and **Copy all differences to the left**: copy everything.

Copying to the right replaces the right side with the left side. "Changed" replaces the right part with the left content, "Deleted (left only)" inserts the left content into the right, and "Inserted (right only)" deletes that part from the right. Copying to the left works the other way around.

Keep these points in mind.

- One operation is one undo step in the document you write to. Even when you copy many differences, one <kbd>Ctrl</kbd>+<kbd>Z</kbd> undoes them.
- You cannot copy into a read-only document.
- Differences that change the length are not copied into targets whose length cannot change, such as a disk or process memory. You are told how many were skipped.
- Copying does not save. The target document is only marked as modified. Save it yourself.

## Synchronized scrolling {#synchronized-scrolling}

In a comparison tab, synchronized scrolling is on at first. When you scroll one side, the other side scrolls to the matching position. When you move the cursor, the other cursor moves to the matching position too.

- In a byte-by-byte comparison, rows at the same position line up at the same height.
- In an insertion-aware comparison, matching positions line up at the same height. Where bytes were inserted or deleted, a hatched band shows the gap.

Turn it on or off with **Synchronized scrolling** in the toolbar. When it is off, the two sides scroll on their own. When you turn it back on, it follows the side that has the focus.

## Comparing process memory snapshots {#process-snapshots}

You can save the memory of a process at one moment and compare it with another moment or with the current memory. It helps you find out which parts of memory change as a program runs.

### Create a snapshot

1. Open the memory of the process in a tab.
2. Select **Analysis > Compare > Create Process Snapshot...**.
3. Enter a **Name** and select **Create**.

This command works in a tab that has process memory open. Besides the name, you can choose the regions to include (**All readable regions**, the **Region or module selected in the memory map**, or **Writable regions only**) and **Pause the process while capturing**, as when you save a snapshot (see [Save a process snapshot or minidump](opening-files.md#process-snapshot)).

The snapshot is saved as a `.hexsnap` file in the temporary folder and is deleted when you quit the app. To keep it, use **Save As** and save it somewhere else. Some processes need administrator rights to be read. In that case you are asked whether to create the snapshot again with administrator rights. Pages that could not be read are not in the snapshot.

### Compare

1. Each snapshot opens in a tab. Open the ones you want to compare. You can also open a saved `.hexsnap` file with **File > Open**.
2. Select **Analysis > Compare > Compare Snapshots...**. You can also run **Compare: Compare snapshots** from the command palette.
3. The compare dialog opens. On the left it picks the oldest snapshot, and on the right the newest snapshot (or the process memory tab if there is only one snapshot). Change the **Target** or use **Snapshot...** if needed, then select **Compare**.

You can compare two snapshots, or a snapshot with the current memory.

Regions that start at the same address are compared with each other. A region that exists on only one side is listed as **Region added** or **Region removed**. For a region whose size changed, the shorter length is compared and the rest is "left only" or "right only".

The list of differences shows virtual addresses and region names (such as module name + offset) instead of offsets. Pages that could not be read are shown as "Unreadable" and are not counted as differences.

To open process memory, see [Opening files](opening-files.md).
