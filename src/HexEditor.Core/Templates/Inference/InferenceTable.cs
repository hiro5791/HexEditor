using System.Globalization;
using System.Text.RegularExpressions;
using HexEditor.Core.Templates.Editing;

namespace HexEditor.Core.Templates.Inference;

/// <summary>フィールドの表の行 1 つ (TPL-40 の仕様 3)。変更は新しい行で置き換える (取り消しの履歴に前の行を残す)。</summary>
public sealed record InferenceRow
{
    public required int Offset { get; init; }

    public required int Size { get; init; }

    /// <summary>名前 (埋め草は null)。</summary>
    public string? Name { get; init; }

    public required InferenceType Type { get; init; }

    /// <summary>列挙・フラグの型の名前 (<c>Kind_18</c>、<c>Flags_1A</c>)。</summary>
    public string? TypeName { get; init; }

    public required InferredKind Kind { get; init; }

    public required InferenceConfidence Confidence { get; init; }

    public required InferenceMessage Reason { get; init; }

    public InferenceMessage? NearMiss { get; init; }

    /// <summary>ほかの候補 (TPL-39 の仕様 6。先頭は推定した規則)。</summary>
    public IReadOnlyList<InferredRule> Candidates { get; init; } = [];

    /// <summary>このフィールドのエンディアン (数値でなければ null)。</summary>
    public bool? BigEndian { get; init; }

    /// <summary>列挙の値・フラグのビット。</summary>
    public IReadOnlyList<ulong> Values { get; init; } = [];

    public bool ManualName { get; init; }

    public bool ManualType { get; init; }

    /// <summary>結合・分割で範囲を変えた。</summary>
    public bool ManualRange { get; init; }

    /// <summary>「手で変更」の印 (TPL-40 の仕様 3)。</summary>
    public bool IsManual => ManualName || ManualType || ManualRange;

    /// <summary>確度 低 の注意の印 (TPL-39 の仕様 9)。</summary>
    public bool IsLowConfidence => Confidence == InferenceConfidence.Low;

    public int End => Offset + Size;

    /// <summary>画面の「型」の列の表示。</summary>
    public string TypeDisplay => Type.Display(TypeName);

    /// <summary>推定したフィールドから行を作る。</summary>
    public static InferenceRow From(InferredField field) => FromRule(field.Offset, field.Size, field.Name, field.Rule, field.Candidates, field.NearMiss);

    internal static InferenceRow FromRule(int offset, int size, string? name, InferredRule rule, IReadOnlyList<InferredRule> candidates, InferenceMessage? nearMiss) => new()
    {
        Offset = offset,
        Size = size,
        Name = name,
        Type = rule.Type,
        TypeName = TypeNameOf(rule.Type, offset),
        Kind = rule.Kind,
        Confidence = rule.Confidence,
        Reason = rule.Reason,
        NearMiss = nearMiss,
        Candidates = candidates,
        BigEndian = rule.BigEndian,
        Values = rule.Values,
    };

    internal static string? TypeNameOf(InferenceType type, int offset) => type.Id switch
    {
        "enum" => "Kind_" + offset.ToString("X2", CultureInfo.InvariantCulture),
        "flags" => "Flags_" + offset.ToString("X2", CultureInfo.InvariantCulture),
        _ => null,
    };
}

/// <summary>型の選択肢 1 つ (ほかの候補の規則、または大きさに合う型)。</summary>
public sealed record InferenceTypeChoice(InferenceType Type, InferredRule? Rule, string? TypeName)
{
    public string Display => Type.Display(TypeName);
}

/// <summary>
/// テンプレートの推定のパネルのフィールドの表 (TPL-40 の仕様 3)。名前・型の変更、前のフィールドとの結合、分割、取り消し・やり直し
/// (ドキュメントの Undo とは別の履歴)、「推定し直す」の後の手での変更の引き継ぎ。
/// </summary>
public sealed partial class InferenceTable
{
    private readonly Stack<List<InferenceRow>> _undo = new();
    private readonly Stack<List<InferenceRow>> _redo = new();
    private List<InferenceRow> _rows = [];

    public InferenceTable(TemplateInferenceResult? result = null)
    {
        if (result is not null)
        {
            Load(result, keepManual: false);
        }
    }

    public TemplateInferenceResult? Result { get; private set; }

    public IReadOnlyList<InferenceRow> Rows => _rows;

    /// <summary>レコード全体のエンディアン (下書きの <c>#pragma endian</c>)。</summary>
    public bool BigEndian => Result?.BigEndian ?? false;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>表が変わった (下書きを作り直す)。</summary>
    public event EventHandler? Changed;

    /// <summary>推定の結果を入れる。<paramref name="keepManual"/> なら、同じ列・同じ大きさのフィールドの手で変えた名前と型を引き継ぐ。</summary>
    public void Load(TemplateInferenceResult result, bool keepManual)
    {
        List<InferenceRow> previous = _rows;
        Result = result;
        var rows = result.Fields.Select(InferenceRow.From).ToList();
        if (keepManual)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                InferenceRow row = rows[i];
                if (previous.FirstOrDefault(p => p.Offset == row.Offset && p.Size == row.Size) is not { } old)
                {
                    continue;
                }

                if (old.ManualType)
                {
                    row = row with
                    {
                        Type = old.Type,
                        TypeName = old.TypeName,
                        Kind = old.Kind,
                        Values = old.Values,
                        BigEndian = old.BigEndian,
                        ManualType = true,
                        Name = old.Type.Id == "padding" ? null : row.Name ?? old.Name,
                    };
                }

                if (old.ManualName && old.Name is not null && row.Type.Id != "padding")
                {
                    row = row with { Name = old.Name, ManualName = true };
                }

                rows[i] = row;
            }

            // 引き継いだ名前と、推定し直した名前が重なったら、推定した方に番号を付ける
            var used = new HashSet<string>(rows.Where(r => r.ManualName && r.Name is not null).Select(r => r.Name!), StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++)
            {
                if (!rows[i].ManualName && rows[i].Name is { } name)
                {
                    rows[i] = rows[i] with { Name = Unique(name, used) };
                }
            }
        }

        _rows = rows;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>表を空にする (「固定長のレコードが見つかりませんでした」など)。</summary>
    public void Clear(TemplateInferenceResult? result)
    {
        Result = result;
        _rows = [];
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ───────────── 名前 ─────────────

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    private static readonly Lazy<HashSet<string>> ReservedNames = new(() =>
    {
        TemplateSyntaxLists lists = TemplateLanguageService.SyntaxLists();
        return new HashSet<string>(lists.Keywords.Concat(Runtime.BuiltinTypes.Names).Append("Record").Append("records"), StringComparer.Ordinal);
    });

    /// <summary>名前が正しくなければ理由 (正しければ null)。識別子として正しくない名前・予約語・同じ名前 (TPL-40 の仕様 3)。</summary>
    public InferenceMessage? ValidateName(int index, string name)
    {
        if (_rows[index].Type.Id == "padding")
        {
            return new InferenceMessage("TplInfer_Name_Padding");
        }

        if (!IdentifierPattern().IsMatch(name))
        {
            return new InferenceMessage("TplInfer_Name_Invalid", name);
        }

        if (ReservedNames.Value.Contains(name) || _rows.Any(r => r.TypeName == name))
        {
            return new InferenceMessage("TplInfer_Name_Reserved", name);
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            if (i != index && string.Equals(_rows[i].Name, name, StringComparison.Ordinal))
            {
                return new InferenceMessage("TplInfer_Name_Duplicate", name);
            }
        }

        return null;
    }

    /// <summary>名前を変える。正しくない名前は確定しない (false)。</summary>
    public bool Rename(int index, string name)
    {
        if (index < 0 || index >= _rows.Count || ValidateName(index, name) is not null)
        {
            return false;
        }

        if (_rows[index].Name == name)
        {
            return true;
        }

        Mutate(rows => rows[index] = rows[index] with { Name = name, ManualName = true });
        return true;
    }

    // ───────────── 型 ─────────────

    /// <summary>行の型の選択肢: ほかの候補と、大きさに合う型 (TPL-40 の仕様 3)。</summary>
    public IReadOnlyList<InferenceTypeChoice> TypeChoices(int index)
    {
        InferenceRow row = _rows[index];
        int n = row.Size;
        var list = new List<InferenceTypeChoice>();
        foreach (InferredRule r in row.Candidates)
        {
            Add(new InferenceTypeChoice(r.Type, r, InferenceRow.TypeNameOf(r.Type, row.Offset)));
        }

        if (row.Type.IsEnumOrFlags)
        {
            Add(new InferenceTypeChoice(row.Type, null, row.TypeName));
        }

        string bits = (n * 8).ToString(CultureInfo.InvariantCulture);
        if (n is 1 or 2 or 4 or 8)
        {
            Add(new InferenceTypeChoice(new InferenceType("u" + bits, n), null, null));
            Add(new InferenceTypeChoice(new InferenceType("s" + bits, n), null, null));
        }

        foreach (string id in n switch
        {
            4 => new[] { "f32", "time32_t", "dosdatetime" },
            8 => ["f64", "time64_t", "filetime"],
            _ => [],
        })
        {
            Add(new InferenceTypeChoice(new InferenceType(id, n), null, null));
        }

        Add(new InferenceTypeChoice(new InferenceType("char", n), null, null));
        if (n % 2 == 0)
        {
            Add(new InferenceTypeChoice(new InferenceType("char16", n), null, null));
        }

        Add(new InferenceTypeChoice(new InferenceType("bytes", n), null, null));
        Add(new InferenceTypeChoice(new InferenceType("padding", n), null, null));
        return list;

        void Add(InferenceTypeChoice choice)
        {
            if (!list.Any(c => c.Type.Id == choice.Type.Id))
            {
                list.Add(choice);
            }
        }
    }

    /// <summary>型を変える (<see cref="TypeChoices"/> の 1 つ、または型の ID)。</summary>
    public bool SetType(int index, InferenceTypeChoice choice)
    {
        if (index < 0 || index >= _rows.Count || choice.Type.Size != _rows[index].Size)
        {
            return false;
        }

        InferenceRow row = _rows[index];
        if (row.Type.Id == choice.Type.Id && row.TypeName == choice.TypeName)
        {
            return true;
        }

        Mutate(rows =>
        {
            InferredKind kind = choice.Rule?.Kind ?? KindOfType(choice.Type, row.Kind);
            string? name = choice.Type.Id == "padding" ? null
                : row.Name ?? Unique(FieldInferrer.BaseName(kind, row.Offset) ?? "field", NamesExcept(rows, index));
            rows[index] = row with
            {
                Type = choice.Type,
                TypeName = choice.TypeName,
                Kind = kind,
                Values = choice.Rule?.Values ?? (choice.Type.IsEnumOrFlags ? row.Values : []),
                BigEndian = choice.Type.IsEndianSensitive ? choice.Rule?.BigEndian ?? row.BigEndian ?? BigEndian : null,
                Confidence = choice.Rule?.Confidence ?? row.Confidence,
                Reason = choice.Rule?.Reason ?? row.Reason,
                Name = name,
                ManualType = true,
            };
        });
        return true;
    }

    /// <summary>型の ID (<c>s32</c> など) で型を変える。</summary>
    public bool SetType(int index, string typeId)
    {
        InferenceTypeChoice? choice = TypeChoices(index).FirstOrDefault(c => c.Type.Id == typeId);
        return choice is not null && SetType(index, choice);
    }

    private static InferredKind KindOfType(InferenceType type, InferredKind current) => type.Id switch
    {
        "f32" or "f64" => InferredKind.Float,
        "time32_t" or "time64_t" or "filetime" or "dosdatetime" => InferredKind.Time,
        "char" => InferredKind.AsciiString,
        "char16" => InferredKind.Utf16String,
        "padding" => InferredKind.Padding,
        "bytes" => current is InferredKind.Magic or InferredKind.Constant ? current : InferredKind.Unknown,
        "enum" => InferredKind.Enum,
        "flags" => InferredKind.Flags,
        _ => current is InferredKind.Counter or InferredKind.Offset or InferredKind.Length ? current : InferredKind.Integer,
    };

    // ───────────── 結合・分割 ─────────────

    public bool CanMergeWithPrevious(int index) => Result?.Inferrer is not null && index > 0 && index < _rows.Count;

    public bool CanSplit(int index) => Result?.Inferrer is not null && index >= 0 && index < _rows.Count && _rows[index].Size >= 2;

    /// <summary>「前のフィールドと結合」: 前のフィールドと 1 つにし、その範囲で規則を当て直す。</summary>
    public bool MergeWithPrevious(int index)
    {
        if (!CanMergeWithPrevious(index))
        {
            return false;
        }

        InferenceRow first = _rows[index - 1], second = _rows[index];
        Mutate(rows =>
        {
            rows.RemoveAt(index);
            rows[index - 1] = Reinfer(rows, index - 1, first.Offset, second.End - first.Offset);
        });
        return true;
    }

    /// <summary>「分割...」: フィールドの先頭から <paramref name="at"/> バイトの位置で 2 つに分け、それぞれに規則を当て直す。</summary>
    public bool Split(int index, int at)
    {
        if (!CanSplit(index) || at <= 0 || at >= _rows[index].Size)
        {
            return false;
        }

        InferenceRow row = _rows[index];
        Mutate(rows =>
        {
            rows[index] = Reinfer(rows, index, row.Offset, at, exclude: index);
            rows.Insert(index + 1, row with { Name = null });
            rows[index + 1] = Reinfer(rows, index + 1, row.Offset + at, row.Size - at);
        });
        return true;
    }

    private InferenceRow Reinfer(List<InferenceRow> rows, int index, int offset, int size, int exclude = -1)
    {
        InferredField field = Result!.Inferrer!.InferSpan(offset, size);
        string? name = field.Name is null ? null : Unique(field.Name, NamesExcept(rows, exclude < 0 ? index : exclude));
        return InferenceRow.FromRule(offset, size, name, field.Rule, field.Candidates, field.NearMiss) with { ManualRange = true };
    }

    // ───────────── 取り消し・やり直し ─────────────

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Push(_rows);
        _rows = _undo.Pop();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        _undo.Push(_rows);
        _rows = _redo.Pop();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Mutate(Action<List<InferenceRow>> change)
    {
        var next = new List<InferenceRow>(_rows);
        change(next);
        _undo.Push(_rows);
        _redo.Clear();
        _rows = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>列 (レコードの先頭からの位置) を含む行の番号 (なければ −1)。</summary>
    public int RowAt(int column) => _rows.FindIndex(r => column >= r.Offset && column < r.End);

    private static HashSet<string> NamesExcept(List<InferenceRow> rows, int index)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i != index && rows[i].Name is { } n)
            {
                set.Add(n);
            }
        }

        return set;
    }

    private static string Unique(string name, HashSet<string> used)
    {
        string unique = name;
        for (int i = 2; !used.Add(unique); i++)
        {
            unique = name + "_" + i.ToString(CultureInfo.InvariantCulture);
        }

        return unique;
    }
}
