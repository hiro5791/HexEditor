# HexEditor

Windows 向けのバイナリエディタ (Hex Editor) です。数百 GB のファイルや物理ディスクでも、開く・表示・編集・検索・保存ができます。

A hex editor for Windows. It opens, shows, edits, searches and saves files and disks of any size, even hundreds of GB.

[日本語](#日本語) | [English](#english)

## 日本語

### ダウンロード

[GitHub Releases](https://github.com/hiro5791/HexEditor/releases) から選びます。

| こんなとき | ファイル |
| --- | --- |
| 普通にインストールしたい (おすすめ) | `HexEditor-<版>-x64-Setup.exe` |
| インストールせずに使いたい (USB メモリなど) | `HexEditor-<版>-x64-portable.zip` |
| SmartScreen の警告なしにインストールしたい | Microsoft Store 版 (公開の準備中) |

ARM の PC では `x64` の代わりに `arm64` を選んでください。32 bit の Windows には対応していません。動作環境は Windows 10 (1809) 以降です。

### Windows SmartScreen の警告について

インストーラ版とポータブル版はコード署名をしていないため、初めて実行するときに「Windows によって PC が保護されました」(SmartScreen) と表示されることがあります。続けるには **詳細情報** を押し、**実行** を押します。

実行する前に、ダウンロードしたファイルをリリースの `SHA256SUMS.txt` と比べて確認できます。

```powershell
Get-FileHash .\HexEditor-<版>-x64-Setup.exe -Algorithm SHA256
```

警告なしにインストールしたい場合は Microsoft Store 版を使ってください。ただし Store 版では、管理者権限が必要な機能 (物理ディスク、内蔵ディスクのボリューム、昇格したプロセスのメモリ) は、アプリ全体を **管理者として実行** で起動したときだけ使えます (スタートメニューで HexEditor を右クリック > その他 > 管理者として実行)。インストーラ版とポータブル版は、必要なときだけ UAC で確認します。自分のプロセスのメモリと USB ストレージのボリュームは、どの版でも管理者権限なしで使えます。

### データの保存先

| 版 | 場所 |
| --- | --- |
| インストーラ版 | `%LocalAppData%\HexEditorData\` (アンインストールしても残ります) |
| ポータブル版 | `HexEditor.exe` の隣の `Data\` (`portable.marker` の `DataDirectory=` で変更可) |
| Microsoft Store 版 | アプリのパッケージのフォルダ (アンインストールで消えます) |

テレメトリは送りません。ログとクラッシュ情報は PC の中にだけ残り、ファイルの内容は含みません。

### ビルド

```powershell
dotnet build src/HexEditor.App -p:Platform=x64
dotnet test tests/HexEditor.Core.Tests --filter "Category!=Nightly"
dotnet test tests/HexEditor.Platform.Tests
./build/publish.ps1 -Distro Portable -Arch x64   # Msix / Installer / Portable、x64 / arm64
```

## English

### Download

Choose a file from [GitHub Releases](https://github.com/hiro5791/HexEditor/releases).

| If you want to... | Download |
| --- | --- |
| Install it normally (recommended) | `HexEditor-<version>-x64-Setup.exe` |
| Use it without installing (USB drive and so on) | `HexEditor-<version>-x64-portable.zip` |
| Install without the SmartScreen warning | Microsoft Store version (coming soon) |

On an ARM PC, choose `arm64` instead of `x64`. 32-bit Windows is not supported. Requires Windows 10 (1809) or later.

### About the Windows SmartScreen warning

The installer and the portable version are not code-signed, so the first time you run them Windows may show "Windows protected your PC" (SmartScreen). To continue, click **More info** and then **Run anyway**.

Before you run a file, you can check it against `SHA256SUMS.txt` in the release:

```powershell
Get-FileHash .\HexEditor-<version>-x64-Setup.exe -Algorithm SHA256
```

If you prefer to install without the warning, use the Microsoft Store version. In the Store version, features that need administrator rights (physical disks, internal disk volumes, memory of elevated processes) work only when you start the whole app with **Run as administrator** (Start menu > right-click HexEditor > More > Run as administrator). The installer and portable versions ask for permission (UAC) only when needed. Memory of your own processes and USB storage volumes work in every version without administrator rights.

### Where data is kept

| Version | Location |
| --- | --- |
| Installer | `%LocalAppData%\HexEditorData\` (kept when you uninstall) |
| Portable | `Data\` next to `HexEditor.exe` (change it with `DataDirectory=` in `portable.marker`) |
| Microsoft Store | The app's package folder (removed when you uninstall) |

HexEditor sends no telemetry. Logs and crash reports stay on your PC and never contain the contents of your files.

### Build

```powershell
dotnet build src/HexEditor.App -p:Platform=x64
dotnet test tests/HexEditor.Core.Tests --filter "Category!=Nightly"
dotnet test tests/HexEditor.Platform.Tests
./build/publish.ps1 -Distro Portable -Arch x64   # Msix / Installer / Portable, x64 / arm64
```

## License

MIT License. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.txt](src/HexEditor.App/THIRD-PARTY-NOTICES.txt).
