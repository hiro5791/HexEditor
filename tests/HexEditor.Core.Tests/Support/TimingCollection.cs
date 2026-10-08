namespace HexEditor.Core.Tests.Support;

/// <summary>
/// 時間を計るテストの集まり。他のテストと並列に動かさない (xUnit は並列のテストがすべて終わってから、このコレクションだけを動かす)。
/// 並列に動く他のテストの CPU・GC の負荷で、混んだ CI のランナーでは計った時間が目標を超えてしまうため。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
