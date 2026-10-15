using System.Globalization;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates;

/// <summary>診断の重さ (TPL-27 の仕様 4)。</summary>
public enum TemplateSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// テンプレートのエラー・警告 (TPL-27 の仕様 2)。<see cref="Code"/> は <c>HXT1004</c> の形で、画面の文言はリソース
/// <c>TplDiag_&lt;Code&gt;</c> を <see cref="Arguments"/> で埋めて作る (仕様 7)。<see cref="Message"/> は英語の既定の文言
/// (コマンドラインやスクリプトの例外で使う)。
/// </summary>
public sealed record TemplateDiagnostic(
    TemplateSeverity Severity,
    string Code,
    IReadOnlyList<string> Arguments,
    TemplateSourceLocation? Location = null,
    long? DataOffset = null,
    string? Path = null,
    IReadOnlyList<string>? CallStack = null)
{
    public string Message => TemplateMessages.Format(Code, Arguments);

    /// <summary>データの位置がセクションの中の場合のセクション名。</summary>
    public string? SectionName { get; init; }

    /// <summary>内部エラー (評価器の不具合) の詳細 (仕様: TPL-37 の 5)。</summary>
    public string? InternalDetail { get; init; }

    public override string ToString()
    {
        string where = Location is { } l ? $"{l.File}:{l.Line}:{l.Column}: " : string.Empty;
        return $"{where}{Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
    }
}

/// <summary>エラーのコードと英語の既定の文言 (TPL-27 の仕様 7)。</summary>
public static class TemplateMessages
{
    // 構文・読み込み (1xxx)
    public const string UnexpectedCharacter = "HXT1001";
    public const string Expected = "HXT1002";
    public const string UnexpectedToken = "HXT1003";
    public const string InvalidNumber = "HXT1004";
    public const string UnterminatedString = "HXT1005";
    public const string InvalidEscape = "HXT1006";
    public const string UnterminatedComment = "HXT1007";
    public const string NestingTooDeep = "HXT1008";
    public const string InvalidByteString = "HXT1009";
    public const string InvalidUtf8 = "HXT1010";
    public const string TemplateTooLarge = "HXT1011";
    public const string TooManyErrors = "HXT1012";
    public const string InvalidPragma = "HXT1013";
    public const string UnknownPragma = "HXT1014";
    public const string DuplicateDefinition = "HXT1015";
    public const string ImportNotFound = "HXT1016";
    public const string NotPermitted = "HXT1017";
    public const string AppVersionRequired = "HXT1018";
    public const string UnknownAttribute = "HXT1019";
    public const string EnumValueOverflow = "HXT1020";
    public const string DuplicateEnumValue = "HXT1021";
    public const string LimitClamped = "HXT1022";
    public const string TemplatesTooLarge = "HXT1023";

    // 評価 (2xxx)
    public const string Undefined = "HXT2001";
    public const string DivisionByZero = "HXT2002";
    public const string TypeMismatch = "HXT2003";
    public const string RecursionTooDeep = "HXT2004";
    public const string StepLimit = "HXT2005";
    public const string PatternLimit = "HXT2006";
    public const string MemoryLimit = "HXT2007";
    public const string TimeLimit = "HXT2008";
    public const string PlacementOutOfRange = "HXT2009";
    public const string ReadOutOfRange = "HXT2010";
    public const string PatternOutOfRange = "HXT2011";
    public const string NegativePosition = "HXT2012";
    public const string ZeroSizeElements = "HXT2013";
    public const string ArrayReachedEnd = "HXT2014";
    public const string ArrayTruncated = "HXT2015";
    public const string PartialElement = "HXT2016";
    public const string StringNotTerminated = "HXT2017";
    public const string LebTooLong = "HXT2018";
    public const string StructTooLarge = "HXT2019";
    public const string PointerNotInteger = "HXT2020";
    public const string PointerOutOfRange = "HXT2021";
    public const string PatternInFunction = "HXT2022";
    public const string AssertFailed = "HXT2023";
    public const string ExpectFailed = "HXT2024";
    public const string RequireFailed = "HXT2025";
    public const string ExtensionNotRegistered = "HXT2026";
    public const string ExtensionFailed = "HXT2027";
    public const string ExtensionNotPermitted = "HXT2028";
    public const string IndexOutOfRange = "HXT2029";
    public const string SizeofVariable = "HXT2030";
    public const string BitfieldOverflow = "HXT2031";
    public const string BitfieldTooWide = "HXT2032";
    public const string DecompressFailed = "HXT2033";
    public const string DecompressLimit = "HXT2034";
    public const string UnsupportedCompression = "HXT2035";
    public const string HashTooLong = "HXT2036";
    public const string InvalidAssignment = "HXT2037";
    public const string DisplayFunctionFailed = "HXT2038";
    public const string DataUnreadable = "HXT2039";
    public const string UserWarning = "HXT2040";
    public const string Cancelled = "HXT2041";
    public const string ValueOverflow = "HXT2042";
    public const string NotCallable = "HXT2043";
    public const string ArgumentCount = "HXT2044";
    public const string UnsupportedKaitai = "HXT2046";
    public const string ContentsMismatch = "HXT2047";
    public const string ValidFailed = "HXT2048";
    public const string OutputTruncated = "HXT2049";
    public const string SectionTotalLimit = "HXT2050";
    public const string NotAPattern = "HXT2051";
    public const string UnknownMember = "HXT2052";
    public const string InvalidArgument = "HXT2053";
    public const string MagicMismatch = "HXT2054";
    public const string Print = "HXT2055";
    public const string TempFileFailed = "HXT2056";

    // .ksy (3xxx)
    public const string YamlSyntax = "HXT3001";
    public const string YamlTag = "HXT3002";
    public const string YamlAliasLimit = "HXT3003";
    public const string YamlDuplicateKey = "HXT3004";
    public const string YamlTab = "HXT3005";
    public const string KsyInvalid = "HXT3006";
    public const string YamlTooDeep = "HXT3007";

    public const string Internal = "HXT9001";

    private static readonly Dictionary<string, string> English = new()
    {
        [UnexpectedCharacter] = "Unexpected character '{0}'",
        [Expected] = "'{0}' expected",
        [UnexpectedToken] = "Unexpected '{0}'",
        [InvalidNumber] = "Invalid number '{0}'",
        [UnterminatedString] = "String literal is not terminated",
        [InvalidEscape] = "Invalid escape sequence '{0}'",
        [UnterminatedComment] = "Comment is not terminated",
        [NestingTooDeep] = "Nesting is too deep (limit {0})",
        [InvalidByteString] = "Invalid byte string '{0}'",
        [InvalidUtf8] = "The template is not valid UTF-8",
        [TemplateTooLarge] = "The template is too large (limit 4 MB)",
        [TooManyErrors] = "Too many errors; only the first {0} are shown",
        [InvalidPragma] = "Invalid value '{1}' for pragma '{0}'",
        [UnknownPragma] = "Unknown pragma '{0}' was ignored",
        [DuplicateDefinition] = "'{0}' is already defined at {1}",
        [ImportNotFound] = "'{0}' was not found (searched: {1})",
        [NotPermitted] = "Templates cannot perform this operation: {0}",
        [AppVersionRequired] = "This template requires app version {0} or later",
        [UnknownAttribute] = "Unknown attribute '{0}' was ignored",
        [EnumValueOverflow] = "{0} does not fit in {1}",
        [DuplicateEnumValue] = "Value {0} has more than one name ('{1}' is used)",
        [LimitClamped] = "{0} {1} exceeds the maximum; {2} is used",
        [TemplatesTooLarge] = "The templates including imports are too large (limit 32 MB)",
        [Undefined] = "'{0}' is not defined{1}",
        [DivisionByZero] = "Division by zero",
        [TypeMismatch] = "Type mismatch: {0}",
        [RecursionTooDeep] = "Recursion is too deep (limit {0})",
        [StepLimit] = "The number of executed statements reached the limit ({0})",
        [PatternLimit] = "The number of patterns reached the limit ({0})",
        [MemoryLimit] = "The evaluation memory reached the limit ({0})",
        [TimeLimit] = "The evaluation time reached the limit ({0} s)",
        [PlacementOutOfRange] = "Placement 0x{0} is outside the data",
        [ReadOutOfRange] = "{0}(0x{1}) is outside the data",
        [PatternOutOfRange] = "'{0}' (0x{1}, {2} bytes) is outside the data",
        [NegativePosition] = "The position cannot be negative ({0})",
        [ZeroSizeElements] = "The array cannot end because the element size is 0",
        [ArrayReachedEnd] = "The array ended because the end of the data was reached",
        [ArrayTruncated] = "The array was truncated at the limit ({0} elements)",
        [PartialElement] = "The incomplete last element ({0} bytes) was not included",
        [StringNotTerminated] = "The string terminator was not found",
        [LebTooLong] = "LEB128 is longer than 10 bytes (offset 0x{0})",
        [StructTooLarge] = "The content of the structure ({0} bytes) exceeds the specified size ({1} bytes)",
        [PointerNotInteger] = "The pointer type must be an integer type",
        [PointerOutOfRange] = "Out of range (0x{0})",
        [PatternInFunction] = "Patterns cannot be declared inside functions",
        [AssertFailed] = "Assertion failed: {0}",
        [ExpectFailed] = "{0}",
        [RequireFailed] = "{0}",
        [ExtensionNotRegistered] = "Extension function '{0}' is not registered",
        [ExtensionFailed] = "Extension function '{0}' failed: {1}",
        [ExtensionNotPermitted] = "Extension function '{0}' is not permitted to run",
        [IndexOutOfRange] = "Index {0} is out of range (0 to {1})",
        [SizeofVariable] = "sizeof({0}) cannot be used because the size of the type is not fixed",
        [BitfieldOverflow] = "The bit field total ({0} bit) exceeds {1}",
        [BitfieldTooWide] = "A bit field cannot be wider than 64 bits ({0})",
        [DecompressFailed] = "{0} decompression failed: {1}",
        [DecompressLimit] = "Decompression stopped because the data reached the limit ({0})",
        [UnsupportedCompression] = "{0} is not supported in this version",
        [HashTooLong] = "The length ({0}) exceeds the limit of 256 MB per call",
        [InvalidAssignment] = "Cannot assign to '{0}'",
        [DisplayFunctionFailed] = "{0}",
        [DataUnreadable] = "The data at 0x{0} cannot be read",
        [UserWarning] = "{0}",
        [Cancelled] = "The evaluation was cancelled",
        [ValueOverflow] = "{0} does not fit in {1}",
        [NotCallable] = "'{0}' is not a function",
        [ArgumentCount] = "'{0}' takes {1} argument(s)",
        [UnsupportedKaitai] = "Kaitai Struct feature '{0}' is not supported",
        [ContentsMismatch] = "The contents do not match the expected bytes",
        [ValidFailed] = "Validation failed: {0}",
        [OutputTruncated] = "The output was truncated because it is too long",
        [SectionTotalLimit] = "The sections reached the total limit ({0})",
        [NotAPattern] = "'{0}' is not a pattern",
        [UnknownMember] = "'{0}' has no member '{1}'",
        [InvalidArgument] = "Invalid argument for '{0}': {1}",
        [MagicMismatch] = "The data does not match the magic {0}",
        [Print] = "{0}",
        [TempFileFailed] = "The temporary file could not be written: {0}",
        [YamlSyntax] = "YAML syntax error: {0}",
        [YamlTag] = "YAML tags are not allowed ({0})",
        [YamlAliasLimit] = "Too many YAML alias expansions",
        [YamlDuplicateKey] = "Duplicate key '{0}'",
        [YamlTab] = "Tab characters cannot be used for indentation",
        [KsyInvalid] = "Invalid .ksy: {0}",
        [YamlTooDeep] = "The YAML nesting is too deep",
        [Internal] = "Internal error in the template evaluator: {0}",
    };

    /// <summary>すべてのコード (ヘルプの説明ページとリソースの検査に使う。TC-TPL-27-04)。</summary>
    public static IReadOnlyCollection<string> AllCodes => English.Keys;

    public static string Format(string code, IReadOnlyList<string> args)
    {
        if (!English.TryGetValue(code, out string? format))
        {
            return code;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, args.Cast<object>().ToArray());
        }
        catch (FormatException)
        {
            return format;
        }
    }
}

/// <summary>テンプレートの読み込み (字句・構文・インポート) の失敗。</summary>
public sealed class TemplateLoadException(IReadOnlyList<TemplateDiagnostic> diagnostics)
    : Exception(diagnostics.Count > 0 ? diagnostics[0].ToString() : "template load failed")
{
    public IReadOnlyList<TemplateDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>診断を集める (構文エラーは最大 100 件。TPL-27 の仕様 1)。</summary>
internal sealed class DiagnosticBag
{
    public const int MaxErrors = 100;

    private readonly List<TemplateDiagnostic> _items = [];

    public IReadOnlyList<TemplateDiagnostic> Items => _items;

    public int ErrorCount { get; private set; }

    public bool Full => ErrorCount >= MaxErrors;

    public bool HasErrors => ErrorCount > 0;

    public void Add(TemplateDiagnostic d)
    {
        if (d.Severity == TemplateSeverity.Error)
        {
            if (ErrorCount >= MaxErrors)
            {
                return;
            }

            ErrorCount++;
        }

        _items.Add(d);
    }

    public void Error(string code, TemplateSourceLocation? at, params string[] args) =>
        Add(new TemplateDiagnostic(TemplateSeverity.Error, code, args, at));

    public void Warning(string code, TemplateSourceLocation? at, params string[] args) =>
        Add(new TemplateDiagnostic(TemplateSeverity.Warning, code, args, at));

    public void AddRange(IEnumerable<TemplateDiagnostic> items)
    {
        foreach (TemplateDiagnostic d in items)
        {
            Add(d);
        }
    }
}
