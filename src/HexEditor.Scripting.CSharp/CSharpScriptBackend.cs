using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using HexEditor.Sdk;
using HexEditor.Sdk.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Text;

namespace HexEditor.Scripting.CSharp;

/// <summary>
/// C# スクリプトのコンパイル (AUTO-03 の仕様 1・5・6)。Roslyn の C# スクリプトの書式に従う。<c>#load</c> はスクリプトのフォルダと
/// スクリプトライブラリのフォルダだけ、<c>#r</c> はアプリが許可したアセンブリだけ。NuGet の参照 (<c>#r "nuget:..."</c>) は使えない。
/// コンパイルしたアセンブリにはポータブル PDB を埋め込み、例外のスタックトレースに .csx の行番号を出す (AUTO-22 の仕様 3)。
/// </summary>
public sealed class CSharpScriptBackend : ICSharpScriptBackend, ICSharpCompletionBackend
{
    /// <summary>補完の候補の数の上限。</summary>
    private const int MaxCompletionItems = 400;

    public string Version => typeof(CSharpScriptBackend).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public CSharpCompileResult Compile(CSharpCompileRequest request, CancellationToken cancellationToken)
    {
        (ScriptOptions options, RestrictedSourceResolver sources) = Options(request);
        Script<object> script = CSharpScript.Create(request.Code, options, globalsType: typeof(ScriptGlobals));
        Compilation compilation = script.GetCompilation();
        var diagnostics = new List<CSharpDiagnostic>();
        foreach (Diagnostic d in compilation.GetDiagnostics(cancellationToken))
        {
            if (d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            {
                diagnostics.Add(ToDiagnostic(d));
            }
        }

        return Emit(sources, compilation, diagnostics, cancellationToken);
    }

    /// <summary>
    /// スクリプトエディタの補完 (08 の AUTO-20 の仕様 3)。位置の直前が <c>式.</c> ならその型 (または名前空間・型) のメンバー、そうでなければ
    /// その位置で見える名前 (グローバルの <c>App</c>・<c>Doc</c> など、ローカル変数、型、名前空間)。
    /// </summary>
    public IReadOnlyList<CSharpCompletionItem> Complete(CSharpCompileRequest request, int position, CancellationToken cancellationToken)
    {
        (ScriptOptions options, _) = Options(request);
        Script<object> script = CSharpScript.Create(request.Code, options, globalsType: typeof(ScriptGlobals));
        Compilation compilation = script.GetCompilation();
        SyntaxTree? tree = compilation.SyntaxTrees.LastOrDefault();
        if (tree is null)
        {
            return [];
        }

        SemanticModel model = compilation.GetSemanticModel(tree);
        position = Math.Clamp(position, 0, tree.Length);
        SyntaxToken token = position > 0 ? tree.GetRoot(cancellationToken).FindToken(position - 1) : default;
        ExpressionSyntax? receiver = token.IsKind(SyntaxKind.DotToken) && token.Parent is MemberAccessExpressionSyntax access ? access.Expression
            : token.Parent is SimpleNameSyntax { Parent: MemberAccessExpressionSyntax outer } name && outer.Name == name ? outer.Expression
            : null;
        IEnumerable<ISymbol> symbols;
        if (receiver is not null)
        {
            ISymbol? target = model.GetSymbolInfo(receiver, cancellationToken).Symbol;
            ITypeSymbol? type = model.GetTypeInfo(receiver, cancellationToken).Type;
            if (target is INamespaceOrTypeSymbol container && (target is INamespaceSymbol || type is null || SymbolEqualityComparer.Default.Equals(target, type)))
            {
                // 名前空間・型の名前の後: 入れ子の型と静的なメンバー
                symbols = model.LookupStaticMembers(position, container).Concat(container is INamespaceSymbol ns ? ns.GetMembers() : []);
            }
            else if (type is not null)
            {
                symbols = model.LookupSymbols(position, type, includeReducedExtensionMethods: true).Where(s => !s.IsStatic || s is IMethodSymbol { MethodKind: MethodKind.ReducedExtension });
            }
            else
            {
                return [];
            }
        }
        else
        {
            symbols = model.LookupSymbols(position);
        }

        // 入力中の名前 (位置の直前の識別子) で絞り、ローカル・グローバル・メンバーを型・名前空間より先にして上限まで返す
        int start = position;
        while (start > 0 && (char.IsLetterOrDigit(request.Code[start - 1]) || request.Code[start - 1] == '_'))
        {
            start--;
        }

        string prefix = request.Code[start..position];
        var chosen = new List<ISymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ISymbol symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!symbol.CanBeReferencedByName || symbol.IsImplicitlyDeclared && symbol is not IParameterSymbol || symbol.Name.StartsWith('<')
                || symbol is IMethodSymbol { MethodKind: not (MethodKind.Ordinary or MethodKind.ReducedExtension or MethodKind.LocalFunction) }
                || !symbol.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(symbol.Name))
            {
                continue;
            }

            chosen.Add(symbol);
        }

        return [.. chosen
            .OrderBy(s => s is INamespaceOrTypeSymbol ? 1 : 0)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCompletionItems)
            .Select(s => new CSharpCompletionItem(s.Name, KindOf(s), s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), Summary(s)))];
    }

    private static string KindOf(ISymbol symbol) => symbol switch
    {
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol { IsConst: true } => "constant",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        INamedTypeSymbol => "type",
        INamespaceSymbol => "namespace",
        _ => "variable",
    };

    /// <summary>XML ドキュメントの summary (参照のアセンブリに XML があるとき)。</summary>
    private static string? Summary(ISymbol symbol)
    {
        string? xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrEmpty(xml))
        {
            return null;
        }

        int start = xml.IndexOf("<summary>", StringComparison.Ordinal);
        int end = xml.IndexOf("</summary>", StringComparison.Ordinal);
        if (start < 0 || end <= start)
        {
            return null;
        }

        string text = System.Text.RegularExpressions.Regex.Replace(xml[(start + 9)..end], "<[^>]+>", string.Empty);
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static (ScriptOptions Options, RestrictedSourceResolver Sources) Options(CSharpCompileRequest request)
    {
        var references = new List<MetadataReference>();
        foreach (string name in request.AllowedReferences)
        {
            if (request.ReferencePaths.TryGetValue(name, out string? path) && File.Exists(path))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        var sources = new RestrictedSourceResolver(request.LoadFolders, request.Path);
        var metadata = new RestrictedMetadataResolver(request.AllowedReferences, request.ReferencePaths);
        ScriptOptions options = ScriptOptions.Default
            .WithReferences(references)
            .WithImports("System", "System.IO", "System.Linq", "System.Text", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks",
                "HexEditor.Sdk")
            .WithMetadataResolver(metadata)
            .WithSourceResolver(sources)
            .WithEmitDebugInformation(true)
            .WithFileEncoding(new UTF8Encoding(false))
            .WithOptimizationLevel(request.Debug ? OptimizationLevel.Debug : OptimizationLevel.Release)
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithAllowUnsafe(false);
        if (request.Path is not null)
        {
            options = options.WithFilePath(request.Path);
        }

        return (options, sources);
    }

    private static CSharpCompileResult Emit(RestrictedSourceResolver sources, Compilation compilation, List<CSharpDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (diagnostics.Any(d => d.IsError))
        {
            return new CSharpCompileResult { Diagnostics = diagnostics, LoadedFiles = sources.Loaded };
        }

        using var pe = new MemoryStream();
        EmitResult emit = compilation.Emit(pe, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded),
            cancellationToken: cancellationToken);
        diagnostics.AddRange(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(ToDiagnostic));
        if (!emit.Success)
        {
            return new CSharpCompileResult { Diagnostics = diagnostics, LoadedFiles = sources.Loaded };
        }

        IMethodSymbol entry = compilation.GetEntryPoint(cancellationToken)
            ?? throw new InvalidOperationException("スクリプトの入口が見つかりません。");
        return new CSharpCompileResult
        {
            Assembly = pe.ToArray(),
            Diagnostics = diagnostics,
            LoadedFiles = sources.Loaded,
            EntryType = entry.ContainingType.MetadataName is var typeName && entry.ContainingType.ContainingNamespace is { IsGlobalNamespace: false } ns
                ? ns.ToDisplayString() + "." + typeName
                : entry.ContainingType.MetadataName,
            EntryMethod = entry.MetadataName,
        };
    }

    private static CSharpDiagnostic ToDiagnostic(Diagnostic d)
    {
        FileLinePositionSpan span = d.Location.GetMappedLineSpan();
        return new CSharpDiagnostic(d.Id, d.Severity == DiagnosticSeverity.Error, d.GetMessage(System.Globalization.CultureInfo.CurrentUICulture),
            string.IsNullOrEmpty(span.Path) ? null : span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    /// <summary><c>#load</c> の解決 (仕様 1: 同じフォルダとスクリプトライブラリのフォルダのみ)。読んだファイルとハッシュを記録する。</summary>
    private sealed class RestrictedSourceResolver(IReadOnlyList<string> folders, string? scriptPath) : SourceReferenceResolver
    {
        private readonly Dictionary<string, string> _loaded = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> Loaded => _loaded;

        public override bool Equals(object? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => 0;

        public override string? NormalizePath(string path, string? baseFilePath) => path;

        public override string? ResolveReference(string path, string? baseFilePath)
        {
            string? baseDir = baseFilePath is not null ? Path.GetDirectoryName(baseFilePath) : scriptPath is not null ? Path.GetDirectoryName(scriptPath) : null;
            string full = Path.GetFullPath(Path.IsPathRooted(path) || baseDir is null ? path : Path.Combine(baseDir, path));
            if (!File.Exists(full) && !Path.IsPathRooted(path))
            {
                foreach (string folder in folders)
                {
                    string candidate = Path.GetFullPath(Path.Combine(folder, path));
                    if (File.Exists(candidate))
                    {
                        full = candidate;
                        break;
                    }
                }
            }

            bool allowed = folders.Any(f => IsUnder(full, f));
            return allowed && File.Exists(full) ? full : null;
        }

        public override Stream OpenRead(string resolvedPath)
        {
            byte[] bytes = File.ReadAllBytes(resolvedPath);
            _loaded[resolvedPath] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return new MemoryStream(bytes);
        }

        public override SourceText ReadText(string resolvedPath)
        {
            using Stream stream = OpenRead(resolvedPath);
            return SourceText.From(stream, Encoding.UTF8);
        }

        private static bool IsUnder(string full, string folder)
        {
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary><c>#r</c> の解決 (仕様 1・5: 許可したアセンブリの名前だけ。NuGet は使えない)。</summary>
    private sealed class RestrictedMetadataResolver(IReadOnlyList<string> allowed, IReadOnlyDictionary<string, string> paths) : MetadataReferenceResolver
    {
        public override bool Equals(object? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => 0;

        public override ImmutableArray<PortableExecutableReference> ResolveReference(string reference, string? baseFilePath, MetadataReferenceProperties properties)
        {
            string name = reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? reference[..^4] : reference;
            if (allowed.Contains(name, StringComparer.OrdinalIgnoreCase) && paths.TryGetValue(name, out string? path) && File.Exists(path))
            {
                return [MetadataReference.CreateFromFile(path, properties)];
            }

            return [];
        }

        public override bool ResolveMissingAssemblies => false;
    }
}
