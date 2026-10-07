# バイナリエディタ機能調査

調査日: 2026-10-07

主要なバイナリエディタの機能を洗い出し、このプロジェクトで実装すべき機能の一覧にまとめたもの。あわせて、巨大ファイル対応と 3 種類の配布形態 (MSIX / ポータブル / インストーラ) についての技術調査の結果も記録する。

各項目のうち、ベンダーが内部構造を公開していないもの (HxD など) は推測を含む。推測した箇所はその旨を書いている。

## 1. 調査対象

| エディタ | 種別 | プラットフォーム | 特徴 |
| --- | --- | --- | --- |
| 010 Editor | 商用 ($59.95〜) | Win / Mac / Linux | C 風のバイナリテンプレートとスクリプト。最大 8 EB |
| WinHex (X-Ways) | 商用 ($44.90〜) | Windows | フォレンジック向け。ディスク・RAM・ファイルシステム解析 |
| Hex Workshop | 商用 ($89.95) | Windows | 構造体ビューア、スマートブックマーク。2014 年以降更新なし |
| Hex Editor Neo (HHD) | 無料〜商用 | Windows | 定数時間の編集エンジン、分岐する Undo 履歴、マルチ選択 |
| UltraEdit (Hex モード) | 商用 | Win / Mac / Linux | テキストエディタの補助機能。上書きのみの簡易なもの |
| HxD | フリー | Windows | 定番。最大 8 EB、ディスク・RAM 編集、データインスペクタ |
| ImHex | OSS (GPLv2) | Win / Mac / Linux | 強力なパターン言語、ノード型データ処理、YARA |
| Hex Fiend | OSS (BSD) | macOS | B+ 木のピーステーブル。巨大ファイル対応の手本 |
| wxHexEditor | OSS (GPL) | Win / Mac / Linux | 一時ファイルを作らない巨大ファイル編集、XOR ビュー |
| Frhed | OSS (GPLv2) | Windows | WinMerge のバイナリ比較で使われる |
| Okteta | OSS (KDE) | Linux ほか | XML / JavaScript の構造体定義。全体をメモリに読み込む (約 1 GiB まで) |
| GHex | OSS (GNOME) | Linux | バッファ実装を差し替え可能 (malloc / mmap / direct) |
| VS Code Hex Editor | OSS (MS) | VS Code | デバッグ中プロセスのメモリ編集、Copy As の形式が豊富 |
| Synalyze It! / Hexinator | 商用 / フリーミアム | Mac / Win / Linux | XML 文法 + Python / Lua スクリプト |
| Hiew | 商用 | Windows | 逆アセンブル・インプレースアセンブル、PE / ELF 解析 |

## 2. 機能カタログ

全エディタの機能を合わせた一覧。「代表例」は、その機能が特に充実しているエディタ。

### 2.1 データソース (開けるもの)

| 機能 | 代表例 |
| --- | --- |
| ファイル (サイズ上限なし) | 全般。HxD / 010 / Neo は最大 8 EB |
| 物理ディスク・論理ボリューム (セクタ単位の読み書き) | HxD, WinHex, 010, Neo |
| パーティション解析 (MBR, GPT, LDM, LVM) | WinHex, 010 |
| ファイルシステム解析 (NTFS, FAT, exFAT など。クラスタとファイルの対応付け) | WinHex, 010, Neo (Volume Navigator) |
| ディスクイメージ (raw, E01) とセクタサイズの指定 | WinHex, HxD |
| プロセスメモリ (領域・モジュール一覧、未割り当て領域の表示) | HxD, WinHex, 010, Neo, ImHex |
| プロセスのスナップショットと比較、ダンプ保存 | Neo |
| 物理 RAM | WinHex, Neo |
| NTFS 代替データストリーム (ADS) | Neo |
| Intel HEX / S-record / Base64 を開いてバイナリとして扱う | ImHex, Neo |
| リモート (GDB サーバー, SSH / SFTP, UDP) | ImHex |
| 選択範囲やブックマークを別タブで開く | ImHex |
| 外部での変更を検知 (破棄・マージ・比較を選べる) | Neo, HxD, GHex |
| 範囲を指定して部分的に開く | Frhed |

### 2.2 表示

| 機能 | 代表例 |
| --- | --- |
| 1 行のバイト数を変える (固定または自動、最大 1024〜4096) | HxD, 010 |
| バイトのグループ化 (1 / 2 / 4 / 8 / 16 バイト) | 全般 |
| セルの表示形式 (Hex, 10 進, 8 進, 2 進, float, double) | 010, Neo, ImHex |
| エンディアンの切り替え、データを変えない見かけ上のバイトスワップ | 010, Neo |
| オフセットの表示形式 (Hex, 10 進, 8 進, セクタ / LBA)、開始アドレスの指定 | 010, ImHex, wxHexEditor |
| 文字コード列 (ANSI, OEM, EBCDIC, Mac, UTF-8/16/32, CJK, 130 種類以上、独自定義の表) | Neo, 010, ImHex |
| 複数の文字コードの同時表示 (位置がずれた UTF-16 を含む) | WinHex (最大 5 種類) |
| Hex だけ、テキストだけの表示 | HxD, WinHex |
| 変更したバイトの強調表示、バイト値による色分け (バイトテーマ)、ゼロをグレー表示 | HxD, Hex Fiend, ImHex |
| レコード表示 (固定長ごとに背景色を交互に変える) | WinHex |
| ミニマップ (エントロピー・値の分布を色で表示) | 010, ImHex |
| バイトを画像として表示 (ビジュアライザ) | Hex Workshop, ImHex |
| 画面分割、同じファイルの複数ビュー、複数ウィンドウのスクロール同期 | 010, WinHex, Neo |
| ダーク / ライトテーマ、高 DPI、多言語 | 全般 |

### 2.3 編集

| 機能 | 代表例 |
| --- | --- |
| 上書きモードと挿入モード | 全般 (UltraEdit は上書きのみ) |
| 削除、ファイルサイズの変更、カーソル位置での切り詰め | 全般, Hiew |
| 貼り付けの 2 方式 (挿入して貼る / 上書きして貼る) | HxD, WinHex |
| パターン・乱数・ファイルでの塗りつぶしと挿入 | 全般 |
| 演算 (加減乗除、AND / OR / XOR、シフト、ローテート、反転、バイトスワップ) | 010, WinHex, Neo, Okteta |
| 桁あふれの扱いを選べる演算 (ラップ / 飽和)、オペランドの増分 | WinHex, 010 |
| ビット単位の挿入・削除 (ビットシフト) | WinHex |
| 暗号化・復号 (AES、Windows の暗号 API)、圧縮・展開 (zlib, zstd) | WinHex, Neo |
| 文字コード変換 (ASCII ↔ EBCDIC など)、大文字・小文字変換 | 010, WinHex |
| 矩形選択と矩形挿入 | 010 |
| マルチ選択・マルチカーソル | Neo |
| 無制限の Undo / Redo | 全般 |
| 分岐する Undo 履歴 (木構造、名前付け、保存と読み込み) | Neo |
| 履歴からパッチを作る (IPS, 自己適用型 EXE) | Neo, ImHex |
| 安全な消去 (複数回の上書き、空き領域・スラック領域の消去) | WinHex |

### 2.4 クリップボード・Copy As

| 機能 | 代表例 |
| --- | --- |
| Hex 文字列としてコピー、Hex 文字列から貼り付け | 全般 |
| 各言語の配列としてコピー (C, C++, C#, Java, JavaScript, Python, Rust, Go, Pascal, VB.NET) | ImHex, Neo, HxD, VS Code |
| エンコードしてコピー (Base64, Base32, Ascii85, UUEncode, Quoted-Printable) | Neo, 010 |
| 画面の見た目どおりにコピー (オフセット + Hex + ASCII)、HTML, RTF, Markdown | WinHex, ImHex, wxHexEditor |
| スマート貼り付け (テキストを数値列として解釈する) | Neo |
| 他のエディタのクリップボード形式と相互に貼り付けできる | HxD |
| 複数のユーザークリップボード | 010 (9 個) |

### 2.5 検索・置換

| 機能 | 代表例 |
| --- | --- |
| Hex 検索 (ニブル単位のワイルドカードを含む) | Hex Workshop, ImHex |
| テキスト検索 (任意の文字コード、UTF-16 LE / BE、大文字小文字、単語単位) | 全般 |
| 正規表現 (ECMAScript)、バイト列に対する正規表現 | Neo, 010, ImHex |
| 数値検索 (8〜64 bit 整数、float、double、エンディアン、範囲、許容誤差) | 010, Hex Workshop, ImHex |
| ビットマスク検索 | Hex Workshop |
| アラインメント条件 (オフセット mod x = y) | WinHex |
| すべて検索して結果を一覧に出す (結果をマルチ選択として扱う、エクスポート) | Neo, 010 |
| 複数の語の同時検索 (複数の文字コードにまたがる) | WinHex |
| 一致しない箇所を探す | Hex Workshop |
| 文字列の抽出 (最小長、文字コード) | 010, ImHex, Okteta |
| 複数ファイルを対象にした検索と置換 | 010, Neo |
| インクリメンタルサーチ | Synalyze It! |
| YARA ルールでスキャン | ImHex |

### 2.6 データインスペクタ

| 機能 | 代表例 |
| --- | --- |
| 整数 8 / 16 / 24 / 32 / 48 / 64 / 128 bit (符号あり・なし) | HxD, ImHex |
| 浮動小数点 (half, bfloat16, float, double, 80 bit long double) と固定小数点 | ImHex, WinHex |
| 可変長整数 (LEB128, SQLite varint) | HxD, WinHex |
| 日時 (DOS, FILETIME, OLE, time_t 32/64, HFS+, APFS, Java など) | WinHex |
| GUID, 色 (RGBA8, RGB565), 2 進表示 | ImHex, HxD |
| 文字 (ANSI, UTF-8, UTF-16) | HxD |
| 1 命令ぶんの逆アセンブル | 010, HxD |
| インスペクタから値を書き換えられる | 全般 |
| ポインタとして解釈してジャンプする (絶対 / 相対) | HxD |
| 配列として前・次の要素に移動 | HxD |
| ユーザー定義型、プラグインで型を追加 | Neo, HxD, ImHex |

### 2.7 構造解析 (テンプレート)

| 機能 | 代表例 |
| --- | --- |
| C 風のテンプレート言語 (struct, union, enum, ビットフィールド, 条件分岐, ループ, 関数) | 010, ImHex, Neo |
| 表示用の属性 (色, 書式, コメント, 非表示, 読み書き変換) | 010, ImHex |
| ポインタ、動的なサイズの配列、仮想セクション (展開したデータの解析など) | ImHex |
| 開いたファイルのマジックや MIME で自動的に適用 | ImHex, 010, Hex Fiend |
| 解析結果をツリー表示、ツリーからの値の編集 | 全般 |
| カーソルに追従する構造体、オフセットに固定した構造体 | Hex Workshop |
| Kaitai Struct (.ksy) の読み込み | Neo |
| テンプレートのデバッガ | 010, ImHex |
| オンラインリポジトリからの取得 | 010, ImHex |

### 2.8 比較 (Diff)

| 機能 | 代表例 |
| --- | --- |
| 単純な比較 (バイトごと) | 全般 |
| 挿入・削除を考慮した比較 (Myers / LCS 方式、再同期) | 010, Neo, Hex Fiend |
| ファイルごとに比較範囲・開始オフセットを指定 | WinHex, Hex Workshop |
| 差分のマージ | 010, wxHexEditor |
| 結果を一覧とグラフで表示、次・前の差分へジャンプ | 010, WinHex |

### 2.9 チェックサム・ハッシュ

| 種類 | 代表例 |
| --- | --- |
| 加算チェックサム (8〜64 bit、エンディアン)、XOR | Neo, 010 |
| CRC (CRC-8/16/32/64 と各バリアント、多項式などを指定するカスタム CRC) | Neo, ImHex, HxD |
| Adler-32, Fletcher, FNV, xxHash, Murmur, SipHash | Neo, ImHex |
| MD2 / MD4 / MD5, SHA-1 / 224 / 256 / 384 / 512, RIPEMD, Tiger, Whirlpool, BLAKE2 / 3 | Neo, Hex Workshop, ImHex |
| 選択範囲 (マルチ選択を含む) に対する計算 | Neo |

### 2.10 解析

| 機能 | 代表例 |
| --- | --- |
| バイト値のヒストグラム (他の型でも可) | 010, WinHex, HxD |
| 記述統計 (平均, 分散, 中央値, 歪度など) | Neo |
| エントロピー (全体、グラフ)、暗号化・圧縮されたデータの検出 | Neo, ImHex |
| ファイル形式の判定 (マジック, MIME) | ImHex, Okteta |
| ダイグラム (2 バイト組) の分布グラフ | ImHex |

### 2.11 ブックマーク・注釈

| 機能 | 代表例 |
| --- | --- |
| ブックマーク (名前、色、コメント、Markdown) | ImHex |
| 型付きブックマーク、アドレスを式で指定 | Hex Workshop |
| 長い説明を付けられる位置マネージャ (ツールチップ表示) | WinHex |
| 色付けルール (値・範囲・正規表現で一致した箇所を色分け) | Neo, 010 |
| タグのインポート / エクスポート | wxHexEditor |
| ジャンプ履歴 (戻る / 進む) | WinHex |

### 2.12 インポート・エクスポート

| 形式 | 代表例 |
| --- | --- |
| Intel HEX (8 / 16 / 32)、Motorola S-record (S1 / S2 / S3)。チェックサム検証、空き領域の省略 | 010, HxD, Neo |
| Base64, UUEncode, Quoted-Printable, URL エンコード | 010, WinHex |
| ソースコード (C, Java, C# など)、HTML, RTF, TeX, テキストダンプ | HxD, 010 |
| IPS / IPS32 パッチ | ImHex |

### 2.13 逆アセンブル

| 機能 | 代表例 |
| --- | --- |
| Capstone ベースの多アーキテクチャ対応 (x86 / x64, ARM / ARM64, MIPS, PowerPC, RISC-V, 68K, 6502 など) | 010, Neo, ImHex |
| MSIL (.NET) | Neo |
| PE / ELF のセクションとシンボルの解釈、仮想アドレス表示 | Neo, Hiew |
| インプレースアセンブル | Hiew |

### 2.14 自動化・拡張

| 機能 | 代表例 |
| --- | --- |
| スクリプト言語 (C 風, TypeScript, Python / Lua)、デバッガ | 010, Neo, Synalyze It! |
| マクロの記録 | Neo |
| プラグイン API (DLL)、外部からの自動操作 (COM) | HxD, Neo, WinHex, ImHex |
| コマンドライン (オフセットを指定して開く、スクリプトの実行、比較、ハッシュ計算、ヘッダなし実行) | 010, ImHex, WinHex |

### 2.15 ツール類

| 機能 | 代表例 |
| --- | --- |
| 電卓、基数変換 | 010, Hex Workshop, ImHex |
| ファイルの分割・結合・インターリーブ | WinHex, HxD |
| 安全な削除 (シュレッダー) | HxD, ImHex |
| 印刷 (プレビュー、ヘッダ・フッタ) | 010, Hex Workshop, HxD |
| 進捗表示 (残り時間) とキャンセル | HxD |
| ワークスペース (開いているファイルとレイアウトの保存) | 010, ImHex |
| Explorer の右クリックメニューに追加 | HxD, 010 |
| シンボル名のデマングル | ImHex |

### 2.16 フォレンジック (WinHex 系)

WinHex / X-Ways に固有の、範囲の大きい機能群。

- 失われたパーティションのスキャン、シグネチャによるファイルの切り出し (カービング)
- ディスクのクローンとイメージ作成、セクタ範囲の保存と復元
- HPA / DCO の検出、SMART の確認
- OS 全体でのデバイス書き込み保護
- RAID の再構成
- ファイルシステムを理解した上での削除・墨塗り

## 3. 巨大ファイル対応の設計

### 3.1 各エディタの方式

| エディタ | 方式 |
| --- | --- |
| Hex Fiend | ファイルの範囲を指す「スライス」と、メモリ上のスライスを 10 分木の B+ 木で管理する。118 GB のファイルで動作確認されている。保存時は上書きされる範囲を計算し、まだ参照されている元データを先に退避してからその場で書き込む (追加のディスク容量が不要) |
| 010 Editor | ファイルを「ブロック」のリストとして持ち、各ブロックはディスク上かメモリ上を指す。大きな範囲のコピー・貼り付けもブロックの参照だけで済む |
| Hex Editor Neo | ほとんどの操作が定数時間。1 KB の挿入も 1 GB の挿入も同じ速さ。操作データは圧縮してディスクに退避する |
| WinHex | 変更を一時ファイルに書く「通常モード」と、元ファイルに直接書く「インプレースモード」を切り替えられる |
| wxHexEditor | 変更点のリストだけを持ち、一時ファイルを作らない |
| Okteta | ファイル全体をメモリに読み込むため約 1 GiB までしか扱えない (避けるべき例) |
| HxD | 内部構造は非公開 |

### 3.2 採用する設計 (案)

- **データソースの抽象化**: `IByteSource` (`long Length`、位置を指定した読み込み、挿入できるか・サイズ変更できるか・セクタサイズ) を用意し、ファイル、ディスク、ボリューム、プロセスメモリで実装する。
- **ピースツリー**: 元ファイルの範囲・追加バッファの範囲・塗りつぶし (同じ値の繰り返し) のいずれかを指すピースを、平衡木 (B+ 木) で管理する。各ノードに部分木の合計長を持たせ、オフセット検索・挿入・削除をピースの数の O(log n) にする。
  - 「50 GB を 00 で埋める」は 1 つの塗りつぶしピースで表し、実データは作らない。
  - 追加バッファは一定サイズを超えたら一時ファイルに逃がす。
- **Undo / Redo**: 木を不変 (永続データ構造) にし、Undo の状態は古いルートへの参照だけで持つ。
- **ディスク・プロセスメモリ (長さ固定)**: ピースツリーではなく、セクタ単位の変更ブロックのマップで持つ。
- **読み込み**: `RandomAccess.Read` (位置指定で、スレッドセーフ) を使う。`MemoryMappedFile` はメイン経路には使わない。
  - 理由: I/O エラーでプロセスが落ちる。デバイスをマップできない。マップ中はサイズを変えられない。
  - キャッシュは 64 KB 単位の LRU にする。描画が I/O 待ちで止まらないよう、読み込み中の箇所は仮表示しておく。
- **保存**:
  1. 長さが変わらない場合: 変更箇所だけを書き込む。
  2. 長さが変わる場合 (既定): 同じフォルダの一時ファイルに書き出し、`ReplaceFile` で置き換える。事前に空き容量を確認する。
  3. 長さが変わる場合 (オプション): Hex Fiend と同じくその場でずらしながら書く。追加の容量は要らないが、途中で落ちるとファイルが壊れる。
- **検索**: チャンク単位で読み、境界に `パターン長 - 1` バイトを重ねる。`Span.IndexOf` と `SearchValues` (SIMD 化されている) を使い、キャンセルと進捗表示に対応させる。
- **スクロール**: 100 GB / 16 バイトで約 67 億行になり、XAML のスクロール (float32 の座標) は使えない。表示は自前で描画するコントロールにし、行位置は `long` で正確に持つ。スクロールバーの値は行位置を縮尺して対応させる。
- **ディスクの書き込み**: 管理者権限が必要。マウント中のボリュームには `FSCTL_LOCK_VOLUME` (またはディスマウント) してから書く。読み書きはセクタ境界に揃える。管理者権限で動く補助プロセスを分けると、UI 側を昇格させずに済む。

## 4. 配布形態 (3 種類)

| 項目 | MSIX | インストーラ | ポータブル |
| --- | --- | --- | --- |
| ビルド設定 | パッケージあり | `WindowsPackageType=None` + 自己完結型 | 同左 |
| 設定の保存先 | `%LocalAppData%` (パッケージ内にリダイレクトされる) | `%LocalAppData%\HexEditorData` (Velopack のインストール先 `%LocalAppData%\HexEditor` とは分ける) | exe と同じフォルダ (目印ファイルがある場合) |
| ファイル関連付け | マニフェストで宣言 | レジストリ | アプリ内のオプションで登録・解除 |
| 右クリックメニュー | Windows 11 の新しいメニューに出せる (`IExplorerCommand`) | 従来のメニュー (「その他のオプション」の中) | 同左 (オプション) |
| 自動更新 | Store / App Installer | Velopack (差分更新) | 手動 |
| ディスク編集の管理者権限 | 制限あり (Store では受け付けられない可能性が高い) | 昇格した補助プロセス | 同左 |

推奨構成:

- csproj は 1 つにして、MSBuild のプロパティで 3 種類を作り分ける。
- インストーラは **Velopack** を使う (ユーザー単位のインストールで管理者権限が不要、差分更新、Setup.exe を生成)。MSI が必要な場合だけ WiX を検討する。WiX は v6 以降、収益があるとメンテナンス費用が必要になる。
- 起動処理を自分で書く (`DISABLE_XAML_GENERATED_MAIN`)。Velopack の初期化、単一インスタンス化 (`AppInstance`)、XAML の起動の順に行う。
- パッケージ版かどうかは実行時に判定し、設定の保存先と Explorer 連携を抽象化する。
- GitHub Actions で 3 種類 × x64 / ARM64 をまとめてビルドする。

## 5. UI の表示言語

| エディタ | 言語数 | 日本語 | 翻訳の方法 | アプリ内で切り替え |
| --- | --- | --- | --- | --- |
| HxD | 18 | ○ | ボランティア翻訳、全言語を 1 つのセットアップに同梱 | OS の言語で決まる (切り替えは未確認) |
| Hex Editor Neo | 12 | ○ | 言語パックをダウンロード。GitHub で翻訳を受け付け、アプリ内で文字列を直接編集するモードもある | ○ |
| UltraEdit | 10 | ○ | ベンダー翻訳 | ○ (再起動が必要) |
| GHex | 約 65 | ○ (100%) | GNOME の翻訳チーム (gettext) | × (OS の言語) |
| VS Code Hex Editor | 14 | ○ | Microsoft の翻訳 | VS Code の表示言語に従う |
| ImHex | 14 | △ (約 37%) | 言語ごとの JSON を Pull Request で受け付け | ○ |
| Okteta | 58 | △ (50〜80%) | KDE の翻訳チーム (gettext) | ○ |
| wxHexEditor | 16 | ○ (完成度は未確認) | gettext | 未確認 |
| Frhed | 14 | ○ (完成度は未確認) | gettext | 未確認 |
| WinHex | 9 | △ (一般には提供されていない) | 個人・企業による翻訳 | ○ |
| Synalyze It! | 2 (英・独) | × | ベンダー翻訳 | OS の言語 |
| 010 Editor, Hex Workshop, Hex Fiend, Hexinator, Hiew | 1 (英語) | × | なし | なし |

WinUI 3 での実装方法:

- 文字列は `Strings/<言語タグ>/Resources.resw` に置き、XAML は `x:Uid`、コードは `ResourceLoader.GetString` で参照する。
- アプリ内での切り替えは `Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride` を起動時に設定する。`Windows.Globalization` 版は非パッケージのアプリで落ちることがあるので使わない。
- コミュニティ翻訳には Crowdin か Weblate を使う。どちらも `.resw` に対応し、OSS 向けの無料プランがある。

出典: <https://mh-nexus.de/en/downloads.php?product=HxD20> , <https://docs.hhdsoftware.com/hex/customization/user-interface/languages.html> , <https://www.ultraedit.com/blog/ultraedit-2025-1-release-blog/> , <https://l10n.gnome.org/module/ghex/> , <https://github.com/WerWolv/ImHex/tree/master/plugins/builtin/romfs/lang> , <https://learn.microsoft.com/windows/apps/windows-app-sdk/mrtcore/mrtcore-overview>

## 6. 決めておくべきこと

1. **機能の優先順位**: 全部入りを目指すと範囲が非常に大きい (特に、テンプレート言語、スクリプト、逆アセンブル、フォレンジック系)。どの順で実装するか段階を決める必要がある。
2. **テンプレート言語**: 独自言語を作るか、既存のもの (Kaitai Struct、ImHex パターン言語の互換、010 の互換) を取り込むか。
3. **スクリプト言語**: C# スクリプト (Roslyn) か JavaScript か、など。
4. **フォレンジック機能の範囲**: ファイルシステム解析やカービングまで含めるか。
5. **MSIX 版でのディスク編集**: Store 配布をするかどうかで扱いが変わる。

## 出典 (主要なもの)

- 010 Editor: <https://www.sweetscape.com/010editor/features.html> , <https://sweetscape.com/010editor/manual/DataEngine.htm>
- WinHex: <https://www.x-ways.net/winhex/manual.pdf>
- Hex Workshop: <http://www.hexworkshop.com/features.html>
- Hex Editor Neo: <https://hhdsoftware.com/hex-editor/compare-features> , <https://hhdsoftwaredocs.online/hex/>
- UltraEdit: <https://wiki.ultraedit.com/Hex_edit>
- HxD: <https://mh-nexus.de/en/hxd/>
- ImHex: <https://github.com/WerWolv/ImHex> , <https://docs.werwolv.net/pattern-language>
- Hex Fiend: <https://github.com/HexFiend/HexFiend>
- wxHexEditor: <https://github.com/EUA/wxHexEditor>
- VS Code Hex Editor: <https://github.com/microsoft/vscode-hexeditor>
- .NET RandomAccess: <https://learn.microsoft.com/dotnet/api/system.io.randomaccess>
- ReplaceFile: <https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-replacefilew>
- FSCTL_LOCK_VOLUME: <https://learn.microsoft.com/windows/win32/api/winioctl/ni-winioctl-fsctl_lock_volume>
- 非パッケージの WinUI アプリ: <https://learn.microsoft.com/windows/apps/package-and-deploy/unpackage-winui-app>
- 単一インスタンス: <https://learn.microsoft.com/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance>
- Velopack: <https://docs.velopack.io/packaging/overview>
