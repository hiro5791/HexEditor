using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using HexEditor.Core.Disassembly.Images;
using HexEditor.Core.Engine;

namespace HexEditor.Core.Disassembly.Msil;

/// <summary>メソッド 1 つ (型とメソッドの木の葉。ANA-27 の仕様 1)。<see cref="BodyOffset"/> はメソッド本体のファイル上の位置 (なければ null)。</summary>
public sealed record MsilMethod(int Token, string TypeName, string Name, string Signature, int Rva, long? BodyOffset)
{
    public string DisplayName => Name + Signature;
}

/// <summary>型 1 つ (名前空間・型・入れ子の型の名前とメソッド)。</summary>
public sealed record MsilType(string Namespace, string Name, IReadOnlyList<MsilMethod> Methods);

/// <summary>MSIL のリスティングの行の種類。</summary>
public enum MsilLineKind
{
    /// <summary>メソッドのヘッダ・ローカル変数・例外処理の節 (命令ではない行)。</summary>
    Info,
    Instruction,
}

/// <summary>
/// MSIL のリスティングの 1 行 (ANA-27 の仕様 2・4)。<see cref="Offset"/> と <see cref="Length"/> はファイル上の範囲 (Hex ビューと連動する)。
/// <see cref="BranchTarget"/> は分岐先の IL のオフセット、<see cref="MethodToken"/> は call の呼び出し先のメソッド (同じアセンブリの MethodDef)。
/// </summary>
public sealed record MsilLine(MsilLineKind Kind, long Offset, int Length, int IlOffset, string Mnemonic, string Operand, int? BranchTarget = null,
    int? MethodToken = null)
{
    public string Text => Kind == MsilLineKind.Info ? Mnemonic : $"IL_{IlOffset:x4}: {Mnemonic}{(Operand.Length > 0 ? " " + Operand : string.Empty)}";
}

/// <summary>ReadyToRun のネイティブコードの範囲 (ANA-27 の仕様 6)。</summary>
public sealed record NativeCode(long Offset, int Length, ulong Address, string ArchitectureId);

/// <summary>
/// .NET アセンブリ (CLI ヘッダを持つ PE) の MSIL の逆アセンブル (ANA-27)。メタデータは <c>System.Reflection.Metadata</c> で読み (仕様 1)、
/// メソッド本体 (ヘッダ、ローカル変数のシグネチャ、IL、例外処理の節) はファイル上のバイトから解釈する (仕様 2・4)。トークンは名前に解決する
/// (仕様 3。解決できなければ <c>token 0x06000001</c>。仕様 7)。ReadyToRun のネイティブコードの位置も求める (仕様 6)。
/// メタデータを読むのはメタデータの範囲だけで、ファイルサイズに比例しない。
/// </summary>
public sealed class MsilModule : IDisposable
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private readonly PEReader _pe;
    private readonly MetadataReader? _md;
    private readonly DocumentSnapshot _snapshot;
    private readonly ExecutableImage _image;
    private readonly Dictionary<int, MsilMethod> _methods = [];
    private readonly NameProvider _names;
    private (int Rva, int Size)? _runtimeFunctions;
    private int _methodEntryPoints = -1;

    private MsilModule(DocumentSnapshot snapshot, ExecutableImage image, PEReader pe, MetadataReader? md, List<string> issues)
    {
        _snapshot = snapshot;
        _image = image;
        _pe = pe;
        _md = md;
        Issues = issues;
        _names = new NameProvider(md);
        Types = md is null ? [] : ReadTypes(md, issues);
        ReadReadyToRun();
    }

    /// <summary>メタデータを読めなかった部分 (「メタデータの一部を読めません」。ANA-27 の「エラー」)。</summary>
    public IReadOnlyList<string> Issues { get; }

    public IReadOnlyList<MsilType> Types { get; }

    /// <summary>ReadyToRun のネイティブコードを持つか。</summary>
    public bool IsReadyToRun => _methodEntryPoints >= 0 && _runtimeFunctions is not null;

    /// <summary>ReadyToRun のネイティブコードのアーキテクチャ。</summary>
    public string? NativeArchitecture => _pe.PEHeaders.CoffHeader.Machine switch
    {
        Machine.Amd64 => DisasmArchitectures.X86_64,
        Machine.Arm64 => DisasmArchitectures.Arm64,
        Machine.I386 => DisasmArchitectures.X86_32,
        _ => null,
    };

    /// <summary>.NET の PE なら開く。そうでなければ null (例外にしない)。</summary>
    public static MsilModule? TryOpen(DocumentSnapshot snapshot, ExecutableImage image, CancellationToken token = default)
    {
        if (!image.IsDotNet)
        {
            return null;
        }

        var issues = new List<string>();
        var pe = new PEReader(new SnapshotStream(snapshot), PEStreamOptions.LeaveOpen | PEStreamOptions.PrefetchMetadata);
        try
        {
            token.ThrowIfCancellationRequested();
            MetadataReader? md = null;
            try
            {
                md = pe.GetMetadataReader();
            }
            catch (BadImageFormatException ex)
            {
                issues.Add(ex.Message);
            }

            return new MsilModule(snapshot, image, pe, md, issues);
        }
        catch (BadImageFormatException)
        {
            pe.Dispose();
            return null;
        }
    }

    private List<MsilType> ReadTypes(MetadataReader md, List<string> issues)
    {
        var types = new List<MsilType>();
        foreach (TypeDefinitionHandle handle in md.TypeDefinitions)
        {
            try
            {
                TypeDefinition type = md.GetTypeDefinition(handle);
                string name = _names.TypeDefName(handle, withNamespace: false);
                string ns = NamespaceOf(md, type);
                var methods = new List<MsilMethod>();
                foreach (MethodDefinitionHandle mh in type.GetMethods())
                {
                    try
                    {
                        MethodDefinition m = md.GetMethodDefinition(mh);
                        int rva = m.RelativeVirtualAddress;
                        long? body = rva != 0 ? _image.ToOffset(_image.ImageBase + (ulong)rva) : null;
                        var method = new MsilMethod(MetadataTokens.GetToken(mh), (ns.Length > 0 ? ns + "." : string.Empty) + name, md.GetString(m.Name),
                            _names.MethodSignatureText(m), rva, body);
                        methods.Add(method);
                        _methods[method.Token] = method;
                    }
                    catch (BadImageFormatException ex)
                    {
                        issues.Add(ex.Message);
                    }
                }

                types.Add(new MsilType(ns, name, methods));
            }
            catch (BadImageFormatException ex)
            {
                issues.Add(ex.Message);
            }
        }

        return types;
    }

    private static string NamespaceOf(MetadataReader md, TypeDefinition type)
    {
        TypeDefinition t = type;
        while (t.GetDeclaringType() is { IsNil: false } outer)
        {
            t = md.GetTypeDefinition(outer);
        }

        return md.GetString(t.Namespace);
    }

    public MsilMethod? MethodByToken(int token) => _methods.GetValueOrDefault(token);

    /// <summary>名前 (型名::メソッド名) でメソッドを探す。</summary>
    public MsilMethod? FindMethod(string typeName, string name) =>
        _methods.Values.FirstOrDefault(m => m.TypeName == typeName && m.Name == name);

    // ---- メソッド本体 ----

    /// <summary>メソッド本体を逆アセンブルする (ANA-27 の仕様 2)。本体がなければ空の一覧。</summary>
    public IReadOnlyList<MsilLine> Disassemble(MsilMethod method)
    {
        var lines = new List<MsilLine>();
        if (method.BodyOffset is not { } at)
        {
            return lines;
        }

        byte[] head = Read(at, 12);
        if (head.Length == 0)
        {
            return lines;
        }

        int headerSize;
        int codeSize;
        int maxStack = 8;
        int localSig = 0;
        bool moreSections = false;
        if ((head[0] & 3) == 2)
        {
            headerSize = 1;
            codeSize = head[0] >> 2;
            lines.Add(new MsilLine(MsilLineKind.Info, at, 1, 0, $".method header: tiny (code size {codeSize})", string.Empty));
        }
        else if ((head[0] & 3) == 3 && head.Length >= 12)
        {
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(head);
            headerSize = (flags >> 12) * 4;
            maxStack = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2));
            codeSize = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
            localSig = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
            moreSections = (flags & 0x8) != 0;
            lines.Add(new MsilLine(MsilLineKind.Info, at, headerSize, 0,
                $".method header: fat (code size {codeSize}, .maxstack {maxStack}{((flags & 0x10) != 0 ? ", init locals" : string.Empty)})", string.Empty));
        }
        else
        {
            lines.Add(new MsilLine(MsilLineKind.Info, at, 1, 0, ".method header: invalid", string.Empty));
            return lines;
        }

        if (localSig != 0)
        {
            lines.Add(new MsilLine(MsilLineKind.Info, at + 8, 4, 0, ".locals " + _names.LocalsText(localSig), string.Empty));
        }

        codeSize = (int)Math.Clamp(codeSize, 0, Math.Max(0, _snapshot.Length - (at + headerSize)));
        byte[] code = Read(at + headerSize, Math.Min(codeSize, 16 * 1024 * 1024));
        Instructions(code, at + headerSize, lines);
        if (moreSections)
        {
            ExceptionClauses(at + headerSize + codeSize, lines);
        }

        return lines;
    }

    /// <summary>メソッドの IL のバイト列 (テスト用)。</summary>
    public byte[] IlBytes(MsilMethod method)
    {
        if (method.BodyOffset is not { } at)
        {
            return [];
        }

        byte[] head = Read(at, 12);
        if ((head[0] & 3) == 2)
        {
            return Read(at + 1, head[0] >> 2);
        }

        int headerSize = (BinaryPrimitives.ReadUInt16LittleEndian(head) >> 12) * 4;
        return Read(at + headerSize, BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4)));
    }

    private void Instructions(byte[] code, long fileOffset, List<MsilLine> lines)
    {
        int pc = 0;
        while (pc < code.Length)
        {
            int start = pc;
            short value = code[pc++];
            if (value == 0xFE && pc < code.Length)
            {
                value = (short)(0xFE00 | code[pc++]);
            }

            if (!OpCodesByValue.TryGetValue(value, out OpCode op))
            {
                lines.Add(new MsilLine(MsilLineKind.Instruction, fileOffset + start, pc - start, start, ".byte", $"0x{code[start]:x2}"));
                continue;
            }

            int operandSize = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => pc + 4 <= code.Length ? 4 + 4 * (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(pc)), 100_000) : 4,
                _ => 4,
            };
            if (pc + operandSize > code.Length)
            {
                lines.Add(new MsilLine(MsilLineKind.Instruction, fileOffset + start, code.Length - start, start, op.Name ?? "?", "(truncated)"));
                break;
            }

            ReadOnlySpan<byte> operand = code.AsSpan(pc, operandSize);
            int next = pc + operandSize;
            string text = string.Empty;
            int? branch = null;
            int? method = null;
            switch (op.OperandType)
            {
                case OperandType.ShortInlineBrTarget:
                    branch = next + (sbyte)operand[0];
                    text = $"IL_{branch:x4}";
                    break;
                case OperandType.InlineBrTarget:
                    branch = next + BinaryPrimitives.ReadInt32LittleEndian(operand);
                    text = $"IL_{branch:x4}";
                    break;
                case OperandType.ShortInlineI:
                    text = (op.Value == OpCodes.Unaligned.Value ? (int)operand[0] : (sbyte)operand[0]).ToString(CultureInfo.InvariantCulture);
                    break;
                case OperandType.InlineI:
                    text = BinaryPrimitives.ReadInt32LittleEndian(operand).ToString(CultureInfo.InvariantCulture);
                    break;
                case OperandType.InlineI8:
                    text = "0x" + BinaryPrimitives.ReadInt64LittleEndian(operand).ToString("x", CultureInfo.InvariantCulture);
                    break;
                case OperandType.ShortInlineR:
                    text = BinaryPrimitives.ReadSingleLittleEndian(operand).ToString("R", CultureInfo.InvariantCulture);
                    break;
                case OperandType.InlineR:
                    text = BinaryPrimitives.ReadDoubleLittleEndian(operand).ToString("R", CultureInfo.InvariantCulture);
                    break;
                case OperandType.ShortInlineVar:
                    text = VariableName(op, operand[0]);
                    break;
                case OperandType.InlineVar:
                    text = VariableName(op, BinaryPrimitives.ReadUInt16LittleEndian(operand));
                    break;
                case OperandType.InlineSwitch:
                {
                    int count = (operandSize - 4) / 4;
                    var targets = new List<string>(count);
                    for (int i = 0; i < count; i++)
                    {
                        targets.Add($"IL_{next + BinaryPrimitives.ReadInt32LittleEndian(operand[(4 + i * 4)..]):x4}");
                    }

                    text = "(" + string.Join(", ", targets) + ")";
                    break;
                }

                case OperandType.InlineString:
                    text = _names.UserString(BinaryPrimitives.ReadInt32LittleEndian(operand));
                    break;
                case OperandType.InlineMethod:
                {
                    int token = BinaryPrimitives.ReadInt32LittleEndian(operand);
                    text = _names.TokenText(token);
                    if ((token >> 24) == 0x06)
                    {
                        method = token;
                    }

                    break;
                }

                case OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok or OperandType.InlineSig:
                    text = _names.TokenText(BinaryPrimitives.ReadInt32LittleEndian(operand));
                    break;
            }

            lines.Add(new MsilLine(MsilLineKind.Instruction, fileOffset + start, next - start, start, op.Name ?? "?", text, branch, method));
            pc = next;
        }
    }

    private static string VariableName(OpCode op, int index)
    {
        string name = op.Name ?? string.Empty;
        return name.Contains("arg", StringComparison.Ordinal) ? "A_" + index.ToString(CultureInfo.InvariantCulture) : "V_" + index.ToString(CultureInfo.InvariantCulture);
    }

    private void ExceptionClauses(long codeEnd, List<MsilLine> lines)
    {
        long at = (codeEnd + 3) & ~3L;
        for (int guard = 0; guard < 64; guard++)
        {
            byte[] h = Read(at, 4);
            if (h.Length < 4)
            {
                return;
            }

            byte kind = h[0];
            bool fat = (kind & 0x40) != 0;
            int size = fat ? h[1] | (h[2] << 8) | (h[3] << 16) : h[1];
            int clauseSize = fat ? 24 : 12;
            int count = Math.Max(0, (size - 4) / clauseSize);
            byte[] data = Read(at + 4, Math.Min(count * clauseSize, 1 << 20));
            for (int i = 0; i < count && (i + 1) * clauseSize <= data.Length; i++)
            {
                ReadOnlySpan<byte> c = data.AsSpan(i * clauseSize, clauseSize);
                uint flags = fat ? BinaryPrimitives.ReadUInt32LittleEndian(c) : BinaryPrimitives.ReadUInt16LittleEndian(c);
                uint tryOffset = fat ? BinaryPrimitives.ReadUInt32LittleEndian(c[4..]) : BinaryPrimitives.ReadUInt16LittleEndian(c[2..]);
                uint tryLength = fat ? BinaryPrimitives.ReadUInt32LittleEndian(c[8..]) : c[4];
                uint handlerOffset = fat ? BinaryPrimitives.ReadUInt32LittleEndian(c[12..]) : BinaryPrimitives.ReadUInt16LittleEndian(c[5..]);
                uint handlerLength = fat ? BinaryPrimitives.ReadUInt32LittleEndian(c[16..]) : c[7];
                int classToken = BinaryPrimitives.ReadInt32LittleEndian(c[(fat ? 20 : 8)..]);
                string type = (flags & 7) switch
                {
                    0 => "catch " + _names.TokenText(classToken),
                    1 => "filter IL_" + classToken.ToString("x4", CultureInfo.InvariantCulture),
                    2 => "finally",
                    4 => "fault",
                    _ => "unknown",
                };
                lines.Add(new MsilLine(MsilLineKind.Info, at + 4 + i * clauseSize, clauseSize, 0,
                    $".try IL_{tryOffset:x4} to IL_{tryOffset + tryLength:x4} {type} handler IL_{handlerOffset:x4} to IL_{handlerOffset + handlerLength:x4}",
                    string.Empty));
            }

            if ((kind & 0x80) == 0)
            {
                return;
            }

            at = (at + size + 3) & ~3L;
        }
    }

    private byte[] Read(long offset, int count)
    {
        if (offset < 0 || count <= 0)
        {
            return [];
        }

        byte[] b = new byte[(int)Math.Min(count, Math.Max(0, _snapshot.Length - offset))];
        int n = _snapshot.Read(offset, b).BytesReturned;
        return n == b.Length ? b : b[..n];
    }

    // ---- ReadyToRun (仕様 6) ----

    private void ReadReadyToRun()
    {
        try
        {
            CorHeader? cor = _pe.PEHeaders.CorHeader;
            DirectoryEntry native = cor?.ManagedNativeHeaderDirectory ?? default;
            if (native.RelativeVirtualAddress == 0 || native.Size < 16 || _image.ToOffset(_image.ImageBase + (ulong)native.RelativeVirtualAddress) is not { } at)
            {
                return;
            }

            byte[] header = Read(at, 16);
            if (header.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x00525452)
            {
                return;
            }

            uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            byte[] sections = Read(at + 16, (int)Math.Min(count, 256) * 12);
            for (int i = 0; i + 12 <= sections.Length; i += 12)
            {
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(sections.AsSpan(i));
                int rva = BinaryPrimitives.ReadInt32LittleEndian(sections.AsSpan(i + 4));
                int size = BinaryPrimitives.ReadInt32LittleEndian(sections.AsSpan(i + 8));
                if (type == 102)
                {
                    _runtimeFunctions = (rva, size);
                }
                else if (type == 103)
                {
                    _methodEntryPoints = rva;
                }
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            _runtimeFunctions = null;
        }
    }

    private long? OffsetOfRva(long rva) => _image.ToOffset(_image.ImageBase + (ulong)rva);

    private byte ByteAtRva(long rva) => OffsetOfRva(rva) is { } o && Read(o, 1) is [var b] ? b : throw new BadImageFormatException();

    /// <summary>NativeFormat の可変長の符号なし整数 (ReadyToRun の表)。</summary>
    private long DecodeUnsigned(long rva, out uint value)
    {
        uint b = ByteAtRva(rva);
        if ((b & 1) == 0)
        {
            value = b >> 1;
            return rva + 1;
        }

        if ((b & 2) == 0)
        {
            value = (b >> 2) | ((uint)ByteAtRva(rva + 1) << 6);
            return rva + 2;
        }

        if ((b & 4) == 0)
        {
            value = (b >> 3) | ((uint)ByteAtRva(rva + 1) << 5) | ((uint)ByteAtRva(rva + 2) << 13);
            return rva + 3;
        }

        if ((b & 8) == 0)
        {
            value = (b >> 4) | ((uint)ByteAtRva(rva + 1) << 4) | ((uint)ByteAtRva(rva + 2) << 12) | ((uint)ByteAtRva(rva + 3) << 20);
            return rva + 4;
        }

        if ((b & 16) == 0)
        {
            value = (uint)ByteAtRva(rva + 1) | ((uint)ByteAtRva(rva + 2) << 8) | ((uint)ByteAtRva(rva + 3) << 16) | ((uint)ByteAtRva(rva + 4) << 24);
            return rva + 5;
        }

        throw new BadImageFormatException();
    }

    /// <summary>NativeArray の要素の位置 (ILCompiler の NativeArray.TryGetAt と同じ)。</summary>
    private long? NativeArrayAt(long baseRva, uint index)
    {
        long b = DecodeUnsigned(baseRva, out uint header);
        uint elements = header >> 2;
        int entrySize = (int)(header & 3);
        if (index >= elements)
        {
            return null;
        }

        const uint BlockSize = 16;
        long offset = entrySize switch
        {
            0 => ByteAtRva(b + index / BlockSize),
            1 => ByteAtRva(b + 2 * (index / BlockSize)) | (ByteAtRva(b + 2 * (index / BlockSize) + 1) << 8),
            _ => (long)ByteAtRva(b + 4 * (index / BlockSize)) | ((long)ByteAtRva(b + 4 * (index / BlockSize) + 1) << 8)
                | ((long)ByteAtRva(b + 4 * (index / BlockSize) + 2) << 16) | ((long)ByteAtRva(b + 4 * (index / BlockSize) + 3) << 24),
        };
        offset += b;
        for (uint bit = BlockSize >> 1; bit > 0; bit >>= 1)
        {
            long after = DecodeUnsigned(offset, out uint val);
            if ((index & bit) != 0)
            {
                if ((val & 2) != 0)
                {
                    offset += val >> 2;
                    continue;
                }
            }
            else if ((val & 1) != 0)
            {
                offset = after;
                continue;
            }

            if ((val & 3) == 0)
            {
                if ((val >> 2) == (index & (BlockSize - 1)))
                {
                    offset = after;
                    break;
                }
            }

            return null;
        }

        return offset;
    }

    /// <summary>メソッドの ReadyToRun のネイティブコード (なければ null)。</summary>
    public NativeCode? NativeCodeOf(MsilMethod method)
    {
        if (!IsReadyToRun || _runtimeFunctions is not { } rf || NativeArchitecture is not { } arch)
        {
            return null;
        }

        try
        {
            uint rid = (uint)(method.Token & 0xFFFFFF);
            if (rid == 0 || NativeArrayAt(_methodEntryPoints, rid - 1) is not { } entry)
            {
                return null;
            }

            DecodeUnsigned(entry, out uint id);
            id = (id & 1) != 0 ? id >> 2 : id >> 1;
            bool x64 = arch != DisasmArchitectures.Arm64;
            int entrySize = x64 ? 12 : 8;
            if ((id + 1) * entrySize > rf.Size)
            {
                return null;
            }

            long? at = OffsetOfRva(rf.Rva + (long)id * entrySize);
            byte[] e = at is { } a ? Read(a, entrySize) : [];
            if (e.Length < entrySize)
            {
                return null;
            }

            uint begin = BinaryPrimitives.ReadUInt32LittleEndian(e);
            int length;
            if (x64)
            {
                length = (int)(BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(4)) - begin);
            }
            else
            {
                uint unwind = BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(4));
                if ((unwind & 3) != 0)
                {
                    length = (int)((unwind >> 2) & 0x7FF) * 4;
                }
                else
                {
                    long? x = OffsetOfRva(unwind);
                    byte[] xdata = x is { } xo ? Read(xo, 4) : [];
                    length = xdata.Length == 4 ? (int)(BinaryPrimitives.ReadUInt32LittleEndian(xdata) & 0x3FFFF) * 4 : 0;
                }
            }

            return OffsetOfRva(begin) is { } offset && length > 0 ? new NativeCode(offset, length, _image.ImageBase + begin, arch) : null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    public void Dispose() => _pe.Dispose();

    /// <summary>ドキュメントのスナップショットを読む読み取り専用のストリーム (PEReader に渡す)。</summary>
    private sealed class SnapshotStream(DocumentSnapshot snapshot) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => snapshot.Length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = (int)Math.Clamp(snapshot.Length - _position, 0, buffer.Length);
            if (n == 0)
            {
                return 0;
            }

            int read = snapshot.Read(_position, buffer[..n]).BytesReturned;
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => snapshot.Length + offset,
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
