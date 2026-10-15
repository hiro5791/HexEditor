using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Scripting;
using HexEditor.Sdk.Plugins;

namespace HexEditor.Core.Plugins;

/// <summary>プラグインのコマンドの実行 (AUTO-31 の <c>ICommand</c>)。例外は見張りが記録し、アプリは動き続ける。</summary>
public static class PluginCommands
{
    /// <summary>今実行できるか (UI スレッドで呼ぶ。10 ms の決まり。遅ければ警告して止める)。止めたプラグインは false。</summary>
    public static bool CanExecute(PluginManager manager, RegisteredExtension registration, IPluginDocument? document)
    {
        if (registration.Extension is not ICommand command || !registration.IsUsable)
        {
            return false;
        }

        var context = new CommandContext(document, new PluginLog(manager, registration.Plugin));
        return registration.Guard.Invoke("CanExecute", () => command.CanExecute(context), false, uiThread: true);
    }

    /// <summary>実行する。実行できた (例外にならなかった) なら true。</summary>
    public static bool Execute(PluginManager manager, RegisteredExtension registration, IPluginDocument? document)
    {
        if (registration.Extension is not ICommand command || !registration.IsUsable)
        {
            return false;
        }

        var context = new CommandContext(document, new PluginLog(manager, registration.Plugin));
        return registration.Guard.Invoke("Execute", () =>
        {
            command.Execute(context);
            return true;
        }, false);
    }

    private sealed class CommandContext(IPluginDocument? document, IPluginLog log) : ICommandContext
    {
        public IPluginDocument? Document => document;

        public IPluginLog Log => log;
    }
}

/// <summary>
/// プラグインに見せるドキュメント (<see cref="IPluginDocument"/>)。読み取りは 16 MB まで、書き込みは 1 回の Undo で戻せる上書き。
/// 読み取り専用・処理中のドキュメントへの書き込みは例外にする。
/// </summary>
public sealed class PluginDocument(Document document, string name, string? path, Func<long> cursor, Func<(long Start, long Length)> selection) : IPluginDocument
{
    public const int MaxRead = 16 * 1024 * 1024;

    public string Name => name;

    public string? Path => path;

    public long Length => document.Length;

    public long Cursor => cursor();

    public (long Start, long Length) Selection => selection();

    public byte[] Read(long offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (count < 0 || count > MaxRead)
        {
            throw new ArgumentOutOfRangeException(nameof(count), Msg.T("読み取りは 16 MB までです。", "Reads are limited to 16 MB."));
        }

        DocumentSnapshot snapshot = document.Current;
        int n = (int)Math.Clamp(snapshot.Length - offset, 0, count);
        byte[] data = new byte[n];
        ReadResult result = snapshot.Read(offset, data);
        if (!result.IsComplete)
        {
            throw new IOException(Msg.T($"オフセット 0x{result.Unreadable[0].Offset:X} を読み込めません。", $"Cannot read offset 0x{result.Unreadable[0].Offset:X}."));
        }

        return data;
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (document.IsReadOnly)
        {
            throw new DocumentReadOnlyException();
        }

        if (offset < 0 || offset + data.Length > document.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        document.Overwrite(offset, data, Msg.T("プラグインによる上書き", "Overwrite by plugin"));
    }
}
