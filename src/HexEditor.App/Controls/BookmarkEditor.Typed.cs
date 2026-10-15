using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Engine;
using HexEditor.Core.Inspector;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>
/// ブックマークの編集の「型」「エンディアン」「位置の式」の欄 (INSP-29 の「画面」)。型を付けると長さを型のサイズにし (可変長の型は解釈した長さ)、
/// 位置の式は入力と同時に設定して評価し直す (結果または理由を欄の下に出す)。
/// </summary>
public sealed partial class BookmarkEditor
{
    /// <summary>型の選択肢 (表示名, 型の ID)。先頭は「型なし」。</summary>
    private readonly List<(string Name, string? Id)> _types = [];

    /// <summary>位置の式が変わった (持ち主が式を評価し直す)。</summary>
    public event EventHandler<Bookmark>? ExpressionChanged;

    /// <summary>型が変わった (持ち主が一覧の値の列を出す)。</summary>
    public event EventHandler<Bookmark>? TypeChanged;

    private void LoadTyped()
    {
        if (_types.Count == 0)
        {
            _types.Add((Loc.Get("Bookmark_TypeNone"), null));
            foreach (InspectorType t in InspectorTypes.All)
            {
                _types.Add((InspectorViewModel.RowName(t.Id, false, Endianness.Little), t.Id));
            }

            foreach ((string name, _) in _types)
            {
                TypeChoice.Items.Add(name);
            }

            EndianChoice.Items.Add(Loc.Get("Bookmark_EndianDocument"));
            EndianChoice.Items.Add("LE");
            EndianChoice.Items.Add("BE");
            TypeChoice.Header = Loc.Get("Bookmark_TypeHeader");
            EndianChoice.Header = Loc.Get("Bookmark_EndianHeader");
            ExpressionBox.Header = Loc.Get("Bookmark_ExpressionHeader");
            ExpressionBox.PlaceholderText = Loc.Get("Bookmark_ExpressionPlaceholder");
            AutomationProperties.SetName(TypeChoice, Loc.Get("Bookmark_TypeHeader"));
            AutomationProperties.SetName(EndianChoice, Loc.Get("Bookmark_EndianHeader"));
            AutomationProperties.SetName(ExpressionBox, Loc.Get("Bookmark_ExpressionHeader"));
        }

        Bookmark b = _bookmark!;
        TypeChoice.SelectedIndex = Math.Max(0, _types.FindIndex(t => t.Id == b.TypeId));
        EndianChoice.SelectedIndex = b.TypeEndian switch { Endianness.Little => 1, Endianness.Big => 2, _ => 0 };
        ExpressionBox.Text = b.PositionExpression ?? string.Empty;
        UpdateExpressionInfo();
    }

    private void TypeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && TypeChoice.SelectedIndex >= 0)
        {
            ApplyType(_types[TypeChoice.SelectedIndex].Id);
        }
    }

    /// <summary>入力した文字列の型を選ぶ (型の ID か表示名。部分一致なら最初のもの)。</summary>
    private void TypeChoice_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        string text = args.Text.Trim();
        int index = _types.FindIndex(t => string.Equals(t.Id, text, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, text, StringComparison.CurrentCultureIgnoreCase));
        if (index < 0)
        {
            index = _types.FindIndex(t => t.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        }

        if (index >= 0)
        {
            args.Handled = true;
            TypeChoice.SelectedIndex = index;
            ApplyType(_types[index].Id);
        }
    }

    private void EndianChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && _bookmark is { } b && _bookmarks is not null && EndianChoice.SelectedIndex >= 0)
        {
            _bookmarks.SetType(b, b.TypeId, Endian, b.TypeId is null ? null : TypeLength(b.TypeId));
            TypeChanged?.Invoke(this, b);
        }
    }

    private Endianness? Endian => EndianChoice.SelectedIndex switch { 1 => Endianness.Little, 2 => Endianness.Big, _ => null };

    /// <summary>型を付ける・外す (テスト用の命令の通り道からも使う)。</summary>
    internal void ApplyType(string? typeId)
    {
        if (_bookmark is not { } b || _bookmarks is null)
        {
            return;
        }

        _bookmarks.SetType(b, typeId, Endian, typeId is null ? null : TypeLength(typeId));
        bool loading = _loading;
        _loading = true;
        LengthBox.Text = b.Length.ToString(CultureInfo.InvariantCulture);
        TypeChoice.SelectedIndex = Math.Max(0, _types.FindIndex(t => t.Id == typeId));
        _loading = loading;
        UpdateRange(apply: false);
        TypeChanged?.Invoke(this, b);
    }

    /// <summary>型のサイズ (可変長の型は開始位置から解釈した長さ。INSP-29 の仕様 1)。</summary>
    private long TypeLength(string typeId)
    {
        InspectorType type = InspectorTypes.Get(typeId);
        if (!type.Variable || _editor is null || _bookmark is null)
        {
            return type.Size;
        }

        DocumentSnapshot snapshot = _editor.Document.Current;
        int count = (int)Math.Clamp(snapshot.Length - _bookmark.Start, 0, InspectorDecoder.MaxReadLength);
        byte[] data = new byte[count];
        var states = new ByteState[count];
        count = count > 0 ? snapshot.ReadForDisplay(_bookmark.Start, data, states) : 0;
        InspectorValue value = InspectorDecoder.Decode(type, data.AsSpan(0, count), states.AsSpan(0, count), Endian ?? Endianness.Little, new InspectorOptions());
        return Math.Max(type.Size, value.ByteCount);
    }

    private void ExpressionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _bookmark is not { } b || _bookmarks is null)
        {
            return;
        }

        _bookmarks.SetPositionExpression(b, ExpressionBox.Text);
        ExpressionChanged?.Invoke(this, b);
        UpdateExpressionInfo();
    }

    /// <summary>位置の式の解釈結果か理由を出し、開始の欄を今の位置にする (式があるときは開始の欄は使わない)。</summary>
    internal void UpdateExpressionInfo()
    {
        if (_bookmark is not { } b || _bookmarks is null || _editor is null)
        {
            return;
        }

        bool hasExpression = b.PositionExpression is not null;
        StartBox.IsEnabled = !hasExpression;
        if (!hasExpression)
        {
            ExpressionInfo.Text = string.Empty;
            return;
        }

        long? value = BookmarkExpressions.Evaluate(_bookmarks, b, b.PositionExpression!, new EditorExpressionContext(_editor), _editor.Document.Length,
            out BookmarkExpressionError? error);
        ExpressionInfo.Foreground = (Brush)Application.Current.Resources[value is null ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
        ExpressionInfo.Text = value is { } v ? DialogParts.Interpretation(v) : BookmarkExpressionText.Describe(error);
        bool loading = _loading;
        _loading = true;
        StartBox.Text = "0x" + b.Start.ToString("X", CultureInfo.InvariantCulture);
        _loading = loading;
    }

    /// <summary>位置の式の欄に入れる (テスト用の命令の通り道。入力と同じ処理)。</summary>
    internal void SetExpressionText(string text) => ExpressionBox.Text = text;

    /// <summary>位置の式の解釈結果の文字列 (テスト用)。</summary>
    internal string ExpressionInfoText => ExpressionInfo.Text;
}
