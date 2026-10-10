namespace HexEditor.App.Controls;

/// <summary>ほかの機能 (統計: ANA-10 の仕様 8、ANA-14 の仕様 4、ANA-15 の仕様 2) が検索バーに検索語を入れる口。</summary>
public sealed partial class FindBar
{
    /// <summary>種類を Hex にして、検索欄にバイト列を入れる (開いてから呼ぶ)。</summary>
    public void SetHexQuery(byte[] bytes)
    {
        _kindChosen = true;
        KindChoice.SelectedIndex = 0;
        SetQueryText(ToHex(bytes));
        Validate();
    }
}
