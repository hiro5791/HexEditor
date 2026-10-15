using HexEditor.Core.Engine;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.CSharp;
using HexEditor.Core.Sources;
using HexEditor.Sdk;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>
/// スクリプトのテストの環境: 一時的な設定フォルダ、<see cref="ScriptHost"/>、UI スレッドの代わりの <see cref="SerialDispatcher"/>、
/// 答えを決められる UI (<see cref="FakeScriptUi"/>)。ドキュメントは仮想のデータソースやメモリ上のデータで作る。
/// </summary>
public sealed class ScriptTestHost : IDisposable
{
    public ScriptTestHost(ScriptLimits? limits = null, string policy = "", CSharpComponentManager? csharp = null, string language = "en")
    {
        Root = Path.Combine(Path.GetTempPath(), "HexEditor", "script-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Folders = new ScriptFolders(Path.Combine(Root, "settings"), Path.Combine(Root, "data"));
        Directory.CreateDirectory(Folders.Scripts);
        Trust = new TrustStore(Folders.TrustFile);
        Limits = limits ?? new ScriptLimits();
        Policy = policy;
        Host = new ScriptHost(Folders, Trust, () => Limits, () => Policy, csharp) { ForceStopGrace = TimeSpan.FromSeconds(1) };
        Env = new CoreScriptEnvironment(language: language);
        Ui = new FakeScriptUi(Env);
        Env.Ui = Ui;
    }

    public string Root { get; }

    public ScriptFolders Folders { get; }

    public TrustStore Trust { get; }

    public ScriptLimits Limits { get; set; }

    public string Policy { get; set; }

    public ScriptHost Host { get; }

    public CoreScriptEnvironment Env { get; }

    public FakeScriptUi Ui { get; }

    /// <summary>UI スレッドで実行する。</summary>
    public T OnUi<T>(Func<T> work) => Env.Dispatcher.Invoke(work);

    public void OnUi(Action work) => Env.Dispatcher.Invoke(work);

    /// <summary>メモリ上のデータのドキュメントを開く (UI スレッドで作る)。</summary>
    public CoreDocumentHost Open(byte[] data, string name = "test.bin", bool activate = true) =>
        OnUi(() => Env.Add(new MemoryByteSource(data, name), name, activate: activate));

    public CoreDocumentHost Open(IByteSource source, string name = "test.bin", string kind = "file", bool activate = true) =>
        OnUi(() => Env.Add(source, name, kind, activate));

    /// <summary>スクリプトのファイルを書く (設定フォルダの scripts の下)。</summary>
    public string WriteScript(string name, string code)
    {
        string path = Path.Combine(Folders.Scripts, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, code);
        return path;
    }

    /// <summary>信頼済み (「常に許可する」の記録) にする。</summary>
    public void TrustScript(string path, params string[] permissions) =>
        Trust.Add(TrustStore.ScriptKind, TrustStore.ScriptKey(path), TrustStore.HashOf(File.ReadAllText(path)), TrustSource.UserAllowed, permissions);

    /// <summary>コンソールの入力として実行する (信頼の確認なし・許可なし)。</summary>
    public Task<ScriptRunResult> RunCodeAsync(string code, string name = "test.js") =>
        Host.RunAndWaitAsync(new ScriptRunRequest { Code = code, Origin = ScriptOrigin.Console, DisplayName = name }, Env);

    /// <summary>エディタの内容として実行する (信頼の確認なし・宣言した許可あり)。</summary>
    public Task<ScriptRunResult> RunEditorAsync(string code, string? path = null, string name = "test.js") =>
        Host.RunAndWaitAsync(new ScriptRunRequest { Code = code, Path = path, Origin = ScriptOrigin.Editor, DisplayName = name }, Env);

    public Task<ScriptRunResult> RunFileAsync(string path, bool headless = false) =>
        Host.RunAndWaitAsync(new ScriptRunRequest { Path = path, Headless = headless }, Env);

    public ScriptRun Start(ScriptRunRequest request) => Host.Start(request, Env);

    /// <summary>出力の本文 (チャンネルを問わない)。</summary>
    public IReadOnlyList<string> OutputText => [.. Env.Output.Lines.Select(l => l.Text)];

    public IReadOnlyList<ScriptOutputLine> OutputLines => Env.Output.Lines;

    public static void AssertSucceeded(ScriptRunResult result) =>
        Assert.True(result.Status == ScriptRunStatus.Succeeded, $"{result.Status} {result.Failure}: {result.Message}\n{result.StackTrace}");

    public void Dispose()
    {
        Host.Dispose();
        foreach (CoreDocumentHost doc in Env.Hosts)
        {
            OnUi(() => doc.Document.Dispose());
        }

        Env.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>答えを決められる UI。呼ばれた内容を記録する。</summary>
public sealed class FakeScriptUi(CoreScriptEnvironment env) : HeadlessScriptUi(env)
{
    public TrustAnswer TrustAnswer { get; set; } = TrustAnswer.RunOnce;

    public List<TrustPrompt> TrustPrompts { get; } = [];

    public bool PermissionAnswer { get; set; }

    public List<string> PermissionPrompts { get; } = [];

    /// <summary>時間の上限での答え (呼ばれた回数ごと)。</summary>
    public Func<int, bool> ContinueAnswer { get; set; } = _ => false;

    public int TimeoutPrompts { get; private set; }

    /// <summary>フォームの答え (null はキャンセル)。</summary>
    public Func<IReadOnlyList<ScriptFormField>, IReadOnlyDictionary<string, string>?> FormAnswer { get; set; } = _ => null;

    public List<IReadOnlyList<ScriptFormField>> Forms { get; } = [];

    public List<(string? Error, IReadOnlyDictionary<string, string> Values)> FormValidations { get; } = [];

    public Func<string, IReadOnlyList<string>?> PickAnswer { get; set; } = _ => null;

    public int ConfirmAnswer { get; set; } = -1;

    public List<(string Text, string Kind, bool Modal)> Messages { get; } = [];

    public List<(string Title, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows)> Results { get; } = [];

    public Func<bool> OfferComponent { get; set; } = () => false;

    public override Task<TrustAnswer> ConfirmTrustAsync(TrustPrompt prompt)
    {
        TrustPrompts.Add(prompt);
        return Task.FromResult(TrustAnswer);
    }

    public override Task<bool> ConfirmPermissionAsync(string scriptName, string permission, string detail)
    {
        PermissionPrompts.Add(permission);
        return Task.FromResult(PermissionAnswer);
    }

    public override Task<bool> ConfirmContinueAfterTimeoutAsync(ScriptRun run, TimeSpan limit) => Task.FromResult(ContinueAnswer(++TimeoutPrompts));

    public override Task<IReadOnlyDictionary<string, string>?> FormAsync(string scriptName, string? title, IReadOnlyList<ScriptFormField> fields,
        Func<IReadOnlyDictionary<string, string>, (string Field, string Message)?> validate)
    {
        Forms.Add(fields);
        IReadOnlyDictionary<string, string>? answer = FormAnswer(fields);
        if (answer is not null)
        {
            FormValidations.Add((validate(answer)?.Message, answer));
        }

        return Task.FromResult(answer);
    }

    public override Task<IReadOnlyList<string>?> PickAsync(string scriptName, string kind, PickOptions options) => Task.FromResult(PickAnswer(kind));

    public override Task<int> ConfirmAsync(string scriptName, string text, IReadOnlyList<string> buttons) => Task.FromResult(ConfirmAnswer);

    public override Task MessageAsync(string scriptName, string text, string kind, bool modal)
    {
        Messages.Add((text, kind, modal));
        return base.MessageAsync(scriptName, text, kind, modal);
    }

    public override void ShowResults(string scriptName, string title, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<string> columns,
        IScriptDocumentHost? document) => Results.Add((title, rows));

    public override Task<bool> OfferCSharpComponentAsync() => Task.FromResult(OfferComponent());
}
