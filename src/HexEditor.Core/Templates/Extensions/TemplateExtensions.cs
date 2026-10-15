namespace HexEditor.Core.Templates.Extensions;

/// <summary>拡張点の種類 (TPL-19 の仕様 1)。</summary>
public enum TemplateExtensionPoint
{
    /// <summary>書式関数 (<c>format(関数)</c>)。</summary>
    Format,

    /// <summary>変換関数 (<c>read(関数)</c>)。</summary>
    Read,

    /// <summary>変換関数 (<c>write(関数)</c>)。</summary>
    Write,

    /// <summary>ポインタの変換 (<c>pointer_map(関数)</c>)。</summary>
    PointerMap,

    /// <summary>セクションのデータを作る関数 (独自の展開・復号。バイト列を受け取りバイト列を返す)。.ksy の独自の <c>process</c> にも使う。</summary>
    SectionProducer,

    /// <summary>計算だけの一般の関数。</summary>
    Function,
}

/// <summary>拡張関数に渡せる値の型 (TPL-19 の仕様 6)。</summary>
public enum TemplateValueType
{
    Void,
    Integer,
    Float,
    Bool,
    String,

    /// <summary>バイト列 (最大 64 MB)。</summary>
    Bytes,

    /// <summary>パターンの読み取り専用の参照 (<see cref="TemplateNode"/>)。</summary>
    Pattern,

    /// <summary>何でも受け付ける。</summary>
    Any,
}

/// <summary>
/// テンプレート拡張として登録された関数 (TPL-19 の仕様 1)。<see cref="Invoke"/> の引数・戻り値は
/// <see cref="long"/> / <see cref="ulong"/> / <see cref="Int128"/> / <see cref="double"/> / <see cref="bool"/> / <see cref="string"/> /
/// <see cref="byte"/>[] / <see cref="TemplateNode"/> / null。拡張関数からドキュメントを書き換えることはできない
/// (<see cref="TemplateNode"/> には書き込みの操作がない)。
/// </summary>
public sealed class TemplateExtensionFunction
{
    public required string Name { get; init; }

    public TemplateExtensionPoint Point { get; init; } = TemplateExtensionPoint.Function;

    public IReadOnlyList<TemplateValueType> Parameters { get; init; } = [];

    public TemplateValueType Returns { get; init; } = TemplateValueType.Any;

    /// <summary>登録したスクリプト・プラグインの識別子 (実行の許可の確認に使う)。</summary>
    public string? Owner { get; init; }

    public required Func<IReadOnlyList<object?>, CancellationToken, object?> Invoke { get; init; }
}

/// <summary>
/// 拡張関数を提供する側 (スクリプト・プラグインの実行環境。08 の「テンプレート拡張」)。評価器はこのインターフェースだけを通して
/// 拡張関数を呼ぶ。
/// </summary>
public interface ITemplateExtensionHost
{
    TemplateExtensionFunction? Find(string name);

    /// <summary>実行の許可がある (08 の規則。許可がなければ呼び出しは失敗し、その部分だけが部分結果になる。TPL-19 の仕様 4)。</summary>
    bool IsPermitted(TemplateExtensionFunction function);

    /// <summary>1 回の呼び出しの時間の上限 (08 の上限)。</summary>
    TimeSpan CallTimeout { get; }

    /// <summary>拡張関数の中の例外の詳細 (スクリプトのスタックトレース) を出力パネルに出すための文字列にする。</summary>
    string Describe(Exception exception);
}

/// <summary>拡張関数の登録簿 (スクリプト・プラグインが登録する)。</summary>
public sealed class TemplateExtensionRegistry : ITemplateExtensionHost
{
    private readonly Dictionary<string, TemplateExtensionFunction> _functions = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>実行の許可の判定 (既定ではすべて許可しない)。</summary>
    public Func<TemplateExtensionFunction, bool> Permission { get; set; } = _ => false;

    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>テスト用: 拡張関数の呼び出し回数 (TC-TPL-19-03)。</summary>
    public long CallCount => Interlocked.Read(ref _calls);

    private long _calls;

    public event EventHandler? Changed;

    public void Register(TemplateExtensionFunction function)
    {
        lock (_lock)
        {
            _functions[function.Name] = function;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Unregister(string name)
    {
        bool removed;
        lock (_lock)
        {
            removed = _functions.Remove(name);
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>あるスクリプト・プラグインが登録した関数をすべて外す (スクリプトの終了時)。</summary>
    public void UnregisterOwner(string owner)
    {
        lock (_lock)
        {
            foreach (string key in _functions.Where(kv => kv.Value.Owner == owner).Select(kv => kv.Key).ToList())
            {
                _functions.Remove(key);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<TemplateExtensionFunction> Functions
    {
        get
        {
            lock (_lock)
            {
                return [.. _functions.Values];
            }
        }
    }

    public TemplateExtensionFunction? Find(string name)
    {
        lock (_lock)
        {
            if (!_functions.TryGetValue(name, out TemplateExtensionFunction? f))
            {
                return null;
            }

            return new TemplateExtensionFunction
            {
                Name = f.Name,
                Point = f.Point,
                Parameters = f.Parameters,
                Returns = f.Returns,
                Owner = f.Owner,
                Invoke = (args, ct) =>
                {
                    Interlocked.Increment(ref _calls);
                    return f.Invoke(args, ct);
                },
            };
        }
    }

    public bool IsPermitted(TemplateExtensionFunction function) => Permission(function);

    public string Describe(Exception exception) => exception.ToString();
}
