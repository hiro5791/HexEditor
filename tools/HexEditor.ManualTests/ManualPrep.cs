using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HexEditor.ManualTests;

/// <summary>準備ボタンが起動する HexEditor (docs/test/manual-test-prepare.md の「起動」)。</summary>
public enum LaunchKind
{
    /// <summary>開発用のビルド (src/HexEditor.App/bin の Debug。テスト用の仕組みが入っている)。</summary>
    Dev,

    /// <summary>開発用のビルドを管理者として。</summary>
    DevAdmin,

    /// <summary>ツールが作ったポータブル版。</summary>
    Portable,

    /// <summary>ツールが作ったポータブル版を管理者として。</summary>
    PortableAdmin,

    /// <summary>インストールしたインストーラ版。</summary>
    Installer,

    /// <summary>HexEditor を起動しない (別の PC で行うものなど)。</summary>
    None,
}

/// <summary>機材 (この PC のほかに要るもの) か、この PC で行う設定・操作 (環境) の用意のしかた。</summary>
public sealed record PrepNeed(string Name, bool IsEquipment, IReadOnlyList<string> Lines);

/// <summary>準備ボタンで作れないテストデータの用意のしかた。</summary>
public sealed record DataHowTo(string Id, IReadOnlyList<string> Lines);

/// <summary>テストケースごとの準備 (docs/test/manual-test-prepare.md の 4 章)。</summary>
public sealed record CasePrep(
    string Id,
    LaunchKind Launch,
    string? Arguments,
    IReadOnlyList<string> Needs,
    string? Settings,
    string? Hooks,
    string? CopyTo,
    string? AdminCommand,
    IReadOnlyList<string> Steps)
{
    public static CasePrep Default(string id) => new(id, LaunchKind.Dev, null, [], null, null, null, null, []);

    /// <summary>「引数」が「なし」(ファイルを開かない)。</summary>
    public bool OpensNothing => Arguments == "なし";

    /// <summary>「引数」の中の {TD-…} のテストデータ。</summary>
    public IReadOnlyList<string> ArgumentData =>
        Arguments is null ? [] : Regex.Matches(Arguments, @"\{(TD-[A-Z0-9][A-Z0-9-]*[A-Z0-9])\}").Select(m => m.Groups[1].Value).ToList();

    public bool IsAdmin => Launch is LaunchKind.DevAdmin or LaunchKind.PortableAdmin;

    public bool IsPortable => Launch is LaunchKind.Portable or LaunchKind.PortableAdmin;
}

/// <summary>
/// 手動テストの準備の手順 (docs/test/manual-test-prepare.md) を読む。手動テスト支援ツールが表示し、CI が書式を検査する (テスト方針 8 章)。
/// </summary>
public sealed partial class PrepBook
{
    public const string FileName = "manual-test-prepare.md";

    public const string VirtualDiskNeed = "仮想ディスク";
    public const string AdminNeed = "管理者として実行";
    public const string PortableNeed = "ポータブル版";
    public const string InstallerNeed = "インストーラ版";

    private readonly List<string> _parseProblems = [];

    private PrepBook(Dictionary<string, PrepNeed> needs, Dictionary<string, CasePrep> cases, Dictionary<string, DataHowTo> data)
    {
        Needs = needs;
        Cases = cases;
        Data = data;
    }

    public IReadOnlyDictionary<string, PrepNeed> Needs { get; }

    public IReadOnlyDictionary<string, CasePrep> Cases { get; }

    public IReadOnlyDictionary<string, DataHowTo> Data { get; }

    [GeneratedRegex(@"^### (機材|環境): (.+)$")]
    private static partial Regex NeedHeading();

    [GeneratedRegex(@"^### (TC-[A-Z]+-\d+-\d+)\s*$")]
    private static partial Regex CaseHeading();

    [GeneratedRegex(@"^### (TD-[A-Z0-9][A-Z0-9-]*[A-Z0-9])\s*$")]
    private static partial Regex DataHeading();

    [GeneratedRegex(@"^\| (.+?) \| (.*) \|$")]
    private static partial Regex TableRow();

    [GeneratedRegex(@"TC-[A-Z]+-\d+-\d+")]
    private static partial Regex CaseReference();

    /// <summary>テストケースのフォルダ (docs/test/cases) の隣の準備の手順を読む。なければ空。</summary>
    public static PrepBook LoadNextTo(string casesDirectory)
    {
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(casesDirectory).TrimEnd(Path.DirectorySeparatorChar))!, FileName);
        return File.Exists(path) ? Parse(File.ReadAllLines(path)) : Parse([]);
    }

    public static PrepBook Parse(IReadOnlyList<string> lines)
    {
        var needs = new Dictionary<string, PrepNeed>();
        var cases = new Dictionary<string, CasePrep>();
        var data = new Dictionary<string, DataHowTo>();
        var problems = new List<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (!line.StartsWith("### ", StringComparison.Ordinal))
            {
                continue;
            }

            int end = i + 1;
            while (end < lines.Count && !lines[end].StartsWith("### ", StringComparison.Ordinal) && !lines[end].StartsWith("## ", StringComparison.Ordinal))
            {
                end++;
            }

            List<string> body = lines.Skip(i + 1).Take(end - i - 1).Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
            if (NeedHeading().Match(line) is { Success: true } n)
            {
                string name = n.Groups[2].Value.Trim();
                if (!needs.TryAdd(name, new PrepNeed(name, n.Groups[1].Value == "機材", body)))
                {
                    problems.Add($"「{name}」が 2 回あります。");
                }
            }
            else if (CaseHeading().Match(line) is { Success: true } c)
            {
                CasePrep prep = ParseCase(c.Groups[1].Value, body, problems);
                if (!cases.TryAdd(prep.Id, prep))
                {
                    problems.Add($"{prep.Id} が 2 回あります。");
                }
            }
            else if (DataHeading().Match(line) is { Success: true } d)
            {
                data[d.Groups[1].Value] = new DataHowTo(d.Groups[1].Value, body);
            }
            else
            {
                problems.Add($"見出し「{line}」は、機材・環境・TD・TC のどれでもありません。");
            }

            i = end - 1;
        }

        var book = new PrepBook(needs, cases, data);
        book._parseProblems.AddRange(problems);
        return book;
    }

    private static CasePrep ParseCase(string id, List<string> body, List<string> problems)
    {
        var fields = new Dictionary<string, string>();
        var steps = new List<string>();
        foreach (string line in body)
        {
            if (TableRow().Match(line) is { Success: true } row)
            {
                string key = row.Groups[1].Value.Trim();
                if (key is not ("項目" or "---"))
                {
                    fields[key] = row.Groups[2].Value.Trim();
                }
            }
            else
            {
                steps.Add(line);
            }
        }

        LaunchKind launch = LaunchKind.Dev;
        if (fields.TryGetValue("起動", out string? launchText))
        {
            LaunchKind? parsed = launchText switch
            {
                "開発版" => LaunchKind.Dev,
                "開発版 (管理者)" => LaunchKind.DevAdmin,
                "ポータブル版" => LaunchKind.Portable,
                "ポータブル版 (管理者)" => LaunchKind.PortableAdmin,
                "インストーラ版" => LaunchKind.Installer,
                "なし" => LaunchKind.None,
                _ => null,
            };
            if (parsed is null)
            {
                problems.Add($"{id}: 起動「{launchText}」は使えません。");
            }

            launch = parsed ?? LaunchKind.Dev;
        }

        foreach (string key in fields.Keys.Where(k => k is not ("起動" or "引数" or "必要なもの" or "設定" or "フック" or "コピー先" or "コマンド (管理者)")))
        {
            problems.Add($"{id}: 項目「{key}」は使えません。");
        }

        IReadOnlyList<string> needs = fields.TryGetValue("必要なもの", out string? needText)
            ? needText.Split('、', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        return new CasePrep(id, launch, fields.GetValueOrDefault("引数"), needs, fields.GetValueOrDefault("設定"), fields.GetValueOrDefault("フック"),
            fields.GetValueOrDefault("コピー先"), fields.GetValueOrDefault("コマンド (管理者)"), steps);
    }

    public CasePrep For(string id) => Cases.GetValueOrDefault(id) ?? CasePrep.Default(id);

    /// <summary>
    /// テストケースに要るもの (書いたものに加えて、起動の種類・仮想ディスクのテストデータから分かるもの)。順は書いた順、重複なし。
    /// 名前に当たる項目がなければ、本文のない環境として返す (書式の検査で見つかる)。
    /// </summary>
    public IReadOnlyList<PrepNeed> NeedsOf(TestCase testCase)
    {
        CasePrep prep = For(testCase.Id);
        var names = new List<string>(prep.Needs);
        if (testCase.TestData.Any(id => id.StartsWith("TD-VHDX", StringComparison.Ordinal)))
        {
            names.Add(VirtualDiskNeed);
        }

        if (prep.IsAdmin)
        {
            names.Add(AdminNeed);
        }

        if (prep.IsPortable)
        {
            names.Add(PortableNeed);
        }

        if (prep.Launch == LaunchKind.Installer)
        {
            names.Add(InstallerNeed);
        }

        return names.Distinct().Select(n => Needs.GetValueOrDefault(n) ?? new PrepNeed(n, false, [])).ToList();
    }

    /// <summary>この PC のほかに機材が要る (「機材が必要なテストを除く」で一覧から外す)。</summary>
    public bool NeedsEquipment(TestCase testCase) => NeedsOf(testCase).Any(n => n.IsEquipment);

    /// <summary>機材の名前 (報告に書く)。</summary>
    public IReadOnlyList<string> EquipmentOf(TestCase testCase) => NeedsOf(testCase).Where(n => n.IsEquipment).Select(n => n.Name).ToList();

    public bool NeedsVirtualDisks(TestCase testCase) => NeedsOf(testCase).Any(n => n.Name == VirtualDiskNeed);

    /// <summary>前提が参照する別のテストケース (自分を除く)。</summary>
    public static IReadOnlyList<string> ReferencedCases(TestCase testCase) =>
        CaseReference().Matches(testCase.Precondition).Select(m => m.Value).Where(id => id != testCase.Id).Distinct().ToList();

    /// <summary>書式の検査 (CI)。手動・半自動のテストケースすべてについて、準備の手順がそろっているかを見る。</summary>
    public IEnumerable<string> Validate(IReadOnlyCollection<TestCase> manualCases)
    {
        foreach (string problem in _parseProblems)
        {
            yield return problem;
        }

        var ids = manualCases.Select(c => c.Id).ToHashSet();
        foreach (string id in Cases.Keys.Where(id => !ids.Contains(id)))
        {
            yield return $"{id} は手動・半自動のテストケースではありません。";
        }

        foreach (TestCase c in manualCases)
        {
            if (!Cases.TryGetValue(c.Id, out CasePrep? prep))
            {
                yield return $"{c.Id} の準備 (### {c.Id}) がありません。";
                continue;
            }

            foreach (PrepNeed need in NeedsOf(c).Where(n => !Needs.ContainsKey(n.Name)))
            {
                yield return $"{c.Id}: 必要なもの「{need.Name}」の項目 (### 機材: / ### 環境:) がありません。";
            }

            foreach (string id in c.TestData.Concat(prep.ArgumentData).Distinct())
            {
                if (!CaseSetup.CanPrepare(id) && !Data.ContainsKey(id))
                {
                    yield return $"{c.Id}: テストデータ {id} は準備ボタンで作れず、用意のしかた (### {id}) もありません。";
                }
            }

            foreach ((string name, string? json) in new[] { ("設定", prep.Settings), ("フック", prep.Hooks) })
            {
                if (json is not null && !IsJsonObject(json))
                {
                    yield return $"{c.Id}: {name}が JSON のオブジェクトではありません。";
                }
            }

            if (prep.CopyTo is not (null or "デスクトップ"))
            {
                yield return $"{c.Id}: コピー先「{prep.CopyTo}」は使えません (デスクトップだけ)。";
            }
        }
    }

    private static bool IsJsonObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
