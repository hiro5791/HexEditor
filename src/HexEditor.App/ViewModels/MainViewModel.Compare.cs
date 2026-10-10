namespace HexEditor.App.ViewModels;

/// <summary>
/// ステータスバーに出す文書 (UI-06)。通常は選んでいるタブの文書。比較タブ (ANA-04) を表示している間は、フォーカスのある側の表示
/// (比較タブ自身のカーソルと選択範囲) にする。
/// </summary>
public sealed partial class MainViewModel
{
    private DocumentViewModel? _statusOverride;

    public DocumentViewModel? StatusDocument => _statusOverride ?? Selected;

    /// <summary>比較タブの表示中に、ステータスバーに出す側 (null で通常に戻す)。</summary>
    public DocumentViewModel? StatusOverride
    {
        get => _statusOverride;
        set
        {
            if (!ReferenceEquals(_statusOverride, value))
            {
                _statusOverride = value;
                OnPropertyChanged(nameof(StatusDocument));
            }
        }
    }

    /// <summary>選んでいる文書が変わった (MainWindow が Selected の変更で呼ぶ)。</summary>
    public void RaiseStatusDocumentChanged() => OnPropertyChanged(nameof(StatusDocument));
}
