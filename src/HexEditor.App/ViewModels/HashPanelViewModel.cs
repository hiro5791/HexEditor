using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;
using HexEditor.Core.Settings;
using HexEditor.Core.View;
using Microsoft.UI.Dispatching;

namespace HexEditor.App.ViewModels;

/// <summary>対象範囲の選択欄 (06 の 0.1)。</summary>
public enum HashTargetKind
{
    Selection,
    WholeDocument,
    Custom,
}

/// <summary>アルゴリズムの一覧の 1 項目 (ANA-19 の「画面」)。</summary>
public sealed partial class HashAlgorithmItem(HashAlgorithmInfo info) : ObservableObject
{
    public HashAlgorithmInfo Info { get; } = info;

    public string Id => Info.Id;

    public string Name => HashPanelViewModel.LocalizedName(Info);

    public bool IsAvailable => Info.IsAvailable;

    /// <summary>使えない理由 (ANA-19 の「エラー」の「この環境では利用できません」) と安全でない注記。</summary>
    public string? ToolTip => !Info.IsAvailable ? Loc.Get("Hash_Unavailable") : Info.IsInsecure ? Loc.Get("Hash_Insecure") : null;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    /// <summary>パラメータ (シード、結果の補数)。</summary>
    [ObservableProperty]
    public partial HashParameters Parameters { get; set; } = HashParameters.Default;

    public HashAlgorithmChoice Choice => new(Info, Parameters);
}

/// <summary>アルゴリズムの一覧のグループ。</summary>
public sealed record HashAlgorithmGroupViewModel(string Title, string Key, IReadOnlyList<HashAlgorithmItem> Items);

/// <summary>結果の表の 1 行 (ANA-18 の仕様 5、ANA-21 の仕様 6)。</summary>
/// <param name="rangeIndex">「範囲ごとに値を求める」の範囲の番号 (0 始まり。連結・単一の範囲なら -1。ANA-18 の仕様 1)。</param>
/// <param name="error">パラメータが不正で計算しなかった理由 (ANA-18 の「エラー」。その行だけ計算しない)。</param>
public sealed partial class HashRowViewModel(HashResultRow row, int rangeIndex = -1, string? error = null) : ObservableObject
{
    public HashResultRow Row { get; } = row;

    /// <summary>行の識別子 (AutomationId に使う。範囲ごとの行は <c>crc32@2</c> のように範囲の番号を付ける)。</summary>
    public string Id => rangeIndex < 0 ? Row.Algorithm.Id : $"{Row.Algorithm.Id}@{rangeIndex + 1}";

    /// <summary>アルゴリズムの ID。</summary>
    public string AlgorithmId => Row.Algorithm.Id;

    public string Name => (Row.Algorithm.Parameters == HashParameterKinds.None
        ? HashPanelViewModel.LocalizedName(Row.Algorithm)
        : Row.Choice.DisplayName.Replace(Row.Algorithm.Name, HashPanelViewModel.LocalizedName(Row.Algorithm), StringComparison.Ordinal))
        + (rangeIndex >= 0 && Row.Ranges.Count > 0 ? $" [0x{Row.Ranges[0].Offset:X}–0x{Row.Ranges[0].End - 1:X}]" : string.Empty);

    public string BitsText => Loc.Format("Hash_Bits", Row.Algorithm.BitsFor(Row.Choice.Parameters));

    /// <summary>パラメータが不正で計算しなかった (行を赤枠にする)。</summary>
    public bool IsInvalid => error is not null;

    /// <summary>
    /// 行の注記: 安全でないこと (MD5、SHA-1 など)、遅いアルゴリズム (全体の速度がこのアルゴリズムで決まる。ANA-18 の「巨大ファイル」)、
    /// 符号ありの合計の符号付き 10 進 (ANA-19 の仕様 1)、パラメータの誤り。
    /// </summary>
    public string Note
    {
        get
        {
            var parts = new List<string>();
            if (error is not null)
            {
                parts.Add(error);
            }

            if (Row.Algorithm.IsInsecure)
            {
                parts.Add(Loc.Get("Hash_Insecure"));
            }

            if (Row.Algorithm.IsSlow)
            {
                parts.Add(Loc.Get("Hash_Slow"));
            }

            if (error is null && Row.Choice.Parameters.Signed && Row.Algorithm.Parameters.HasFlag(HashParameterKinds.Signed) && Row.Value.Length is > 0 and <= 8)
            {
                int bits = Row.Value.Length * 8;
                ulong raw = HashBytes.ToNumber(Row.Value);
                long signedValue = bits == 64 ? (long)raw : (long)(raw << (64 - bits)) >> (64 - bits);
                parts.Add(Loc.Format("Hash_SignedValue", signedValue.ToString(CultureInfo.CurrentCulture)));
            }

            return string.Join(" ", parts);
        }
    }

    public bool HasParameters => Row.Algorithm.Parameters != HashParameterKinds.None;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    /// <summary>前回の結果と比べて変わった (ANA-18 の仕様 9)。</summary>
    [ObservableProperty]
    public partial bool IsChanged { get; set; }

    /// <summary>照合の結果。期待値がなければ null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchText), nameof(HasMatch), nameof(IsMatch), nameof(IsMismatch), nameof(AutomationName))]
    public partial HashMatch? Match { get; set; }

    public bool HasMatch => Match is not null;

    public bool IsMatch => Match is HashMatch.Match or HashMatch.MatchReversed;

    public bool IsMismatch => Match is HashMatch.None;

    public string MatchText => Match switch
    {
        HashMatch.Match => Loc.Get("Hash_Match"),
        HashMatch.MatchReversed => Loc.Get("Hash_MatchReversed"),
        HashMatch.None => Loc.Get("Hash_NoMatch"),
        _ => string.Empty,
    };

    /// <summary>スクリーンリーダーの読み上げ (「SHA-256、一致」。照合していなければ「SHA-256、値」)。</summary>
    public string AutomationName => Loc.Format("Hash_RowName", Name, HasMatch ? MatchText : Value);

    /// <summary>一覧の項目の名前 (UI オートメーションは項目の ToString を名前にする)。</summary>
    public override string ToString() => AutomationName;

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(AutomationName));
}

/// <summary>
/// ハッシュパネル (ANA-18、ANA-21、ANA-22)。計算は Core の <see cref="HashEngine"/> で、処理センターに読み取りのみの処理として登録する。
/// </summary>
public sealed partial class HashPanelViewModel : ObservableObject
{
    public const string AlgorithmsKey = "hash.algorithms";
    public const string SetsKey = "hash.sets";
    public const string AutoKey = "hash.autoRecompute";

    /// <summary>対象範囲が変わってから自動で計算するまでの時間 (ANA-18 の仕様 7)。</summary>
    public static readonly TimeSpan AutoDelay = TimeSpan.FromMilliseconds(300);

    private readonly OperationCenter _operations;
    private IReadOnlyList<HashRange> _lastExclusions = [];
    private readonly SettingsStore? _settings;
    private readonly DispatcherQueue? _queue;
    private readonly DispatcherQueueTimer? _timer;
    private Dictionary<string, byte[]> _previous = [];
    private DocumentViewModel? _target;
    private LongRunningOperation? _operation;
    private DocumentSnapshot? _computedSnapshot;
    private ExpectedHash? _expected;
    private bool _targetChosen;
    private bool _applyingSet;
    private IReadOnlyList<HashRange> _lastRanges = [];

    /// <summary>最後に計算を始めた (または自動で計算しないと決めた) 範囲。</summary>
    private IReadOnlyList<HashRange> _requestedRanges = [];
    private DocumentSnapshot? _seenSnapshot;

    public HashPanelViewModel(OperationCenter operations, SettingsStore? settings)
    {
        _operations = operations;
        _settings = settings;
        _queue = DispatcherQueue.GetForCurrentThread();
        if (_queue is not null)
        {
            _timer = _queue.CreateTimer();
            _timer.Interval = AutoDelay;
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => OnAutoTimer();
        }

        RebuildAlgorithms();
        HashCatalog.CustomChanged += Catalog_CustomChanged;
        AutoRecompute = _settings?.GetBool(AutoKey, true) ?? true;
        UserSets = [.. HashSelection.DeserializeSets(_settings?.GetString(SetsKey, string.Empty))];
        IReadOnlyList<HashAlgorithmChoice> saved = HashSelection.Deserialize(_settings?.GetString(AlgorithmsKey, string.Empty));
        ApplyChoices(saved.Count > 0 ? saved : [.. HashCatalog.DefaultSet.AlgorithmIds.Select(id => new HashAlgorithmChoice(HashCatalog.Get(id)))]);
        RebuildSetNames();
    }

    /// <summary>
    /// アルゴリズムの一覧を作り直す (カスタム CRC の追加・削除。ANA-20)。選んでいたアルゴリズムとパラメータは引き継ぐ。
    /// </summary>
    private void RebuildAlgorithms()
    {
        Dictionary<string, (bool Checked, HashParameters Parameters)> previous = Algorithms.ToDictionary(a => a.Id, a => (a.IsChecked, a.Parameters));
        bool first = Algorithms.Count == 0;
        _applyingSet = true;
        try
        {
            Algorithms.Clear();
            foreach (HashAlgorithmInfo info in HashCatalog.All)
            {
                var item = new HashAlgorithmItem(info);
                if (previous.TryGetValue(info.Id, out var state))
                {
                    item.IsChecked = state.Checked;
                    item.Parameters = state.Parameters;
                }

                item.IsVisible = info.MatchesFilter(Filter);
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(HashAlgorithmItem.IsChecked) or nameof(HashAlgorithmItem.Parameters) && !_applyingSet)
                    {
                        SaveSelection();
                    }
                };
                Algorithms.Add(item);
            }
        }
        finally
        {
            _applyingSet = false;
        }

        _groups = null;
        if (!first)
        {
            OnPropertyChanged(nameof(Groups));
        }
    }

    private void Catalog_CustomChanged(object? sender, EventArgs e)
    {
        if (_queue is { HasThreadAccess: false } queue)
        {
            queue.TryEnqueue(RebuildAlgorithms);
            return;
        }

        RebuildAlgorithms();
    }

    /// <summary>除外範囲 (ANA-18 の仕様 2)。</summary>
    public ObservableCollection<HashExclusionViewModel> Exclusions { get; } = [];

    /// <summary>除外の方法 (0: 飛ばす、1: 置き換える)。</summary>
    [ObservableProperty]
    public partial int ExclusionModeIndex { get; set; }

    /// <summary>「置き換える」で使う値 (既定 0x00)。</summary>
    [ObservableProperty]
    public partial string ReplacementText { get; set; } = "00";

    [ObservableProperty]
    public partial bool IsReplacementError { get; set; }

    /// <summary>マルチ選択の計算方法 (0: 連結して 1 つの値、1: 範囲ごと。ANA-18 の仕様 1)。</summary>
    [ObservableProperty]
    public partial int RangeModeIndex { get; set; }

    /// <summary>対象範囲が複数 (マルチ選択) か。計算方法の選択を表示する。</summary>
    [ObservableProperty]
    public partial bool IsMultiRange { get; set; }

    /// <summary>読めない範囲 (「読めない範囲を除外範囲に追加」のボタンを出す。ANA-18 の「エラー」)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnreadable))]
    public partial HashRange? Unreadable { get; set; }

    public bool HasUnreadable => Unreadable is not null;

    /// <summary>最後に計算したときの除外範囲 (書き込みの警告に使う。ANA-22 の仕様 4)。</summary>
    public IReadOnlyList<HashRange> LastExclusions => _lastExclusions;

    /// <summary>除外範囲を加える (開始と長さの入力式)。</summary>
    public void AddExclusion(string start, string length)
    {
        Exclusions.Add(new HashExclusionViewModel(this) { StartText = start, LengthText = length });
        RefreshExclusions();
    }

    /// <summary>「選択範囲を追加」。</summary>
    public void AddSelectionExclusion()
    {
        if (_target is { } doc && doc.Editor.HasSelection)
        {
            AddExclusion($"0x{doc.Editor.SelectionStart:X}", $"0x{doc.Editor.SelectionLength:X}");
        }
    }

    /// <summary>「読めない範囲を除外範囲に追加」(ANA-18 の「エラー」)。</summary>
    public void AddUnreadableExclusion()
    {
        if (Unreadable is { } r)
        {
            AddExclusion($"0x{r.Offset:X}", $"0x{r.Length:X}");
            Unreadable = null;
        }
    }

    public void RemoveExclusion(HashExclusionViewModel item)
    {
        Exclusions.Remove(item);
        RefreshExclusions();
    }

    /// <summary>除外範囲の入力を評価し、対象範囲の外の行に警告を付ける (計算では無視する)。</summary>
    internal void RefreshExclusions()
    {
        IReadOnlyList<HashRange> targets = ResolveRanges() ?? [];
        foreach (HashExclusionViewModel item in Exclusions)
        {
            item.Resolve(_target is { } doc ? new EditorExpressionContext(doc.Editor) : null, _target?.Document.Length ?? 0, targets);
        }

        IsReplacementError = !TryParseReplacement(out _);
    }

    private bool TryParseReplacement(out byte value)
    {
        value = 0;
        if (_target is not { } doc)
        {
            return true;
        }

        if (!ExpressionEvaluator.TryEvaluate(ReplacementText, new EditorExpressionContext(doc.Editor), out long v, out _) || v is < 0 or > 255)
        {
            return false;
        }

        value = (byte)v;
        return true;
    }

    /// <summary>対象範囲を選び直す (マルチ選択が変わったとき)。</summary>
    public void RefreshTarget() => UpdateTarget(scheduleAuto: true);

    partial void OnExclusionModeIndexChanged(int value) => RefreshExclusions();

    partial void OnReplacementTextChanged(string value) => RefreshExclusions();

    partial void OnRangeModeIndexChanged(int value)
    {
        // 計算方法を変えたら、同じ範囲でも計算し直す。
        _requestedRanges = [];
        UpdateTarget(scheduleAuto: true);
    }

    /// <summary>テキストをクリップボードに入れる (アプリのクリップボードの抽象。テストでは代わりのもの)。</summary>
    public Action<string>? SetClipboardText { get; set; }

    /// <summary>保存先を選ぶ (null はキャンセル)。</summary>
    public Func<string, Task<string?>>? PickSavePath { get; set; }

    /// <summary>チェックサムファイルを選ぶ (null はキャンセル)。</summary>
    public Func<Task<string?>>? PickChecksumFile { get; set; }

    public ObservableCollection<HashAlgorithmItem> Algorithms { get; } = [];

    /// <summary>グループごとのアルゴリズム (一覧はグループごとに折りたためる。ANA-19 の「画面」)。</summary>
    public IReadOnlyList<HashAlgorithmGroupViewModel> Groups => _groups ??= [.. Algorithms.GroupBy(a => a.Info.Group)
        .Select(g => new HashAlgorithmGroupViewModel(Loc.Get("Hash_Group_" + g.Key), g.Key.ToString(), [.. g]))];

    private IReadOnlyList<HashAlgorithmGroupViewModel>? _groups;

    public ObservableCollection<HashRowViewModel> Rows { get; } = [];

    /// <summary>セットの一覧の表示名 (最初から用意するもの、利用者のもの)。</summary>
    public ObservableCollection<string> SetNames { get; } = [];

    /// <summary>チェックサムファイルにこのファイルの行がないときの、ファイル内の行の一覧。</summary>
    public ObservableCollection<string> VerifyLines { get; } = [];

    private List<UserHashSet> UserSets { get; }

    private IReadOnlyList<ChecksumEntry> _verifyEntries = [];

    [ObservableProperty]
    public partial HashTargetKind TargetKind { get; set; }

    [ObservableProperty]
    public partial string CustomStart { get; set; } = "0";

    [ObservableProperty]
    public partial string CustomLength { get; set; } = string.Empty;

    /// <summary>2 つ目の欄を「終了 (このバイトを含む)」として読む (06 の 0.1。false なら長さ)。終了は <c>sel.last</c> と同じく最後のバイトの位置。</summary>
    [ObservableProperty]
    public partial bool CustomUsesEnd { get; set; }

    /// <summary>実際の開始・終了 (このバイトを含む)・長さ (0.1)。</summary>
    [ObservableProperty]
    public partial string RangeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRangeError { get; set; }

    [ObservableProperty]
    public partial bool AutoRecompute { get; set; } = true;

    /// <summary>値の表示形式 (<see cref="HashValueFormat"/> の番号)。</summary>
    [ObservableProperty]
    public partial int FormatIndex { get; set; }

    /// <summary>64 bit 以下の値をリトルエンディアンのバイト列で表記する (ANA-18 の仕様 6)。</summary>
    [ObservableProperty]
    public partial bool LittleEndian { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedSetIndex { get; set; } = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsComputing { get; set; }

    public bool IsIdle => !IsComputing;

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string SpeedText { get; set; } = string.Empty;

    /// <summary>計算できなかった理由など。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>計算後にドキュメントが編集された (0.2 の 3)。</summary>
    [ObservableProperty]
    public partial bool IsStale { get; set; }

    /// <summary>対象が大きいため自動で計算しなかった (「計算」ボタンを強調表示する。ANA-18 の仕様 7)。</summary>
    [ObservableProperty]
    public partial bool ComputeHighlighted { get; set; }

    [ObservableProperty]
    public partial string ExpectedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsExpectedError { get; set; }

    [ObservableProperty]
    public partial string VerifyMessage { get; set; } = string.Empty;

    public HashDisplayOptions Display => new((HashValueFormat)Math.Max(0, FormatIndex), LittleEndian);

    /// <summary>計算した範囲 (書き込みの警告などに使う)。</summary>
    public IReadOnlyList<HashRange> LastRanges => _lastRanges;

    /// <summary>計算の対象のドキュメント (選んでいるタブ)。</summary>
    public DocumentViewModel? Target
    {
        get => _target;
        set
        {
            if (ReferenceEquals(_target, value))
            {
                return;
            }

            if (_target is not null)
            {
                _target.EditorChanged -= Editor_Changed;
                _target.Document.Changed -= Document_Changed;
                SaveHistory(_target);
            }

            CancelComputation();
            _target = value;
            _targetChosen = false;
            Rows.Clear();
            IsStale = false;
            StatusText = string.Empty;

            // 結果の履歴はドキュメントごとに、パネルを閉じるまで持つ (ANA-18 の仕様 9)。タブを切り替えて戻ると前の結果が見える。
            DocumentHashHistory history = _target is null ? new() : _history.GetOrCreateValue(_target);
            _previous = history.Previous;
            _computedSnapshot = history.Computed;
            _lastRanges = history.LastRanges;
            _requestedRanges = history.Requested;
            _seenSnapshot = history.Seen;
            foreach (HashRowViewModel row in history.Rows)
            {
                row.Value = Display.Display(row.Row);
                Rows.Add(row);
            }

            if (_target is not null)
            {
                _target.EditorChanged += Editor_Changed;
                _target.Document.Changed += Document_Changed;
                IsStale = Rows.Count > 0 && _computedSnapshot is not null && !ReferenceEquals(_target.Document.Current, _computedSnapshot);
            }

            UpdateMatches();
            OnPropertyChanged();
            UpdateTarget(scheduleAuto: true);
        }
    }

    /// <summary>アルゴリズムの表示名 (チェックサムは訳す。それ以外はアルゴリズムの名前のまま)。</summary>
    public static string LocalizedName(HashAlgorithmInfo info) =>
        info.Group == HashGroup.Checksum ? Loc.TryGet("Hash_Alg_" + info.Id) ?? info.Name : info.Name;

    /// <summary>これより要素の多いマルチ選択は、選択が変わるたびに要素を並べない (自動で計算しない)。</summary>
    private const long AutoRangeLimit = 100_000;

    /// <summary>対象範囲 (ドキュメントの範囲)。指定が正しくなければ null。</summary>
    public IReadOnlyList<HashRange>? ResolveRanges()
    {
        if (_target is not { } doc)
        {
            return null;
        }

        EditorState editor = doc.Editor;
        long length = doc.Document.Length;
        switch (TargetKind)
        {
            case HashTargetKind.Selection when editor.HasMultipleRanges:
                // マルチ選択・矩形選択: すべての要素 (オフセットの小さい順。ANA-18 の仕様 1)。
                return [.. editor.SelectedRanges.Select(r => new HashRange(r.Start, r.Length))];
            case HashTargetKind.Selection when editor.HasSelection:
                return [new HashRange(editor.SelectionStart, editor.SelectionLength)];
            case HashTargetKind.Selection:
            case HashTargetKind.WholeDocument:
                return [new HashRange(0, length)];
            default:
                var context = new EditorExpressionContext(editor);
                if (!ExpressionEvaluator.TryEvaluate(CustomStart, context, out long start, out _) || start < 0 || start > length)
                {
                    return null;
                }

                long count = length - start;
                if (CustomLength.Trim().Length > 0)
                {
                    if (!ExpressionEvaluator.TryEvaluate(CustomLength, context, out long value, out _))
                    {
                        return null;
                    }

                    // 終了 (このバイトを含む) なら、長さは 終了 - 開始 + 1。
                    count = CustomUsesEnd ? value - start + 1 : value;
                    if (count < 0 || (CustomUsesEnd && value >= length) || start + count > length)
                    {
                        return null;
                    }
                }

                return [new HashRange(start, count)];
        }
    }

    partial void OnTargetKindChanged(HashTargetKind value) => UpdateTarget(scheduleAuto: true);

    partial void OnCustomStartChanged(string value) => UpdateTarget(scheduleAuto: true);

    partial void OnCustomLengthChanged(string value) => UpdateTarget(scheduleAuto: true);

    partial void OnCustomUsesEndChanged(bool value) => UpdateTarget(scheduleAuto: true);

    /// <summary>パネルを閉じた: 結果の履歴を捨てる (ANA-18 の仕様 9)。</summary>
    public void ClearHistory()
    {
        _history.Clear();
        _previous = [];
        Rows.Clear();
    }

    // ---- コマンド (ANA-21、ANA-22 の「呼び出し」) ----

    /// <summary>
    /// 「解析: ハッシュ値をコピー」: 結果が 1 行なら値だけ、複数なら「アルゴリズム名: 値」をすべての行についてコピーする。結果がなければ false。
    /// </summary>
    public bool CopyResults()
    {
        if (Rows.Count == 0)
        {
            return false;
        }

        if (Rows.Count == 1)
        {
            CopyValue(Rows[0]);
        }
        else
        {
            CopyNameValue(null);
        }

        return true;
    }
    partial void OnAutoRecomputeChanged(bool value) => _settings?.SetBool(AutoKey, value, true);

    partial void OnFormatIndexChanged(int value) => RefreshValues();

    partial void OnLittleEndianChanged(bool value) => RefreshValues();

    partial void OnFilterChanged(string value)
    {
        foreach (HashAlgorithmItem item in Algorithms)
        {
            item.IsVisible = item.Info.MatchesFilter(value);
        }
    }

    partial void OnSelectedSetIndexChanged(int value)
    {
        if (value < 0)
        {
            return;
        }

        if (value < HashCatalog.BuiltInSets.Count)
        {
            ApplyChoices([.. HashCatalog.BuiltInSets[value].AlgorithmIds.Select(id => new HashAlgorithmChoice(HashCatalog.Get(id)))]);
        }
        else if (value - HashCatalog.BuiltInSets.Count < UserSets.Count)
        {
            ApplyChoices(UserSets[value - HashCatalog.BuiltInSets.Count].Algorithms);
        }

        SaveSelection();
    }

    partial void OnExpectedTextChanged(string value)
    {
        if (value.Trim().Length == 0)
        {
            _expected = null;
            IsExpectedError = false;
        }
        else if (ExpectedHash.TryParse(value, out ExpectedHash? e))
        {
            _expected = e;
            IsExpectedError = false;
        }
        else
        {
            _expected = null;
            IsExpectedError = true;
        }

        UpdateMatches();
    }

    /// <summary>ユーザーが対象範囲の選択欄を選んだ (以後、選択範囲の有無で既定を切り替えない)。</summary>
    public void ChooseTarget(HashTargetKind kind)
    {
        _targetChosen = true;
        TargetKind = kind;
    }

    /// <summary>計算する (「計算」ボタン)。</summary>
    [RelayCommand]
    public async Task ComputeAsync()
    {
        if (_target is not { } doc)
        {
            return;
        }

        _timer?.Stop();
        CancelComputation();
        IReadOnlyList<HashRange>? ranges = ResolveRanges();
        HashAlgorithmChoice[] selected = [.. Algorithms.Where(a => a.IsChecked && a.IsAvailable).Select(a => a.Choice)];

        // パラメータが不正な行は計算しない (ANA-18 の「エラー」)。
        HashAlgorithmChoice[] choices = [.. selected.Where(c => c.Algorithm.Validate(c.Parameters) == HashParameterError.None)];
        HashAlgorithmChoice[] invalid = [.. selected.Except(choices)];
        RefreshExclusions();
        if (ranges is null || selected.Length == 0 || IsReplacementError)
        {
            StatusText = ranges is null ? Loc.Get("Hash_RangeInvalid") : IsReplacementError ? Loc.Get("Hash_ReplacementInvalid") : Loc.Get("Hash_NoAlgorithm");
            return;
        }

        TryParseReplacement(out byte replacement);
        IReadOnlyList<HashRange> exclusions = [.. Exclusions.Where(x => x.Range is not null && !x.IsOutside).Select(x => x.Range!.Value)];
        DocumentSnapshot snapshot = doc.Document.Current;
        _requestedRanges = ranges;
        bool perRange = ranges.Count > 1 && RangeModeIndex == 1;
        var request = new HashRequest
        {
            Ranges = ranges,
            Algorithms = choices,
            RangeMode = perRange ? HashRangeMode.PerRange : HashRangeMode.Concatenate,
            Exclusions = exclusions,
            ExclusionMode = ExclusionModeIndex == 1 ? HashExclusionMode.Replace : HashExclusionMode.Skip,
            ReplacementValue = replacement,
        };
        Unreadable = null;
        long total = HashEngine.TotalBytes(snapshot, request);
        ComputeHighlighted = false;
        StatusText = string.Empty;
        IsStale = false;
        Rows.Clear();
        Progress = 0;
        SpeedText = string.Empty;
        IsComputing = true;
        LongRunningOperation? mine = null;
        try
        {
            HashComputation result = await _operations.RunAsync(Loc.Get("Operation_Hash"), OperationKind.ReadOnly, doc.Document, total, op =>
            {
                mine = op;
                _operation = op;
                op.ProgressChanged += (_, _) => _queue?.TryEnqueue(() => ShowProgress(op));
                return Task.FromResult(HashEngine.Compute(snapshot, request, op));
            });
            if (!ReferenceEquals(_target, doc))
            {
                return;
            }

            _computedSnapshot = snapshot;
            _lastRanges = result.Ranges;
            _lastExclusions = exclusions;
            IsStale = !ReferenceEquals(doc.Document.Current, snapshot);
            foreach (HashAlgorithmChoice c in invalid)
            {
                Rows.Add(new HashRowViewModel(new HashResultRow(c, [], ranges), error: Loc.Get("Hash_ParameterError_" + c.Algorithm.Validate(c.Parameters))));
            }

            foreach (HashResultRow row in result.Rows)
            {
                int rangeIndex = perRange ? IndexOfRange(result.Ranges, row.Ranges) : -1;
                var vm = new HashRowViewModel(row, rangeIndex) { Value = Display.Display(row) };
                string key = row.Choice.DisplayName + "|" + string.Join(';', row.Ranges);
                vm.IsChanged = _previous.TryGetValue(key, out byte[]? before) && !before.AsSpan().SequenceEqual(row.Value);
                _previous[key] = row.Value;
                Rows.Add(vm);
            }

            UpdateMatches();
        }
        catch (OperationCanceledException)
        {
            // キャンセルした場合、結果は破棄する (途中までのハッシュ値は意味がない)。
            if (ReferenceEquals(_operation, mine) || mine is null)
            {
                StatusText = Loc.Get("Hash_Cancelled");
            }
        }
        catch (HashReadException e)
        {
            StatusText = Loc.Format("Hash_ReadError", "0x" + e.Range.Offset.ToString("X", CultureInfo.InvariantCulture));
            Unreadable = new HashRange(e.Range.Offset, e.Range.Length);
        }
        finally
        {
            if (ReferenceEquals(_operation, mine))
            {
                _operation = null;
                IsComputing = false;
            }
        }
    }

    private static int IndexOfRange(IReadOnlyList<HashRange> all, IReadOnlyList<HashRange> rowRanges) =>
        rowRanges.Count == 0 ? -1 : Math.Max(0, all.ToList().IndexOf(rowRanges[0]));

    [RelayCommand(CanExecute = nameof(IsComputing))]
    public void Cancel() => CancelComputation();

    /// <summary>名前を付けてセットを保存する (ANA-18 の仕様 3)。同じ名前があれば置き換える。</summary>
    public void SaveSet(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return;
        }

        UserSets.RemoveAll(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        UserSets.Add(new UserHashSet(name, [.. Algorithms.Where(a => a.IsChecked).Select(a => a.Choice)]));
        _settings?.SetString(SetsKey, HashSelection.SerializeSets(UserSets), string.Empty);
        RebuildSetNames();
    }

    /// <summary>アルゴリズムのパラメータを変える (結果の行の「設定」)。</summary>
    public void SetParameters(string id, HashParameters parameters)
    {
        if (Algorithms.FirstOrDefault(a => a.Id == id) is { } item)
        {
            item.Parameters = parameters;
            item.IsChecked = true;
            if (Rows.Count > 0 && AutoRecompute)
            {
                _ = ComputeAsync();
            }
        }
    }

    public HashParameters ParametersOf(string id) => Algorithms.FirstOrDefault(a => a.Id == id)?.Parameters ?? HashParameters.Default;

    // ---- コピー (ANA-22 の仕様 1) ----

    public void CopyValue(HashRowViewModel row) => SetClipboardText?.Invoke(HashExport.Value(row.Row, Display));

    public void CopyNameValue(HashRowViewModel? row) => SetClipboardText?.Invoke(HashExport.NameValue(RowsOrAll(row), Display));

    public void CopyChecksumLine(HashRowViewModel? row) => SetClipboardText?.Invoke(HashExport.ChecksumLines(RowsOrAll(row), DocumentFileName));

    public void CopyJson() => SetClipboardText?.Invoke(HashExport.Json(Rows.Select(r => r.Row), Display));

    public void CopyCsv() => SetClipboardText?.Invoke(HashExport.Csv(Rows.Select(r => r.Row), Display));

    /// <summary>「チェックサムファイルとして保存...」(ANA-22 の仕様 2)。</summary>
    public async Task SaveChecksumFileAsync(HashRowViewModel row)
    {
        if (PickSavePath is null)
        {
            return;
        }

        string suggested = Path.GetFileName(DocumentFileName) + HashExport.ChecksumExtension(row.Row.Algorithm);
        if (await PickSavePath(suggested) is not { } path)
        {
            return;
        }

        try
        {
            await Task.Run(() => HashExport.WriteChecksumFile(path, [row.Row], DocumentFileName));
            StatusText = Loc.Format("Hash_Saved", Path.GetFileName(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = Loc.Format("Hash_SaveFailed", e.Message);
        }
    }

    // ---- 照合 (ANA-21) ----

    /// <summary>「チェックサムファイルで検証...」(ANA-21 の仕様 4)。</summary>
    public async Task VerifyWithFileAsync()
    {
        if (PickChecksumFile is null || _target is null || await PickChecksumFile() is not { } path)
        {
            return;
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            VerifyMessage = Loc.Format("Hash_SaveFailed", e.Message);
            return;
        }

        _verifyEntries = ChecksumFile.Parse(content, Path.GetExtension(path));
        VerifyLines.Clear();
        if (ChecksumFile.FindEntry(_verifyEntries, DocumentFileName) is { } entry)
        {
            await VerifyEntryAsync(entry);
            return;
        }

        VerifyMessage = Loc.Get("Hash_VerifyLineNotFound");
        foreach (ChecksumEntry e in _verifyEntries)
        {
            VerifyLines.Add(ChecksumFile.Describe(e));
        }
    }

    /// <summary>ファイル内の行の一覧から選んだ行で照合する。</summary>
    public Task VerifyLineAsync(int index) =>
        index >= 0 && index < _verifyEntries.Count ? VerifyEntryAsync(_verifyEntries[index]) : Task.CompletedTask;

    private async Task VerifyEntryAsync(ChecksumEntry entry)
    {
        if (ChecksumFile.Resolve(entry) is not { } resolved)
        {
            VerifyMessage = Loc.Get("Hash_VerifyUnknownAlgorithm");
            return;
        }

        // その行のアルゴリズムでドキュメント全体を計算していなければ、選んで計算する。
        HashAlgorithmItem item = Algorithms.First(a => a.Id == resolved.Algorithm.Id);
        bool computed = Rows.Any(r => r.Id == item.Id && r.Row.Choice.Parameters == HashParameters.Default)
            && TargetKind == HashTargetKind.WholeDocument && !IsStale;
        if (!computed)
        {
            item.Parameters = HashParameters.Default;
            item.IsChecked = true;
            _targetChosen = true;
            TargetKind = HashTargetKind.WholeDocument;
            await ComputeAsync();
        }

        ExpectedText = Convert.ToHexString(entry.Value);
        HashRowViewModel? row = Rows.FirstOrDefault(r => r.Id == item.Id);
        VerifyMessage = row is null ? string.Empty
            : Loc.Format("Hash_VerifyResult", row.Name, row.MatchText);
    }

    // ---- 一致するアルゴリズムを探す (ANA-21 の仕様 5) ----

    /// <summary>「一致するアルゴリズムを探す」の結果の文。</summary>
    [ObservableProperty]
    public partial string FindResult { get; set; } = string.Empty;

    /// <summary>対象が 1 MB を超えるときの確認 (所要時間の目安の秒数を渡す。null なら確認しない)。</summary>
    public Func<long, Task<bool>>? ConfirmFind { get; set; }

    /// <summary>この大きさを超える対象では、探す前に所要時間の目安を示して確認する。</summary>
    public const long FindConfirmLimit = 1_000_000;

    /// <summary>期待値の長さに合うすべてのアルゴリズムで計算し、一致したものを一覧にする (1 回の読み込み)。</summary>
    public async Task FindMatchingAlgorithmsAsync()
    {
        if (_target is not { } doc)
        {
            return;
        }

        if (_expected is not { } expected)
        {
            FindResult = Loc.Get("Hash_FindNeedsExpected");
            return;
        }

        RefreshExclusions();
        if (ResolveRanges() is not { } ranges)
        {
            FindResult = Loc.Get("Hash_RangeInvalid");
            return;
        }

        int candidates = AlgorithmFinder.Candidates(expected.Value.Length * 8).Count;
        long total = ranges.Sum(r => r.Length);
        if (total > FindConfirmLimit && ConfirmFind is { } confirm)
        {
            // 目安: 1 種類あたり 500 MB/s、最短 1 秒。
            long seconds = Math.Max(1, (long)(total * (double)Math.Max(1, candidates) / 500_000_000));
            if (!await confirm(seconds))
            {
                return;
            }
        }

        TryParseReplacement(out byte replacement);
        var target = new HashRequest
        {
            Ranges = ranges,
            Algorithms = [],
            Exclusions = [.. Exclusions.Where(x => x.Range is not null && !x.IsOutside).Select(x => x.Range!.Value)],
            ExclusionMode = ExclusionModeIndex == 1 ? HashExclusionMode.Replace : HashExclusionMode.Skip,
            ReplacementValue = replacement,
        };
        DocumentSnapshot snapshot = doc.Document.Current;
        FindResult = Loc.Get("Hash_Finding");
        try
        {
            AlgorithmSearchResult result = await _operations.RunAsync(Loc.Get("Operation_FindAlgorithm"), OperationKind.ReadOnly, doc.Document,
                total, op => Task.FromResult(AlgorithmFinder.Find(snapshot, expected, target, op)));
            FindResult = result.Matches.Count == 0
                ? Loc.Format("Hash_FindNone", result.Tried)
                : Loc.Format("Hash_FindFound", string.Join(", ", result.Matches.Select(m =>
                    m.Match == HashMatch.MatchReversed ? Loc.Format("Hash_FindReversed", m.DisplayName) : m.DisplayName)), result.Tried);
        }
        catch (OperationCanceledException)
        {
            FindResult = Loc.Get("Hash_Cancelled");
        }
        catch (HashReadException e)
        {
            FindResult = Loc.Format("Hash_ReadError", "0x" + e.Range.Offset.ToString("X", CultureInfo.InvariantCulture));
        }
    }

    // ---- カーソル位置に書き込む (ANA-22 の仕様 3・4)、カスタム CRC (ANA-20) ----

    /// <summary>「カーソル位置に書き込む」(ウィンドウがダイアログを出して書き込む)。</summary>
    public Func<HashRowViewModel, Task>? WriteAtCursor { get; set; }

    /// <summary>カスタム CRC の追加・エクスポート・インポート・削除 (ウィンドウがダイアログ・ファイルの選択を出す)。</summary>
    public Func<Task>? AddCustomCrc { get; set; }

    public Func<Task>? ExportCustomCrc { get; set; }

    public Func<Task>? ImportCustomCrc { get; set; }

    public Action<string>? RemoveCustomCrc { get; set; }

    // ---- 内部 ----

    /// <summary>ドキュメントごとの結果の履歴 (ANA-18 の仕様 9)。</summary>
    private sealed class DocumentHashHistory
    {
        public List<HashRowViewModel> Rows { get; set; } = [];

        public Dictionary<string, byte[]> Previous { get; } = [];

        public DocumentSnapshot? Computed { get; set; }

        public DocumentSnapshot? Seen { get; set; }

        public IReadOnlyList<HashRange> LastRanges { get; set; } = [];

        public IReadOnlyList<HashRange> Requested { get; set; } = [];
    }

    /// <summary>閉じたドキュメントの履歴は残さない (ドキュメントが回収されれば消える)。</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DocumentViewModel, DocumentHashHistory> _history = new();

    private void SaveHistory(DocumentViewModel doc)
    {
        DocumentHashHistory history = _history.GetOrCreateValue(doc);
        history.Rows = [.. Rows];
        history.Computed = _computedSnapshot;
        history.Seen = _seenSnapshot;
        history.LastRanges = _lastRanges;
        history.Requested = _requestedRanges;
    }

    private string DocumentFileName => _target?.FilePath ?? _target?.DisplayName ?? "data.bin";

    private IEnumerable<HashResultRow> RowsOrAll(HashRowViewModel? row) => row is null ? Rows.Select(r => r.Row) : [row.Row];

    private void ApplyChoices(IReadOnlyList<HashAlgorithmChoice> choices)
    {
        _applyingSet = true;
        try
        {
            foreach (HashAlgorithmItem item in Algorithms)
            {
                HashAlgorithmChoice? choice = choices.FirstOrDefault(c => c.Algorithm.Id == item.Id);
                item.IsChecked = choice is not null && item.IsAvailable;
                item.Parameters = choice?.Parameters ?? HashParameters.Default;
            }
        }
        finally
        {
            _applyingSet = false;
        }
    }

    private void SaveSelection() =>
        _settings?.SetString(AlgorithmsKey, HashSelection.Serialize(Algorithms.Where(a => a.IsChecked).Select(a => a.Choice)), string.Empty);

    private void RebuildSetNames()
    {
        SetNames.Clear();
        foreach (HashAlgorithmSet set in HashCatalog.BuiltInSets)
        {
            SetNames.Add(Loc.Get("Hash_Set_" + set.Id));
        }

        foreach (UserHashSet set in UserSets)
        {
            SetNames.Add(set.Name);
        }
    }

    private void RefreshValues()
    {
        foreach (HashRowViewModel row in Rows)
        {
            row.Value = Display.Display(row.Row);
        }
    }

    private void UpdateMatches()
    {
        foreach (HashRowViewModel row in Rows)
        {
            row.Match = _expected?.Compare(row.Row);
        }
    }

    private void ShowProgress(LongRunningOperation op)
    {
        if (!ReferenceEquals(op, _operation))
        {
            return;
        }

        Progress = op.Fraction ?? 0;
        SpeedText = Loc.Format("Hash_Speed", (op.BytesPerSecond / (1024 * 1024)).ToString("N0", CultureInfo.CurrentCulture));
    }

    private void CancelComputation()
    {
        _operation?.Cancel();
    }

    private void Editor_Changed(object? sender, EventArgs e) => UpdateTarget(scheduleAuto: true);

    private void Document_Changed(object? sender, DocumentChangedEventArgs e)
    {
        if (_computedSnapshot is not null && Rows.Count > 0 && _target is { } doc)
        {
            IsStale = !ReferenceEquals(doc.Document.Current, _computedSnapshot);
        }

        UpdateTarget(scheduleAuto: false);
    }

    /// <summary>対象範囲の表示を更新し、範囲が変わったら自動で計算する準備をする。</summary>
    private void UpdateTarget(bool scheduleAuto)
    {
        if (_target is not { } doc)
        {
            RangeText = string.Empty;
            return;
        }

        // 選択範囲の有無で既定を切り替える (0.1 の「既定になる条件」)。利用者が選んだ後は変えない。
        if (!_targetChosen && TargetKind != HashTargetKind.Custom)
        {
            bool selected = doc.Editor.HasSelection || doc.Editor.HasMultipleRanges;
            HashTargetKind auto = selected ? HashTargetKind.Selection : HashTargetKind.WholeDocument;
            if (TargetKind != auto)
            {
                TargetKind = auto;
                return;
            }
        }

        // 要素の多いマルチ選択・矩形 (100 GB の全体にわたる矩形など) は、要素を並べずに数だけを示し、自動では計算しない
        // (要素は「計算」を押したときに作る)。
        if (TargetKind == HashTargetKind.Selection && doc.Editor.HasMultipleRanges && doc.Editor.SelectedRangeCount > AutoRangeLimit)
        {
            IsMultiRange = true;
            IsRangeError = false;
            RangeText = Loc.Format("Hash_Ranges", doc.Editor.SelectedRangeCount,
                doc.Editor.SelectedByteCount.ToString("N0", CultureInfo.CurrentCulture));
            _timer?.Stop();
            ComputeHighlighted = true;
            _requestedRanges = [];
            return;
        }

        IReadOnlyList<HashRange>? ranges = ResolveRanges();
        IsRangeError = ranges is null;
        if (ranges is null)
        {
            RangeText = Loc.Get("Hash_RangeInvalid");
            return;
        }

        IsMultiRange = ranges.Count > 1;
        RefreshExclusions();
        HashRange r = ranges[0];
        RangeText = ranges.Count > 1
            ? Loc.Format("Hash_Ranges", ranges.Count, ranges.Sum(x => x.Length).ToString("N0", CultureInfo.CurrentCulture))
            : r.Length == 0
            ? Loc.Format("Hash_RangeEmpty", Hex(r.Offset))
            : Loc.Format("Hash_Range", Hex(r.Offset), Hex(r.End - 1), r.Length.ToString("N0", CultureInfo.CurrentCulture));
        // 編集による変化 (ドキュメント全体の長さが変わったなど) では自動で計算しない (結果は「編集前の内容のもの」と表示する)。
        bool edited = _seenSnapshot is not null && !ReferenceEquals(doc.Document.Current, _seenSnapshot);
        _seenSnapshot = doc.Document.Current;
        bool changed = !_requestedRanges.SequenceEqual(ranges);
        if (!scheduleAuto || edited || !changed || !AutoRecompute)
        {
            return;
        }

        // 64 MB を超える場合は自動で計算せず、「計算」ボタンを強調表示する (ANA-18 の仕様 7)。
        long total = ranges.Sum(x => x.Length);
        _timer?.Stop();
        if (total > HashEngine.AutoComputeLimit)
        {
            _requestedRanges = ranges;
            ComputeHighlighted = true;
            return;
        }

        ComputeHighlighted = false;
        _timer?.Start();
    }

    private void OnAutoTimer()
    {
        if (AutoRecompute && ResolveRanges() is { } ranges && ranges.Sum(x => x.Length) <= HashEngine.AutoComputeLimit)
        {
            _ = ComputeAsync();
        }
    }

    private static string Hex(long value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);
}

/// <summary>除外範囲の一覧の 1 行 (ANA-18 の仕様 2)。開始と長さは入力式。</summary>
public sealed partial class HashExclusionViewModel(HashPanelViewModel owner) : ObservableObject
{
    [ObservableProperty]
    public partial string StartText { get; set; } = "0";

    [ObservableProperty]
    public partial string LengthText { get; set; } = "1";

    /// <summary>評価した範囲 (入力が正しくなければ null)。</summary>
    public HashRange? Range { get; private set; }

    /// <summary>対象範囲の外にある (計算では無視する)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial bool IsOutside { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial bool IsInvalid { get; set; }

    public bool HasWarning => IsOutside || IsInvalid;

    /// <summary>警告の文言 (「対象範囲の外です」、入力の誤り)。</summary>
    [ObservableProperty]
    public partial string Warning { get; set; } = string.Empty;

    /// <summary>範囲の表示 (例: <c>0x10–0x13 (4)</c>)。</summary>
    [ObservableProperty]
    public partial string RangeText { get; set; } = string.Empty;

    partial void OnStartTextChanged(string value) => owner.RefreshExclusions();

    partial void OnLengthTextChanged(string value) => owner.RefreshExclusions();

    internal void Resolve(IExpressionContext? context, long documentLength, IReadOnlyList<HashRange> targets)
    {
        Range = null;
        if (context is not null
            && ExpressionEvaluator.TryEvaluate(StartText, context, out long start, out _)
            && ExpressionEvaluator.TryEvaluate(LengthText, context, out long length, out _)
            && start >= 0 && length > 0 && start <= documentLength)
        {
            Range = new HashRange(start, Math.Min(length, documentLength - start));
        }

        IsInvalid = Range is null;
        IsOutside = Range is { } r && HashEngine.ExclusionsOutside(targets, [r]).Count > 0;
        Warning = IsInvalid ? Loc.Get("Hash_ExclusionInvalid") : IsOutside ? Loc.Get("Hash_ExclusionOutside") : string.Empty;
        RangeText = Range is { } x && x.Length > 0 ? $"0x{x.Offset:X}–0x{x.End - 1:X} ({x.Length.ToString("N0", CultureInfo.CurrentCulture)})" : string.Empty;
    }
}
