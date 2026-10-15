using HexEditor.Core.Templates.Loading;

namespace HexEditor.Core.Templates.Presentation;

/// <summary>インスペクタに出すテンプレートの型 1 つ (TPL-29)。</summary>
public sealed record TemplateInspectorType(string TemplatePath, string TypeName, string DisplayName)
{
    /// <summary>登録時の警告: 可変長の型、または 4 KB を超える型 (TPL-29 の仕様 4)。</summary>
    public bool NotFixedSize { get; init; }
}

/// <summary>インスペクタの行に出す値 (評価に失敗したら <see cref="Error"/>)。</summary>
public sealed record TemplateInspectorValue(string Display, IReadOnlyList<(string Name, string Value)> Fields, string? Error, TemplateNode? Node);

/// <summary>
/// テンプレートの型をデータインスペクタで使う (TPL-29)。設定「インスペクタ用テンプレート」に登録したテンプレートの
/// <c>[[inspector("表示名")]]</c> の型を集め、カーソル位置で追従 (TPL-26) と同じ上限 (200 ms・10 万パターン) で評価する。
/// </summary>
public sealed class TemplateInspectorTypes
{
    /// <summary>インスペクタ用の型の推奨の上限 (バイト)。</summary>
    public const int RecommendedMaxBytes = 4096;

    private readonly Dictionary<string, TemplateProgram> _programs = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TemplateInspectorType> Types { get; private set; } = [];

    /// <summary>読み込みのエラー (ファイル名と診断)。</summary>
    public IReadOnlyList<(string File, TemplateDiagnostic Diagnostic)> Errors { get; private set; } = [];

    /// <summary>テンプレートのファイルを読み込み直す (起動時と設定の変更時)。</summary>
    public void Load(IEnumerable<string> templatePaths, TemplateLoadOptions? options = null)
    {
        var types = new List<TemplateInspectorType>();
        var errors = new List<(string, TemplateDiagnostic)>();
        _programs.Clear();
        foreach (string path in templatePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TemplateProgram program = TemplateLoader.LoadFile(path, options ?? new TemplateLoadOptions { SearchPaths = [Path.GetDirectoryName(Path.GetFullPath(path))!] });
            foreach (TemplateDiagnostic d in program.Diagnostics.Where(d => d.Severity == TemplateSeverity.Error))
            {
                errors.Add((path, d));
            }

            if (program.HasErrors)
            {
                continue;
            }

            _programs[path] = program;
            foreach ((string type, string display) in program.InspectorTypes)
            {
                types.Add(new TemplateInspectorType(path, type, display) { NotFixedSize = !IsSmallFixed(program, type) });
            }
        }

        Types = types;
        Errors = errors;
    }

    /// <summary>可変長か 4 KB を超えるか (違うデータで 2 回評価して大きさを比べる)。</summary>
    private static bool IsSmallFixed(TemplateProgram program, string type)
    {
        long? size = null;
        foreach (byte fill in new byte[] { 0x00, 0xFF })
        {
            byte[] data = new byte[RecommendedMaxBytes * 2];
            Array.Fill(data, fill);
            using TemplateResult r = TemplateEngine.ApplyAndWait(program, TemplateDataSource.FromBytes(data), Options(type, 0));
            long s = r.Root.Size;
            if (size is { } previous && previous != s)
            {
                return false;
            }

            size = s;
        }

        return size is > 0 and <= RecommendedMaxBytes;
    }

    private static TemplateApplyOptions Options(string type, long offset) => new()
    {
        TypeName = type,
        StartOffset = offset,
        LimitOverride = TemplateApplication.FollowLimits,
    };

    /// <summary>
    /// カーソル位置で型を評価する (TPL-29 の仕様 2)。値は <c>format</c> があればその文字列、構造体なら子の一覧。評価に失敗したら
    /// <see cref="TemplateInspectorValue.Error"/> (インスペクタは「—」とツールチップで理由を出す)。結果は破棄しない (書き換えに使う)。
    /// </summary>
    public TemplateInspectorValue Evaluate(TemplateInspectorType type, TemplateDataSource data, long offset, out TemplateResult? result)
    {
        result = null;
        if (!_programs.TryGetValue(type.TemplatePath, out TemplateProgram? program))
        {
            return new TemplateInspectorValue("—", [], "not loaded", null);
        }

        TemplateResult r = TemplateEngine.ApplyAndWait(program, data, Options(type.TypeName, offset));
        result = r;
        return r.Invoke(() =>
        {
            if (r.Root.Children.Count == 0)
            {
                string reason = r.Diagnostics.FirstOrDefault(d => d.Severity == TemplateSeverity.Error)?.Message ?? "no value";
                return new TemplateInspectorValue("—", [], reason, null);
            }

            TemplateNode node = r.Root.Children[0];
            if (node.Diagnostics.FirstOrDefault(d => d.Severity == TemplateSeverity.Error) is { } error)
            {
                return new TemplateInspectorValue("—", [], error.Message, node);
            }

            string display = node.GetDisplay(TemplateDisplayContext.Current);
            var fields = new List<(string, string)>();
            if (node.Kind is TemplateNodeKind.Struct or TemplateNodeKind.Union)
            {
                foreach (TemplateNode c in node.Children.Take(64))
                {
                    fields.Add((c.DisplayName, c.GetDisplay(TemplateDisplayContext.Current)));
                }
            }

            return new TemplateInspectorValue(display, fields, null, node);
        });
    }

    /// <summary>インスペクタからの書き換え (TPL-22 と同じ規則。TPL-29 の仕様 3)。書き込む内容を返す。</summary>
    public static TemplateEditPlan PlanEdit(TemplateNode node, string input)
    {
        TemplateValueEditSession? session = node.Result.Invoke(() => TemplateValueEditSession.Create(node, documentReadOnly: false, out _));
        return session is null ? new TemplateEditPlan(0, null, TemplateEditError.Blocked, null) : session.Plan(input);
    }
}
