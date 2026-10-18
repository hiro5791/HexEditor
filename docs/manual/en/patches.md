# Patches

A patch records the differences between two versions of data. You can create a patch from your changes and apply it to the original file, here or in another program. This page explains how to create, apply and share patches.

## Creating a patch {#create}

1. Select **Data > Patch > Create patch...**.
2. Under **Compare**, choose what to compare.
3. Under **Format**, choose the patch format.
4. Choose the **Output file**.
5. Check the number of records and the estimated size, and select **Create**.

| Compare | Meaning |
| --- | --- |
| **Saved content and current content** | Your unsaved changes. This is the default and is quick, because only the changed places are looked at |
| **Two files (tabs)** | From another open tab (the original) to this tab |
| **Two points in the history** | From one point of the undo history to another |

You can also create a patch from the history panel (**Create patch from these two points...**) and from a comparison (**Create patch from comparison...** on the comparison toolbar).

| Format | Notes |
| --- | --- |
| **Automatic (IPS or IPS32)** | Uses IPS when possible, and IPS32 when there are changes at 16 MiB or more |
| **IPS** | Widely supported. Changes must be below 16 MiB. Inserted or deleted bytes are written as changes to all later bytes |
| **IPS32** | Like IPS, for offsets below 4 GiB |
| **UPS** | No size limit. Records the CRC-32 of the original and the result, and can be reversed |
| **BPS** | No size limit. Records CRC-32 values and describes insertions and deletions efficiently |
| **HexEditor patch (.hexpatch)** | No size limit. Records insertions, deletions and the identity of the original file, and can be reversed |
| **Self-applying (EXE)** | A program that applies the patch by itself (see below) |

A format that cannot represent your changes is unavailable, and the reason is shown. If your changes insert or delete bytes and the format cannot represent that efficiently, you see a warning suggesting BPS or the HexEditor patch format.

Comparing the original and the result reads the whole data for UPS and BPS (for the CRC-32), so large files take longer. Creating a patch runs in the background with progress.

## Applying a patch {#apply}

1. Select **Data > Patch > Apply patch...** and choose the patch file. The format is detected from the file.
2. Check the information: the format, the number of records, the number of bytes to change and the length after applying.
3. Check **Check of the original**:
   - **Match**: the patch was made for this data.
   - **Mismatch**: the patch may be for a different file. The button changes to **Apply anyway**, and **Cancel** is the default.
   - **Cannot be checked**: IPS and IPS32 patches do not record the original.
4. Select **Apply**.

The patch is applied as one edit, so one <kbd>Ctrl</kbd>+<kbd>Z</kbd> undoes it. Nothing is written to the file until you save. For UPS and BPS, checking reads the whole document; for large files you can select **Skip check and apply**. After a UPS, BPS or HexEditor patch is applied, the result is checked against the patch, and the patch is undone if it does not match.

To undo a patch that was already applied to a file, turn on **Reverse**. This is available for UPS and HexEditor patches.

A patch that changes the length cannot be applied to data with a fixed length, such as a disk.

## Self-applying patches {#self-applying}

A self-applying patch is a small program with the patch inside. People who do not have HexEditor can run it to patch their copy of the file.

1. Create a patch and choose the format **Self-applying (EXE)**.
2. Optionally set the **Default file name to patch** and a **Message shown to the user**.
3. Choose the **Applier program**: **x64** or **ARM64**, for the PC that will run it.
4. Create the file.

The program is not code-signed, so Windows SmartScreen shows a warning when it is run. When someone runs it, it:

1. Shows the message and asks them to select the file to patch.
2. Checks that the file is the original the patch was made for, and warns if it is not.
3. Asks whether to create a backup (`name.bak`). The default is yes.
4. Writes the result to a temporary file, checks it, and then replaces the file. If anything fails, the file is not changed.
5. Shows the result.

The program uses the Windows display language when a translation is available, and English otherwise.
