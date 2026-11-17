using System.Text.RegularExpressions;

namespace HexEditor.ManualTests;

/// <summary>「いま行うこと」の 1 段階の種類。ボタンで行うもの、手で行うもの ([済んだ] を押す)、最後の「手順を行う」。</summary>
public enum GuideAction
{
    /// <summary>利用者が手で行う (機材を用意する、設定を変えるなど)。終わったら [済んだ] を押す。</summary>
    Hand,

    /// <summary>「仮想ディスクを作る」(管理者の確認が出る)。</summary>
    MakeVirtualDisks,

    /// <summary>「ポータブル版を作る」。</summary>
    BuildPortable,

    /// <summary>「インストーラ版を作ってインストールする」。</summary>
    BuildInstaller,

    /// <summary>「隠すファイル」の名前を一時的に変える (準備の前に自動で行う)。</summary>
    HideFiles,

    /// <summary>「準備」(テストデータと設定フォルダを作り、HexEditor を起動する)。</summary>
    Prepare,

    /// <summary>テストケースの手順を行い、結果を押す (最後の段階)。</summary>
    RunSteps,
}

/// <summary>
/// 「いま行うこと」の 1 段階。<paramref name="Title"/> は 1 文で行うことを書く。<paramref name="Lines"/> はそのやり方 (短い行)。
/// <paramref name="SkipOutcome"/> が「対象外」か「保留」なら、機材などがないときにその結果を付けて飛ばせる (<paramref name="SkipComment"/> はコメントの文)。
/// </summary>
public sealed record GuideStep(
    string Key,
    GuideAction Action,
    string Title,
    IReadOnlyList<string> Lines,
    string? SkipOutcome = null,
    string? SkipComment = null);

/// <summary>テストケースの準備の流れ。<see cref="Steps"/> は行う順 (最後は必ず <see cref="GuideAction.RunSteps"/>)。<see cref="Notes"/> は手順の間に気をつけること。</summary>
public sealed record GuidePlan(IReadOnlyList<GuideStep> Steps, IReadOnlyList<PrepNeed> Notes);

/// <summary>
/// テストケースの準備 (<see cref="PrepBook"/>) から、「いま行うこと」の段階を作る。手動テスト支援ツールは、これを 1 つずつ表示する
/// (利用者が説明を読んで次のボタンを探さなくてよいように)。順は、機材 → 作れないテストデータ → 仮想ディスク → ポータブル版 → インストーラ版
/// → 準備の前に行う設定 → 準備 → 準備の後に行うこと → 手順。
/// </summary>
public static partial class PrepGuide
{
    /// <summary>手順を行いながら気をつけること (段階にはせず、最後の段階に書く)。</summary>
    private static readonly HashSet<string> DuringTest =
    [
        PrepBook.AdminNeed, "一般ユーザーの権限", "ハイコントラスト", "ライトとダークのテーマ", "グレースケール", "表示倍率 200%", "日本語 IME",
        "実際のキーボード", "半自動の目視",
    ];

    /// <summary>準備の後 (HexEditor を起動してから) 手で行うこと。</summary>
    private static readonly HashSet<string> AfterPrepare = ["ナレーター", "TestTarget"];

    [GeneratedRegex(@"「(対象外|保留)」(?:を押し|にし)")]
    private static partial Regex SkipOutcomeText();

    [GeneratedRegex(@"コメントに「([^「」]+)」と書く")]
    private static partial Regex SkipCommentText();

    public static GuidePlan Plan(PrepBook book, TestCase testCase)
    {
        CasePrep prep = book.For(testCase.Id);
        IReadOnlyList<PrepNeed> needs = book.NeedsOf(testCase);
        var before = new List<GuideStep>();
        var after = new List<GuideStep>();
        var notes = new List<PrepNeed>();

        foreach (PrepNeed need in needs.Where(n => n.IsEquipment))
        {
            before.Add(Hand("need:" + need.Name, $"機材を用意します: {need.Name}", need.Lines));
        }

        foreach (string id in testCase.TestData.Concat(prep.ArgumentData).Distinct()
            .Where(id => !CaseSetup.CanPrepare(id) && !id.StartsWith("TD-VHDX", StringComparison.Ordinal)))
        {
            before.Add(Hand("data:" + id, $"テストデータ {id} を用意します", book.Data.TryGetValue(id, out DataHowTo? how) ? how.Lines : []));
        }

        if (needs.Any(n => n.Name == PrepBook.VirtualDiskNeed))
        {
            before.Add(new GuideStep("vhd", GuideAction.MakeVirtualDisks, "仮想ディスクを作ります",
                ["「仮想ディスクを作る」を押します。管理者の確認が出たら「はい」を押します (1 分ほどかかります)。",
                 "PC を再起動したあとは、もう一度押します (作ってあるファイルを接続し直します)。"]));
        }

        if (prep.IsPortable || needs.Any(n => n.IsEquipment && n.Name.Contains("仮想マシン", StringComparison.Ordinal)))
        {
            before.Add(new GuideStep("portable", GuideAction.BuildPortable, "ポータブル版を作ります", ["「ポータブル版を作る」を押します (数分かかります)。"]));
        }

        if (prep.Launch == LaunchKind.Installer)
        {
            before.Add(new GuideStep("installer", GuideAction.BuildInstaller, "インストーラ版を作ってインストールします",
                ["「インストーラ版を作ってインストールする」を押します (数分かかります)。"]));
        }

        foreach (PrepNeed need in needs.Where(n => !n.IsEquipment && !IsButtonNeed(n.Name)))
        {
            if (DuringTest.Contains(need.Name))
            {
                notes.Add(need);
            }
            else if (AfterPrepare.Contains(need.Name))
            {
                after.Add(Hand("need:" + need.Name, $"「{need.Name}」を用意します", need.Lines));
            }
            else
            {
                before.Add(Hand("need:" + need.Name, $"「{need.Name}」を用意します", need.Lines));
            }
        }

        // このテストケースだけの補足。「準備の前に」とあれば準備の前の段階にし、ほかは手順の間に読むことにする。
        if (prep.Steps.Count > 0)
        {
            if (prep.Steps.Any(s => s.Contains("準備の前に", StringComparison.Ordinal)))
            {
                before.Add(new GuideStep("case", GuideAction.Hand, "準備の前に、下のことを行います", prep.Steps));
            }
            else
            {
                notes.Add(new PrepNeed("このテストケースの補足", false, prep.Steps));
            }
        }

        var steps = new List<GuideStep>(before);
        if (prep.HiddenFiles.Count > 0)
        {
            steps.Add(new GuideStep("hide", GuideAction.HideFiles,
                $"補助プロセス ({string.Join("、", prep.HiddenFiles)}) を一時的に隠します",
                ["テストが終わると (結果を押す・別のテストケースを選ぶ・ツールを閉じる) 自動で元に戻します。"]));
        }

        var prepareLines = new List<string>();
        if (prep.IsAdmin || prep.AdminCommand is not null)
        {
            prepareLines.Add("管理者の確認が出たら「はい」を押します。");
        }

        steps.Add(new GuideStep("prepare", GuideAction.Prepare,
            prep.Launch == LaunchKind.None ? "「準備」を押して、テストデータを作ります" : "「準備」を押して、HexEditor を起動します", prepareLines));
        steps.AddRange(after);
        string run = testCase.Steps.Count switch
        {
            0 => "下の期待結果を確かめ、結果を押してください",
            1 => "下の手順 1 を行い、結果を押してください",
            int n => $"下の手順 1〜{n} を順に行い、結果を押してください",
        };
        steps.Add(new GuideStep("run", GuideAction.RunSteps, run, []));
        return new GuidePlan(steps, notes);
    }

    /// <summary>ボタンの段階で用意するもの (仮想ディスク・ポータブル版・インストーラ版)。</summary>
    private static bool IsButtonNeed(string name) => name is PrepBook.VirtualDiskNeed or PrepBook.PortableNeed or PrepBook.InstallerNeed;

    /// <summary>手で行う段階。やり方に「ないとき: 「対象外」を押し、コメントに「…」と書く」があれば、飛ばすボタンを付ける。</summary>
    private static GuideStep Hand(string key, string title, IReadOnlyList<string> lines)
    {
        string? outcome = null;
        string? comment = null;
        foreach (string line in lines)
        {
            if (SkipOutcomeText().Match(line) is { Success: true } m)
            {
                outcome = m.Groups[1].Value;
                comment = SkipCommentText().Match(line) is { Success: true } c ? c.Groups[1].Value : null;
                break;
            }
        }

        return new GuideStep(key, GuideAction.Hand, title, lines, outcome, comment);
    }
}
