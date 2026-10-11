# CLAUDE.md

このファイルは、このリポジトリで作業する Claude Code 向けのガイドです。

## プロジェクト概要

WinUI 3 (Windows App SDK) と C# で作る、Windows 向けのバイナリエディタ (Hex Editor)。

現在は初期段階で、まだコードはない。以下の構成・方針は計画であり、実装が進んだら実態に合わせてこのファイルを更新すること。

## 技術スタック

- 言語: C# (最新の安定版)、`Nullable` 有効
- UI: WinUI 3 / Windows App SDK、XAML
- ランタイム: .NET 8 以降
- MVVM: CommunityToolkit.Mvvm (`[ObservableProperty]`、`[RelayCommand]`)
- テスト: xUnit (コアロジックが対象)

## 想定するプロジェクト構成

```text
HexEditor.sln
src/
  HexEditor.Core/   # UI に依存しないロジック (バッファ、編集、検索、Undo/Redo)
  HexEditor.App/    # WinUI 3 アプリ (View、ViewModel、コントロール)
tests/
  HexEditor.Core.Tests/
```

- `HexEditor.Core` は WinUI を参照しない。ファイル操作や編集ロジックはすべてここに置き、単体テストを書く。
- `HexEditor.App` は Core を参照し、表示と入力処理に専念する。

## ビルド・実行

```powershell
dotnet build HexEditor.sln
dotnet test
```

アプリの起動やビルドエラーの対応には `winui:winui-dev-workflow` スキルを使う。

## 必須要件

- **ファイルサイズに上限を設けない。** 数百 GB のファイルや物理ディスクでも、開く・表示・編集・検索・保存ができること。メモリ使用量はファイルサイズに比例させない。
- **使いやすさを重視する。** 機能を増やすより、基本操作の速さと分かりやすさを優先する。守る原則は [docs/roadmap.md](docs/roadmap.md) の「使いやすさの原則」。
- **機能の優先順位は [docs/roadmap.md](docs/roadmap.md) に従う。** フォレンジック機能は最後。
- **機能の振る舞いは [docs/spec/](docs/spec/00-overview.md) の機能仕様書に従う。** 実装前に該当する仕様 (ENG-01 など) を読み、受け入れ基準をテストにする。仕様と違う実装が必要になったら、先に仕様書を直す。
- **テストは [docs/test/](docs/test/00-test-strategy.md) のテスト仕様書に従う。** 機能を実装するときは、その機能の「自動」のテストケースをすべて自動テストにして同じプルリクエストに含める。自動テストにはテストケース ID (`TC-ENG-01-01` など) を属性で付ける。
- **UI の表示言語は 23 言語。** 文字列はすべてリソース化し、英語と日本語を書く (詳細は [docs/spec/00-overview.md](docs/spec/00-overview.md) の 5 章)。
- **3 種類の配布形態を 1 つのコードベースから作る。**
  1. MSIX パッケージ
  2. ポータブル版 (インストール不要、zip で配布、自己完結型)
  3. 通常のインストーラ版
  - パッケージ版と非パッケージ版で挙動が変わる箇所 (設定の保存先、ファイル関連付けなど) は抽象化して分岐させる。

## 主な機能 (予定)

1. ファイルを開く / 保存する / 名前を付けて保存する
2. 16 進ダンプ表示 (オフセット | Hex | ASCII の 3 カラム)
3. Hex と ASCII のどちらからでも編集でき、上書きモードと挿入モードを切り替えられる
4. Undo / Redo
5. 選択、コピー、貼り付け (Hex 文字列とバイト列の両方)
6. 検索と置換 (バイト列、テキスト)
7. 指定オフセットへのジャンプ
8. 選択位置の値を解釈して表示するインスペクタ (int8〜int64、float、double、エンディアン切替)

## 設計方針

### 大きなファイルへの対応

- ファイル全体をメモリに読み込まない。`RandomAccess.Read` で必要な範囲だけ読む。`MemoryMappedFile` はメインの読み込み経路に使わない (I/O エラーでプロセスが落ちる、デバイスをマップできない)。詳細は [docs/research/feature-survey.md](docs/research/feature-survey.md) の 3 章。
- 編集内容は Piece Table などの差分構造で持ち、元ファイルは保存するまで変更しない。
- 表示は仮想化し、見えている行だけを描画する。全行分の UI 要素を作らない。
- 保存は一時ファイルに書き出してから置き換える (途中で失敗しても元ファイルを壊さない)。

### UI

- MVVM を守る。コードビハインドに置くのは View 固有の処理 (フォーカス、描画など) だけにする。
- バインディングは `{Binding}` ではなく `{x:Bind}` を使う。
- Light / Dark / ハイコントラストに対応する。色は直接書かず `ThemeResource` を使う。
- Hex 表示には等幅フォント (`Cascadia Mono` / `Consolas`) を使う。
- キーボードだけで操作できるようにする (矢印、PageUp/Down、Ctrl+Home/End、Tab で Hex と ASCII を切替)。
- XAML の設計・レビューには `winui:winui-design` スキルを使う。

### コード規約

- 標準の .NET 命名規約に従う (型・メソッドは PascalCase、private フィールドは `_camelCase`)。
- ファイル I/O は非同期 API (`async`/`await`) を使い、UI スレッドをブロックしない。
- オフセットやサイズは `long` で扱う (2GB を超えるファイルを想定)。
- コメントは日本語でよい。

## 作業時の注意

- 新しい機能はまず Core にロジックとテストを書き、そのあと UI につなぐ。
- コミット前に `winui:winui-code-review` スキルでのレビューを検討する。
- サブエージェントを使うときは、使用量を抑えるため、作業の難しさに合わせてモデルを指定する。
  - Haiku: ログの取得・状況の確認・読み取りだけの調査など、簡単な作業
  - Sonnet: 翻訳、マージの衝突の解消、CI で失敗したテストの修正 (待ち方・タイミングなど)、文書の修正などの定型的な作業
  - Opus (既定): 新機能の設計、仕様との照合、複数の領域にまたがる統合、安全に関わるコードなど、難しい作業だけ
  - 同時に動かすエージェントの数は少なめにする。
