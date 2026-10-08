namespace HexEditor.Core.Commands;

/// <summary>割り当ての由来 (UI-18 の仕様 1)。</summary>
public enum BindingOrigin
{
    Default,
    Preset,
    User,
}

/// <summary>コマンドへのキーの割り当て 1 つ。</summary>
public sealed record KeyAssignment(string Command, KeyBinding Binding)
{
    public KeyChord Chord => Binding.Chord;

    public KeyScope Scope => Binding.Scope;
}

/// <summary>今の割り当て (既定・プリセット・利用者の変更を反映したもの) 1 つと、その由来。</summary>
public sealed record EffectiveBinding(string Command, KeyBinding Binding, BindingOrigin Origin);

public enum KeyMatchKind
{
    /// <summary>どのコマンドにも割り当てられていない。</summary>
    None,

    /// <summary>2 打鍵の連続キーの 1 打鍵目。次の打鍵を待つ。</summary>
    Prefix,

    /// <summary>コマンドに一致した。</summary>
    Command,
}

public readonly record struct KeyResolution(KeyMatchKind Kind, EffectiveBinding? Binding = null);

/// <summary>同じ有効範囲で重なる 2 つの割り当て (UI-18 の仕様 6)。</summary>
public sealed record KeyConflict(KeyAssignment First, KeyAssignment Second);

/// <summary>プリセットを切り替えるときの、利用者の割り当てとプリセットの割り当ての重複 (UI-19 の仕様 4)。</summary>
public sealed record PresetSwitchPreview(string Preset, IReadOnlyList<KeyConflict> Conflicts, string? Error);

public enum KeyImportMode
{
    /// <summary>今の利用者の割り当てを捨てて、読み込んだものにする。</summary>
    Replace,

    /// <summary>読み込んだ側を優先して追加する。</summary>
    Merge,
}

/// <summary>インポートの差分 (UI-21 の仕様 2、3)。<see cref="KeyMap.ApplyImport"/> に渡して確定する。</summary>
public sealed record KeyImportPreview(
    KeyImportMode Mode,
    IReadOnlyList<KeyAssignment> Added,
    IReadOnlyList<KeyAssignment> Removed,
    IReadOnlyList<string> ChangedCommands,
    IReadOnlyList<KeyConflict> Conflicts,
    int UnknownCommands,
    string ResultPreset,
    IReadOnlyList<KeyBindingEntry> ResultEntries);

/// <summary>
/// キー割り当て (UI-18〜UI-21): 既定 (コマンド登録の既定のショートカット) にプリセットの差分 (UI-19) を当て、さらに利用者の差分
/// (keybindings.json) を当てたもの。利用者の差分は、今の割り当てとプリセットの割り当ての違いとして持つ (UI-18 の仕様 9)。
/// 知らないコマンドの行 (後で読み込むプラグインのものなど) は捨てずに残す。アプリ全体で 1 つ (変更は全ウィンドウに反映する)。
/// </summary>
public sealed class KeyMap
{
    private readonly CommandCatalog _catalog;
    private List<KeyBindingEntry> _presetEntries = [];
    private List<KeyBindingEntry> _user = [];
    private List<KeyAssignment> _base = [];
    private HashSet<KeyAssignment> _presetAdds = [];
    private HashSet<KeyAssignment> _userAdds = [];
    private List<EffectiveBinding> _effective = [];

    public KeyMap(CommandCatalog catalog)
    {
        _catalog = catalog;
        _catalog.Changed += (_, _) => Rebuild();
        Rebuild();
    }

    public CommandCatalog Catalog => _catalog;

    /// <summary>選んでいるプリセットの ID。</summary>
    public string Preset { get; private set; } = KeyPresets.Default;

    /// <summary>プリセットが読めなかった理由 (<c>default</c> を使っている)。</summary>
    public string? PresetError { get; private set; }

    /// <summary>利用者の差分 (keybindings.json の bindings)。</summary>
    public IReadOnlyList<KeyBindingEntry> UserEntries => _user;

    /// <summary>今の割り当てのすべて。</summary>
    public IReadOnlyList<EffectiveBinding> All => _effective;

    /// <summary>割り当てが変わった。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<EffectiveBinding> BindingsFor(string command)
    {
        command = _catalog.Canonical(command);
        return _effective.Where(b => b.Command == command).ToList();
    }

    /// <summary>既定 + プリセットの割り当て (利用者の変更の前)。</summary>
    public IReadOnlyList<KeyAssignment> BaseAssignments => _base;

    // ---- 読み込みと保存 ----

    public void Load(KeyBindingsDocument document)
    {
        SetPresetCore(document.Preset);
        _user = [.. document.Bindings];
        Rebuild();
    }

    public KeyBindingsDocument ToDocument() => new() { Preset = Preset, Bindings = [.. _user] };

    // ---- キーの振り分け ----

    /// <summary>
    /// 押したキー (1 打鍵、または連続キーの 2 打鍵) をコマンドに振り分ける。<paramref name="scopes"/> は狭い順
    /// (例: エディタ、グローバル)。狭い有効範囲の割り当てが優先する (00-overview.md 8.1)。
    /// </summary>
    public KeyResolution Resolve(IReadOnlyList<KeyStroke> pressed, IReadOnlyList<KeyScope> scopes)
    {
        foreach (KeyScope scope in scopes)
        {
            bool prefix = false;
            foreach (EffectiveBinding b in _effective)
            {
                if (b.Binding.Scope != scope)
                {
                    continue;
                }

                IReadOnlyList<KeyStroke> strokes = b.Binding.Chord.Strokes;
                if (strokes.Count == pressed.Count && strokes.SequenceEqual(pressed))
                {
                    return new KeyResolution(KeyMatchKind.Command, b);
                }

                if (strokes.Count > pressed.Count && strokes.Take(pressed.Count).SequenceEqual(pressed))
                {
                    prefix = true;
                }
            }

            if (prefix)
            {
                return new KeyResolution(KeyMatchKind.Prefix);
            }
        }

        return new KeyResolution(KeyMatchKind.None);
    }

    // ---- 重複 ----

    /// <summary>同じ有効範囲で、他のコマンドに割り当て済みのもの (同じキー、または連続キーの先頭が同じ)。</summary>
    public IReadOnlyList<EffectiveBinding> FindConflicts(string command, KeyBinding binding)
    {
        command = _catalog.Canonical(command);
        return _effective.Where(b => b.Command != command && Overlaps(b.Binding, binding)).ToList();
    }

    /// <summary>狭い有効範囲の割り当てが隠すグローバルの割り当て (確定はできるが警告する。UI-18 の仕様 6)。</summary>
    public IReadOnlyList<EffectiveBinding> FindShadowed(string command, KeyBinding binding)
    {
        command = _catalog.Canonical(command);
        return binding.Scope == KeyScope.Global
            ? []
            : _effective.Where(b => b.Command != command && b.Binding.Scope == KeyScope.Global && b.Binding.Chord == binding.Chord).ToList();
    }

    private static bool Overlaps(KeyBinding a, KeyBinding b) =>
        a.Scope == b.Scope && (a.Chord == b.Chord || a.Chord.OverlapsPrefix(b.Chord));

    // ---- 変更 (UI-18) ----

    /// <summary>
    /// キーを追加する。<paramref name="replaceConflicts"/> なら、重複する他のコマンドの割り当てを外す (「置き換える」)。
    /// 重複を残したままは追加しない (同じ有効範囲で 1 つのキーに 2 つのコマンドは割り当てられない)。追加したら true。
    /// </summary>
    public bool Add(string command, KeyBinding binding, bool replaceConflicts)
    {
        command = _catalog.Canonical(command);
        if (KeyValidation.Check(binding.Chord) != KeyRejection.None || !_catalog.Contains(command))
        {
            return false;
        }

        var conflicts = FindConflicts(command, binding);
        if (conflicts.Count > 0 && !replaceConflicts)
        {
            return false;
        }

        List<KeyAssignment> desired = Current();
        desired.RemoveAll(a => conflicts.Any(c => c.Command == a.Command && c.Binding == a.Binding));
        var added = new KeyAssignment(command, binding);
        if (!desired.Contains(added))
        {
            desired.Add(added);
        }

        SetEffective(desired);
        return true;
    }

    public void Remove(string command, KeyBinding binding)
    {
        command = _catalog.Canonical(command);
        List<KeyAssignment> desired = Current();
        desired.Remove(new KeyAssignment(command, binding));
        SetEffective(desired);
    }

    /// <summary>コマンド 1 つを既定 (プリセットの割り当て) に戻す (UI-18 の仕様 8)。</summary>
    public void ResetCommand(string command)
    {
        command = _catalog.Canonical(command);
        List<KeyAssignment> desired = Current();
        desired.RemoveAll(a => a.Command == command);
        desired.AddRange(_base.Where(a => a.Command == command));
        SetEffective(desired);
    }

    /// <summary>すべて既定に戻す: 利用者の差分を空にする (UI-18 の受け入れ基準 5)。</summary>
    public void ResetAll()
    {
        _user = [];
        Rebuild();
    }

    // ---- プリセット (UI-19) ----

    public PresetSwitchPreview PreviewPreset(string preset)
    {
        IReadOnlyList<KeyBindingEntry> entries = KeyPresets.Load(preset, out string? error);
        List<KeyAssignment> newBase = Apply(Defaults(), entries, out _);
        List<KeyAssignment> afterUser = Apply(newBase, _user, out _);
        var conflicts = new List<KeyConflict>();
        foreach (KeyAssignment user in UserAdds())
        {
            foreach (KeyAssignment other in afterUser)
            {
                if (other.Command != user.Command && Overlaps(other.Binding, user.Binding) && newBase.Contains(other)
                    && !_base.Contains(other))
                {
                    conflicts.Add(new KeyConflict(user, other));
                }
            }
        }

        return new PresetSwitchPreview(preset, conflicts, error);
    }

    /// <summary>
    /// プリセットを切り替える。利用者が個別に変更した割り当ては保つ (UI-19 の仕様 4)。重複は <paramref name="preferUser"/> なら
    /// プリセットの割り当てを外し、そうでなければ利用者の割り当てを外す。
    /// </summary>
    public void SwitchPreset(string preset, bool preferUser)
    {
        PresetSwitchPreview preview = PreviewPreset(preset);
        SetPresetCore(preset);
        foreach (KeyConflict conflict in preview.Conflicts)
        {
            if (preferUser)
            {
                _user.Add(new KeyBindingEntry(conflict.Second.Command, conflict.Second.Chord, conflict.Second.Scope, Remove: true));
            }
            else
            {
                _user.RemoveAll(e => !e.Remove && e.Command == conflict.First.Command && e.Binding == conflict.First.Binding);
            }
        }

        Rebuild();
    }

    // ---- インポート (UI-21) ----

    /// <summary>
    /// 読み込むファイルと今の割り当ての差分を作る。読み込んだ割り当てと重複する割り当ては、確定のときに外す
    /// (読み込んだ側が優先)。存在しないコマンド ID は無視して数える。
    /// </summary>
    public KeyImportPreview PreviewImport(KeyBindingsDocument document, KeyImportMode mode)
    {
        var known = document.Bindings.Where(e => _catalog.Contains(e.Command))
            .Select(e => e with { Command = _catalog.Canonical(e.Command) }).ToList();
        int unknown = document.Bindings.Count - known.Count;

        string preset = mode == KeyImportMode.Replace && KeyPresets.Exists(document.Preset) ? document.Preset : Preset;
        List<KeyAssignment> start = mode == KeyImportMode.Replace
            ? Apply(Defaults(), KeyPresets.Load(preset, out _), out _)
            : Current();

        // まず素直に当て、読み込んだ割り当てと重なるものを重複として集める。
        List<KeyAssignment> naive = Apply(start, known, out _);
        var imported = known.Where(e => !e.Remove).Select(e => new KeyAssignment(e.Command, e.Binding)).ToList();
        var conflicts = new List<KeyConflict>();
        foreach (KeyAssignment a in imported)
        {
            foreach (KeyAssignment other in naive)
            {
                if (other.Command != a.Command && Overlaps(other.Binding, a.Binding) && !imported.Contains(other))
                {
                    conflicts.Add(new KeyConflict(a, other));
                }
            }
        }

        List<KeyAssignment> result = naive.Where(a => !conflicts.Any(c => c.Second == a)).ToList();
        List<KeyAssignment> resultBase = Apply(Defaults(), KeyPresets.Load(preset, out _), out _);
        List<KeyBindingEntry> entries = Derive(resultBase, result);
        if (mode == KeyImportMode.Merge)
        {
            entries.AddRange(Orphans());
        }

        List<KeyAssignment> current = Current();
        var added = result.Where(a => !current.Contains(a)).ToList();
        var removed = current.Where(a => !result.Contains(a)).ToList();
        var changed = added.Select(a => a.Command).Concat(removed.Select(a => a.Command)).Distinct().ToList();
        return new KeyImportPreview(mode, added, removed, changed, conflicts, unknown, preset, entries);
    }

    public void ApplyImport(KeyImportPreview preview)
    {
        SetPresetCore(preview.ResultPreset);
        _user = [.. preview.ResultEntries];
        Rebuild();
    }

    // ---- 内部 ----

    private void SetPresetCore(string preset)
    {
        _presetEntries = [.. KeyPresets.Load(preset, out string? error)];
        PresetError = error;
        Preset = error is null && KeyPresets.Exists(preset) ? preset : KeyPresets.Default;
    }

    private List<KeyAssignment> Defaults() =>
        _catalog.All.SelectMany(c => c.DefaultBindings.Select(b => new KeyAssignment(c.Id, b))).ToList();

    private List<KeyAssignment> Current() => _effective.Select(b => new KeyAssignment(b.Command, b.Binding)).ToList();

    private IEnumerable<KeyAssignment> UserAdds() => _userAdds;

    private IEnumerable<KeyBindingEntry> Orphans() => _user.Where(e => !_catalog.Contains(e.Command));

    /// <summary>差分を当てる。知らないコマンドの行は飛ばす。<paramref name="added"/> は追加で入った割り当て。</summary>
    private List<KeyAssignment> Apply(IEnumerable<KeyAssignment> start, IEnumerable<KeyBindingEntry> entries, out HashSet<KeyAssignment> added)
    {
        var list = start.ToList();
        added = [];
        foreach (KeyBindingEntry e in entries)
        {
            if (!_catalog.Contains(e.Command))
            {
                continue;
            }

            var a = new KeyAssignment(_catalog.Canonical(e.Command), e.Binding);
            if (e.Remove)
            {
                list.Remove(a);
                added.Remove(a);
            }
            else if (!list.Contains(a))
            {
                list.Add(a);
                added.Add(a);
            }
        }

        return list;
    }

    /// <summary>望む割り当てと、もとの割り当ての違いを差分の行にする。</summary>
    private static List<KeyBindingEntry> Derive(List<KeyAssignment> start, List<KeyAssignment> desired)
    {
        var entries = new List<KeyBindingEntry>();
        foreach (KeyAssignment a in start.Where(a => !desired.Contains(a)))
        {
            entries.Add(new KeyBindingEntry(a.Command, a.Chord, a.Scope, Remove: true));
        }

        foreach (KeyAssignment a in desired.Where(a => !start.Contains(a)))
        {
            entries.Add(new KeyBindingEntry(a.Command, a.Chord, a.Scope));
        }

        return entries;
    }

    private void SetEffective(List<KeyAssignment> desired)
    {
        _user = [.. Derive(_base, desired), .. Orphans()];
        Rebuild();
    }

    private void Rebuild()
    {
        _base = Apply(Defaults(), _presetEntries, out HashSet<KeyAssignment> presetAdds);
        _presetAdds = presetAdds;
        List<KeyAssignment> effective = Apply(_base, _user, out HashSet<KeyAssignment> userAdds);
        _userAdds = userAdds;
        _effective = effective.Select(a => new EffectiveBinding(a.Command, a.Binding,
            _userAdds.Contains(a) ? BindingOrigin.User : _presetAdds.Contains(a) ? BindingOrigin.Preset : BindingOrigin.Default)).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
