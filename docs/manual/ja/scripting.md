# スクリプト

> TODO: JavaScript と C# のスクリプトで HexEditor を自動化する方法を書く。

## JavaScript と C# {#languages}

> TODO

## スクリプトを実行する {#run-a-script}

> TODO

## スクリプトエディタ {#script-editor}

> TODO

## スクリプトの API {#script-api}

> TODO

## 信頼と許可 {#permissions}

> TODO

## スクリプトコンソール {#console}

> TODO

## イベントで実行するスクリプト {#event-scripts}

> TODO

## C# スクリプト コンポーネントの追加 {#csharp-component}

> TODO

## JavaScript のデバッグ {#debugging-javascript}

> TODO

## C# スクリプトのデバッグ {#debugging-csharp-scripts}

C# スクリプトは、アプリ内でステップ実行できません。C# スクリプトのエディタでは、<kbd>F9</kbd>、<kbd>F10</kbd>、<kbd>F11</kbd>、<kbd>Shift</kbd>+<kbd>F11</kbd> は何もしません。<kbd>F5</kbd> で実行し、<kbd>Shift</kbd>+<kbd>F5</kbd> で止めます。

C# スクリプトをデバッグするには、外部のデバッガを HexEditor にアタッチします。

1. スクリプトをスクリプトエディタで開きます。
2. ツールバーの **デバッグ実行** を選びます。HexEditor はデバッグ情報付きでスクリプトをコンパイルし、プロセス ID を表示します。
3. Visual Studio または Visual Studio Code (C# 拡張) で、そのプロセス ID のプロセスにアタッチします。
4. `.csx` ファイルにブレークポイントを置きます。決まった場所で止めるには、スクリプトの中で `System.Diagnostics.Debugger.Break()` を呼びます。
5. デバッガがアタッチされると、スクリプトの実行が始まります。デバッガなしで始めるには、**アタッチせずに実行** を選びます。

`Console.WriteLine` と `Ui.Output` の出力は、出力パネルに出ます。例外のスタックトレースには、`.csx` ファイルの行番号が出ます。
