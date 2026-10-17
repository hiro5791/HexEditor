namespace HexEditor.Sdk;

/// <summary>
/// Overrides the TypeScript type written to <c>hexeditor.d.ts</c> for a type, member or parameter.
/// <ja><c>hexeditor.d.ts</c> に書く TypeScript の型を指定する。</ja>
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Method
    | AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Interface)]
public sealed class TsTypeAttribute(string type) : Attribute
{
    /// <summary>The TypeScript type. <ja>TypeScript の型。</ja></summary>
    public string Type { get; } = type;
}

/// <summary>
/// Overrides the JavaScript name of a member (the default is the C# name in camelCase without the <c>Async</c> suffix).
/// <ja>メンバーの JavaScript での名前を指定する (既定は C# の名前を camelCase にして <c>Async</c> を除いたもの)。</ja>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method)]
public sealed class ScriptNameAttribute(string name) : Attribute
{
    /// <summary>The JavaScript name. <ja>JavaScript での名前。</ja></summary>
    public string Name { get; } = name;
}

/// <summary>
/// The method returns an integer that JavaScript receives as <c>bigint</c> when the type argument named <see cref="TypeParameter"/>
/// is a 64-bit type (<c>i64</c> / <c>u64</c>) and as <c>number</c> otherwise.
/// <ja>整数を返すメソッドで、<see cref="TypeParameter"/> の型の引数が 64 bit (<c>i64</c> / <c>u64</c>) なら JavaScript では <c>bigint</c>、それ以外は <c>number</c> で返す。</ja>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WideIntegerResultAttribute(string typeParameter) : Attribute
{
    /// <summary>The name of the parameter that holds the integer type. <ja>整数の型を受け取る引数の名前。</ja></summary>
    public string TypeParameter { get; } = typeParameter;
}

/// <summary>
/// The member needs the permission <see cref="Permission"/> (AUTO-34). Shown in the API reference.
/// <ja>このメンバーには許可 <see cref="Permission"/> が必要 (AUTO-34)。API リファレンスに出す。</ja>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface)]
public sealed class RequiresPermissionAttribute(string permission) : Attribute
{
    /// <summary>The permission name. <ja>許可の名前。</ja></summary>
    public string Permission { get; } = permission;
}
