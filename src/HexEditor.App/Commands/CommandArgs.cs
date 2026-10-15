using System.Globalization;

namespace HexEditor.App.Commands;

/// <summary>
/// コマンドの引数 (08 の AUTO-06 の仕様 2: スクリプトとマクロからの実行)。スクリプトの <c>app.commands.execute(id, args)</c> は、実行中の流れ
/// (非同期の呼び出しをまたいで) に引数を置く。ダイアログを使うコマンド (塗りつぶし・範囲を選択など) は、引数があればダイアログを出さずに
/// その値で実行する。ダイアログで確定した値は <see cref="Report"/> で記録の流れに渡す (マクロの記録。AUTO-27 の仕様 2)。
/// </summary>
internal static class CommandArgs
{
    private static readonly AsyncLocal<Scope?> CurrentScope = new();

    /// <summary>スクリプト・マクロからの実行か (引数がなくても true)。</summary>
    public static bool IsScripted => CurrentScope.Value is not null;

    /// <summary>引数の辞書 (スクリプトからの実行でなければ null)。</summary>
    public static IReadOnlyDictionary<string, object?>? Values => CurrentScope.Value?.Values;

    /// <summary><c>args.argument</c> (引数を尋ねるコマンドの入力)。</summary>
    public static string? Argument => Get<string>("argument");

    /// <summary>引数の値を読む (文字列・数値・真偽値を型に直す)。なければ既定値。</summary>
    public static T? Get<T>(string name)
    {
        if (Values is not { } values || !values.TryGetValue(name, out object? value) || value is null)
        {
            return default;
        }

        try
        {
            return value is T typed ? typed : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return default;
        }
    }

    public static bool Has(string name) => Values?.ContainsKey(name) == true;

    /// <summary>スクリプトからの実行を始める。戻り値を Dispose すると終わる。</summary>
    public static IDisposable Begin(IReadOnlyDictionary<string, object?> values)
    {
        Scope? previous = CurrentScope.Value;
        CurrentScope.Value = new Scope(values);
        return new Restore(previous);
    }

    private sealed record Scope(IReadOnlyDictionary<string, object?> Values);

    private sealed class Restore(Scope? previous) : IDisposable
    {
        public void Dispose() => CurrentScope.Value = previous;
    }
}
