using System.Text;
using System.Text.Json;
using HexEditor.ManualTests;

namespace ManualTestRunner;

/// <summary>テストケースの結果 (テスト方針 8.1)。</summary>
public enum Outcome
{
    NotRun,
    Pass,
    Fail,
    Hold,
    NotApplicable,
}

public sealed record CaseResult(Outcome Outcome, string Comment, string? Screenshot, DateTimeOffset At, string Environment);

/// <summary>
/// 実行中のテストの記録。途中で止めても次回は続きから再開できるよう、結果を付けるたびにファイルに保存する。
/// </summary>
public sealed class RunStore
{
    private readonly Dictionary<string, CaseResult> _results;

    private RunStore(string path, Dictionary<string, CaseResult> results)
    {
        PathName = path;
        _results = results;
    }

    public string PathName { get; }

    public static string Folder => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "HexEditorManualTests");

    public static RunStore LoadOrCreate()
    {
        string path = Path.Combine(Folder, "current-run.json");
        Directory.CreateDirectory(Folder);
        Dictionary<string, CaseResult> results = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, CaseResult>>(File.ReadAllText(path)) ?? []
            : [];
        return new RunStore(path, results);
    }

    public Outcome OutcomeOf(string id) => _results.TryGetValue(id, out CaseResult? r) ? r.Outcome : Outcome.NotRun;

    public CaseResult? ResultOf(string id) => _results.GetValueOrDefault(id);

    public void Record(string id, CaseResult result)
    {
        _results[id] = result;
        Save();
    }

    /// <summary>新しい実行を始める。今の記録は日時付きの名前で残す。</summary>
    public void StartNew()
    {
        if (File.Exists(PathName))
        {
            File.Move(PathName, Path.Combine(Folder, $"run-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
        }

        _results.Clear();
        Save();
    }

    /// <summary>結果を Markdown と JSON で書き出し、Markdown のパスを返す。</summary>
    public string ExportReport(IReadOnlyList<TestCase> cases)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string json = Path.Combine(Folder, $"report-{stamp}.json");
        string md = Path.Combine(Folder, $"report-{stamp}.md");
        File.WriteAllText(json, JsonSerializer.Serialize(_results, new JsonSerializerOptions { WriteIndented = true }));

        var sb = new StringBuilder();
        sb.AppendLine($"# 手動テストの結果 ({DateTime.Now:yyyy-MM-dd HH:mm})").AppendLine();
        sb.AppendLine($"- 環境: {EnvironmentInfo.Describe()}");
        foreach (Outcome o in Enum.GetValues<Outcome>().Where(o => o != Outcome.NotRun))
        {
            sb.AppendLine($"- {Label(o)}: {cases.Count(c => OutcomeOf(c.Id) == o)} 件");
        }

        sb.AppendLine($"- 未実施: {cases.Count(c => OutcomeOf(c.Id) == Outcome.NotRun)} 件").AppendLine();
        sb.AppendLine("| ID | 名前 | 結果 | コメント |").AppendLine("| --- | --- | --- | --- |");
        foreach (TestCase c in cases)
        {
            CaseResult? r = ResultOf(c.Id);
            sb.AppendLine($"| {c.Id} | {c.Title} | {Label(r?.Outcome ?? Outcome.NotRun)} | {r?.Comment.Replace("|", "\\|").ReplaceLineEndings(" ")} |");
        }

        File.WriteAllText(md, sb.ToString());
        return md;
    }

    public static string Label(Outcome outcome) => outcome switch
    {
        Outcome.Pass => "合格",
        Outcome.Fail => "不合格",
        Outcome.Hold => "保留",
        Outcome.NotApplicable => "対象外",
        _ => "未実施",
    };

    private void Save() => File.WriteAllText(PathName, JsonSerializer.Serialize(_results, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>環境の情報 (OS・言語・表示倍率)。不合格の記録に自動で添付する。</summary>
public static class EnvironmentInfo
{
    public static string Describe() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} / {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} / "
        + $"UI {System.Globalization.CultureInfo.CurrentUICulture.Name}";
}
