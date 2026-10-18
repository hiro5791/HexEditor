# Encryption and compression

You can encrypt, decrypt, compress and decompress the selection, and edit decompressed or decrypted data in its own tab and write it back. If nothing is selected, the whole document is used. These commands work on one range; they do not work on a multiple or rectangular selection.

## Encrypt and decrypt {#encrypt}

1. Select the bytes.
2. Select **Data > Encrypt...** or **Data > Decrypt...**.
3. Choose the **Algorithm** and the **Mode**.
4. Enter the key, the IV or nonce, and the other settings described below.
5. Choose where to write the result, and select **Encrypt** or **Decrypt**.

| Algorithm | Modes | Notes |
| --- | --- | --- |
| **AES-128**, **AES-192**, **AES-256** | ECB, CBC, CFB8, CFB128, CTR, GCM | GCM is authenticated |
| **ChaCha20-Poly1305** | — | Authenticated. Not available on older versions of Windows |
| **3DES (TripleDES)**, **DES** | ECB, CBC | For compatibility only. The dialog says that the method is not secure |
| **RC4** | — | For compatibility only |

Authenticated encryption (GCM and ChaCha20-Poly1305) can be used for data up to 1 GiB. The other modes have no size limit; a large range is processed in the background with progress, and you can cancel it.

### Key {#key}

- **Enter in hex**: type the key bytes. The length must match the algorithm (for example 32 bytes for AES-256). The dialog tells you when the length is wrong.
- **Make from a passphrase (PBKDF2-HMAC-SHA256)**: type the **Passphrase**, the number of **Iterations** (600,000 by default) and the **Salt** in hex. If you leave the salt empty, 16 random bytes are generated and shown after the operation. Keep the salt; you need it to decrypt.

The key fields are password fields. Select **Show** to see what you typed.

### IV and nonce {#iv}

CBC, CFB and CTR use a 16-byte IV (8 bytes for 3DES and DES). For CTR, the IV is the initial counter. GCM and ChaCha20-Poly1305 use a 12-byte nonce. ECB and RC4 need no IV. When encrypting, **Generate randomly** fills in a random IV.

### Padding {#padding}

ECB and CBC can use **PKCS#7** (the default), **Zeros**, **ANSI X9.23**, **ISO 10126** or **None**. With **None**, the length must be a multiple of the block size. When decryption finds wrong padding, you can choose **Decrypt without padding**. Zero padding is not removed when you decrypt.

### Authentication tag {#tag}

For GCM and ChaCha20-Poly1305 you can enter **Additional authenticated data** in hex. When encrypting, the 16-byte tag is appended to the result by default; choose **Only show the authentication tag** to see it in a separate box instead. When decrypting, the tag is the last 16 bytes of the selection by default, or you can enter it in hex. If the tag does not match, you see "Authentication failed" and the data is not changed.

### Keys are not saved {#not-saved}

Keys, passphrases and IVs are never saved in the settings, history, recovery data or logs. When the dialog closes, the key bytes are erased from memory. Only the chosen algorithm, mode and padding are remembered.

## Compress and decompress {#compress}

1. Select the bytes.
2. Select **Data > Compress...** or **Data > Decompress...**.
3. Choose the **Format**. For compression, set the **Compression level**.
4. Choose where to write the result, and select **Compress** or **Decompress**.

The formats are deflate (raw), zlib, gzip, Brotli, zstd, LZ4 (frame and block), LZMA (.lzma) and XZ. For gzip you can store the file name and modification time in the header.

### Detecting the format {#detect}

When you decompress with **Detect automatically**, the format is found from the first bytes: gzip, zlib, zstd, LZ4 frame, XZ and LZMA can be detected. Deflate (raw), Brotli and LZ4 block have no signature; if detection fails, the dialog asks you to choose the format.

Decompression stops at the end of the compressed data. If the selection continues after it, you see how many bytes were used, and the rest is not changed.

### Errors and large data {#errors}

- If the compressed data is damaged, you see the offset of the error and can open the part that was decompressed in a new tab.
- If a checksum (gzip CRC-32, zlib Adler-32, zstd, LZ4) does not match, you are asked whether to use the result.
- If the decompressed data grows beyond the size set in **Decompression size to confirm (MiB)** (4 GiB by default), decompression pauses and asks whether to **Continue** or **Abort**. This protects you from compression bombs. Abort leaves the document unchanged.
- The LZ4 block format can compress up to 256 MiB. Use the LZ4 frame format for larger data.

### Select compressed data {#select-compressed}

Put the cursor at the start of compressed data, for example just after the local file header of a zip entry, and select **Data > Select compressed data**. HexEditor tries to decompress from the cursor and selects the compressed data. The message shows the format and the decompressed size. The data is not changed.

## Where the result goes {#destination}

- **Replace the selection**: the result replaces the range in one edit, which you can undo with <kbd>Ctrl</kbd>+<kbd>Z</kbd>. This is the default for compression and encryption.
- **Open in a new tab**: the result opens in a new tab. This is the default for decompression.

If the document is read-only, or its length cannot change, the result can only open in a new tab.

## Editing decompressed or decrypted data {#derived}

When you decompress or decrypt into a new tab, the tab is a derived document. Its title shows the source range and the transform, such as `image.png [0x21-0x1A0 decompressed]`. A band at the top shows the source document, the range and the transform.

1. Edit the derived document as usual. Its length can change.
2. Select **Write back to source** in the band, or **Data: Write back to source** in the command palette.

The data is compressed or encrypted again and replaces the source range in one edit, which you can undo in the source document with <kbd>Ctrl</kbd>+<kbd>Z</kbd>. Afterwards the band shows the new range.

- For decrypted data you must enter the key again, because it is not kept. The IV is the same as when you opened the tab. For GCM and ChaCha20-Poly1305 a new nonce is generated by default, because reusing a nonce is not secure; the new nonce is shown after writing back, and you must store it where your data format expects it.
- If the source range was changed after you opened the derived document, you are asked whether to overwrite it.
- If the length changes and the source document cannot change its length, the data is not written back.
- If you close the source document, the derived document becomes a normal new document and can no longer be written back.

You can open a derived document from a derived document, up to eight levels deep.
