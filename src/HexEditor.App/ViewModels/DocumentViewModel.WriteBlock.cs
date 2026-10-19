using HexEditor.Core.Engine;

namespace HexEditor.App.ViewModels;

/// <summary>書き込み保護モード (12 の FOR-01) のタブの状態: 読み取り専用にする前の理由と、ドキュメント単位の例外 (開いた錠)。</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>ドキュメント単位の例外のタブの見出しの記号 (開いた錠。FOR-01 の仕様 5)。</summary>
    public const string UnlockGlyph = "🔓";

    private bool _writeBlockException;

    /// <summary>書き込み保護モードでこのドキュメントを読み取り専用にする前の理由 (モードを解除したら戻す)。モードで変えていなければ null。</summary>
    internal ReadOnlyReason? ReadOnlyBeforeWriteBlock { get; set; }

    /// <summary>ドキュメント単位の例外を設定した (タブの錠のアイコンを「開いた錠」にする)。</summary>
    public bool WriteBlockException
    {
        get => _writeBlockException;
        set
        {
            if (SetProperty(ref _writeBlockException, value))
            {
                OnPropertyChanged(nameof(Header));
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }

    /// <summary>保存して別のファイルになった (書き込み保護の対象の一覧を直す)。引数は前のパス。</summary>
    internal event EventHandler<string?>? FilePathChanged;

    private Core.MemoryDumps.MemoryDumpInfo? _memoryDump;

    /// <summary>メモリダンプとして開いたドキュメントの、ダンプの種類と取得日時 (01 の ENG-36 の「画面」。タブのツールチップに出す)。</summary>
    public Core.MemoryDumps.MemoryDumpInfo? MemoryDump
    {
        get => _memoryDump;
        set
        {
            if (SetProperty(ref _memoryDump, value))
            {
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }
}
