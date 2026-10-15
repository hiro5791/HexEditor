namespace HexEditor.Sdk;

/// <summary>API version information. <ja>API のバージョンの情報。</ja></summary>
public static class ApiInfo
{
    /// <summary>
    /// The automation API version (<c>major.minor</c>). Within a major version the API stays backward compatible.
    /// <ja>自動化 API のバージョン (<c>メジャー.マイナー</c>)。同じメジャーバージョンの中では後方互換を保つ。</ja>
    /// </summary>
    public const string Version = "1.0";
}

/// <summary>
/// The globals of a C# script (AUTO-03). The names match the JavaScript globals.
/// <ja>C# スクリプトのグローバル変数 (AUTO-03)。名前は JavaScript のグローバルと同じ。</ja>
/// </summary>
public class ScriptGlobals
{
    /// <summary>Creates the globals. <ja>グローバル変数を作る。</ja></summary>
    public ScriptGlobals(IApp app, CancellationToken ct)
    {
        App = app;
        Doc = app.ActiveDocument;
        Ct = ct;
    }

    /// <summary>The application. <ja>アプリ。</ja></summary>
    public IApp App { get; }

    /// <summary>The active document when the script started, or null. <ja>実行開始時のアクティブなドキュメント。なければ null。</ja></summary>
    public IDocument? Doc { get; }

    /// <summary>Dialogs, progress and output. <ja>ダイアログ・進捗・出力。</ja></summary>
    public IUi Ui => App.Ui;

    /// <summary>Hashes. <ja>ハッシュ。</ja></summary>
    public IHash Hash => App.Hash;

    /// <summary>The clipboard. <ja>クリップボード。</ja></summary>
    public IClipboard Clipboard => App.Clipboard;

    /// <summary>Application settings. <ja>アプリの設定。</ja></summary>
    public ISettings Settings => App.Settings;

    /// <summary>The running script. <ja>実行中のスクリプト。</ja></summary>
    public IScript Script => App.Script;

    /// <summary>The data inspector. <ja>データインスペクタ。</ja></summary>
    public IInspector Inspector => App.Inspector;

    /// <summary>Text encoding conversion. <ja>文字コードの変換。</ja></summary>
    public IText Text => App.Text;

    /// <summary>Structure templates. <ja>構造体テンプレート。</ja></summary>
    public ITemplates Templates => App.Templates;

    /// <summary>Canceled when the user stops the script. Check it in long loops. <ja>停止ボタンでキャンセル状態になる。長いループで確認する。</ja></summary>
    public CancellationToken Ct { get; }
}
