using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Coloring;

namespace HexEditor.App.ViewModels;

/// <summary>色付けルールの一覧の 1 行 (INSP-33 の「画面」): 有効のチェック、色見本と文字での色の説明、名前、条件の要約。</summary>
public sealed partial class ColoringRuleItem(ColoringRule rule) : ObservableObject
{
    public ColoringRule Rule { get; private set; } = rule;

    public string Id => Rule.Id;

    public bool Enabled => Rule.Enabled;

    public string Name => Rule.Name.Length > 0 ? Rule.Name : Loc.Get("Coloring_Unnamed");

    /// <summary>条件の要約 (例: 「バイト値 20-7E」)。</summary>
    public string Summary => Loc.Format("Coloring_Summary", Loc.Get("Coloring_Kind_" + Rule.Kind), Rule.Kind switch
    {
        ColoringConditionKind.OffsetRange => Rule.Pattern + "〜" + Rule.EndExpression,
        ColoringConditionKind.Period => $"mod {Rule.Modulus} = {Rule.Remainder}, {Rule.PeriodLength}",
        ColoringConditionKind.Number => $"{Rule.NumberType} {(Rule.BigEndian ? "BE" : "LE")} {Rule.Pattern}",
        _ => Rule.Pattern,
    });

    /// <summary>色の文字での説明 (色見本だけで伝えない)。</summary>
    public string ColorText => string.Join(", ", new[]
    {
        Rule.Foreground is { } f ? Loc.Format("Coloring_ForegroundText", ColoringRule.Hex(f)) : null,
        Rule.Background is { } b ? Loc.Format("Coloring_BackgroundText", ColoringRule.Hex(b)) : null,
        Rule.Border != ColoringBorder.None ? Loc.Get("Coloring_Border_" + Rule.Border) : null,
    }.Where(s => s is not null));

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.Brush? ForegroundSwatch { get; set; }

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.Brush? BackgroundSwatch { get; set; }

    /// <summary>条件の誤り (構文の誤り。INSP-33 の「エラー」)。なければ空。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; } = string.Empty;

    public bool HasError => Error.Length > 0;

    public string AutomationName => Name + ", " + Summary + (ColorText.Length > 0 ? ", " + ColorText : string.Empty)
        + ", " + Loc.Get(Enabled ? "Coloring_Enabled" : "Coloring_Disabled");

    public void Replace(ColoringRule rule)
    {
        Rule = rule;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// 「色付けルール」パネルの状態 (INSP-33 の仕様 7、INSP-34 の仕様 1): 「全体」「このドキュメント」の切り替え、ルールの一覧 (上ほど優先)、
/// 選んだルール。変更はその場で表示に反映し、全体のルールは設定、ドキュメントのルールは付随データに保存する。
/// </summary>
public sealed partial class ColoringRulesViewModel : ObservableObject
{
    private DocumentAnnotations? _annotations;

    public ObservableCollection<ColoringRuleItem> Items { get; } = [];

    [ObservableProperty]
    public partial ColoringScope Scope { get; set; } = ColoringScope.Document;

    partial void OnScopeChanged(ColoringScope value) => Reload();

    [ObservableProperty]
    public partial ColoringRuleItem? Selected { get; set; }

    /// <summary>ルールを選び直した・置き換えた (編集欄を入れ直す)。</summary>
    public event EventHandler? SelectedRuleChanged;

    partial void OnSelectedChanged(ColoringRuleItem? value) => SelectedRuleChanged?.Invoke(this, EventArgs.Empty);

    public DocumentAnnotations? Annotations => _annotations;

    /// <summary>エラーの文を作る (UI の言語のリソースから)。</summary>
    public static string ErrorText(ColoringRuleException ex) => Loc.Format("Coloring_Error_" + ex.Kind, ex.Detail);

    public void Attach(DocumentAnnotations? annotations)
    {
        _annotations = annotations;
        Reload();
    }

    /// <summary>今の集まりのルール。</summary>
    public IReadOnlyList<ColoringRule> Rules => Scope == ColoringScope.Global ? GlobalColoringRules.Rules : _annotations?.ColoringRules ?? [];

    /// <summary>一覧を作り直す (選んでいたルールは選んだまま)。</summary>
    public void Reload()
    {
        string? keep = Selected?.Id;
        IReadOnlyList<ColoringRule> rules = Rules;
        IReadOnlyDictionary<string, ColoringRuleException> errors = _annotations?.Coloring.Rules.Errors ?? new Dictionary<string, ColoringRuleException>();
        bool same = Items.Count == rules.Count && Items.Zip(rules).All(p => p.First.Id == p.Second.Id);
        if (!same)
        {
            Items.Clear();
            foreach (ColoringRule rule in rules)
            {
                Items.Add(Prepare(new ColoringRuleItem(rule)));
            }
        }

        for (int i = 0; i < rules.Count; i++)
        {
            if (Items[i].Rule != rules[i])
            {
                Items[i].Replace(rules[i]);
                Prepare(Items[i]);
            }

            Items[i].Error = errors.TryGetValue(rules[i].Id, out ColoringRuleException? ex) ? ErrorText(ex) : CompileError(rules[i]);
        }

        Selected = Items.FirstOrDefault(i => i.Id == keep) ?? (same ? Selected : null);
    }

    private static string CompileError(ColoringRule rule)
    {
        try
        {
            CompiledColoringRule.Compile(rule, null);
            return string.Empty;
        }
        catch (ColoringRuleException ex) when (ex.Kind != ColoringRuleErrorKind.InvalidExpression)
        {
            return ErrorText(ex);
        }
        catch (ColoringRuleException)
        {
            // 名前 (sel.start など) を使う式は、ドキュメントの文脈で解釈できたかを Errors で見る。
            return string.Empty;
        }
    }

    private static ColoringRuleItem Prepare(ColoringRuleItem item)
    {
        item.ForegroundSwatch = item.Rule.Foreground is { } f ? MainWindow.RuleBrush(f) : null;
        item.BackgroundSwatch = item.Rule.Background is { } b ? MainWindow.RuleBrush(b) : null;
        return item;
    }

    /// <summary>選んで表示する (「選択範囲の値で色付けルールを作成」の後など)。</summary>
    public void Show(ColoringScope scope, string id)
    {
        Scope = scope;
        Reload();
        Selected = Items.FirstOrDefault(i => i.Id == id);
    }

    private void Save(IReadOnlyList<ColoringRule> rules)
    {
        if (Scope == ColoringScope.Global)
        {
            GlobalColoringRules.Set(rules);
        }
        else
        {
            _annotations?.SetColoringRules(rules);
        }

        Reload();
    }

    /// <summary>ルールを加える (上限 256 件。INSP-33 の仕様 4)。選んでいるルールの下に置く。</summary>
    public bool Add()
    {
        List<ColoringRule> rules = [.. Rules];
        if (rules.Count >= ColoringRule.MaxRules || Scope == ColoringScope.Document && _annotations is null)
        {
            return false;
        }

        var rule = new ColoringRule
        {
            Name = Loc.Format("Coloring_DefaultName", (rules.Count + 1).ToString(CultureInfo.CurrentCulture)),
            Pattern = "00",
            Background = 0xFFE680,
        };
        int at = Selected is { } s ? rules.FindIndex(r => r.Id == s.Id) + 1 : rules.Count;
        rules.Insert(Math.Clamp(at, 0, rules.Count), rule);
        Save(rules);
        Selected = Items.FirstOrDefault(i => i.Id == rule.Id);
        return true;
    }

    public void Delete()
    {
        if (Selected is not { } item)
        {
            return;
        }

        List<ColoringRule> rules = [.. Rules];
        int index = rules.FindIndex(r => r.Id == item.Id);
        rules.RemoveAt(index);
        Save(rules);
        Selected = Items.Count == 0 ? null : Items[Math.Clamp(index, 0, Items.Count - 1)];
    }

    /// <summary>優先順位を変える (上ほど優先。INSP-34 の仕様 1)。</summary>
    public void Move(int delta)
    {
        if (Selected is not { } item)
        {
            return;
        }

        List<ColoringRule> rules = [.. Rules];
        int index = rules.FindIndex(r => r.Id == item.Id);
        int target = Math.Clamp(index + delta, 0, rules.Count - 1);
        if (target == index)
        {
            return;
        }

        ColoringRule rule = rules[index];
        rules.RemoveAt(index);
        rules.Insert(target, rule);
        Save(rules);
        Selected = Items.FirstOrDefault(i => i.Id == rule.Id);
    }

    /// <summary>有効 / 無効を切り替える (Space、チェック)。</summary>
    public void SetEnabled(ColoringRuleItem item, bool enabled) => Update(item, r => r with { Enabled = enabled });

    /// <summary>ルールを書き換える (編集欄の変更はすぐ反映する)。</summary>
    public void Update(ColoringRuleItem item, Func<ColoringRule, ColoringRule> change)
    {
        List<ColoringRule> rules = [.. Rules];
        int index = rules.FindIndex(r => r.Id == item.Id);
        if (index < 0)
        {
            return;
        }

        ColoringRule updated = change(rules[index]);

        // 条件の構文の誤りがあるルールは無効のまま保存する (INSP-33 の「エラー」)。
        if (updated.Enabled && CompileError(updated).Length > 0)
        {
            updated = updated with { Enabled = false };
        }

        if (updated == rules[index])
        {
            return;
        }

        rules[index] = updated;
        Save(rules);
    }
}
