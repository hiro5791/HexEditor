namespace HexEditor.Core.Scripting;

/// <summary>
/// スクリプトに見せるメッセージ (API の例外・実行結果) の言語。日本語の表示では日本語、それ以外では英語にする。
/// 言語は実行ごとに決め (<see cref="Current"/>。非同期の流れで受け継ぐ)、決まっていないスレッドでは <see cref="Default"/> を使う
/// (App は起動時に表示言語を入れる)。
/// </summary>
public static class Msg
{
    private static readonly AsyncLocal<string?> CurrentLanguage = new();

    /// <summary>実行の外 (UI スレッドなど) で使う言語。</summary>
    public static string Default { get; set; } = "en";

    /// <summary>今の実行の言語。</summary>
    public static string? Current
    {
        get => CurrentLanguage.Value;
        set => CurrentLanguage.Value = value;
    }

    public static bool Japanese => (Current ?? Default).StartsWith("ja", StringComparison.OrdinalIgnoreCase);

    /// <summary>言語に合う方を返す。</summary>
    public static string T(string ja, string en) => Japanese ? ja : en;
}
