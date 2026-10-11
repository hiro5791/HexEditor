using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Hashing;
using HexEditor.Core.Statistics;
using HexEditor.Core.View;

namespace HexEditor.App.ViewModels;

/// <summary>
/// 「埋め込まれた形式を探す」(ANA-17 の仕様 6) の対象範囲。06 の 0.1 の共通の「対象範囲」選択欄 (選択範囲 / マルチ選択 / ドキュメント全体 /
/// 範囲を指定) と、実際の開始・終了・長さの表示。既定は選択範囲・マルチ選択の有無で決まり、利用者が選んだ後は変えない。
/// </summary>
public sealed partial class FileTypeViewModel
{
    private bool _embeddedTargetChosen;
    private EditorState? _watchedEditor;

    /// <summary>対象範囲の選択肢 (<see cref="AnalysisTargetKind"/> の順の番号)。</summary>
    [ObservableProperty]
    public partial int EmbeddedTargetIndex { get; set; } = (int)AnalysisTargetKind.WholeDocument;

    [ObservableProperty]
    public partial string EmbeddedStart { get; set; } = "0";

    [ObservableProperty]
    public partial string EmbeddedLength { get; set; } = string.Empty;

    /// <summary>長さの欄を「終了 (このバイトを含む)」として読む。</summary>
    [ObservableProperty]
    public partial bool EmbeddedUsesEnd { get; set; }

    /// <summary>実際の範囲 (例: 「0x1000〜0x1FFF (4,096 バイト)」)。指定が正しくなければその旨。</summary>
    [ObservableProperty]
    public partial string EmbeddedRangeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEmbeddedCustom { get; set; }

    /// <summary>マルチ選択の範囲 (EDIT-07。ウィンドウが設定する)。</summary>
    public Func<EditorState, IReadOnlyList<HashRange>>? MultiSelectionOf { get; init; }

    /// <summary>利用者が対象範囲の選択欄を選んだ (以後、選択範囲の有無で既定を切り替えない)。</summary>
    public void ChooseEmbeddedTarget(AnalysisTargetKind kind)
    {
        _embeddedTargetChosen = true;
        EmbeddedTargetIndex = (int)kind;
    }

    /// <summary>対象範囲 (ドキュメントの範囲)。指定が正しくなければ null。</summary>
    public IReadOnlyList<HashRange>? ResolveEmbeddedRanges() => _target is { } doc
        ? AnalysisTarget.Resolve((AnalysisTargetKind)Math.Clamp(EmbeddedTargetIndex, 0, 3), doc.Editor, MultiSelectionOf?.Invoke(doc.Editor),
            EmbeddedStart, EmbeddedLength, EmbeddedUsesEnd)
        : null;

    partial void OnEmbeddedTargetIndexChanged(int value)
    {
        IsEmbeddedCustom = value == (int)AnalysisTargetKind.Custom;
        UpdateEmbeddedTarget();
    }

    partial void OnEmbeddedStartChanged(string value) => UpdateEmbeddedTarget();

    partial void OnEmbeddedLengthChanged(string value) => UpdateEmbeddedTarget();

    partial void OnEmbeddedUsesEndChanged(bool value) => UpdateEmbeddedTarget();

    /// <summary>対象のドキュメントが変わった: カーソル・選択の変化を追う。</summary>
    private void WatchTargetEditor(DocumentViewModel? doc)
    {
        if (_watchedEditor is not null)
        {
            _watchedEditor.Changed -= Editor_Changed;
        }

        _watchedEditor = doc?.Editor;
        _embeddedTargetChosen = false;
        if (_watchedEditor is not null)
        {
            _watchedEditor.Changed += Editor_Changed;
        }

        UpdateEmbeddedTarget();
    }

    private void Editor_Changed(object? sender, EventArgs e) => UpdateEmbeddedTarget();

    /// <summary>既定の選択肢と、実際の範囲の表示を更新する。</summary>
    private void UpdateEmbeddedTarget()
    {
        if (_target is not { } doc)
        {
            EmbeddedRangeText = string.Empty;
            return;
        }

        if (!_embeddedTargetChosen && EmbeddedTargetIndex != (int)AnalysisTargetKind.Custom)
        {
            // 要素の数は数えるだけにする (要素を並べると、100 GB にわたる矩形では選択が変わるたびに数 GB を使う)。
            int multiCount = doc.Editor.HasMultipleRanges ? (int)Math.Min(doc.Editor.SelectedRangeCount, int.MaxValue) : 0;
            int auto = (int)AnalysisTarget.DefaultKind(doc.Editor.HasSelection, multiCount);
            if (auto != EmbeddedTargetIndex)
            {
                EmbeddedTargetIndex = auto;
                return;
            }
        }

        // 要素の多いマルチ選択・矩形は、要素を並べずに数だけを示す (要素は探すときに作る)。
        if (EmbeddedTargetIndex == (int)AnalysisTargetKind.MultiSelection && doc.Editor.HasMultipleRanges && doc.Editor.SelectedRangeCount > 100_000)
        {
            EmbeddedRangeText = Loc.Format("Hash_Ranges", doc.Editor.SelectedRangeCount,
                doc.Editor.SelectedByteCount.ToString("N0", CultureInfo.CurrentCulture));
            return;
        }

        if (ResolveEmbeddedRanges() is not { } ranges)
        {
            EmbeddedRangeText = Loc.Get("Stats_RangeInvalid");
            return;
        }

        IReadOnlyList<HashRange> normalized = HashEngine.Normalize(ranges, doc.Document.Length);
        long total = normalized.Sum(r => r.Length);
        EmbeddedRangeText = total == 0
            ? Loc.Format("Hash_RangeEmpty", Hex(normalized.Count > 0 ? normalized[0].Offset : 0))
            : Loc.Format("Hash_Range", Hex(normalized[0].Offset), Hex(normalized[^1].End - 1), total.ToString("N0", CultureInfo.CurrentCulture));

        static string Hex(long value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);
    }
}
