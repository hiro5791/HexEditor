using System.Text;
using HexEditor.Core.Expressions;
using HexEditor.Core.Operations;

namespace HexEditor.Cli;

/// <summary>サブコマンド 1 回の実行の文脈: 引数・出力・メッセージ・長時間処理 (進捗とキャンセル)。</summary>
public sealed class CommandContext : IDisposable
{
    private readonly OperationCenter _operations = new();
    private readonly LazyFileWriter? _file;

    public CommandContext(CliHost host, Messages messages, ParsedArguments args, string commandName, bool redirectOutput = true)
    {
        Host = host;
        M = messages;
        Args = args;
        CommandName = commandName;
        Quiet = args.Flag("quiet");
        Format = args.Value("format") ?? "text";
        if (redirectOutput && args.Value("output") is { Length: > 0 } output)
        {
            OutputPath = host.Resolve(output);
            _file = new LazyFileWriter(OutputPath);
            Out = _file;
        }
        else
        {
            Out = host.Out;
        }
    }

    public CliHost Host { get; }

    public Messages M { get; }

    public ParsedArguments Args { get; }

    public string CommandName { get; }

    public bool Quiet { get; }

    /// <summary><c>--format</c> の値 (既定 <c>text</c>)。</summary>
    public string Format { get; }

    public bool Json => Format == "json";

    /// <summary>結果の出力先 (標準出力、または <c>--output</c> のファイル)。</summary>
    public TextWriter Out { get; }

    /// <summary><c>--output</c> のファイル。標準出力なら null。</summary>
    public string? OutputPath { get; }

    /// <summary>標準エラー出力 (進捗・警告・エラー)。</summary>
    public TextWriter Err => Host.Err;

    public CancellationToken Cancellation => Host.Cancellation;

    /// <summary>警告を標準エラー出力に出す (<c>--quiet</c> では出さない)。</summary>
    public void Warn(string key, params object?[] args)
    {
        if (!Quiet)
        {
            Err.WriteLine(M.Format(key, args));
        }
    }

    /// <summary>出力ファイルを閉じる。成功したら残し、失敗・キャンセルなら消す (AUTO-36 の仕様 6)。</summary>
    public void CloseOutput(bool keep)
    {
        Out.Flush();
        _file?.Close(keep);
    }

    /// <summary>出力先のファイルが入力ファイルと同じなら誤り (入力を壊さない)。</summary>
    public void RejectOutputOverInput(IEnumerable<string> inputs)
    {
        if (OutputPath is null)
        {
            return;
        }

        foreach (string input in inputs)
        {
            if (string.Equals(Path.GetFullPath(input), OutputPath, StringComparison.OrdinalIgnoreCase))
            {
                throw CliException.Usage("Cli_Error_OutputIsInput", OutputPath);
            }
        }
    }

    /// <summary>
    /// 長時間処理として実行する。端末なら標準エラー出力に 1 行で進捗を出し (リダイレクト時は出さない。AUTO-36 の仕様 5)、
    /// キャンセル (Ctrl+C) されたら <see cref="OperationCanceledException"/> を投げる。
    /// </summary>
    public T Run<T>(string name, long? totalBytes, Func<LongRunningOperation, T> work)
    {
        using var progress = new ProgressLine(Host.ErrIsTerminal && !Quiet ? Err : null, name);
        Task<T> task = _operations.RunAsync(name, OperationKind.ReadOnly, null, totalBytes, op =>
        {
            progress.Attach(op);
            using CancellationTokenRegistration registration = Cancellation.Register(op.Cancel);
            return Task.FromResult(work(op));
        });
        try
        {
            return task.GetAwaiter().GetResult();
        }
        finally
        {
            progress.Finish();
        }
    }

    /// <summary>入力式を評価する (接頭辞のない数値は 10 進。AUTO-36 の仕様 4)。誤りは終了コード 2。</summary>
    public long Evaluate(string option, string text, long length)
    {
        try
        {
            return ExpressionEvaluator.Evaluate(text, new CliExpressionContext(length), DefaultRadix.Decimal);
        }
        catch (ExpressionException ex)
        {
            throw CliException.Usage("Cli_Error_Expression", option, text, ex.Position + 1, ExpressionMessage(ex));
        }
    }

    private string ExpressionMessage(ExpressionException ex) => M.Get("Cli_Expression_" + ex.Error);

    /// <summary>範囲 (<c>開始:長さ</c> または <c>開始..終了</c>。終了は含まない) を評価する。ファイルの外なら終了コード 2。</summary>
    public (long Offset, long Length) EvaluateRange(string option, string text, long fileLength)
    {
        (long start, long length) = ParseRange(option, text, fileLength);
        if (start < 0 || length < 0 || start > fileLength || start + length > fileLength)
        {
            throw CliException.Usage("Cli_Error_RangeOutside", option, text, fileLength);
        }

        return (start, length);
    }

    private (long, long) ParseRange(string option, string text, long fileLength)
    {
        int dots = text.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            long start = Evaluate(option, text[..dots], fileLength);
            long end = Evaluate(option, text[(dots + 2)..], fileLength);
            return (start, end - start);
        }

        int colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            return (Evaluate(option, text[..colon], fileLength), Evaluate(option, text[(colon + 1)..], fileLength));
        }

        // 開始だけを書いたら末尾まで。
        long only = Evaluate(option, text, fileLength);
        return (only, fileLength - only);
    }

    public void Dispose() => _file?.Dispose();

    /// <summary>入力式の文脈 (コマンドラインにはカーソルも選択もない。<c>end</c> はファイルの長さ)。</summary>
    private sealed class CliExpressionContext(long length) : IExpressionContext
    {
        public long Cursor => 0;

        public long Length => length;

        public long SelectionStart => 0;

        public long SelectionLength => 0;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }
}

/// <summary>
/// 最初の書き込みまでファイルを作らない出力 (引数の誤りで終わる場合に、既存のファイルを消さないため)。UTF-8 (BOM なし)、改行は LF。
/// </summary>
internal sealed class LazyFileWriter(string path) : TextWriter
{
    private StreamWriter? _writer;
    private bool _closed;

    public override Encoding Encoding => Encoding.UTF8;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string NewLine { get => "\n"; set { _ = value; } }

    private StreamWriter Writer
    {
        get
        {
            if (_writer is null)
            {
                try
                {
                    var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16) { NewLine = "\n" };
                }
                catch (UnauthorizedAccessException)
                {
                    throw new CliException(ExitCodes.Denied, "Cli_Error_AccessDenied", path);
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException)
                {
                    throw CliException.Io("Cli_Error_CannotWrite", path, ex.Message);
                }
            }

            return _writer;
        }
    }

    public override void Write(char value) => Writer.Write(value);

    public override void Write(char[] buffer, int index, int count) => Writer.Write(buffer, index, count);

    public override void Write(string? value) => Writer.Write(value);

    public override void Write(ReadOnlySpan<char> buffer) => Writer.Write(buffer);

    public override void Flush() => _writer?.Flush();

    /// <summary>閉じる。<paramref name="keep"/> が偽なら、書きかけのファイルを消す。</summary>
    public void Close(bool keep)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        if (_writer is null)
        {
            // 何も書かなかった成功は空のファイルを作る。失敗なら何も作っていないので、既存のファイルにも触れない。
            if (!keep)
            {
                return;
            }

            _ = Writer;
        }

        try
        {
            _writer?.Dispose();
        }
        catch (IOException)
        {
            // ディスクが満杯などで最後の書き出しに失敗した。呼び出し側は成功として扱っているため、消して終わる。
            keep = false;
        }

        _writer = null;
        if (!keep)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 消せなければ残る (ファイルが使われている場合など)。
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close(keep: true);
        }

        base.Dispose(disposing);
    }
}

/// <summary>端末の標準エラー出力に出す 1 行の進捗 (AUTO-36 の仕様 5)。<c>null</c> の出力先には何も出さない。</summary>
internal sealed class ProgressLine(TextWriter? err, string name) : IDisposable
{
    private LongRunningOperation? _operation;
    private int _lastWidth;
    private long _lastTicks;
    private readonly object _lock = new();

    public void Attach(LongRunningOperation operation)
    {
        _operation = operation;
        if (err is not null)
        {
            operation.ProgressChanged += OnProgress;
        }
    }

    private void OnProgress(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            if (now - _lastTicks < 100 || sender is not LongRunningOperation op)
            {
                return;
            }

            _lastTicks = now;
            string text = op.Fraction is { } f ? $"{name}: {f * 100:0.0}%" : $"{name}: {op.ProcessedBytes:N0} bytes";
            Write(text);
        }
    }

    private void Write(string text)
    {
        err!.Write("\r" + text.PadRight(_lastWidth));
        err.Flush();
        _lastWidth = text.Length;
    }

    /// <summary>進捗の行を消す。</summary>
    public void Finish()
    {
        lock (_lock)
        {
            if (err is not null && _lastWidth > 0)
            {
                err.Write("\r" + new string(' ', _lastWidth) + "\r");
                err.Flush();
                _lastWidth = 0;
            }
        }
    }

    public void Dispose()
    {
        if (_operation is not null)
        {
            _operation.ProgressChanged -= OnProgress;
        }
    }
}
