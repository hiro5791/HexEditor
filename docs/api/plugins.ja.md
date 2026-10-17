# HexEditor のプラグイン SDK

C# で HexEditor のプラグインを作る方法 (機能仕様書: 08-automation.md の AUTO-29〜AUTO-35)。インターフェイスの説明は
`HexEditor.Sdk` パッケージの XML ドキュメントコメントにある (IntelliSense に英語と日本語の説明が出る)。スクリプトが使う自動化 API は
[reference.ja.md](reference.ja.md) を参照。

## パッケージ

| パッケージ | 内容 |
| --- | --- |
| `HexEditor.Sdk` | `IPlugin`、`IPluginContext`、拡張ポイントのインターフェイス (`HexEditor.Sdk.Plugins`)、自動化 API の型。WinUI に依存しない |
| `HexEditor.Sdk.UI` | `IPanelProvider`、`IPluginSettingsProvider` (WinUI の `UIElement`) |
| `HexEditor.Sdk.Templates` | `dotnet new hexeditor-plugin`: コマンドを 1 つ登録するプラグイン、マニフェスト、テストのプロジェクト、起動設定 |
| `HexEditor.Sdk.Testing` | 単体テスト用の `FakePluginContext`、`FakePluginDocument`、`FakeCommandContext` |

## はじめかた

```text
dotnet new install HexEditor.Sdk.Templates
dotnet new hexeditor-plugin -n MyPlugin
cd MyPlugin
dotnet build
dotnet test
```

Visual Studio で F5 を押すと、起動設定が HexEditor を `--plugin-dev <ビルドの出力先>` 付きで起動し、ビルドの出力から直接プラグインを
読み込む (プラグインマネージャに「開発中」と出る。フォルダの信頼の確認は 1 回だけ)。HexEditor が `%LocalAppData%\HexEditor\current\`
にない場合は、MSBuild のプロパティ `HexEditorExe` で場所を指定する。

## マニフェスト (`plugin.json`)

```json
{
  "id": "example.crc-tools",
  "name": "CRC Tools",
  "name.ja": "CRC ツール",
  "version": "1.2.0",
  "publisher": "Example",
  "sdk": "1.0",
  "minAppVersion": "1.0.0",
  "entry": "Example.CrcTools.dll",
  "permissions": ["file-read"],
  "description": "Additional CRC algorithms."
}
```

- `id`: 英小文字・数字・`-` の部分を `.` でつないだもの (100 文字まで)。
- `sdk`: ビルドに使った SDK のバージョン (`メジャー.マイナー`)。HexEditor は、メジャーバージョンが同じでマイナーバージョンが同じか小さい
  プラグインを読み込む。参照している `HexEditor.Sdk` のアセンブリのバージョンも確かめる。
- `entry`: プラグインのフォルダ直下のアセンブリのファイル名。`IPlugin` を実装した公開のクラスはちょうど 1 つにする。
- `permissions`: 確認ダイアログに出す。プラグインは完全信頼のコードで、利用者にできることは何でもできる。

## パッケージと追加

`.hexplugin` はプラグインのフォルダを zip にしたもの (マニフェストは直下か、1 つだけの最上位のフォルダの直下)。ツール > プラグイン >
プラグインの管理 で「追加...」かドラッグ & ドロップで追加する。HexEditor はパッケージ (パス、大きさ、マニフェスト、署名) を検証し、
信頼の確認を出してから `<設定フォルダ>\plugins\<id>\<version>\` に展開する。入っているものより新しいバージョンは更新になる。
有効 / 無効、削除、更新は再起動後に反映する (プラグインはアンロードできない)。

`HexEditor.Sdk.dll`、`HexEditor.Sdk.UI.dll` は同梱しない (アプリのものを必ず使う)。それ以外の依存ライブラリは、プラグインごとの
`AssemblyLoadContext` でプラグインのフォルダから読み込むため、2 つのプラグインが同じライブラリの別のバージョンを使える。

## 拡張ポイント

`Initialize` の中で `context.Register(...)` で登録する。拡張ポイントの ID はプラグイン ID で始める (`<プラグイン ID>.<名前>`)。
重複した ID は警告を出して無効にする。

| インターフェイス | 表示される場所 | 注意 |
| --- | --- | --- |
| `IInspectorType` | データインスペクタ (「プラグイン」のグループ)、`inspector.types` | UI スレッドで呼ばれる。10 ms 以内に終える。`Encode` を実装すると行を書き換えられる |
| `IHashAlgorithm` | ハッシュパネル | データはチャンクで渡す (`IHashComputation.Append`) |
| `IImportFormat` | インポートのダイアログ、形式の判定 | 入力はチャンク、出力は `IByteSink` |
| `IExportFormat` | エクスポートのダイアログ、形式を選択してコピー (テキストの形式) | 入力はチャンク |
| `IByteSourceProvider` | ファイル > プラグインのデータソースを開く | 読み取り専用か・長さを変えられるかを宣言する。読み取りは作業用のスレッドから呼ばれる |
| `IDataTransform` | データ > プラグインのデータ演算 | チャンクごとにその場で変換する。長さは変えない |
| `ICommand` | コマンドパレット、ショートカット | `CanExecute` は UI スレッド (10 ms) |
| `IMenuContribution` | ツール > プラグイン、右クリックメニュー > プラグイン | 同じプラグインのコマンドを指す |
| `IPanelProvider` (SDK.UI) | 表示 > パネルの表示切り替え | `context.HasUi` のときだけ登録する |
| `IPluginSettingsProvider` (SDK.UI) | プラグインマネージャ > プラグインの設定 | |
| `ITemplateFunction` | テンプレート言語 (`extern fn`) | |
| `IFileTypeDetector` | ファイル形式の判定、テンプレートの自動適用 | 先頭 64 KB を受け取る |
| `IColorRuleProvider` | 色付けルール | 規則はデータで渡し、HexEditor が評価する |
| `IScriptApiProvider` | `app.plugins.get("id").api` | オブジェクトの公開のメンバーがスクリプトから見える |
| `ITransformView`、`IDisassembler` | (登録はできる。変換ビューと逆アセンブラはフェーズ 4 で表示する) | |

すべての拡張ポイントに共通の規則:

- 呼び出しはすべて見張られる。例外は出力パネル (「プラグイン: 名前」) に記録する。1 分間に 10 回の例外で、次の起動までプラグインを
  止め、無効にできる InfoBar を出す。
- UI スレッドの呼び出しが 100 ms を超えると警告し、続けて超えるとプラグインを止める。
- 全データをメモリに持たない。データ量に比例する処理は `ReadOnlySpan<byte>` のチャンク、`CancellationToken`、`PluginOperation` の
  進捗を受け取る。
- 文字列はプラグインが自分で翻訳する。プラグインのフォルダに `Strings/<言語>/Resources.resw` (アプリと同じ形式) を置き、
  `context.Strings` で読む。

## 署名

すべての `.dll` に Authenticode で署名する。プラグインマネージャと確認ダイアログに発行者を出す。署名なしのプラグインは読み込める
(確認で警告を強める)。署名が無効なもの (改ざん、タイムスタンプのない期限切れの証明書、失効) は読み込まない。設定「署名されたプラグイン
だけを読み込む」と組織の設定 `scripts.policy = signedPluginsOnly` は署名なしを拒否する。利用者は「この発行者を常に信頼する」を選べる。

## 互換性

SDK はセマンティックバージョニングに従う。同じメジャーバージョンの中では、古いマイナーバージョンでビルドしたプラグインが動き続ける。
メジャーバージョンを上げるのは 1 年に 1 回以内で、その 1 つ前のメジャーバージョンで廃止する API に `[Obsolete]` を付けておく。
