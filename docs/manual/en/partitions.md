# Partitions

This page shows how to see the partitions of a disk or disk image, open a partition in its own tab, and find partitions that were deleted.

## See the partitions {#show-partitions}

When you open a disk or a disk image, HexEditor reads the partition tables and shows the **Partitions** panel.

1. Open a disk, or select **File > Open as disk image** and choose an image file.
2. The **Partitions** panel shows a bar with the layout of the disk, a list of the partitions, and details of the selected row.

The list shows the number, format (MBR, logical, GPT, APM, BSD), type, name, start and end LBA, sector count, size, start offset, flags, the file system found at the start, and the state. Gaps of 1 MiB or more appear as **Unallocated** rows.

HexEditor reads MBR tables with extended partitions, GPT (primary and backup headers), Apple partition maps and BSD disklabels. It only reads a few sectors, so this is fast for disks of any size.

If you don't see the panel, select **Forensics > Partitions > Show partitions**.

## Warnings {#warnings}

A row with a problem has the state **Warning**. Select the row to read the reason under the list. HexEditor warns about:

- a GPT header or partition entry array whose CRC32 does not match,
- a primary GPT header that differs from the backup header,
- partitions that overlap or point outside the disk,
- an EBR chain that loops back (HexEditor stops following it),
- a hybrid MBR.

If no GPT is found with the sector size you chose but one is found with another size (512 or 4096), a message offers to open the image again with that sector size.

## Work with a partition {#partition-actions}

Right-click a row (or a part of the layout bar) and choose an action:

- **Go to start of partition** jumps to the first byte. You can also double-click the row or press Enter.
- **Select partition** selects the whole partition.
- **Open partition in new tab** opens the partition in its own tab. The first byte of the partition is offset 0 in that tab. The tab shares the data of the disk, so it keeps the sector size and the read-only state.
- **Show entry structure** shows the table entry with the MBR or GPT template.
- **Show file system** opens the file system browser for the partition. See [File systems](file-systems.md).

To open a partition that is not in any table, put the cursor where it starts, right-click the hex view and select **Open here as start of partition**.

The status bar shows the LBA of the cursor and the number of the partition it is in.

## Find lost partitions {#lost-partitions}

When a partition table was deleted or damaged, HexEditor can search the disk for file systems and table traces.

1. Select **Forensics > Partitions > Scan for lost partitions**.
2. Choose the range and where to look: 1 MiB boundaries, cylinder boundaries (63 sectors), or every sector. Every sector reads the whole range, so it takes as long as reading the disk.
3. Select **Scan**. The progress, speed and remaining time appear in the processing center. Candidates appear in the **Forensic results** panel as they are found.

Each candidate shows the start LBA, estimated size, type and confidence. **High** means that both the main and the backup boot sector match, **Medium** means only the main structure matches, and **Low** means only a signature was found.

Right-click a candidate to:

- **Open as partition** in its own tab,
- **Show file system** of the candidate,
- **Create partition table entries (MBR)** or **(GPT)**. This changes the document only. The disk or image is not changed until you save. In write-protect mode these items are not available.

You can cancel the scan at any time. The results found so far stay in the list. To save the list, right-click it and select **Export list as CSV / JSON**.
