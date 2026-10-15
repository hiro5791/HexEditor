namespace HexEditor.Platform;

/// <summary>
/// <c>hexed open --wait</c> (08 の AUTO-37) の合図。<c>hexed</c> が名前付きのイベントを作り、GUI の実行ファイルに <c>--wait-id &lt;ID&gt;</c> を渡す。
/// GUI は、その起動で開いたドキュメントがすべて閉じられたとき (またはアプリが終了するとき) にイベントを Set する。
/// イベントは利用者のセッションの中だけ (<c>Local\</c>)。
/// </summary>
public static class WaitSignal
{
    /// <summary>オプション名。</summary>
    public const string Option = "--wait-id";

    /// <summary>イベントの名前。</summary>
    public static string EventName(string id) => @"Local\HexEditor.Wait." + id;

    /// <summary>合図の ID として使える文字列か (英数字とハイフンだけ。名前付きオブジェクトの名前を壊されないため)。</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>新しい ID。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
}
