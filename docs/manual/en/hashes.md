# Hashes and checksums

A hash is a short value calculated from data. If the data changes even slightly, the value changes. Use it to check that a file is not damaged, or that it is the same as the file from its source. This page explains how to calculate hashes and checksums of a selection or a whole document.

## Calculating a hash {#calculate}

1. Select **Analysis > Hash...**. The **Hash** panel opens. You can also use **View > Panels > Hash**, or run **Analysis: Calculate hash** from the command palette. Right-clicking a selection and choosing **Compute hash** works too.
2. Choose the **Range**: **Selection**, **Multiple selection**, **Whole document** or **Specify range**. When you specify a range, enter **Start** and either **Length** or **End (inclusive)**. Leave it empty to go to the end.
3. Check the algorithms you want.
4. Select **Calculate**.

The results table shows the algorithm name, the value and the number of bits. Even with several algorithms, the data is read only once.

Keep these points in mind.

- The calculation runs in the background and shows progress and speed (MB/s). You can keep scrolling and editing. If you **Cancel**, the partial results are discarded.
- With **Recalculate automatically** on, the values are recalculated 0.3 seconds after the range changes. If the range is larger than 64 MB, they are not calculated automatically and **Calculate** is highlighted.
- After you edit the document, a note says "The results are for the content before the edits." Select **Recalculate** to calculate again.
- Rows whose value differs from the previous result are marked **Changed**.
- If some place cannot be read, the calculation stops with "Can't calculate because offset ... can't be read." Use **Add the unreadable range to the excluded ranges** to leave that range out.

### With a multiple selection

When several ranges are selected, choose how to handle the **Multi-selection**.

- **Concatenate into one value**: joins the ranges in order of offset into one value.
- **One value per range**: shows a row for each range.

### Excluded ranges

You can leave some ranges out of the calculation. For example, use it when you want to calculate without the field that holds the checksum itself.

1. Open **Excluded ranges**, enter **Start** and **Length**, and select **Add**. **Add selection** adds the current selection.
2. Choose **Excluded bytes**. **Skip** leaves the bytes out of the input. **Replace with a value** treats the bytes as a given value (`0x00` at first).

An excluded range outside the target range shows a warning and is ignored.

### Changing the display

- **Format**: **Hex (uppercase)** (default), **Hex (lowercase)**, **Base64**, **Decimal (64-bit or less)**.
- **Byte order (64-bit or less)**: **Big-endian (number)** or **Little-endian bytes**.

### Copying and saving results

- **Copy value** in a row copies the value. You can also copy it as "name: value" or as a checksum line (`<value> *<file name>`).
- **Copy all** copies every row as "name: value", checksum lines, JSON or CSV.
- **Save as checksum file...** saves a checksum file such as `.sha256`, in a form you can check with `sha256sum -c`.
- **Write at cursor...** writes the bytes of the calculated value at the cursor. Use it to put a checksum into a file. For values of 64 bits or less you choose the byte order. The write overwrites, and one <kbd>Ctrl</kbd>+<kbd>Z</kbd> undoes it. If the place you write to is inside the calculated range, a warning appears because the value would change. You can also run **Analysis: Write hash value at cursor** from the command palette.

## Algorithms {#algorithms}

The list of algorithms is grouped, and each group can be collapsed. Type a name in **Filter algorithms** to find one.

| Group | Main ones |
| --- | --- |
| **Checksums** | Sum 8, 16, 32 and 64-bit, word sums, Internet checksum, XOR |
| **CRC** | CRC-8, CRC-16 (ARC, MODBUS, CCITT and more), CRC-24, CRC-32 (standard, CRC-32C, BZIP2, MPEG-2 and more), CRC-64. Aliases work in the filter |
| **Non-cryptographic hashes** | Adler-32, Fletcher, FNV, xxHash, MurmurHash, SipHash and more |
| **Cryptographic hashes** | MD5, SHA-1, SHA-224 / 256 / 384 / 512, SHA-3, RIPEMD, Whirlpool, Tiger, BLAKE2, BLAKE3 and more |

MD5, SHA-1 and a few others show "Not secure: don't use it to detect tampering." They are fine for checking for accidental damage, but use SHA-256 or stronger to detect tampering. If you choose a slow algorithm such as Whirlpool, the whole calculation slows down to its speed, and the row says so.

An algorithm that is not available on your system is disabled in the list, with the reason.

You can pick a combination of algorithms as a **set**. These sets come ready to use:

- **Common**: CRC-32, MD5, SHA-1, SHA-256
- **Cryptographic hashes**
- **Checksums**

To keep your own combination, check the algorithms and select **Save set** to give it a name.

Some algorithms take settings in the **settings** of their row.

- Sum checksums: **Complement of the result** (**None**, **Ones' complement**, **Two's complement**). Word-based ones also let you choose the word byte order and signedness.
- xxHash, MurmurHash and others: **Seed**.
- SipHash, BLAKE2 and others: **Key (hex)** and **Output length (bits)**.

The values you set appear in the result row, such as `xxHash64 (seed=0x1234)`. A row with invalid input is the only one that is not calculated.

## Custom CRC {#custom-crc}

You can define a CRC that is not in the list.

1. Select **Analysis > Define custom CRC...**. **Add custom CRC...** in the algorithm list of the hash panel opens the same dialog.
2. Enter these items. Numbers can be written like `0x8005`.
   - **Name**: the name shown in the list. It must not match another algorithm.
   - **Width (bits)**: 1 to 64.
   - **Polynomial (poly)**, **Initial value (init)**, **Final XOR (xorout)**
   - **Reflect input (refin)**, **Reflect output (refout)**
3. As you type, the value for `123456789` (check) and the residue are shown. Compare them with your documentation.
4. Select **Save**.

Choose an existing preset and select **Duplicate and edit** to start from similar settings.

A defined CRC is saved in the settings and works in every document. Use **Export...** and **Import...** in the algorithm list to write it to a JSON file or load it. If the file you import has an invalid item, the message tells you which one cannot be read, and only the valid items are loaded.

## Comparing with an expected value {#compare-hashes}

You can check whether a calculated value equals the one published by the source.

### Pasting a value

1. Calculate the hashes.
2. Paste the published value into **Expected value** in the **Verify** area at the bottom of the panel.
3. Rows with the same value show **Match**. If it differs, the row shows **No match**.

You can paste hex (upper or lower case, separated by spaces, `:` or `-`, or starting with `0x`), Base64, or one line of a checksum file. Case and separators are ignored. For values of 64 bits or less, the reversed byte order is checked too, and then **Match (reversed byte order)** appears.

The result is shown with an icon and text, not only color.

### Verifying with a checksum file

1. Select **Verify with checksum file...**. You can also run **Analysis: Verify with checksum file** from the command palette.
2. Choose a checksum file: `.md5`, `.sha1`, `.sha256`, `.sha512` or `.sfv`.
3. HexEditor finds the line for the document's file name and checks it. The algorithm comes from the file extension and the length of the value.

If there is no line for the file, the message "There's no line for this file." appears and you can choose a line from the file.

### When you only know the value

If you do not know the algorithm, use **Find matching algorithm**.

1. Paste the value into **Expected value**.
2. Select **Find matching algorithm**. You can also run **Analysis: Find matching algorithm** from the command palette.
3. Read the message and select **Search**.

Every algorithm of the same length is calculated (CRC presets, your custom CRCs, and combinations of checksum options), and the ones that match are listed. If the range is larger than 1 MiB, an estimate of the time is shown first.
