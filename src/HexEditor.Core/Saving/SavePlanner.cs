using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Saving;

/// <summary>保存方式 (ENG-20 の仕様 1)。</summary>
public enum SaveMethod
{
    /// <summary>変更がない。何もしない (書き込まない。更新日時も変えない)。</summary>
    NoChanges,

    /// <summary>保存先がない (無題)、または元のデータソースに書けない。UI は「名前を付けて保存」のダイアログを出す。</summary>
    SaveAs,

    /// <summary>変更した範囲だけを書き込むその場保存 (ENG-23)。</summary>
    InPlace,

    /// <summary>その場保存をジャーナルなしで行う (ENG-23 の仕様 3 の「保護なしで書き込む」)。</summary>
    InPlaceUnprotected,

    /// <summary>一時ファイルに書き出して置き換える安全な保存 (ENG-22)。名前を付けて保存 (ENG-21) もこれ。</summary>
    Safe,
}

/// <summary>保存を始める前に見つかった問題 (ENG-20 の仕様 3)。UI は種類に応じて確認ダイアログまたはエラーを出す。</summary>
public enum SaveIssue
{
    None,

    /// <summary>読み取り専用のため元のデータソースには保存できない。方式は <see cref="SaveMethod.SaveAs"/>。</summary>
    ReadOnly,

    /// <summary>
    /// ファイルシステムのファイルサイズの上限を超える (<see cref="SavePlan.SizeLimit"/>)。空き容量より先に確認し、このエラーだけを出す
    /// (ENG-25 の仕様 5)。
    /// </summary>
    FileTooLarge,

    /// <summary>
    /// 保存先の空き容量が足りない (<see cref="SavePlan.Space"/>)。UI は「別の場所に保存」「キャンセル」のダイアログを出す (ENG-25 の仕様 4。
    /// 「その場でずらしながら保存」は ENG-24 の実装後)。
    /// </summary>
    InsufficientSpace,

    /// <summary>
    /// その場保存のジャーナルが上限を超える、またはジャーナルの置き場所の空き容量が足りない (<see cref="SavePlan.Journal"/>)。UI は
    /// 「安全な保存を使う」(<see cref="SavePlanner.UseSafeSave"/>)「保護なしで書き込む」(<see cref="SavePlanner.WriteWithoutJournal"/>)
    /// 「キャンセル」を選ばせる (ENG-23 の仕様 3)。
    /// </summary>
    JournalTooLarge,
}

/// <summary>空き容量不足の内容 (ENG-25 の仕様 4 のダイアログの「必要」「空き」)。</summary>
public sealed record SpaceShortage(string Drive, long Required, long Available);

/// <summary>ファイルサイズの上限 (ENG-20 の「エラー」)。</summary>
public sealed record FileSizeLimit(string Drive, string FileSystem, long MaxFileSize, long Length);

/// <summary>ジャーナルを書けない理由。<see cref="Space"/> があれば置き場所の空き容量不足、なければ上限 <see cref="Limit"/> 超え。</summary>
public sealed record JournalShortage(long Required, long Limit, SpaceShortage? Space);

/// <summary>保存の設定 (ENG-22 の「画面」の設定項目のうちフェーズ 0 のもの)。</summary>
public sealed record SaveSettings
{
    /// <summary>その場保存のジャーナルの置き場所 (復旧用フォルダ)。</summary>
    public required string JournalDirectory { get; init; }

    /// <summary>ジャーナルの上限 (ENG-23 の仕様 3。既定 1 GiB)。</summary>
    public long JournalLimit { get; init; } = InPlaceSaver.DefaultJournalLimit;

    /// <summary>「長さが変わらない場合の保存方法」が「常に安全な保存」(ENG-20 の仕様 1)。</summary>
    public bool AlwaysSafeSave { get; init; }

    /// <summary>ボリュームの情報 (テストで差し替える)。</summary>
    public IVolumeInfoProvider Volumes { get; init; } = SystemVolumeInfoProvider.Instance;
}

/// <summary>保存の計画: どの方式で、どこに、何を書くか。<see cref="SavePlanner.Plan"/> で作り、UI スレッドで確認してから実行する。</summary>
public sealed record SavePlan
{
    public required Document Document { get; init; }

    /// <summary>計画を作った時点の内容 (書き出すのはこのスナップショット。ENG-20 の仕様 2)。</summary>
    public required DocumentSnapshot Snapshot { get; init; }

    /// <summary>保存先の絶対パス。<see cref="SaveMethod.SaveAs"/> では null のことがある。</summary>
    public string? TargetPath { get; init; }

    public required SaveMethod Method { get; init; }

    public SaveIssue Issue { get; init; }

    public SpaceShortage? Space { get; init; }

    public FileSizeLimit? SizeLimit { get; init; }

    public JournalShortage? Journal { get; init; }

    public required SaveSettings Settings { get; init; }

    /// <summary>書き込みを行う計画で、確認の要る問題がない (<see cref="SavePlanner.Execute"/> できる)。</summary>
    public bool CanExecute => Issue == SaveIssue.None && Method is SaveMethod.InPlace or SaveMethod.InPlaceUnprotected or SaveMethod.Safe;

    /// <summary>書き出す量 (進捗の全体)。</summary>
    public long TotalBytes => Method == SaveMethod.Safe ? Snapshot.Length : InPlaceSaver.JournalSize(Snapshot) * 2;
}

/// <summary>保存の結果。UI スレッドで <see cref="SavePlanner.Complete"/> に渡す。</summary>
public sealed record SaveResult(FileByteSource? SavedFile, InPlaceSaveResult? InPlace);

/// <summary>
/// 保存方式の選択と実行 (ENG-20)。UI は次の順に呼ぶ。
/// 1. <see cref="Plan"/> (UI スレッド)。<see cref="SavePlan.Method"/> が NoChanges なら終わり、SaveAs なら保存ダイアログで
///    パスを選んでもう一度 <see cref="Plan"/>。<see cref="SavePlan.Issue"/> があれば確認ダイアログ・エラーを出す。
/// 2. <see cref="Execute"/> を長時間処理 (ENG-09) としてバックグラウンドで実行する。
/// 3. 成功したら <see cref="Complete"/> (UI スレッド)、失敗・キャンセルしたら <see cref="Abort"/>。
/// </summary>
public static class SavePlanner
{
    /// <summary>保存の方式を決め、始める前の確認をする (ENG-20 の仕様 1・3、ENG-25)。</summary>
    /// <param name="targetPath">保存先。null は「元の場所に保存」(無題なら <see cref="SaveMethod.SaveAs"/>)。</param>
    public static SavePlan Plan(Document document, string? targetPath, SaveSettings settings)
    {
        DocumentSnapshot snapshot = document.Current;
        string? own = (document.Source as FileByteSource)?.Path;
        string? target = targetPath is null ? own : Path.GetFullPath(targetPath);
        var plan = new SavePlan { Document = document, Snapshot = snapshot, TargetPath = target, Method = SaveMethod.SaveAs, Settings = settings };
        if (target is null)
        {
            return plan;
        }

        bool sameFile = own is not null && string.Equals(target, own, StringComparison.OrdinalIgnoreCase);
        if (sameFile && !document.IsModified)
        {
            return plan with { Method = SaveMethod.NoChanges };
        }

        if (sameFile && !document.CanSave)
        {
            return plan with { Method = SaveMethod.SaveAs, Issue = SaveIssue.ReadOnly };
        }

        if (sameFile && !settings.AlwaysSafeSave && InPlaceSaver.CanSaveInPlace(snapshot, target))
        {
            return CheckJournal(plan with { Method = SaveMethod.InPlace });
        }

        return CheckSafe(plan with { Method = SaveMethod.Safe });
    }

    /// <summary>ジャーナルの確認で「安全な保存を使う」を選んだ。安全な保存の確認 (空き容量など) をやり直す。</summary>
    public static SavePlan UseSafeSave(SavePlan plan) =>
        CheckSafe(plan with { Method = SaveMethod.Safe, Issue = SaveIssue.None, Journal = null });

    /// <summary>ジャーナルの確認で「保護なしで書き込む」を選んだ。</summary>
    public static SavePlan WriteWithoutJournal(SavePlan plan) =>
        plan.Method is SaveMethod.InPlace or SaveMethod.InPlaceUnprotected
            ? plan with { Method = SaveMethod.InPlaceUnprotected, Issue = SaveIssue.None, Journal = null }
            : throw new InvalidOperationException("その場保存の計画ではありません。");

    /// <summary>
    /// 計画どおりに書き出す (バックグラウンドで呼ぶ)。書き込み禁止のハンドル (ENG-15) を閉じてから書く。
    /// 確認の要る問題がある計画では例外になる。
    /// </summary>
    public static SaveResult Execute(SavePlan plan, LongRunningOperation? operation = null)
    {
        if (!plan.CanExecute)
        {
            throw new InvalidOperationException($"この計画は実行できません ({plan.Method}, {plan.Issue})。");
        }

        return plan.Method switch
        {
            SaveMethod.Safe => new SaveResult(DocumentSaver.Save(plan.Snapshot, plan.TargetPath!, operation, plan.Settings.Volumes), null),
            _ => new SaveResult(null, InPlaceSaver.Save(plan.Snapshot, plan.Settings.JournalDirectory, plan.Settings.JournalLimit, operation,
                plan.Document.Id, protect: plan.Method == SaveMethod.InPlace, plan.Settings.Volumes)),
        };
    }

    /// <summary>保存の完了を反映する (UI スレッド。ENG-20 の仕様 4)。</summary>
    public static void Complete(SavePlan plan, SaveResult result)
    {
        if (result.InPlace is { } inPlace)
        {
            plan.Document.CompleteInPlaceSave(inPlace);
        }
        else if (result.SavedFile is { } file)
        {
            plan.Document.CompleteSave(file);
        }
    }

    /// <summary>保存が失敗・キャンセルした (UI スレッド)。書き込み禁止のハンドルを元の方針に戻す。</summary>
    public static void Abort(SavePlan plan)
    {
        if (!plan.Document.IsDisposed)
        {
            plan.Document.ResumeLock();
        }
    }

    private static SavePlan CheckJournal(SavePlan plan)
    {
        long size = InPlaceSaver.JournalSize(plan.Snapshot);
        if (size > plan.Settings.JournalLimit)
        {
            return plan with { Issue = SaveIssue.JournalTooLarge, Journal = new JournalShortage(size, plan.Settings.JournalLimit, null) };
        }

        VolumeInfo? volume = plan.Settings.Volumes.GetVolume(ExistingFolder(plan.Settings.JournalDirectory));
        long required = size + DocumentSaver.FreeSpaceMargin;
        if (volume?.AvailableFreeSpace is long available && available < required)
        {
            var space = new SpaceShortage(volume.Name, required, available);
            return plan with { Issue = SaveIssue.JournalTooLarge, Journal = new JournalShortage(size, plan.Settings.JournalLimit, space) };
        }

        return plan;
    }

    private static SavePlan CheckSafe(SavePlan plan)
    {
        string folder = Path.GetDirectoryName(plan.TargetPath!)!;
        VolumeInfo? volume = plan.Settings.Volumes.GetVolume(ExistingFolder(folder));
        long length = plan.Snapshot.Length;
        if (volume?.MaxFileSize is long max && length > max)
        {
            return plan with { Issue = SaveIssue.FileTooLarge, SizeLimit = new FileSizeLimit(volume.Name, volume.FileSystem!, max, length) };
        }

        long required = length > long.MaxValue - DocumentSaver.FreeSpaceMargin ? long.MaxValue : length + DocumentSaver.FreeSpaceMargin;
        if (volume?.AvailableFreeSpace is long available && available < required)
        {
            return plan with { Issue = SaveIssue.InsufficientSpace, Space = new SpaceShortage(volume.Name, required, available) };
        }

        return plan;
    }

    /// <summary>まだないフォルダ (ジャーナルの置き場所など) は、あるところまで親をたどる。</summary>
    private static string ExistingFolder(string folder)
    {
        string? current = Path.GetFullPath(folder);
        while (current is not null && !Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }

        return current ?? folder;
    }
}
