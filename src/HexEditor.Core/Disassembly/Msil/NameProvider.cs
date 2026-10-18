using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

namespace HexEditor.Core.Disassembly.Msil;

/// <summary>
/// メタデータのトークンを ildasm に近い表記の名前にする (ANA-27 の仕様 3): <c>void [System.Console]System.Console::WriteLine(string)</c>、
/// <c>"Hello"</c>。読めないトークンは <c>token 0x06000001</c> (仕様 7)。
/// </summary>
internal sealed class NameProvider : ISignatureTypeProvider<string, object?>
{
    private readonly MetadataReader? _md;

    public NameProvider(MetadataReader? md) => _md = md;

    private static string Token(int token) => "token 0x" + token.ToString("x8", CultureInfo.InvariantCulture);

    public string UserString(int token)
    {
        if (_md is null || (token >> 24) != 0x70)
        {
            return Token(token);
        }

        try
        {
            string s = _md.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF));
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                sb.Append(c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ when char.IsControl(c) => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                    _ => c.ToString(),
                });
            }

            return sb.Append('"').ToString();
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException)
        {
            return Token(token);
        }
    }

    public string TokenText(int token)
    {
        if (_md is null)
        {
            return Token(token);
        }

        try
        {
            EntityHandle handle = MetadataTokens.EntityHandle(token);
            return handle.Kind switch
            {
                HandleKind.MethodDefinition => MethodDefText((MethodDefinitionHandle)handle),
                HandleKind.MemberReference => MemberRefText((MemberReferenceHandle)handle),
                HandleKind.MethodSpecification => MethodSpecText((MethodSpecificationHandle)handle),
                HandleKind.FieldDefinition => FieldDefText((FieldDefinitionHandle)handle),
                HandleKind.TypeDefinition => TypeDefName((TypeDefinitionHandle)handle, true),
                HandleKind.TypeReference => TypeRefName((TypeReferenceHandle)handle),
                HandleKind.TypeSpecification => _md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null),
                HandleKind.StandaloneSignature => "sig " + Token(token),
                _ => Token(token),
            };
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException or InvalidCastException or InvalidOperationException)
        {
            return Token(token);
        }
    }

    private string MethodDefText(MethodDefinitionHandle handle)
    {
        MethodDefinition m = _md!.GetMethodDefinition(handle);
        MethodSignature<string> sig = m.DecodeSignature(this, null);
        string owner = TypeDefName(m.GetDeclaringType(), true);
        return FormatMethod(sig, owner, _md.GetString(m.Name), null);
    }

    private string MemberRefText(MemberReferenceHandle handle)
    {
        MemberReference r = _md!.GetMemberReference(handle);
        string owner = OwnerName(r.Parent);
        string name = _md.GetString(r.Name);
        if (r.GetKind() == MemberReferenceKind.Field)
        {
            return r.DecodeFieldSignature(this, null) + " " + owner + "::" + name;
        }

        return FormatMethod(r.DecodeMethodSignature(this, null), owner, name, null);
    }

    private string MethodSpecText(MethodSpecificationHandle handle)
    {
        MethodSpecification spec = _md!.GetMethodSpecification(handle);
        ImmutableArray<string> args = spec.DecodeSignature(this, null);
        string text = TokenText(MetadataTokens.GetToken(spec.Method));
        int paren = text.IndexOf('(', StringComparison.Ordinal);
        return paren > 0 ? text[..paren] + "<" + string.Join(", ", args) + ">" + text[paren..] : text;
    }

    private string FieldDefText(FieldDefinitionHandle handle)
    {
        FieldDefinition f = _md!.GetFieldDefinition(handle);
        return f.DecodeSignature(this, null) + " " + TypeDefName(f.GetDeclaringType(), true) + "::" + _md.GetString(f.Name);
    }

    private string OwnerName(EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => TypeRefName((TypeReferenceHandle)parent),
        HandleKind.TypeDefinition => TypeDefName((TypeDefinitionHandle)parent, true),
        HandleKind.TypeSpecification => _md!.GetTypeSpecification((TypeSpecificationHandle)parent).DecodeSignature(this, null),
        HandleKind.MethodDefinition => TypeDefName(_md!.GetMethodDefinition((MethodDefinitionHandle)parent).GetDeclaringType(), true),
        HandleKind.ModuleReference => "[.module " + _md!.GetString(_md.GetModuleReference((ModuleReferenceHandle)parent).Name) + "]",
        _ => Token(MetadataTokens.GetToken(parent)),
    };

    private static string FormatMethod(MethodSignature<string> sig, string owner, string name, string? generic)
    {
        string instance = sig.Header.IsInstance ? "instance " : string.Empty;
        return instance + sig.ReturnType + " " + owner + "::" + name + (generic ?? string.Empty) + "(" + string.Join(", ", sig.ParameterTypes) + ")";
    }

    /// <summary>型とメソッドの木に出すメソッドのシグネチャ (<c>(int32)</c> など)。</summary>
    public string MethodSignatureText(MethodDefinition m)
    {
        try
        {
            MethodSignature<string> sig = m.DecodeSignature(this, null);
            return "(" + string.Join(", ", sig.ParameterTypes) + ") : " + sig.ReturnType;
        }
        catch (BadImageFormatException)
        {
            return "(?)";
        }
    }

    public string LocalsText(int token)
    {
        if (_md is null || (token >> 24) != 0x11)
        {
            return Token(token);
        }

        try
        {
            StandaloneSignature s = _md.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(token & 0xFFFFFF));
            ImmutableArray<string> locals = s.DecodeLocalSignature(this, null);
            return "(" + string.Join(", ", locals.Select((t, i) => t + " V_" + i.ToString(CultureInfo.InvariantCulture))) + ")";
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            return Token(token);
        }
    }

    public string TypeDefName(TypeDefinitionHandle handle, bool withNamespace)
    {
        TypeDefinition t = _md!.GetTypeDefinition(handle);
        string name = _md.GetString(t.Name);
        if (t.GetDeclaringType() is { IsNil: false } outer)
        {
            return TypeDefName(outer, withNamespace) + "/" + name;
        }

        string ns = _md.GetString(t.Namespace);
        return withNamespace && ns.Length > 0 ? ns + "." + name : name;
    }

    private string TypeRefName(TypeReferenceHandle handle)
    {
        TypeReference r = _md!.GetTypeReference(handle);
        string name = _md.GetString(r.Name);
        string ns = _md.GetString(r.Namespace);
        string full = ns.Length > 0 ? ns + "." + name : name;
        return r.ResolutionScope.Kind switch
        {
            HandleKind.AssemblyReference => "[" + _md.GetString(_md.GetAssemblyReference((AssemblyReferenceHandle)r.ResolutionScope).Name) + "]" + full,
            HandleKind.TypeReference => TypeRefName((TypeReferenceHandle)r.ResolutionScope) + "/" + name,
            _ => full,
        };
    }

    // ---- ISignatureTypeProvider ----

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "int8",
        PrimitiveTypeCode.Byte => "uint8",
        PrimitiveTypeCode.Int16 => "int16",
        PrimitiveTypeCode.UInt16 => "uint16",
        PrimitiveTypeCode.Int32 => "int32",
        PrimitiveTypeCode.UInt32 => "uint32",
        PrimitiveTypeCode.Int64 => "int64",
        PrimitiveTypeCode.UInt64 => "uint64",
        PrimitiveTypeCode.Single => "float32",
        PrimitiveTypeCode.Double => "float64",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.IntPtr => "native int",
        PrimitiveTypeCode.UIntPtr => "native uint",
        PrimitiveTypeCode.TypedReference => "typedref",
        _ => typeCode.ToString(),
    };

    private static string Kind(byte rawTypeKind) => rawTypeKind == (byte)SignatureTypeKind.ValueType ? "valuetype " : "class ";

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        Kind(rawTypeKind) + TypeDefName(handle, true);

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        Kind(rawTypeKind) + TypeRefName(handle);

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType + " pinned";

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(", ", typeArguments) + ">";

    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index.ToString(CultureInfo.InvariantCulture);

    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index.ToString(CultureInfo.InvariantCulture);

    public string GetFunctionPointerType(MethodSignature<string> signature) =>
        "method " + signature.ReturnType + " *(" + string.Join(", ", signature.ParameterTypes) + ")";

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
        unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier.Replace("class ", string.Empty, StringComparison.Ordinal) + ")";

    public string GetSentinelType() => "...";
}
