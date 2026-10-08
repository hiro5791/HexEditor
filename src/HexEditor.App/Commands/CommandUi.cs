using Microsoft.UI.Xaml;

namespace HexEditor.App.Commands;

/// <summary>
/// メニュー項目・ボタンが参照するコマンド ID (UI-16 の仕様 2)。XAML では <c>cmd:CommandUi.Id="file.open"</c> と書く。
/// メインメニューの項目は <see cref="MenuBinder"/> が、表示名以外 (ショートカットの表示・有効状態・クリック) をコマンドから取る。
/// </summary>
public static class CommandUi
{
    public static readonly DependencyProperty IdProperty =
        DependencyProperty.RegisterAttached("Id", typeof(string), typeof(CommandUi), new PropertyMetadata(null));

    public static string? GetId(DependencyObject element) => (string?)element.GetValue(IdProperty);

    public static void SetId(DependencyObject element, string? value) => element.SetValue(IdProperty, value);
}
