using System.Globalization;
using System.IO.Compression;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Runtime;

internal sealed partial class Evaluator
{
    // ───────────── セクションのデータ (TPL-15 の仕様 2・6・8) ─────────────

    private Val Decompress(string format, CallExpr c, Frame f)
    {
        TemplateSection s = SectionArg(c, f, 0);
        long off = LongArg(c, f, 1);
        long len = LongArg(c, f, 2);
        Space src = _space;
        if (off < 0 || len < 0 || off > src.Length - len)
        {
            throw OutOfRange("std::compress::" + format, off);
        }

        TemplateSourceLocation at = c.Location;
        s.NoteSource(src, off, len);
        s.PendingProducers.Add(() => DecompressInto(s, src, off, len, format, at));
        return Val.Void;
    }

    private void DecompressInto(TemplateSection s, Space src, long off, long len, string format, TemplateSourceLocation at)
    {
        using var input = new SpaceStream(src, off, len, this);
        Stream decoder = format switch
        {
            "inflate" => new DeflateStream(input, CompressionMode.Decompress),
            "zlib" => new ZLibStream(input, CompressionMode.Decompress),
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            _ => new BrotliStream(input, CompressionMode.Decompress),
        };
        byte[] buf = new byte[256 * 1024];
        try
        {
            using (decoder)
            {
                while (true)
                {
                    CheckCancel();
                    int n = decoder.Read(buf, 0, buf.Length);
                    if (n <= 0)
                    {
                        break;
                    }

                    ConsumeSteps(n / 1024);
                    if (!AppendLimited(s, buf.AsSpan(0, n), format))
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            // 展開できた部分までをセクションの内容にする (TPL-15 の「エラー」)
            TemplateNode node = SectionNode(s);
            node.Size = s.Length;
            var d = new TemplateDiagnostic(TemplateSeverity.Error, TemplateMessages.DecompressFailed, [format, ex.Message], at, off, node.Path,
                [.. _callStack]) { SectionName = s.Name };
            Report(d, node);
        }
    }

    /// <summary>上限まで追加する。上限に達したら打ち切って警告し、false を返す (展開爆弾の対策)。</summary>
    private bool AppendLimited(TemplateSection s, ReadOnlySpan<byte> data, string what)
    {
        long room = Math.Min(Limits.Section - s.Length, Limits.SectionTotal - _sectionTotal);
        bool truncated = false;
        if (data.Length > room)
        {
            data = data[..(int)Math.Max(0, room)];
            truncated = true;
        }

        try
        {
            s.Append(data);
        }
        catch (IOException ex)
        {
            throw Error(TemplateMessages.TempFileFailed, ex.Message);
        }

        _sectionTotal += data.Length;
        if (truncated && !s.Truncated)
        {
            s.Truncated = true;
            long limit = Limits.Section - s.Length <= 0 ? Limits.Section : Limits.SectionTotal;
            var d = new TemplateDiagnostic(TemplateSeverity.Warning, TemplateMessages.DecompressLimit, [FormatBytes(limit)], _loc, null, null,
                [.. _callStack]) { SectionName = s.Name };
            Report(d, s.DisplayNode);
        }

        return !truncated;
    }

    private void CopyInto(TemplateSection s, Space src, long off, long len)
    {
        byte[] buf = new byte[Math.Min(len, 1024 * 1024)];
        for (long p = 0; p < len; p += buf.Length)
        {
            int n = (int)Math.Min(buf.Length, len - p);
            src.ReadDirect(off + p, buf.AsSpan(0, n), this, "std::section::copy");
            ConsumeSteps(n / 1024);
            if (!AppendLimited(s, buf.AsSpan(0, n), "copy"))
            {
                break;
            }
        }
    }

    private Val Transform(string kind, CallExpr c, Frame f)
    {
        TemplateSection s = SectionArg(c, f, 0);
        long off = LongArg(c, f, 1);
        long len = LongArg(c, f, 2);
        Space src = _space;
        if (off < 0 || len < 0 || off > src.Length - len)
        {
            throw OutOfRange("std::encoding::" + kind, off);
        }

        byte[] key = [];
        int amount = 0;
        if (kind == "xor")
        {
            Val k = Deref(Arg(c, f, 3));
            key = k.Kind == VK.Int ? [(byte)k.I] : BytesOf(k, c.Location);
            if (key.Length == 0)
            {
                throw ErrorAt(c.Location, TemplateMessages.InvalidArgument, "xor", "empty key");
            }
        }
        else if (kind is "rol" or "ror")
        {
            amount = (int)(LongArg(c, f, 3) & 7);
        }

        // convert(s, offset, length, "変換元", "変換先"): 文字コードの変換 (TPL-18 の std.encoding)。変換先を省くと UTF-8
        System.Text.Encoding? from = null;
        System.Text.Encoding? to = null;
        if (kind == "convert")
        {
            from = EncodingByName(c.Args.Count > 3 ? StrArg(c, f, 3) : f.Encoding);
            to = EncodingByName(c.Args.Count > 4 ? StrArg(c, f, 4) : "utf-8");
        }

        TemplateSourceLocation at = c.Location;
        s.NoteSource(src, off, len);
        s.PendingProducers.Add(() =>
        {
            if (len > Limits.Section * 4L)
            {
                throw ErrorAt(at, TemplateMessages.InvalidArgument, kind, len.ToString(CultureInfo.InvariantCulture));
            }

            byte[] data = new byte[len];
            src.ReadDirect(off, data, this, "std::encoding::" + kind);
            byte[] output;
            switch (kind)
            {
                case "base64":
                    try
                    {
                        output = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(data));
                    }
                    catch (FormatException ex)
                    {
                        throw ErrorAt(at, TemplateMessages.DecompressFailed, "base64", ex.Message);
                    }

                    break;
                case "hex":
                {
                    string text = new([.. System.Text.Encoding.ASCII.GetString(data).Where(Uri.IsHexDigit)]);
                    if (text.Length % 2 != 0)
                    {
                        text = text[..^1];
                    }

                    output = Convert.FromHexString(text);
                    break;
                }

                case "xor":
                    output = new byte[data.Length];
                    for (int i = 0; i < data.Length; i++)
                    {
                        output[i] = (byte)(data[i] ^ key[i % key.Length]);
                    }

                    break;
                case "rol":
                case "ror":
                    output = new byte[data.Length];
                    for (int i = 0; i < data.Length; i++)
                    {
                        int b = data[i];
                        output[i] = kind == "rol" ? (byte)((b << amount) | (b >> (8 - amount))) : (byte)((b >> amount) | (b << (8 - amount)));
                    }

                    break;
                case "convert":
                    output = to!.GetBytes(from!.GetString(data));
                    break;
                default:
                    output = data;
                    break;
            }

            AppendLimited(s, output, kind);
        });
        return Val.Void;
    }

    /// <summary>アドレス空間の範囲を読むストリーム (展開の入力)。</summary>
    private sealed class SpaceStream(Space space, long offset, long length, Evaluator ev) : Stream
    {
        private long _pos;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _pos;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset1, int count) => Read(buffer.AsSpan(offset1, count));

        public override int Read(Span<byte> buffer)
        {
            int n = (int)Math.Min(buffer.Length, length - _pos);
            if (n <= 0)
            {
                return 0;
            }

            space.ReadDirect(offset + _pos, buffer[..n], ev, "decompress");
            _pos += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset1, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset1, int count) => throw new NotSupportedException();
    }
}
