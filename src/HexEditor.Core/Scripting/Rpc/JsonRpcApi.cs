using System.Collections;
using System.Numerics;
using System.Reflection;
using HexEditor.Core.Scripting.Docs;
using HexEditor.Core.Scripting.Js;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting.Rpc;

/// <summary>JSON-RPC で公開するメンバー 1 つ (メソッド、プロパティの取得・代入)。</summary>
internal sealed record JsonRpcMember(string Name, Type Owner, MethodInfo? Method, PropertyInfo? Property, string? UnavailableReason)
{
    public bool IsAvailable => UnavailableReason is null;
}

/// <summary>
/// 外部からの自動操作 (AUTO-45 の仕様 5) のメソッドの一覧。AUTO-04 の API の型 (HexEditor.Sdk) から、スクリプトと同じ名前の規則
/// (<c>オブジェクト.メソッド</c>。例: <c>document.readBytes</c>、<c>app.open</c>) で作る。プロパティも同じ名前で公開する
/// (例: <c>app.documents</c>、<c>document.length</c>。引数 <c>value</c> を渡すと代入)。
/// スクリプトの関数を受け取るもの (コールバック)・スクリプト自身を終えるもの・列挙子を返すものは JSON-RPC では使えない
/// (理由付きで一覧に載せ、呼ぶと「メソッドがありません」のエラー)。<c>app.on</c> は関数の代わりに通知で知らせる。
/// </summary>
internal static class JsonRpcApi
{
    private static readonly Lazy<IReadOnlyDictionary<string, JsonRpcMember>> Table = new(Build);

    public static IReadOnlyDictionary<string, JsonRpcMember> Members => Table.Value;

    /// <summary>オブジェクトの名前 (<c>document</c>、<c>templateNode</c> など)。</summary>
    public static string ObjectName(Type t) => JsBinder.CamelCase(JsBinder.TsName(t));

    private static IReadOnlyDictionary<string, JsonRpcMember> Build()
    {
        var table = new Dictionary<string, JsonRpcMember>(StringComparer.Ordinal);
        foreach (Type t in ApiDocGenerator.ApiTypes.Where(t => t.IsInterface || t.IsAbstract))
        {
            string obj = ObjectName(t);
            foreach (MemberInfo m in ApiDocGenerator.MembersOf(t))
            {
                string name = obj + "." + JsBinder.JsName(m);
                JsonRpcMember member = m switch
                {
                    MethodInfo method => new JsonRpcMember(name, t, method, null, Unavailable(t, method)),
                    PropertyInfo property => new JsonRpcMember(name, t, null, property, Unavailable(t, property)),
                    _ => throw new InvalidOperationException(),
                };
                table.TryAdd(name, member);
            }
        }

        return table;
    }

    /// <summary>JSON-RPC で使えない理由 (英語。一覧と、呼んだときのエラーに出す)。使えるなら null。</summary>
    private static string? Unavailable(Type owner, MethodInfo method)
    {
        if (owner == typeof(IApp) && method.Name is nameof(IApp.Stop) or nameof(IApp.Exit))
        {
            return "This ends a running script; disconnect the pipe instead.";
        }

        if (owner == typeof(ScriptEventArgs))
        {
            return "Event notifications cannot be canceled from JSON-RPC.";
        }

        if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            return "Use document.readBytes or document.exportRange instead of an enumerator.";
        }

        if (!(owner == typeof(IApp) && method.Name == nameof(IApp.On)) && method.GetParameters().Any(p => typeof(Delegate).IsAssignableFrom(p.ParameterType)))
        {
            return "This method takes a script function (callback).";
        }

        return null;
    }

    private static string? Unavailable(Type owner, PropertyInfo property)
    {
        if (typeof(Delegate).IsAssignableFrom(property.PropertyType) || property.PropertyType == typeof(CancellationToken))
        {
            return "This property is only meaningful inside a script.";
        }

        if (owner == typeof(IScript) && property.Name is nameof(IScript.Args))
        {
            return "This property is only meaningful inside a script.";
        }

        return null;
    }

    /// <summary>
    /// 呼び出しの対象を引数から決める必要がない「アプリの部品」の型 (app.hash、app.ui など、app と app の部品のプロパティでたどれるもの)。
    /// </summary>
    public static object? ServiceOf(IApp app, Type type)
    {
        if (type.IsInstanceOfType(app))
        {
            return app;
        }

        foreach (PropertyInfo p in typeof(IApp).GetProperties())
        {
            if (p.PropertyType == type && !IsDocumentType(type))
            {
                return p.GetValue(app);
            }
        }

        foreach (PropertyInfo p in typeof(IApp).GetProperties().Where(p => p.PropertyType.IsInterface && p.PropertyType != typeof(IDocument)
            && !p.PropertyType.IsGenericType))
        {
            PropertyInfo? inner = p.PropertyType.GetProperties().FirstOrDefault(q => q.PropertyType == type);
            if (inner is not null && p.GetValue(app) is { } service)
            {
                return inner.GetValue(service);
            }
        }

        return null;
    }

    /// <summary>ドキュメントの部品の型 (document.selection、document.bookmarks、document.highlights)。</summary>
    public static PropertyInfo? DocumentPart(Type type) =>
        typeof(IDocument).GetProperties().FirstOrDefault(p => p.PropertyType == type);

    private static bool IsDocumentType(Type type) => type == typeof(IDocument) || DocumentPart(type) is not null;

    /// <summary>引数・結果として「参照」(id / handle) で渡す型 (API のインターフェイスと抽象クラス)。</summary>
    public static bool IsReferenceType(Type type) =>
        (type.IsInterface || type.IsAbstract) && type.Namespace == typeof(IApp).Namespace && !typeof(IEnumerable).IsAssignableFrom(type);

    /// <summary>JSON の数値として安全に渡せる整数か (JavaScript の Number で失われない範囲)。</summary>
    public static bool IsSafeInteger(BigInteger value) => BigInteger.Abs(value) <= (BigInteger.One << 53) - 1;
}
