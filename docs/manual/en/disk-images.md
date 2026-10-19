# Disk images and clones

This page explains how to acquire disk images, open them as disks, verify them, clone a disk, save and restore sector ranges, and rebuild a RAID.

## Create a disk image {#create-image}

You can copy a disk, a volume, a partition, or any open document into an image file. The source is only read.

1. Open the source. For a disk, select **File > Open disk**.
2. Select **Forensics > Image > Create image**.
3. Choose the format:
   - **Raw (dd)**: one file such as `disk.dd`.
   - **Split raw**: files `name.001`, `name.002`, and so on. Type the size of each file, for example `2G` or `100M`.
   - **E01**: the EnCase 6 compatible Expert Witness format. Type the segment size and the case information, and choose the compression.
4. Choose the hashes (MD5, SHA-1, SHA-256). They are computed from the data while it is read.
5. Select **Start**.

The progress, speed, time left, and number of unreadable sectors appear in the processing center. When the image is done, a message shows the hashes and an **Open acquisition log** button.

- Unreadable sectors are read again (3 times by default), then one sector at a time. Sectors that still cannot be read are filled with the fill value (`00` by default) and listed in the acquisition log and in the E01 file.
- **Verify after completion** (on by default) reads the image again and compares the hashes.
- If write-protect mode is off, the dialog suggests turning it on first.
- The output must not be on the disk that you acquire. The app checks the free space and the file size limit of the output (for example 4 GiB on FAT32) before it starts.

If you cancel, you can keep or delete the files written so far. The acquisition log then says **Interrupted**.

### The acquisition log {#acquisition-log}

Next to the image, the app writes `name.txt`. It contains the source (model, serial number, capacity, sector size, path), the output format and files, the start and end time in UTC and local time, the number of sectors read, the unreadable sectors, the hashes, the verification result, the app version, and the case information.

## Sparse and skeleton images {#sparse-skeleton}

To make a smaller image, choose **Ranges to acquire** in the dialog, or select **Forensics > Image > Create skeleton image**.

- **Sparse** leaves out the unallocated areas of NTFS, FAT, and exFAT partitions. Everything else is included.
- **Skeleton** includes only the partition tables and the file system metadata. You can also include the bookmarks and multi-selection ranges.

Raw images are written as NTFS sparse files, so the left-out areas take no space. The list of included ranges is written to `name.ranges.csv`. The log shows two sets of hashes: the included ranges only, and the whole image with the left-out areas as zeros. Neither matches the hash of the whole source disk.

When you open the image, the ranges that were not acquired are shown with a hatched pattern.

## Open an image as a disk {#open-image}

Select **Forensics > Image > Open image**, or open the file with **File > Open**. Files ending in `.E01`, `.001`, `.vhd`, `.vhdx`, `.avhdx`, and `.vmdk` open as disk images automatically.

- Split raw: open `name.001`. The following files are joined automatically.
- E01: open `name.E01`. The other segments are found automatically.
- VHD, VHDX, and VMDK: fixed, dynamic, and differencing disks. The parent disk of a differencing disk is found from the path stored in the file.

Images open read-only. If a segment is missing, the app lists the missing files and lets you **Open available segments**. The missing ranges then read as errors.

To see the case information, the acquisition time and tool, the chunks, the compression, and the stored hashes, select **Forensics > Image > Image information**. Sectors that could not be read during the acquisition are shown with a hatched pattern.

If an E01 chunk is damaged, its range is shown as a read error and a message tells you where it is.

## Verify an image {#verify-image}

1. Open the image, or select **Forensics > Image > Verify image** and choose the file.
2. Choose the hashes.
3. To compare with a value that you have, type it in **Value to compare**.
4. Select **Verify**.

The app compares the computed hashes with every value it finds: the hashes stored in an E01 file, the acquisition log next to the image, the evidence list, and the value that you typed. Each hash shows **Match**, **Mismatch**, or **No reference value**. If some data cannot be read, the result is **Cannot verify because of read errors**.

## Clone a disk {#clone-disk}

Cloning writes the source sector by sector onto another physical disk. Everything on the target disk is lost.

1. Open the source (a disk, an image, or a partition).
2. Select **Forensics > Image > Clone disk**.
3. Choose the target disk and the start LBA on it, then select **Next**.
4. Read the confirmation. It lists the volumes on the target disk.
5. Type the name of the target disk (for example `Disk 2`) and select **Clone to Disk 2**.

All volumes on the target disk are locked while the clone runs. If a volume cannot be locked, nothing is written. After the clone, the written range is read back and compared with the source. Cloning is not available in write-protect mode or onto a disk in the evidence list. If you stop a clone, the target disk is left partly written.

## Save and restore sector ranges {#sector-ranges}

Before you repair a partition table, you can save sectors such as the MBR or GPT.

1. Open the disk.
2. Select **Forensics > Image > Save sector range**.
3. Type the start LBA and the number of sectors. You can use expressions such as `sel.start / sector`.
4. Choose the output and select **Save**.

The app writes the data (`name.bin`) and a sidecar file (`name.bin.json`) with the disk model, serial number, capacity, sector size, start LBA, number of sectors, time saved (UTC), and SHA-256.

To write the sectors back, select **Forensics > Image > Restore sector range** and choose the `.bin` file.

- If the SHA-256 of the data file does not match the sidecar file, the restore is refused.
- If the target disk differs from the disk the data came from, a warning is shown.
- By default, the current content is saved first to `name.before-<time>.bin`.
- After writing, the sectors are read back and compared.

Restoring is not available in write-protect mode.

## Reconstruct a RAID {#raid}

You can combine disks or images as the members of a RAID and open the result as one read-only disk.

1. Open the members.
2. Select **Forensics > Image > Reconstruct RAID**.
3. Remove the documents that are not members, and put the members in order with **Up** and **Down**.
4. Choose the type (JBOD, RAID 0, RAID 1, or RAID 5), the stripe size, and for RAID 5 the parity layout. Mark a missing member with **This member is missing** (RAID 5 can have one).
5. Check **Detection**. It shows whether a partition table and a file system signature are found in the first 1 MiB. Change the order or the parameters until it looks right.
6. Select **Open**.

A missing RAID 5 member is computed from the others. You can save the configuration with **Save configuration** and load it later.
