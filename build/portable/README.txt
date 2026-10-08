HexEditor - portable version
============================

(日本語は下にあります)

HexEditor is a hex editor for files and disks of any size.
This portable version needs no installation and leaves nothing in the
registry or your user folders unless you register it yourself.

How to use
----------
1. Extract the whole "HexEditor" folder from the zip file to any place
   (for example a USB drive or C:\Tools\HexEditor).
2. Run HexEditor.exe.

Settings, recovery data and logs are kept in the "Data" folder next to
HexEditor.exe. To keep them somewhere else, write one line to
portable.marker, for example:

    DataDirectory=..\HexData

A relative path is resolved from the folder of HexEditor.exe. If the data
folder cannot be written to (a CD-ROM or a write-protected USB drive),
HexEditor still starts, but settings are kept only until you close it.
Temporary files go to %TEMP%\HexEditor-<hash>\ and are deleted when you exit.

Windows itself may still record some things that HexEditor cannot control
(the history of the Open dialog, jump lists, Prefetch).

Command line
------------
The command-line tool hexed.exe is in the same folder (in versions that
include it). The portable version does not change PATH. Call it with its
full path:

    "C:\Tools\HexEditor\hexed.exe" --version

To call it as just "hexed", add the HexEditor folder to your user PATH
yourself (Settings > System > About > Advanced system settings >
Environment Variables > User variables > Path).

You can also open a file directly:

    "C:\Tools\HexEditor\HexEditor.exe" file.bin

Updating
--------
The portable version does not replace itself. When a new version is out,
HexEditor shows "Version X is available" with "Open download page". To
update:

1. Close HexEditor.
2. Extract the new zip to a new folder.
3. Move the "Data" folder and portable.marker from the old HexEditor folder
   to the new one (replace the new portable.marker).
4. Delete the old folder. If you registered the Explorer context menu,
   HexEditor offers to update the registration when you start the new copy.

Checking for updates uses the internet (GitHub). Turn it off in the settings
(update.checkAutomatically) or with offline mode (network.offline).

Removing HexEditor
------------------
If you registered the Explorer context menu or file associations from the
settings screen, remove them first with "hexed --unregister":

    "C:\Tools\HexEditor\hexed.exe" --unregister

(or Tools > Settings > Advanced > "Unregister from this PC").
Then delete the HexEditor folder. That is all.

Windows SmartScreen warning
---------------------------
HexEditor is not code-signed, so the first time you run HexEditor.exe
Windows may show "Windows protected your PC" (SmartScreen).
To continue, click "More info" and then "Run anyway".

Before you run it, you can check that the download is intact: compare the
SHA-256 of the zip file with the value in SHA256SUMS.txt on the release page.
In PowerShell:

    Get-FileHash .\HexEditor-<version>-x64-portable.zip -Algorithm SHA256

If you prefer to install without the warning, use the Microsoft Store
version. Note that in the Store version, features that need administrator
rights (physical disks, internal disk volumes, memory of elevated
processes) work only when you start the whole app with "Run as
administrator". The installer and portable versions ask for permission
(UAC) only when needed. Memory of your own processes and USB storage
volumes work in every version without administrator rights.

Licenses: see LICENSE and THIRD-PARTY-NOTICES.txt.


HexEditor ポータブル版
======================

HexEditor は、どんな大きさのファイルやディスクも扱えるバイナリエディタです。
ポータブル版はインストール不要で、自分で登録しない限り、レジストリや
ユーザーフォルダに何も残しません。

使い方
------
1. zip の中の「HexEditor」フォルダを好きな場所 (USB メモリや
   C:\Tools\HexEditor など) にまるごと展開します。
2. HexEditor.exe を実行します。

設定・復旧用データ・ログは HexEditor.exe の隣の「Data」フォルダに保存します。
別の場所にしたい場合は、portable.marker に次のように 1 行書きます。

    DataDirectory=..\HexData

相対パスは HexEditor.exe のフォルダから数えます。データフォルダに書き込めない
場合 (CD-ROM、書き込み禁止の USB メモリなど) でも起動できますが、設定は終了
するまでの間だけ有効です。一時ファイルは %TEMP%\HexEditor-<ハッシュ>\ に置き、
終了時に消します。

ファイルを開くダイアログの履歴、ジャンプリスト、Prefetch など、Windows 自身が
記録するものは HexEditor からは制御できません。

コマンドライン
--------------
コマンドラインのツール hexed.exe は同じフォルダにあります (同梱している版の
場合)。ポータブル版は PATH を変えません。フルパスで呼んでください。

    "C:\Tools\HexEditor\hexed.exe" --version

「hexed」だけで呼びたい場合は、HexEditor のフォルダを自分でユーザーの PATH に
追加してください (設定 > システム > バージョン情報 > システムの詳細設定 >
環境変数 > ユーザー環境変数 > Path)。

ファイルを直接開くこともできます。

    "C:\Tools\HexEditor\HexEditor.exe" file.bin

更新のしかた
------------
ポータブル版は自分では入れ替わりません。新しい版が出ると「版 X があります」と
「ダウンロードページを開く」を表示します。更新するには次のようにします。

1. HexEditor を終了します。
2. 新しい zip を新しいフォルダに展開します。
3. 古い HexEditor フォルダの「Data」フォルダと portable.marker を、新しい
   フォルダに移します (新しい portable.marker は置き換えます)。
4. 古いフォルダを削除します。右クリックメニューを登録していた場合は、新しい
   フォルダの HexEditor を起動すると、登録の更新を案内します。

更新の確認はインターネット (GitHub) に接続します。設定
(update.checkAutomatically) またはオフラインモード (network.offline) で止められます。

削除のしかた
------------
設定画面で右クリックメニューやファイルの関連付けを登録した場合は、先に登録を
解除します。

    "C:\Tools\HexEditor\hexed.exe" --unregister

(またはツール > 設定 > 詳細 >「この PC から登録を解除」)
その後、HexEditor のフォルダを削除すれば完了です。

Windows SmartScreen の警告
--------------------------
HexEditor はコード署名をしていないため、初めて HexEditor.exe を実行するときに
「Windows によって PC が保護されました」(SmartScreen) と表示されることが
あります。続けるには「詳細情報」を押し、「実行」を押します。

実行する前に、ダウンロードしたファイルが壊れていないかを確認できます。zip の
SHA-256 を、リリースのページの SHA256SUMS.txt の値と比べてください。
PowerShell では次のとおりです。

    Get-FileHash .\HexEditor-<版>-x64-portable.zip -Algorithm SHA256

警告なしにインストールしたい場合は、Microsoft Store 版を使ってください。
ただし Store 版では、管理者権限が必要な機能 (物理ディスク、内蔵ディスクの
ボリューム、昇格したプロセスのメモリ) は、アプリ全体を「管理者として実行」で
起動したときだけ使えます。インストーラ版とポータブル版は、必要なときだけ
UAC で確認します。自分のプロセスのメモリと USB ストレージのボリュームは、
どの版でも管理者権限なしで使えます。

ライセンス: LICENSE と THIRD-PARTY-NOTICES.txt を見てください。
