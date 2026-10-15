using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using HexEditor.Sdk;
using Jint;
using JintEngine = Jint.Engine;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

namespace HexEditor.Core.Scripting.Js;

/// <summary>
/// 自動化 API (HexEditor.Sdk のインターフェイス) を JavaScript に見せる (AUTO-04 の仕様 3・4・5)。
/// <list type="bullet">
/// <item>名前は camelCase (C# の名前の先頭を小文字にし、<c>Async</c> を除く。<see cref="ScriptNameAttribute"/> で上書きできる)。</item>
/// <item>インターフェイスは生きたオブジェクト (同じ C# のオブジェクトには同じ JavaScript のオブジェクト)。結果の型 (record など) は値のコピー。</item>
/// <item>バイト列は <c>Uint8Array</c>、オフセットは <c>number</c> (2^53 まで)、64 bit の値は <c>bigint</c>、<c>Task</c> は <c>Promise</c>、
/// <c>IAsyncEnumerable</c> は非同期イテレータ、<c>CancellationToken</c> は <c>AbortSignal</c>。</item>
/// <item>API の例外は同じ名前の JavaScript の例外にする (AUTO-04 の仕様 9。<c>RangeError</c> は組み込みのもの)。</item>
/// </list>
/// .NET の型そのものはスクリプトに渡さない (AUTO-02 の仕様 2。Jint の CLR 連携は無効)。
/// </summary>
internal sealed class JsBinder
{
    private static readonly ConcurrentDictionary<Type, TypeShape> Shapes = new();

    private readonly JintEngine _engine;
    private readonly JsSession _session;
    private readonly ConditionalWeakTable<object, ObjectInstance> _wrappers = new();
    private readonly ConditionalWeakTable<ObjectInstance, object> _targets = new();
    private readonly ConditionalWeakTable<ObjectInstance, SignalBox> _signals = new();
    private readonly ObjectInstance _helpers;
    private readonly ObjectInstance _errors;
    private ObjectInstance? _scriptSignal;

    public JsBinder(JintEngine engine, JsSession session, ObjectInstance helpers)
    {
        _engine = engine;
        _session = session;
        _helpers = helpers;
        _errors = helpers.Get("errors").AsObject();
    }

    private ScriptContext Context => _session.Context;

    // ======================== C# → JavaScript ========================

    public JsValue Wrap(object? value, Type declared)
    {
        if (value is null)
        {
            return JsValue.Null;
        }

        Type t = Nullable.GetUnderlyingType(declared) ?? declared;
        switch (value)
        {
            case JsValue js:
                return js;
            case string s:
                return new JsString(s);
            case bool b:
                return b ? JsBoolean.True : JsBoolean.False;
            case int or short or byte or sbyte or ushort or uint or float or double:
                return new JsNumber(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case long l:
                return new JsNumber(ScriptValues.CheckSafe(l));
            case ulong ul:
                return ul <= ScriptValues.MaxSafeInteger ? new JsNumber(ul) : new JsBigInt(ul);
            case BigInteger bi:
                return new JsBigInt(bi);
            case byte[] bytes:
                return _engine.Intrinsics.Uint8Array.Construct(bytes);
            case CancellationToken ct:
                return SignalFor(ct);
            case Sdk.ByteRange r:
                return RangeObject(r);
            case SearchResults results:
                return SearchResultsArray(results);
            case JsonNode node:
                return FromJson(node);
            case Task task:
                return FromTask(task, ResultTypeOf(task.GetType()));
            case Enum e:
                return new JsString(CamelCase(e.ToString()));
            case DateTimeOffset dto:
                return new JsString(dto.ToString("o", CultureInfo.InvariantCulture));
        }

        Type runtime = value.GetType();
        if (AsyncItemType(runtime) is Type itemType)
        {
            return (JsValue)typeof(JsBinder).GetMethod(nameof(AsyncIterable), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(itemType).Invoke(this, [value])!;
        }

        Type? api = ApiTypeOf(t, runtime);
        if (api is not null)
        {
            return WrapperFor(value, api);
        }

        if (value is IDictionary dict)
        {
            var o = new JsObject(_engine);
            foreach (DictionaryEntry e in dict)
            {
                o.FastSetDataProperty(Convert.ToString(e.Key, CultureInfo.InvariantCulture)!, Wrap(e.Value, typeof(object)));
            }

            return o;
        }

        if (value is IEnumerable list)
        {
            Type item = ItemTypeOf(t) ?? ItemTypeOf(runtime) ?? typeof(object);
            var items = new List<JsValue>();
            foreach (object? x in list)
            {
                items.Add(Wrap(x, item));
            }

            return new JsArray(_engine, [.. items]);
        }

        if (runtime.Namespace == typeof(IApp).Namespace || runtime.Namespace?.StartsWith("HexEditor.Core", StringComparison.Ordinal) == true)
        {
            return PlainObject(value, runtime);
        }

        return new JsString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
    }

    private static Type? ApiTypeOf(Type declared, Type runtime)
    {
        if (IsLiveApiType(declared))
        {
            return declared;
        }

        if (declared == typeof(object))
        {
            return runtime.GetInterfaces().FirstOrDefault(IsLiveApiType) ?? (IsLiveApiType(runtime.BaseType) ? runtime.BaseType : null);
        }

        return null;
    }

    /// <summary>生きたオブジェクトとして包む型 (SDK のインターフェイスと抽象クラス)。</summary>
    private static bool IsLiveApiType(Type? t) =>
        t is not null && t.Namespace == typeof(IApp).Namespace && (t.IsInterface || t.IsAbstract) && !t.IsSealed;

    private JsValue RangeObject(Sdk.ByteRange r)
    {
        var o = new JsObject(_engine);
        o.FastSetDataProperty("start", new JsNumber(ScriptValues.CheckSafe(r.Start)));
        o.FastSetDataProperty("length", new JsNumber(ScriptValues.CheckSafe(r.Length)));
        return o;
    }

    private JsValue SearchResultsArray(SearchResults results)
    {
        var items = new JsValue[results.Count];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = RangeObject(results[i]);
        }

        var array = new JsArray(_engine, items);
        array.FastSetDataProperty("truncated", results.Truncated ? JsBoolean.True : JsBoolean.False);
        return array;
    }

    private JsValue PlainObject(object value, Type type)
    {
        var o = new JsObject(_engine);
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.Name == "EqualityContract")
            {
                continue;
            }

            o.FastSetDataProperty(JsName(p), Wrap(p.GetValue(value), p.PropertyType));
        }

        return o;
    }

    private JsValue FromJson(JsonNode? node) => node switch
    {
        null => JsValue.Null,
        JsonObject obj => JsonObjectToJs(obj),
        JsonArray arr => new JsArray(_engine, [.. arr.Select(FromJson)]),
        JsonValue v when v.TryGetValue(out bool b) => b ? JsBoolean.True : JsBoolean.False,
        JsonValue v when v.TryGetValue(out long l) && Math.Abs(l) <= ScriptValues.MaxSafeInteger => new JsNumber(l),
        JsonValue v when v.TryGetValue(out double d) => new JsNumber(d),
        JsonValue v when v.TryGetValue(out string? s) => new JsString(s ?? ""),
        _ => new JsString(node.ToJsonString()),
    };

    private JsObject JsonObjectToJs(JsonObject obj)
    {
        var o = new JsObject(_engine);
        foreach ((string key, JsonNode? value) in obj)
        {
            o.FastSetDataProperty(key, FromJson(value));
        }

        return o;
    }

    /// <summary>インターフェイスの生きたオブジェクト。</summary>
    private ObjectInstance WrapperFor(object target, Type api)
    {
        if (_wrappers.TryGetValue(target, out ObjectInstance? existing))
        {
            return existing;
        }

        var o = new JsObject(_engine);
        TypeShape shape = Shapes.GetOrAdd(api, TypeShape.Build);
        foreach (PropertyInfo p in shape.Properties)
        {
            PropertyInfo prop = p;
            var getter = new ClrFunction(_engine, "get " + JsName(prop), (_, _) => Call(() => Wrap(prop.GetMethod!.Invoke(target, BindingFlags.DoNotWrapExceptions, null, null, CultureInfo.InvariantCulture), prop.PropertyType)));
            ClrFunction? setter = prop.CanWrite
                ? new ClrFunction(_engine, "set " + JsName(prop), (_, args) => Call(() =>
                {
                    prop.SetMethod!.Invoke(target, BindingFlags.DoNotWrapExceptions, null, [ToClr(args.Length > 0 ? args[0] : JsValue.Undefined, prop.PropertyType, JsName(prop))], CultureInfo.InvariantCulture);
                    return JsValue.Undefined;
                }))
                : null;
            o.FastSetProperty(JsName(prop), new GetSetPropertyDescriptor(getter, setter ?? (JsValue)JsValue.Undefined, enumerable: true, configurable: true));
        }

        foreach (MethodInfo m in shape.Methods)
        {
            MethodInfo method = m;
            string name = JsName(method);
            o.FastSetProperty(name, new PropertyDescriptor(new ClrFunction(_engine, name, (_, args) => Invoke(target, method, args)),
                writable: false, enumerable: false, configurable: true));
        }

        if (shape.ListItemType is Type itemType)
        {
            Type listType = typeof(IReadOnlyList<>).MakeGenericType(itemType);
            PropertyInfo count = typeof(IReadOnlyCollection<>).MakeGenericType(itemType).GetProperty("Count")!;
            PropertyInfo indexer = listType.GetProperty("Item")!;
            var length = new ClrFunction(_engine, "length", (_, _) => Call(() => new JsNumber((int)count.GetValue(target)!)));
            var at = new ClrFunction(_engine, "at", (_, args) => Call(() =>
                Wrap(indexer.GetValue(target, [(int)args[0].AsNumber()]), itemType)));
            _engine.Call(_helpers.Get("makeIterable"), JsValue.Undefined, [o, length, at]);
        }

        _wrappers.AddOrUpdate(target, o);
        _targets.AddOrUpdate(o, target);
        return o;
    }

    private JsValue Invoke(object target, MethodInfo method, JsValue[] args) => Call(() =>
    {
        ParameterInfo[] ps = method.GetParameters();
        object?[] clr = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            JsValue a = i < args.Length ? args[i] : JsValue.Undefined;
            if (a.IsUndefined())
            {
                clr[i] = DefaultFor(ps[i], method);
            }
            else
            {
                clr[i] = ToClr(a, ps[i].ParameterType, ps[i].Name ?? "arg" + i);
            }
        }

        object? result;
        try
        {
            result = method.Invoke(target, BindingFlags.DoNotWrapExceptions, null, clr, CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }

        if (method.ReturnType == typeof(void))
        {
            return JsValue.Undefined;
        }

        if (result is BigInteger big && method.GetCustomAttribute<WideIntegerResultAttribute>() is { } wide)
        {
            int index = Array.FindIndex(ps, p => p.Name == wide.TypeParameter);
            string? type = index >= 0 ? clr[index] as string : null;
            if (type is not ("i64" or "u64"))
            {
                return new JsNumber((double)big);
            }
        }

        return Wrap(result, method.ReturnType);
    });

    private static object? DefaultFor(ParameterInfo p, MethodInfo method)
    {
        if (p.HasDefaultValue)
        {
            object? d = p.DefaultValue;
            if (d is null or DBNull)
            {
                return p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) is null
                    ? Activator.CreateInstance(p.ParameterType)
                    : null;
            }

            return d;
        }

        if (!p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) is not null)
        {
            var nullability = new NullabilityInfoContext().Create(p);
            if (nullability.WriteState == NullabilityState.Nullable)
            {
                return null;
            }
        }

        throw new Sdk.ArgumentError(Msg.T($"{method.Name} の引数 {p.Name} を指定してください。", $"Specify the argument {p.Name} of {method.Name}."));
    }

    /// <summary>API の呼び出しを包み、例外を JavaScript の例外にする。実行の制御の例外はそのまま伝える。</summary>
    private JsValue Call(Func<JsValue> work)
    {
        // API の呼び出しのたびにメモリの上限を確かめる (大きな配列を作った直後の呼び出しで止める)。
        _session.CheckMemory();
        try
        {
            return work();
        }
        catch (Exception ex) when (ex is not JavaScriptException && !ScriptErrorMap.IsControl(ex) && ex is not RecursionDepthOverflowException)
        {
            throw new JavaScriptException(ToJsError(ex));
        }
    }

    /// <summary>例外を JavaScript のエラーのオブジェクトにする。</summary>
    public JsValue ToJsError(Exception ex)
    {
        if (ex is JavaScriptException jse)
        {
            return jse.Error;
        }

        if (ex is AggregateException { InnerExceptions.Count: 1 } agg)
        {
            return ToJsError(agg.InnerExceptions[0]);
        }

        ScriptError se = ScriptErrorMap.ToScriptError(ex);
        JsValue ctor = _errors.Get(se.Name);
        ObjectInstance error = _engine.Construct(ctor, [new JsString(se.Message)]);
        switch (se)
        {
            case PermissionError pe:
                error.Set("permission", new JsString(pe.Permission));
                break;
            case LimitError le:
                error.Set("limit", new JsString(le.Limit));
                break;
            case IoError { Offset: long off }:
                error.Set("offset", new JsNumber(off));
                break;
        }

        return error;
    }

    // ======================== Promise・非同期イテレータ ========================

    private static Type? ResultTypeOf(Type taskType)
    {
        for (Type? t = taskType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
            {
                Type arg = t.GetGenericArguments()[0];
                return arg.Name == "VoidTaskResult" ? null : arg;
            }
        }

        return null;
    }

    public JsValue FromTask(Task task, Type? resultType, Func<object?, JsValue>? convert = null)
    {
        var (promise, resolve, reject) = _engine.Advanced.RegisterPromise();
        _session.BeginPending();
        task.ContinueWith(t => _session.Post(() =>
        {
            _session.EndPending();
            if (t.IsCompletedSuccessfully)
            {
                object? result = resultType is null ? null : t.GetType().GetProperty("Result")!.GetValue(t);
                JsValue value;
                try
                {
                    value = convert is not null ? convert(result) : resultType is null ? JsValue.Undefined : Wrap(result, resultType);
                }
                catch (Exception ex) when (!ScriptErrorMap.IsControl(ex))
                {
                    reject(ToJsError(ex));
                    return;
                }

                resolve(value);
            }
            else
            {
                Exception error = t.Exception?.InnerExceptions is { Count: 1 } one ? one[0] : (Exception?)t.Exception ?? new CanceledError(Msg.T("キャンセルされました。", "Canceled."));
                if (ScriptErrorMap.IsControl(error))
                {
                    throw error;
                }

                reject(ToJsError(error));
            }
        }), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return promise;
    }

    private static Type? AsyncItemType(Type t) =>
        t.GetInterfaces().Concat([t]).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))?.GetGenericArguments()[0];

    private JsValue AsyncIterable<T>(IAsyncEnumerable<T> source)
    {
        IAsyncEnumerator<T> e = source.GetAsyncEnumerator(Context.Token);
        var next = new ClrFunction(_engine, "next", (_, _) =>
        {
            Task<bool> moved = e.MoveNextAsync().AsTask();
            return FromTask(moved, typeof(bool), r =>
            {
                var step = new JsObject(_engine);
                bool more = r is true;
                step.FastSetDataProperty("value", more ? Wrap(e.Current, typeof(T)) : JsValue.Undefined);
                step.FastSetDataProperty("done", more ? JsBoolean.False : JsBoolean.True);
                return step;
            });
        });
        var close = new ClrFunction(_engine, "return", (_, _) =>
        {
            _ = e.DisposeAsync().AsTask();
            return JsValue.Undefined;
        });
        return _engine.Call(_helpers.Get("makeAsyncIterable"), JsValue.Undefined, [next, close]);
    }

    // ======================== AbortSignal ========================

    private sealed class SignalBox(CancellationToken token, CancellationTokenSource? owner)
    {
        public CancellationToken Token { get; } = token;

        public CancellationTokenSource? Owner { get; } = owner;

        public string Reason { get; set; } = "AbortError";
    }

    /// <summary><c>script.signal</c> などの <c>AbortSignal</c>。</summary>
    public ObjectInstance SignalFor(CancellationToken token)
    {
        if (token == Context.Token)
        {
            return _scriptSignal ??= CreateSignal(token, null);
        }

        return CreateSignal(token, null);
    }

    /// <summary><c>new AbortController()</c> の signal。</summary>
    public ObjectInstance CreateOwnedSignal()
    {
        var cts = new CancellationTokenSource();
        return CreateSignal(cts.Token, cts);
    }

    public void Abort(JsValue signal, string reason)
    {
        if (signal is ObjectInstance o && _signals.TryGetValue(o, out SignalBox? box) && box.Owner is { } cts)
        {
            box.Reason = reason;
            cts.Cancel();
        }
    }

    private ObjectInstance CreateSignal(CancellationToken token, CancellationTokenSource? owner)
    {
        var o = new JsObject(_engine);
        var box = new SignalBox(token, owner);
        o.FastSetProperty("aborted", new GetSetPropertyDescriptor(
            new ClrFunction(_engine, "get aborted", (_, _) => token.IsCancellationRequested ? JsBoolean.True : JsBoolean.False), JsValue.Undefined, true, true));
        o.FastSetProperty("reason", new GetSetPropertyDescriptor(
            new ClrFunction(_engine, "get reason", (_, _) => token.IsCancellationRequested ? new JsString(box.Reason) : JsValue.Undefined), JsValue.Undefined, true, true));
        o.FastSetDataProperty("throwIfAborted", new ClrFunction(_engine, "throwIfAborted", (_, _) =>
        {
            if (token.IsCancellationRequested)
            {
                throw new JavaScriptException(ToJsError(new CanceledError(Msg.T("キャンセルされました。", "Canceled."))));
            }

            return JsValue.Undefined;
        }));
        o.FastSetDataProperty("addEventListener", new ClrFunction(_engine, "addEventListener", (self, args) =>
        {
            if (args.Length >= 2 && args[0].ToString() == "abort" && args[1].IsCallable())
            {
                JsValue fn = args[1];
                token.Register(() => _session.PostAsync(() => _engine.Call(fn, o, [])));
            }

            return JsValue.Undefined;
        }));
        o.FastSetDataProperty("removeEventListener", new ClrFunction(_engine, "removeEventListener", (_, _) => JsValue.Undefined));
        o.FastSetDataProperty("onabort", JsValue.Null);
        token.Register(() => _session.PostAsync(() =>
        {
            JsValue handler = o.Get("onabort");
            if (handler.IsCallable())
            {
                _engine.Call(handler, o, []);
            }
        }));
        _signals.AddOrUpdate(o, box);
        return o;
    }

    // ======================== JavaScript → C# ========================

    public object? ToClr(JsValue v, Type type, string name)
    {
        if (v.IsUndefined() || v.IsNull())
        {
            if (!type.IsValueType || Nullable.GetUnderlyingType(type) is not null)
            {
                return null;
            }

            throw new Sdk.ArgumentError(Msg.T($"{name} を指定してください。", $"Specify {name}."));
        }

        Type t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(object))
        {
            return ToPlain(v);
        }

        if (t == typeof(string))
        {
            return v.IsString() ? v.AsString() : throw Wrong(name, Msg.T("文字列", "a string"));
        }

        if (t == typeof(bool))
        {
            return v.IsBoolean() ? v.AsBoolean() : throw Wrong(name, Msg.T("true か false", "true or false"));
        }

        if (t == typeof(int))
        {
            return (int)Integer(v, name, int.MinValue, int.MaxValue);
        }

        if (t == typeof(ulong))
        {
            BigInteger big = v.IsBigInt() ? BigOf(v) : new BigInteger(Integer(v, name, 0, long.MaxValue));
            return big >= 0 && big <= ulong.MaxValue ? (ulong)big : throw new Sdk.RangeError(Msg.T($"{name} が範囲外です: {big}", $"{name} is out of range: {big}"));
        }

        if (t == typeof(long))
        {
            return Integer(v, name, -ScriptValues.MaxSafeInteger, ScriptValues.MaxSafeInteger);
        }

        if (t == typeof(double))
        {
            return v.IsNumber() ? v.AsNumber() : v.IsBigInt() ? (double)BigOf(v) : throw Wrong(name, Msg.T("数値", "a number"));
        }

        if (t == typeof(BigInteger))
        {
            return v.IsBigInt() ? BigOf(v) : new BigInteger(Integer(v, name, long.MinValue, long.MaxValue));
        }

        if (t == typeof(OffsetArg))
        {
            return v.IsString() ? (OffsetArg)v.AsString() : (OffsetArg)Integer(v, name, -ScriptValues.MaxSafeInteger, ScriptValues.MaxSafeInteger);
        }

        if (t == typeof(RangeArg))
        {
            return v.IsString() ? (RangeArg)v.AsString() : RangeArgOf(v, name);
        }

        if (t == typeof(Sdk.ByteRange))
        {
            return RangeOf(v, name);
        }

        if (t == typeof(BytePattern))
        {
            return v.IsString() ? (BytePattern)v.AsString() : (BytePattern)BytesOf(v, name);
        }

        if (t == typeof(byte[]))
        {
            return BytesOf(v, name);
        }

        if (t == typeof(EncodingArg))
        {
            return v.IsString() ? (EncodingArg)v.AsString() : (EncodingArg)(int)Integer(v, name, 0, 65535);
        }

        if (t == typeof(TransformOperand))
        {
            return v.IsString() ? (TransformOperand)v.AsString()
                : v.IsBigInt() ? (TransformOperand)BigOf(v)
                : (TransformOperand)Integer(v, name, long.MinValue, long.MaxValue);
        }

        if (t == typeof(HashAlgorithmSpec))
        {
            return v.IsString() ? (HashAlgorithmSpec)v.AsString() : (HashAlgorithmSpec)(CustomCrc)ToOptions(v, typeof(CustomCrc), name)!;
        }

        if (t == typeof(CancellationToken))
        {
            return v is ObjectInstance so && _signals.TryGetValue(so, out SignalBox? box) ? box.Token : throw Wrong(name, "AbortSignal");
        }

        if (typeof(Delegate).IsAssignableFrom(t))
        {
            return v.IsCallable() ? MakeDelegate(v, t, name) : throw Wrong(name, Msg.T("関数", "a function"));
        }

        if (IsLiveApiType(t))
        {
            return v is ObjectInstance wo && _targets.TryGetValue(wo, out object? target) && t.IsInstanceOfType(target)
                ? target
                : throw Wrong(name, TsName(t));
        }

        if (t.IsGenericType && t.GetGenericTypeDefinition() is var def
            && (def == typeof(IReadOnlyDictionary<,>) || def == typeof(IDictionary<,>)) && t.GetGenericArguments()[0] == typeof(string))
        {
            return DictionaryOf(v, t.GetGenericArguments()[1], name);
        }

        if (ItemTypeOf(t) is Type itemType && t.IsInterface)
        {
            return ListOf(v, itemType, name);
        }

        if (t.IsClass && t.Namespace == typeof(IApp).Namespace)
        {
            return ToOptions(v, t, name);
        }

        throw Wrong(name, t.Name);
    }

    private static Sdk.ArgumentError Wrong(string name, string expected) => new(Msg.T($"{name} には{expected}を指定してください。", $"Specify {expected} for {name}."));

    private static BigInteger BigOf(JsValue v) => (BigInteger)v.ToObject()!;

    private static long Integer(JsValue v, string name, long min, long max)
    {
        if (v.IsBigInt())
        {
            BigInteger b = BigOf(v);
            return b >= min && b <= max ? (long)b : throw new Sdk.RangeError(Msg.T($"{name} が範囲外です: {b}", $"{name} is out of range: {b}"));
        }

        if (!v.IsNumber())
        {
            throw Wrong(name, Msg.T("数値", "a number"));
        }

        double d = v.AsNumber();
        if (double.IsNaN(d) || Math.Floor(d) != d)
        {
            throw new Sdk.ArgumentError(Msg.T($"{name} には整数を指定してください: {d.ToString(CultureInfo.InvariantCulture)}", $"Specify an integer for {name}: {d.ToString(CultureInfo.InvariantCulture)}"));
        }

        if (d < min || d > max || Math.Abs(d) > 9.2233720368547758E18)
        {
            throw new Sdk.RangeError(Msg.T($"{name} が範囲外です (2^53 を超える値は number で扱えません): {d.ToString(CultureInfo.InvariantCulture)}", $"{name} is out of range (values beyond 2^53 cannot be represented as a number): {d.ToString(CultureInfo.InvariantCulture)}"));
        }

        return (long)d;
    }

    /// <summary>
    /// 範囲のオブジェクト。<c>{ start: 0, length: "50G" }</c> のように欄に入力式の文字列があれば、ドキュメントの文脈で読むよう
    /// <c>(開始):(長さ)</c> / <c>(開始)..(終了)</c> の入力式にする。
    /// </summary>
    private static RangeArg RangeArgOf(JsValue v, string name)
    {
        if (v is ObjectInstance o)
        {
            JsValue start = o.Get("start");
            JsValue length = o.Get("length");
            JsValue end = o.Get("end");
            if (start.IsString() || length.IsString() || end.IsString())
            {
                string Part(JsValue x, string field) => x.IsString() ? "(" + x.AsString() + ")"
                    : "0x" + Integer(x, name + "." + field, 0, ScriptValues.MaxSafeInteger).ToString("X", CultureInfo.InvariantCulture);
                return !length.IsUndefined()
                    ? (RangeArg)(Part(start, "start") + ":" + Part(length, "length"))
                    : (RangeArg)(Part(start, "start") + ".." + Part(end, "end"));
            }
        }

        return RangeOf(v, name);
    }

    private static Sdk.ByteRange RangeOf(JsValue v, string name)
    {
        if (v is not ObjectInstance o)
        {
            throw Wrong(name, Msg.T("範囲 ({ start, length } または入力式の文字列)", "a range ({ start, length } or an input expression string)"));
        }

        long start = Integer(o.Get("start"), name + ".start", 0, ScriptValues.MaxSafeInteger);
        JsValue length = o.Get("length");
        if (!length.IsUndefined())
        {
            return new Sdk.ByteRange(start, Integer(length, name + ".length", 0, ScriptValues.MaxSafeInteger));
        }

        JsValue end = o.Get("end");
        if (!end.IsUndefined())
        {
            long e = Integer(end, name + ".end", 0, ScriptValues.MaxSafeInteger);
            return e >= start ? new Sdk.ByteRange(start, e - start) : throw new Sdk.ArgumentError(Msg.T($"{name} の end が start より前です。", $"The end of {name} is before its start."));
        }

        throw Wrong(name, Msg.T("範囲 ({ start, length })", "a range ({ start, length })"));
    }

    private static byte[] BytesOf(JsValue v, string name)
    {
        if (v.IsUint8Array() || v.IsUint8ClampedArray())
        {
            return v.IsUint8Array() ? v.AsUint8Array() : v.AsUint8ClampedArray();
        }

        if (v.IsArrayBuffer())
        {
            return v.AsArrayBuffer() ?? [];
        }

        if (v.IsInt8Array())
        {
            return [.. v.AsInt8Array().Select(b => (byte)b)];
        }

        if (v.IsArray())
        {
            JsArray a = v.AsArray();
            var bytes = new byte[a.Length];
            for (uint i = 0; i < a.Length; i++)
            {
                bytes[i] = (byte)Integer(a[i], $"{name}[{i}]", 0, 255);
            }

            return bytes;
        }

        throw Wrong(name, Msg.T("バイト列 (Uint8Array、数値の配列、Hex 文字列)", "a byte array (Uint8Array, an array of numbers, or a hex string)"));
    }

    private object ListOf(JsValue v, Type itemType, string name)
    {
        IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;
        if (v.IsArray())
        {
            JsArray a = v.AsArray();
            for (uint i = 0; i < a.Length; i++)
            {
                list.Add(ToClr(a[i], itemType, $"{name}[{i}]"));
            }
        }
        else if (v is ObjectInstance o && !_targets.TryGetValue(o, out _) && _engine.Call(_helpers.Get("isIterable"), JsValue.Undefined, [v]).AsBoolean())
        {
            JsValue array = _engine.Call(_engine.Intrinsics.Array.Get("from"), _engine.Intrinsics.Array, [v]);
            return ListOf(array, itemType, name);
        }
        else
        {
            // 1 つの値は 1 件の一覧として受け付ける (例: hash.compute(doc, null, "sha256"))。
            list.Add(ToClr(v, itemType, name));
        }

        return list;
    }

    private object DictionaryOf(JsValue v, Type valueType, string name)
    {
        if (v is not ObjectInstance o)
        {
            throw Wrong(name, Msg.T("オブジェクト", "an object"));
        }

        IDictionary dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType), StringComparer.Ordinal)!;
        foreach (JsValue key in o.GetOwnPropertyKeys(Types.String))
        {
            string k = key.ToString();
            PropertyDescriptor? desc = o.GetOwnProperty(key);
            if (desc is { Enumerable: true })
            {
                dict[k] = ToClr(o.Get(key), valueType, name + "." + k);
            }
        }

        return dict;
    }

    private object? ToOptions(JsValue v, Type type, string name)
    {
        if (v is not ObjectInstance o)
        {
            throw Wrong(name, Msg.T("オブジェクト", "an object"));
        }

        object options = Activator.CreateInstance(type)!;
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || p.GetIndexParameters().Length > 0)
            {
                continue;
            }

            JsValue value = o.Get(JsName(p));
            if (!value.IsUndefined())
            {
                p.SetValue(options, ToClr(value, p.PropertyType, name + "." + JsName(p)));
            }
        }

        return options;
    }

    /// <summary>型の決まっていない値 (設定の値・保存領域の値・パラメータ)。</summary>
    public object? ToPlain(JsValue v)
    {
        if (v.IsUndefined() || v.IsNull())
        {
            return null;
        }

        if (v.IsBoolean())
        {
            return v.AsBoolean();
        }

        if (v.IsNumber())
        {
            double d = v.AsNumber();
            return Math.Floor(d) == d && Math.Abs(d) <= ScriptValues.MaxSafeInteger ? (long)d : d;
        }

        if (v.IsString())
        {
            return v.AsString();
        }

        if (v.IsBigInt())
        {
            return BigOf(v);
        }

        if (v.IsUint8Array())
        {
            return v.AsUint8Array();
        }

        if (v.IsArray())
        {
            JsArray a = v.AsArray();
            var list = new List<object?>((int)a.Length);
            for (uint i = 0; i < a.Length; i++)
            {
                list.Add(ToPlain(a[i]));
            }

            return list;
        }

        if (v.IsDate())
        {
            return v.AsDate().ToDateTime().ToString("o", CultureInfo.InvariantCulture);
        }

        if (v is ObjectInstance o)
        {
            if (_targets.TryGetValue(o, out object? target))
            {
                return target;
            }

            if (v.IsCallable())
            {
                throw new Sdk.ArgumentError(Msg.T("関数は値として渡せません。", "A function cannot be passed as a value."));
            }

            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (JsValue key in o.GetOwnPropertyKeys(Types.String))
            {
                if (o.GetOwnProperty(key) is { Enumerable: true })
                {
                    dict[key.ToString()] = ToPlain(o.Get(key));
                }
            }

            return dict;
        }

        return v.ToString();
    }

    /// <summary>スクリプトの関数を .NET の関数にする。スクリプトのスレッドで呼ぶ (別のスレッドからは受け渡して待つ)。</summary>
    private Delegate MakeDelegate(JsValue fn, Type type, string name)
    {
        if (type == typeof(Action<ScriptEventArgs>))
        {
            return new Action<ScriptEventArgs>(e => _session.Invoke(() =>
            {
                CallHandler(fn, [Wrap(e, typeof(ScriptEventArgs))]);
                return true;
            }));
        }

        if (type == typeof(Action<double, string?>))
        {
            // 進捗の報告は待たない。
            return new Action<double, string?>((f, s) => _ = _session.PostAsync(() =>
                CallHandler(fn, [new JsNumber(f), s is null ? JsValue.Undefined : new JsString(s)])));
        }

        if (type == typeof(Func<byte[], long, byte[]>))
        {
            return new Func<byte[], long, byte[]>((data, offset) => _session.Invoke(() =>
            {
                JsValue result = _engine.Call(fn, JsValue.Undefined, [_engine.Intrinsics.Uint8Array.Construct(data), new JsNumber(offset)]);
                return BytesOf(result, name + Msg.T(" の戻り値", " return value"));
            }));
        }

        if (type == typeof(Func<IReadOnlyList<object?>, object?>))
        {
            // テンプレート拡張 (AUTO-13 の仕様 6): 引数を JavaScript の値にし、戻り値をテンプレートの値にする。
            return new Func<IReadOnlyList<object?>, object?>(args => _session.InvokeExtension(() =>
            {
                JsValue[] values = [.. args.Select(ExtensionArgument)];
                return ExtensionResult(_engine.Call(fn, JsValue.Undefined, values), name);
            }));
        }

        throw Wrong(name, Msg.T("関数", "a function"));
    }

    /// <summary>テンプレート拡張の引数 (数値・文字列・バイト列・パターンの読み取り専用の参照)。</summary>
    private JsValue ExtensionArgument(object? value) => value switch
    {
        long l when l is >= -ScriptValues.MaxSafeInteger and <= ScriptValues.MaxSafeInteger => new JsNumber(l),
        long l => new JsBigInt(l),
        ulong u => Wrap(u, typeof(ulong)),
        Int128 i => new JsBigInt(BigInteger.Parse(i.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
        UInt128 u => new JsBigInt(BigInteger.Parse(u.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
        Templates.TemplateNode node => Wrap(new Dictionary<string, object?>
        {
            ["name"] = node.DisplayName,
            ["path"] = node.Path,
            ["type"] = node.TypeName,
            ["start"] = node.Offset,
            ["length"] = node.Size,
            ["value"] = node.Value switch
            {
                long l when l is < -ScriptValues.MaxSafeInteger or > ScriptValues.MaxSafeInteger => new BigInteger(l),
                Int128 i => BigInteger.Parse(i.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                var v => v,
            },
            ["displayValue"] = node.GetDisplay(Templates.TemplateDisplayContext.Current),
        }, typeof(object)),
        _ => Wrap(value, typeof(object)),
    };

    /// <summary>テンプレート拡張の戻り値 (整数は long / Int128、小数は double、ほかに文字列・真偽値・バイト列)。</summary>
    private static object? ExtensionResult(JsValue v, string name)
    {
        if (v.IsNull() || v.IsUndefined())
        {
            return null;
        }

        if (v.IsBigInt())
        {
            BigInteger b = BigOf(v);
            return b >= long.MinValue && b <= long.MaxValue ? (long)b : Int128.Parse(b.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        if (v.IsNumber())
        {
            double d = v.AsNumber();
            return Math.Floor(d) == d && Math.Abs(d) <= ScriptValues.MaxSafeInteger ? (object)(long)d : d;
        }

        if (v.IsString())
        {
            return v.AsString();
        }

        if (v.IsBoolean())
        {
            return v.AsBoolean();
        }

        return BytesOf(v, name + Msg.T(" の戻り値", " return value"));
    }

    /// <summary>イベントのハンドラを呼ぶ。ハンドラの例外は出力パネルに出し、スクリプトは続ける。</summary>
    private void CallHandler(JsValue fn, JsValue[] args)
    {
        try
        {
            _engine.Call(fn, JsValue.Undefined, args);
        }
        catch (JavaScriptException ex)
        {
            _session.ReportError(ex);
        }
    }

    // ======================== 名前 ========================

    /// <summary>JavaScript での名前 (AUTO-04 の仕様 3)。</summary>
    public static string JsName(MemberInfo member)
    {
        if (member.GetCustomAttribute<ScriptNameAttribute>() is { } attr)
        {
            return attr.Name;
        }

        string name = member.Name;
        if (member is MethodInfo && name.EndsWith("Async", StringComparison.Ordinal) && name.Length > 5)
        {
            name = name[..^5];
        }

        return CamelCase(name);
    }

    public static string CamelCase(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>TypeScript での型の名前 (インターフェイスの先頭の I を除く)。</summary>
    public static string TsName(Type t) =>
        t.IsInterface && t.Name.Length > 1 && t.Name[0] == 'I' && char.IsUpper(t.Name[1]) ? t.Name[1..] : t.Name;

    private static Type? ItemTypeOf(Type t)
    {
        if (t == typeof(string))
        {
            return null;
        }

        if (t.IsArray)
        {
            return t.GetElementType();
        }

        foreach (Type i in t.GetInterfaces().Concat([t]))
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return i.GetGenericArguments()[0];
            }
        }

        return null;
    }

    /// <summary>API の型のメンバー (継承したインターフェイスを含む。SDK の外のインターフェイスのメンバーは除く)。</summary>
    internal sealed class TypeShape
    {
        public required IReadOnlyList<PropertyInfo> Properties { get; init; }

        public required IReadOnlyList<MethodInfo> Methods { get; init; }

        /// <summary>配列のように列挙できる (<c>IReadOnlyList&lt;T&gt;</c> を継承する) 場合の要素の型。</summary>
        public Type? ListItemType { get; init; }

        public static TypeShape Build(Type type)
        {
            IEnumerable<Type> types = type.IsInterface
                ? new[] { type }.Concat(type.GetInterfaces()).Where(i => i.Namespace == typeof(IApp).Namespace)
                : [type];
            var props = new List<PropertyInfo>();
            var methods = new List<MethodInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | (type.IsInterface ? BindingFlags.DeclaredOnly : 0);
            foreach (Type t in types)
            {
                foreach (PropertyInfo p in t.GetProperties(flags))
                {
                    if (p.GetIndexParameters().Length == 0 && seen.Add(JsName(p)))
                    {
                        props.Add(p);
                    }
                }

                foreach (MethodInfo m in t.GetMethods(flags))
                {
                    if (!m.IsSpecialName && m.DeclaringType != typeof(object) && seen.Add(JsName(m)))
                    {
                        methods.Add(m);
                    }
                }
            }

            Type? item = type.GetInterfaces().Concat([type])
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))?.GetGenericArguments()[0];
            return new TypeShape { Properties = props, Methods = methods, ListItemType = item };
        }
    }
}
