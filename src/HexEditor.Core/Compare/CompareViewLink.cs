using HexEditor.Core.View;

namespace HexEditor.Core.Compare;

/// <summary>
/// 比較タブの左右の表示設定を共通にする (ANA-04 の仕様 2: 1 行のバイト数、グループ化、文字コードなどは左右で共通)。作ったときに右を左に
/// 合わせ、その後はどちらかの表示設定が変わるたびにもう一方へ写す。各側のデータに固有の項目 (基準のアドレス、行の開始のずらし、
/// レコードの開始位置、セクタサイズ) は写さない。
/// </summary>
public sealed class CompareViewLink : IDisposable
{
    private readonly EditorState _left;
    private readonly EditorState _right;
    private bool _copying;

    public CompareViewLink(EditorState left, EditorState right)
    {
        _left = left;
        _right = right;
        Copy(_left, _right);
        _left.ViewChanged += Left_ViewChanged;
        _right.ViewChanged += Right_ViewChanged;
    }

    private void Left_ViewChanged(object? sender, EventArgs e) => Copy(_left, _right);

    private void Right_ViewChanged(object? sender, EventArgs e) => Copy(_right, _left);

    /// <summary><paramref name="from"/> の共通の表示設定を <paramref name="to"/> に写す。</summary>
    private void Copy(EditorState from, EditorState to)
    {
        if (_copying)
        {
            return;
        }

        ViewSettings source = from.View;
        ViewSettings own = to.View;
        ViewSettings next = source with
        {
            BaseAddress = own.BaseAddress,
            RowShift = own.RowShift,
            RecordStart = own.RecordStart,
            SectorSize = own.SectorSize,
        };
        if (next == own)
        {
            return;
        }

        _copying = true;
        try
        {
            to.ApplyView(next);
        }
        finally
        {
            _copying = false;
        }
    }

    public void Dispose()
    {
        _left.ViewChanged -= Left_ViewChanged;
        _right.ViewChanged -= Right_ViewChanged;
    }
}
