# File systems

This page shows how to browse the files of an NTFS, FAT12, FAT16, FAT32, exFAT, ext2/3/4, HFS+, APFS, ISO 9660 or UDF volume without Windows mounting it, find deleted files, see which file uses a cluster, and export files, free space and slack.

HexEditor only reads the file system. It never creates, deletes, renames or undeletes anything on it.

## Open the file system browser {#browser}

1. Open a volume, a partition tab, or an image of a volume. On a disk, put the cursor in the partition first, or use **Show file system** in the **Partitions** panel.
2. Select **Forensics > File system > Show browser**.

HexEditor detects the type from the first sectors. If it cannot, it asks you to choose the type. An encrypted (BitLocker) volume is not opened.

The **File system** panel has:

- the type, volume name, serial number and cluster size, and any warnings (for example a backup boot sector that differs),
- a folder tree with the root and these folders: **$Metadata**, **Deleted files**, **Orphan files**, **Unallocated space** and **Volume slack**,
- the file list, with a filter box (part of the name, or wildcards `*` and `?`) and a **Find files** box that searches the whole volume,
- the details of the selected item, with its fragments.

Folders are read when you open them, and the list fills in while it is read, so even a folder with a million files does not freeze the window. Select **Columns** to show or hide columns. Times are shown in UTC. FAT times have no time zone, so they are shown with `~`.

Select the root (no file selected) to see the summary: type, label, serial number, cluster size, total and free clusters, and all the boot sector fields.

## Work with a file {#file-actions}

- Double-click a file or press Enter to jump to its first byte and select all its fragments.
- Right-click a file for more:
  - **Open file in new tab** opens the content of the file (fragments joined, sparse parts as zeros, NTFS compression expanded) in a read-only tab. The status bar of that tab shows the disk offset of the cursor.
  - **Export files** writes the selected files and folders to a folder you choose. See [Export files](#export).
  - **Go to metadata** jumps to the MFT record or directory entry.
  - **Show clusters of file** highlights all the fragments. See [Clusters](#clusters).
  - **Show MFT record**, **Show INDX** and **Show $LogFile** (NTFS) and **Show directory entry** and **Show cluster chain** (FAT and exFAT) show the structures with a template or in the **Forensic results** panel.
  - **Copy path** and **Export list as CSV / JSON**.

NTFS alternate data streams appear as `file.txt:stream`. Small streams such as `Zone.Identifier` are shown as text in the details. You can hide streams in the settings.

An item with damaged metadata (for example an MFT record whose fixup does not match, or a FAT chain that loops) is marked corrupt, and the reason is shown in the details. The rest of the volume is still read.

On NTFS, a file whose $STANDARD_INFORMATION times are earlier than its $FILE_NAME times is marked with **time mismatch**. This can mean that the times were changed.

On FAT32, if the two FATs differ, the number of different entries is shown and you can choose which FAT to use.

## Deleted files {#deleted}

Open the **Deleted files** folder, or select **Forensics > File system > Find deleted files**. HexEditor reads all MFT records, index slack, and directory entries, and adds the deleted files as it finds them.

The **Recovery** column shows the chance of getting the content back:

- **Good**: all clusters are still free.
- **Partially overwritten**: some clusters are used by other files now.
- **Overwritten**: all clusters are used by other files now.
- **Location unknown**: the position of the data is not known.
- **Resident**: the data is inside the MFT record.

On FAT and exFAT the cluster chain of a deleted file is gone, so HexEditor reads contiguous clusters from the first cluster. The details say so. Open or export deleted files like other files. Before exporting an overwritten file, HexEditor tells you that the content may belong to another file.

## Export files {#export}

1. Select one or more files or folders.
2. Right-click and select **Export files**.
3. Choose the options (keep the folder structure, alternate data streams, slack, deleted files) and the destination folder.

HexEditor writes `export-manifest.csv` with the original path, file ID, size, times (UTC), MD5, SHA-256, destination and recovery state of each file. The times of the exported files are not changed to the original ones. If the destination does not have enough free space, nothing is written. Parts that cannot be read are written as zeros and listed in the manifest.

## Clusters {#clusters}

- The status bar shows the LBA, the cluster number, the offset in the cluster, and the owner of the cluster. Select **Find owner** in the status bar to build the cluster map. HexEditor saves the map and uses it again the next time you open the same volume.
- **Forensics > File system > Go to cluster** jumps to a cluster. FAT clusters start at 2. In this box, `cluster` means the current cluster number, so `cluster+1` is the next cluster.
- **Files using this position** lists the files that use the clusters at the cursor or in the selection, including deleted files.
- **Show clusters of file** highlights all fragments of the selected file and lists them in the **Fragments** panel. Select a row to jump to it. **Go > Next fragment** and **Go > Previous fragment** move between them.

## Free space and slack {#slack}

Select **View > Forensic highlight** to mark metadata, unallocated clusters, RAM slack (from the end of a file to the end of its sector), drive slack (to the end of the cluster), volume slack and gaps between partitions. Each kind has its own pattern and a letter at the start of the row, so you can tell them apart without colors. A legend appears at the top right of the hex view. The first time, HexEditor finds the last cluster of every file before it shows slack.

- **Export unallocated space** joins all free clusters into one file, and **Export slack** joins the slack of all files. Both can write a map file (`.map.csv`) that gives the disk offset of each part.
- **Open unallocated space in new tab** shows the free clusters as one document. The status bar shows the disk offset of the cursor.
- **Select unallocated space** and **Select slack** select these areas, so you can search only in them with the **Selection** scope of the find bar.

## ext2, ext3 and ext4 {#ext}

The browser reads ext2, ext3 and ext4 volumes: the superblock, the group descriptors (also the 64-byte ones of the `64bit` feature and `flex_bg`), inodes, extent trees, direct and indirect blocks, inline data and htree directories. The summary lists the superblock fields, including the `compat`, `incompat` and `ro_compat` features. With `metadata_csum`, the superblock, group descriptor and inode checksums are checked; a mismatch is shown as a warning, or as a damaged mark on the file.

- Right-click a file and select **Show inode** to color the inode as a structure template and move to it. To go to an inode by number, select **Forensics > File system > Go to inode** and enter the number.
- **Deleted files** lists names left in the unused space of directory entries (`rec_len`) and inodes that have a deletion time. ext3 and ext4 usually erase the block list when a file is deleted; such files are marked **Location unknown**.
- The journal (`$Journal` in **$Metadata**) is only listed: right-click it and select **Show journal** to list its descriptor, commit and revoke blocks. HexEditor never replays the journal.
- Files encrypted with fscrypt are marked as encrypted; their names and contents are shown as stored.
- Free space comes from the block bitmaps. ext, HFS+ and APFS count in blocks, so **Go to cluster** is shown as **Go to block**.

## HFS+ and APFS {#apple}

**HFS+ and HFSX**: the summary shows the volume header and whether the alternate volume header at the end of the volume matches it. Files and folders come from the catalog B-tree; fragments after the eighth come from the extents overflow B-tree. A resource fork is listed as `name/..namedfork/rsrc` and opens like any other file. Hard links are resolved to their `iNode` files. Compressed files (decmpfs) are marked as compressed and shown without decompression. The journal is only shown, never replayed. Free space comes from the allocation file.

**APFS**: HexEditor uses the newest container checkpoint whose checksum is correct; if the newest one is damaged, a warning says that an older one is used. The volumes are the top folders of the tree. A volume shows its name, role and whether it is encrypted. Snapshots are listed in the volume's **$Snapshots** folder; their contents cannot be browsed. An encrypted volume (including FileVault) is only detected: it is marked as encrypted and its file list is not shown. Free space comes from the space manager; when it cannot be read, the free space highlight is not available.

Deleted files on HFS+ and APFS are not searched.

## ISO 9660 and UDF {#optical}

Disc images use 2,048-byte sectors (raw 2,352-byte sector images are not supported).

**ISO 9660**: the browser shows the Joliet names if the disc has them, otherwise the Rock Ridge names, otherwise the basic ISO 9660 names. Switch between them with **Names** above the file list. With Rock Ridge names, the **Attributes** column shows the POSIX mode. A bootable disc lists its El Torito boot catalog entries (platform, emulation, start sector, sector count) in the summary, and its boot images in **$Metadata** as `$BootImage1` and so on; export them like files. On a multisession disc the last session is shown; choose another one with **Session**.

**UDF**: versions 1.02 to 2.60 are read, including the metadata partition of UDF 2.50 and later. The summary shows the UDF version. Descriptors whose tag checksum or CRC does not match are reported as warnings and reading continues. Virtual partitions (VAT) and sparing tables are not supported. When a disc has both UDF and ISO 9660, UDF is shown; switch with **Format**. Free space comes from the unallocated space bitmap, if the disc has one. Deleted files are not searched on ISO 9660 and UDF.
