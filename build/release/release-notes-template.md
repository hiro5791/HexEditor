## Which file should I download?

| If you want to... | Download |
| --- | --- |
| Install HexEditor normally (recommended) | `HexEditor-{{VERSION}}-x64-Setup.exe` |
| Use it without installing (USB drive, no admin rights) | `HexEditor-{{VERSION}}-x64-portable.zip` |
| Install without the SmartScreen warning | [Microsoft Store]({{STORE_URL}}) (see the note below about administrator rights) |

Choose `arm64` instead of `x64` on an ARM PC (for example Snapdragon). To check which one you have: Settings > System > About > "System type". 32-bit Windows is not supported.

## Changes

{{CHANGELOG}}

{{KNOWN_ISSUES}}

## Notes for each version

### Windows SmartScreen warning

The installer and the portable version are not code-signed, so the first time you run them Windows may show "Windows protected your PC" (SmartScreen). To continue, click **More info** and then **Run anyway**. Check the file with `SHA256SUMS.txt` (below) before you run it.

If you prefer to install without the warning, use the **Microsoft Store** version. In the Store version, features that need administrator rights (physical disks, internal disk volumes, memory of elevated processes) work only when you start the whole app with **Run as administrator** (Start menu > right-click HexEditor > More > Run as administrator). The installer and portable versions ask for permission (UAC) only when needed. Memory of your own processes and USB storage volumes work in every version without administrator rights.

## Checking the downloads

`SHA256SUMS.txt` lists the SHA-256 of every file in this release (`sha256sum` format). In PowerShell:

```powershell
Get-FileHash .\HexEditor-{{VERSION}}-x64-Setup.exe -Algorithm SHA256
```

Compare the result with the line for that file in `SHA256SUMS.txt`.
