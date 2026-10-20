# Device information and encrypted volumes

Record the state of a disk before you work with it, find hidden areas on ATA disks, and find out whether a volume is encrypted.

## HPA and DCO {#hpa-dco}

An ATA disk can hide part of its capacity from Windows with a Host Protected Area (HPA) or a Device Configuration Overlay (DCO). HexEditor compares the capacity the disk reports with its real capacity.

1. Open the physical disk (**File > Open Disk...**).
2. Select **Forensics > Device > Check HPA / DCO**, or run **Forensics: Check HPA and DCO**.
3. The **Device information** panel opens on the **HPA / DCO** tab.

The panel shows the largest LBA the disk reports (28-bit and 48-bit), the native largest LBA, the largest LBA set by DCO, and whether an HPA or a DCO is present, with its size. When you open a disk, HexEditor checks this automatically and shows a notice if it finds a hidden area. You can turn the automatic check off in **Settings > Forensics**.

HexEditor sends only commands that read information. It never removes an HPA or a DCO, because that would change the disk.

NVMe disks have no HPA or DCO. The panel shows the size and capacity of the namespace instead.

If the commands do not reach the disk, for example through some USB adapters or on virtual disks, the panel says that this cannot be checked on this device. If a command fails, the ATA status and error registers are shown.

Reading this information requires the same rights as opening the disk. If HexEditor opened the disk through the elevated helper, the helper reads it too.

## SMART information {#smart}

1. With a physical disk open, select **Forensics > Device > SMART information**, or run **Forensics: Show SMART information**.
2. The **SMART** tab lists the attributes.

For ATA and SATA disks, each attribute shows its ID, name, current value, worst value, threshold, raw value in hexadecimal and decimal, and a status. The status is **Warning** when the current value is at or below the threshold. Attributes that HexEditor does not know are named **Unknown**.

For NVMe disks, the tab shows the warning bits, temperature, available spare, percentage used, data read and written, power cycles, power-on hours, unsafe shutdowns and media errors.

The **Device** tab shows the model, serial number, firmware, capacity, logical and physical sector size, connection, rotation rate and whether TRIM is supported.

HexEditor never starts a SMART self-test, because that changes the state of the disk.

## Export and record {#export}

Select **Export JSON...** or **Export text...** in the panel to save the information. The file includes the time it was read, in UTC, for example `2026-10-07T03:15:42.1234567Z`, and the serial number.

When a case is open, each check is recorded in the audit log, and the results appear in the **Device information** section of a forensic report.

## Encrypted volumes {#encryption}

HexEditor recognizes encrypted volumes and shows their metadata. It does not decrypt them.

1. Open the disk, volume or disk image.
2. Run **Forensics: Show encryption information**, or select **Forensics > Encryption information...**.

HexEditor looks at each partition (or the whole image when it has no partition table):

- **BitLocker** and **BitLocker To Go**: the encryption method (for example AES-XTS 128), the volume GUID, when it was created, the description (computer name and date), the key protectors (TPM, recovery password, startup key, password and others, with their GUID and creation time) and the encryption state. If the three copies of the metadata do not match, or some are damaged, you see a warning and the items that could be read.
- **LUKS1** and **LUKS2**: the cipher, the UUID and the number of key slots.
- **APFS** (FileVault 2): which volumes in the container are encrypted.
- **Core Storage**: recognized from the partition type.
- **Possibly encrypted or compressed data**: a partition with no file system signature whose first 64 KiB look random. VeraCrypt and TrueCrypt volumes have no signature, so HexEditor can only say this much.

The file system browser does not open encrypted volumes.

If Windows has unlocked a BitLocker volume and it has a drive letter, the dialog explains that you can read the decrypted contents through the volume, and shows **Open as logical volume**. Select it to open the volume, for example `\\.\X:`.
