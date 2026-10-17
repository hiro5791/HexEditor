using System.Text.Json.Nodes;
using HexEditor.Core.Templates.Extensions;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting.Api;

/// <summary><c>templates</c> (AUTO-13)。テンプレートエンジン (07) は <see cref="IScriptEnvironment.Templates"/> から使う。</summary>
internal sealed class ScriptTemplates(ScriptContext ctx) : ITemplates
{
    private IScriptTemplateEngine Engine => ctx.Environment.Templates ?? throw new Sdk.ArgumentError(Msg.T("テンプレートの機能が使えません。", "The template feature is not available."));

    public IReadOnlyList<TemplateInfo> List() => ScriptErrorMap.Guard(() => Engine.List());

    public IReadOnlyList<TemplateInfo> Detect(IDocument document) => ScriptErrorMap.Guard(() =>
    {
        if (document is not ScriptDocument doc)
        {
            throw new Sdk.ArgumentError(Msg.T("document にはドキュメントを指定してください。", "Specify a document for document."));
        }

        doc.ThrowIfClosed();
        return Engine.Detect(doc.Document, doc.Path);
    });

    private readonly Lock _lock = new();
    private readonly List<Registration> _registrations = [];

    /// <summary>
    /// テンプレート拡張を登録する (AUTO-13 の仕様 6、TPL-19)。テンプレートからの呼び出しはスクリプトのスレッドで実行し、時間の上限
    /// (<see cref="TemplateExtensionRegistry.CallTimeout"/>) を過ぎたらその呼び出しを失敗にする。登録している間は常駐する。
    /// </summary>
    public IEventSubscription RegisterExtension(string name, Func<IReadOnlyList<object?>, object?> fn, TemplateExtensionOptions? options = null) =>
        ScriptErrorMap.Guard(() =>
        {
            if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || char.IsAsciiDigit(name[0]))
            {
                throw new Sdk.ArgumentError(Msg.T($"拡張関数の名前が正しくありません: {name}", $"Invalid extension function name: {name}"));
            }

            ArgumentNullException.ThrowIfNull(fn);
            options ??= new TemplateExtensionOptions();
            TemplateExtensionRegistry registry = Engine.Extensions;
            string owner = "script:" + ctx.Run.Id.ToString("N");
            var registration = new Registration(this, registry, name);
            registry.Register(new TemplateExtensionFunction
            {
                Name = name,
                Point = PointOf(options.Point),
                Parameters = [.. (options.Params ?? []).Select(p => TypeOf(p, "params"))],
                Returns = TypeOf(options.Returns, "returns"),
                Owner = owner,
                Invoke = (args, ct) =>
                {
                    if (ctx.StopRequested)
                    {
                        throw new OperationCanceledException(Msg.T("スクリプトは停止しました。", "The script has stopped."));
                    }

                    // 評価のスレッドからスクリプトのスレッドに渡して待つ (上限の時間かキャンセルで待つのをやめる)。
                    Task<object?> call = Task.Run(() => fn(args), CancellationToken.None);
                    try
                    {
                        call.Wait(ct);
                    }
                    catch (AggregateException ae) when (ae.InnerException is { } inner)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(inner);
                    }

                    return call.Result;
                },
            });
            lock (_lock)
            {
                _registrations.Add(registration);
            }

            ctx.AddSubscription();
            return registration;
        });

    private void ReleaseSubscription() => ctx.RemoveSubscription();

    /// <summary>スクリプトの終了で登録をすべて外す。</summary>
    internal void UnregisterAll()
    {
        List<Registration> all;
        lock (_lock)
        {
            all = [.. _registrations];
            _registrations.Clear();
        }

        foreach (Registration r in all)
        {
            r.Remove(countSubscription: false);
        }
    }

    private void Remove(Registration registration)
    {
        bool removed;
        lock (_lock)
        {
            removed = _registrations.Remove(registration);
        }

        if (removed)
        {
            registration.Remove(countSubscription: true);
        }
    }

    private static TemplateExtensionPoint PointOf(string? point) => point switch
    {
        null or "function" => TemplateExtensionPoint.Function,
        "format" => TemplateExtensionPoint.Format,
        "read" => TemplateExtensionPoint.Read,
        "write" => TemplateExtensionPoint.Write,
        "pointerMap" => TemplateExtensionPoint.PointerMap,
        "section" => TemplateExtensionPoint.SectionProducer,
        _ => throw new Sdk.ArgumentError(Msg.T($"拡張点が不明です: {point} (format、read、write、pointerMap、section、function)",
            $"Unknown extension point: {point} (format, read, write, pointerMap, section, function)")),
    };

    private static TemplateValueType TypeOf(string? type, string name) => type switch
    {
        null or "any" => TemplateValueType.Any,
        "integer" => TemplateValueType.Integer,
        "float" => TemplateValueType.Float,
        "bool" => TemplateValueType.Bool,
        "string" => TemplateValueType.String,
        "bytes" => TemplateValueType.Bytes,
        "pattern" => TemplateValueType.Pattern,
        "void" => TemplateValueType.Void,
        _ => throw new Sdk.ArgumentError(Msg.T($"{name} の型が不明です: {type} (integer、float、bool、string、bytes、pattern、any)",
            $"Unknown type in {name}: {type} (integer, float, bool, string, bytes, pattern, any)")),
    };

    private sealed class Registration(ScriptTemplates owner, TemplateExtensionRegistry registry, string name) : IEventSubscription
    {
        public void Off() => owner.Remove(this);

        public void Remove(bool countSubscription)
        {
            registry.Unregister(name);
            if (countSubscription)
            {
                owner.ReleaseSubscription();
            }
        }
    }
}

/// <summary>解析結果のノード (AUTO-13 の仕様 3・4)。</summary>
internal sealed class ScriptTemplateNode(ScriptDocument doc, ITemplateResultNode node) : ITemplateNode
{
    private IReadOnlyList<ITemplateNode>? _children;

    /// <summary>値・表示は評価を伴うことがある (遅延評価・書式関数)。待っている間もテンプレート拡張の呼び出しを処理する。</summary>
    private T Pump<T>(Func<T> work) => doc.Context.Thread is { } thread ? thread.RunPumping(work) : work();

    public string Name => node.Name;

    public string Type => node.Type;

    public long Start => node.Start;

    public long Length => node.Length;

    public object? Value
    {
        get => Pump(() => node.Value);
        set => ScriptErrorMap.Guard(() =>
        {
            // テンプレートのツリーから編集した場合と同じく書き換える (Undo できる)。書き込みの排他と Undo の単位はスクリプトと同じ。
            WriteSession session = doc.Context.AcquireWrite(doc);
            Pump(() => doc.Context.Dispatcher.Invoke(() => session.Apply(_ =>
            {
                node.SetValue(value);
                return true;
            })));
        });
    }

    public string DisplayValue => Pump(() => node.DisplayValue);

    public IReadOnlyList<ITemplateNode> Children => _children ??= Pump(() => (IReadOnlyList<ITemplateNode>)[.. node.Children.Select(c => (ITemplateNode)new ScriptTemplateNode(doc, c))]);

    public ITemplateNode? Get(string path)
    {
        ITemplateNode? current = this;
        foreach (string part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current?.Children.FirstOrDefault(c => c.Name == part);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    public JsonNode ToJson() => Pump(node.ToJson);
}
