namespace HexEditor.Core.Tests.Support;

/// <summary>
/// 他のテストと並列に動かさないテストの集まり (xUnit は並列のテストがすべて終わってから、このコレクションだけを動かす)。
/// 時間を計るテスト (並列に動く他のテストの CPU・GC の負荷で、混んだ CI のランナーでは目標を超えてしまう) と、プロセス全体に効く
/// 静的なフック (SettingsStore.WriteHook など) を使うテスト (並列に動く他のテストの書き込みにも効いてしまう) に使う。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection
{
    public const string Name = "Serial";
}
