namespace HexEditor.Sdk.Plugins;

/// <summary>
/// The plugin SDK version (AUTO-29). The application loads plugins built with the same major version and the same or a lower minor version.
/// <ja>プラグイン SDK のバージョン (AUTO-29)。アプリは、同じメジャーバージョンで、マイナーバージョンが同じか小さい SDK でビルドされたプラグインを読み込む。</ja>
/// </summary>
public static class SdkInfo
{
    /// <summary>The SDK version (<c>major.minor</c>). <ja>SDK のバージョン (<c>メジャー.マイナー</c>)。</ja></summary>
    public const string Version = "1.0";
}

/// <summary>
/// The entry point of a plugin (AUTO-30). The application creates the one public type that implements this interface and calls
/// <see cref="Initialize"/>, where the plugin registers its extensions.
/// <ja>プラグインの入口 (AUTO-30)。アプリはこのインターフェイスを実装した公開の型を 1 つ作り、<see cref="Initialize"/> を呼ぶ。プラグインはここで拡張ポイントを登録する。</ja>
/// </summary>
public interface IPlugin
{
    /// <summary>Registers the extensions of the plugin. <ja>拡張ポイントを登録する。</ja></summary>
    void Initialize(IPluginContext context);
}

/// <summary>What the application gives to a plugin (AUTO-30, AUTO-31). <ja>アプリがプラグインに渡すもの (AUTO-30、AUTO-31)。</ja></summary>
public interface IPluginContext
{
    /// <summary>The plugin ID from the manifest. <ja>マニフェストのプラグイン ID。</ja></summary>
    string Id { get; }

    /// <summary>The display name in the UI language. <ja>表示言語での表示名。</ja></summary>
    string Name { get; }

    /// <summary>The plugin version. <ja>プラグインのバージョン。</ja></summary>
    string Version { get; }

    /// <summary>The folder of the plugin (read-only for the plugin). <ja>プラグインのフォルダ (プラグインからは読み取りだけにする)。</ja></summary>
    string Folder { get; }

    /// <summary>The SDK version of the application. <ja>アプリの SDK のバージョン。</ja></summary>
    string SdkVersion { get; }

    /// <summary>True when the application shows a window (false in headless runs). Register panels only then. <ja>ウィンドウのあるアプリで動いているか (ヘッドレス実行では false)。パネルはこのときだけ登録する。</ja></summary>
    bool HasUi { get; }

    /// <summary>Writes to the output panel channel "Plugin: name". <ja>出力パネルの「プラグイン: 名前」に書く。</ja></summary>
    IPluginLog Log { get; }

    /// <summary>The plugin's own translated strings (AUTO-31 spec 6). <ja>プラグイン自身の翻訳した文字列 (AUTO-31 の仕様 6)。</ja></summary>
    IPluginStrings Strings { get; }

    /// <summary>
    /// Registers an extension. The ID must be unique (<c>&lt;plugin ID&gt;.&lt;name&gt;</c>); a duplicate is disabled with a warning.
    /// <ja>拡張ポイントを登録する。ID (<c>&lt;プラグイン ID&gt;.&lt;名前&gt;</c>) は一意にする。重複したものは警告を出して無効にする。</ja>
    /// </summary>
    void Register(IExtension extension);
}

/// <summary>Output of a plugin. <ja>プラグインの出力。</ja></summary>
public interface IPluginLog
{
    /// <summary>Writes an information line. <ja>情報の行を書く。</ja></summary>
    void Info(string text);

    /// <summary>Writes a warning line. <ja>警告の行を書く。</ja></summary>
    void Warning(string text);

    /// <summary>Writes an error line. <ja>エラーの行を書く。</ja></summary>
    void Error(string text);
}

/// <summary>
/// Translated strings of a plugin, read from <c>Strings/&lt;language&gt;/Resources.resw</c> in the plugin folder (the same format as the application).
/// <ja>プラグインの翻訳した文字列。プラグインのフォルダの <c>Strings/&lt;言語&gt;/Resources.resw</c> (アプリと同じ形式) から読む。</ja>
/// </summary>
public interface IPluginStrings
{
    /// <summary>The UI language tag (for example <c>ja</c>). <ja>表示言語のタグ (例: <c>ja</c>)。</ja></summary>
    string Language { get; }

    /// <summary>The string for <paramref name="key"/> (English, then the key itself, when not translated). <ja><paramref name="key"/> の文字列。訳がなければ英語、それもなければキー。</ja></summary>
    string Get(string key);

    /// <summary>Formats the string for <paramref name="key"/>. <ja><paramref name="key"/> の文字列に値を埋め込む。</ja></summary>
    string Format(string key, params object?[] args);
}

/// <summary>Thrown by a plugin to report a failure in its own words. <ja>プラグインが失敗の理由を伝えるための例外。</ja></summary>
public sealed class PluginException(string message, Exception? inner = null) : Exception(message, inner);
