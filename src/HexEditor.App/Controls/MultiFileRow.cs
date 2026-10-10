using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.Core.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>
/// 複数ファイル検索の結果の 1 行 (FIND-30 の仕様 5)。ファイルの行 (パス・件数・サイズ・更新日時) と、その下の一致の行 (オフセット・長さ・
/// 一致データ)。置換モードではチェックボックスで置換するかを選ぶ (FIND-31 の仕様 2)。ファイルの行のチェックは一致の行をまとめて切り替える。
/// </summary>
public sealed partial class MultiFileRow : ObservableObject
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly FontFamily Normal = new("Segoe UI Variable, Segoe UI");
    private bool _syncing;

    private MultiFileRow(FileSearchResult file, MultiFileRow? parent, SearchMatch? match, string title)
    {
        File = file;
        Parent = parent;
        Match = match;
        Title = title;
    }

    /// <summary>行のファイルの結果。</summary>
    public FileSearchResult File { get; }

    /// <summary>一致の行ならファイルの行。ファイルの行なら null。</summary>
    public MultiFileRow? Parent { get; }

    /// <summary>一致の行なら一致 (ファイルの行なら null)。</summary>
    public SearchMatch? Match { get; }

    public bool IsFile => Match is null;

    /// <summary>ファイルの行の、一致の行。</summary>
    public List<MultiFileRow> Children { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>置換するか (ファイルの行は一致の行のまとめ。一部だけなら null)。</summary>
    [ObservableProperty]
    public partial bool? Checked { get; set; } = true;

    [ObservableProperty]
    public partial Visibility CheckVisibility { get; set; } = Visibility.Collapsed;

    public Thickness Indent => IsFile ? new Thickness(0) : new Thickness(24, 0, 0, 0);

    public FontFamily Font => IsFile ? Normal : Mono;

    public static MultiFileRow ForFile(FileSearchResult file, string title) => new(file, null, null, title);

    public static MultiFileRow ForMatch(MultiFileRow parent, SearchMatch match, string title)
    {
        var row = new MultiFileRow(parent.File, parent, match, title);
        parent.Children.Add(row);
        return row;
    }

    /// <summary>置換する一致 (チェックした一致の行)。</summary>
    public IReadOnlyList<SearchMatch> CheckedMatches => [.. Children.Where(c => c.Checked == true).Select(c => c.Match!.Value)];

    partial void OnCheckedChanged(bool? value)
    {
        if (_syncing)
        {
            return;
        }

        if (IsFile && value is bool all)
        {
            foreach (MultiFileRow child in Children)
            {
                child._syncing = true;
                child.Checked = all;
                child._syncing = false;
            }
        }
        else if (Parent is { } parent)
        {
            int on = parent.Children.Count(c => c.Checked == true);
            parent._syncing = true;
            parent.Checked = on == parent.Children.Count ? true : on == 0 ? false : null;
            parent._syncing = false;
        }
    }
}
