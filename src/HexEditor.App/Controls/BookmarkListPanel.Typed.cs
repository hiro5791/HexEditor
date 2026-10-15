using HexEditor.App.ViewModels;
using HexEditor.Core.Bookmarks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>ブックマーク一覧の値の列の書き換え (型付きブックマーク INSP-29 の仕様 4。規則は INSP-17)。</summary>
public sealed partial class BookmarkListPanel
{
    /// <summary>
    /// 値を書き込む (一覧の持ち主が付ける)。書き込めたら null、書き込めなければ理由 (入力欄のままにする)。
    /// </summary>
    public Func<Bookmark, string, string?>? CommitValue { get; set; }

    /// <summary>値の入力欄の文字列 (今の値) を作る (一覧の持ち主が付ける)。</summary>
    public Func<Bookmark, string>? EditValueOf { get; set; }

    private BookmarkRowViewModel? _editingValueRow;

    private void Value_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BookmarkRowViewModel row })
        {
            e.Handled = BeginValueEdit(row);
        }
    }

    /// <summary>値の列を入力欄にする (型のあるブックマークだけ)。</summary>
    internal bool BeginValueEdit(BookmarkRowViewModel row)
    {
        if (row.Bookmark.TypeId is null || EditValueOf is null)
        {
            return false;
        }

        if (_editingValueRow is { } other && other != row)
        {
            other.IsEditingValue = false;
        }

        _editingValueRow = row;
        row.EditValueText = EditValueOf(row.Bookmark);
        row.IsEditingValue = true;
        return true;
    }

    /// <summary>入力を確定する。書き込めなければ false (入力欄のまま)。</summary>
    internal bool CommitValueEdit(BookmarkRowViewModel row, string text)
    {
        if (!row.IsEditingValue || CommitValue is null)
        {
            return false;
        }

        row.EditValueText = text;
        if (CommitValue(row.Bookmark, text) is { } error)
        {
            ValueError = error;
            return false;
        }

        ValueError = null;
        row.IsEditingValue = false;
        row.Update();
        return true;
    }

    /// <summary>最後の書き換えの誤り (テスト用の状態の表示)。</summary>
    internal string? ValueError { get; private set; }

    private void ValueEditor_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            AutomationProperties.SetName(box, Services.Loc.Get("Bookmarks_ValueEdit"));
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        }
    }

    private void ValueEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: BookmarkRowViewModel row } box)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                if (!CommitValueEdit(row, box.Text))
                {
                    ToolTipService.SetToolTip(box, ValueError);
                }

                break;
            case VirtualKey.Escape:
                e.Handled = true;
                row.IsEditingValue = false;
                break;
        }
    }

    private void ValueEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        // フォーカスが外れたら取り消す (INSP-17 の仕様 1)。
        if (sender is TextBox { DataContext: BookmarkRowViewModel { IsEditingValue: true } row })
        {
            row.IsEditingValue = false;
        }
    }
}
