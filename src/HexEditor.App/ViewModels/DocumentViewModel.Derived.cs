using HexEditor.Core.Editing.Codecs;

namespace HexEditor.App.ViewModels;

/// <summary>
/// 派生ドキュメント (EDIT-46): 展開・復号した結果を開いたタブ。元のドキュメントの範囲と変換を記録し、「元に書き戻す」で逆の変換をかけて元の範囲を
/// 置き換える。元のドキュメントを閉じると、通常の新規ドキュメントになる (書き戻せない)。
/// </summary>
public sealed partial class DocumentViewModel
{
    private DerivedLink? _derived;
    private string _derivedBandText = string.Empty;

    /// <summary>元のドキュメントとのつながり (派生ドキュメントでなければ null)。</summary>
    public DerivedLink? Derived
    {
        get => _derived;
        set
        {
            if (SetProperty(ref _derived, value))
            {
                OnPropertyChanged(nameof(IsDerived));
                OnPropertyChanged(nameof(CanWriteBack));
            }
        }
    }

    /// <summary>元のタブ (派生ドキュメントでなければ null)。</summary>
    public DocumentViewModel? DerivedParent { get; set; }

    public bool IsDerived => _derived is not null;

    /// <summary>「元に書き戻す」を使えるか (元のドキュメントが閉じられていない)。</summary>
    public bool CanWriteBack => _derived is { IsDetached: false };

    /// <summary>帯の文字列 (元のドキュメント、範囲、変換)。</summary>
    public string DerivedBandText
    {
        get => _derivedBandText;
        set => SetProperty(ref _derivedBandText, value);
    }

    /// <summary>元のドキュメントが閉じられた (書き戻せなくなる。EDIT-46 の仕様 7)。</summary>
    public void DetachDerived()
    {
        _derived?.Detach();
        OnPropertyChanged(nameof(CanWriteBack));
    }
}
