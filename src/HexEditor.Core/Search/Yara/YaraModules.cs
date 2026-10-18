using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HexEditor.Core.Search.Yara;

/// <summary>
/// 使えるモジュール (FIND-33 の仕様 5): pe、elf、math、hash、time、string、console。pe・elf は先頭のブロックで解析する (仕様 6)。
/// 外部のデータが必要なモジュール (cuckoo など) はない。フィールドの名前は値がなくても持ち、コンパイル時に名前を確かめる。
/// </summary>
internal static class YaraModules
{
    public static readonly string[] Names = ["pe", "elf", "math", "hash", "time", "string", "console"];

    /// <summary>名前の確かめに使う形 (データなしで作ったもの)。</summary>
    public static YStruct Schema(string name) => Build(name, []);

    /// <summary>モジュールを作る。<paramref name="head"/> は先頭のブロック。</summary>
    public static YStruct Build(string name, ReadOnlySpan<byte> head) => name switch
    {
        "pe" => Pe(head),
        "elf" => Elf(head),
        "math" => MathModule(),
        "hash" => HashModule(),
        "time" => new YStruct().Func("now", (_, _) => YValue.Int(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), 0),
        "string" => StringModule(),
        "console" => ConsoleModule(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>pe・elf のエントリポイント (ファイル上の位置。entrypoint キーワード)。</summary>
    public static long? EntryPoint(ReadOnlySpan<byte> head)
    {
        YValue e = Pe(head)["entry_point"];
        if (e.Kind == YKind.Integer)
        {
            return e.I;
        }

        e = Elf(head)["entry_point"];
        return e.Kind == YKind.Integer ? e.I : null;
    }

    // ---- 範囲の読み込み (math・hash) ----

    /// <summary>関数の引数 (offset, size) または (文字列) のデータを、1 MiB ずつ渡す。範囲が対象の外なら false。</summary>
    private static bool ForEachChunk(YValue[] args, YaraContext c, int first, Action<ReadOnlySpan<byte>> action)
    {
        if (args.Length > first && args[first].Kind == YKind.String)
        {
            action(args[first].S);
            return true;
        }

        if (args.Length < first + 2 || args[first].Kind != YKind.Integer || args[first + 1].Kind != YKind.Integer)
        {
            return false;
        }

        long offset = args[first].I, size = args[first + 1].I;
        if (offset < 0 || size < 0 || offset > c.FileSize)
        {
            return false;
        }

        size = Math.Min(size, c.FileSize - offset);
        byte[] buffer = new byte[(int)Math.Min(size, 1024 * 1024)];
        for (long done = 0; done < size;)
        {
            c.Limits.Check();
            int n = (int)Math.Min(buffer.Length, size - done);
            c.Data.Read(offset + done, buffer.AsSpan(0, n));
            action(buffer.AsSpan(0, n));
            done += n;
        }

        return true;
    }

    private static long[]? Histogram(YValue[] args, YaraContext c, int first)
    {
        long[] counts = new long[256];
        return ForEachChunk(args, c, first, data =>
        {
            foreach (byte b in data)
            {
                counts[b]++;
            }
        }) ? counts : null;
    }

    // ---- math ----

    private static YStruct MathModule()
    {
        var m = new YStruct();
        m.Set("MEAN_BYTES", YValue.Float(127.5));
        m.Func("entropy", (a, c) =>
        {
            long[]? h = Histogram(a, c, 0);
            if (h is null)
            {
                return YValue.Undefined;
            }

            long total = h.Sum();
            double e = 0;
            foreach (long n in h)
            {
                if (n > 0)
                {
                    double p = (double)n / total;
                    e -= p * Math.Log2(p);
                }
            }

            return YValue.Float(e);
        }, 1, 2);
        m.Func("mean", (a, c) =>
        {
            long[]? h = Histogram(a, c, 0);
            if (h is null || h.Sum() == 0)
            {
                return YValue.Undefined;
            }

            double sum = 0;
            for (int i = 0; i < 256; i++)
            {
                sum += (double)i * h[i];
            }

            return YValue.Float(sum / h.Sum());
        }, 1, 2);
        m.Func("deviation", (a, c) =>
        {
            int meanIndex = a.Length == 2 ? 1 : 2;
            long[]? h = Histogram(a, c, 0);
            if (h is null || h.Sum() == 0 || a.Length <= meanIndex || !a[meanIndex].IsNumber)
            {
                return YValue.Undefined;
            }

            double mean = a[meanIndex].AsDouble, sum = 0;
            for (int i = 0; i < 256; i++)
            {
                sum += Math.Abs(i - mean) * h[i];
            }

            return YValue.Float(sum / h.Sum());
        }, 2, 3);
        m.Func("serial_correlation", (a, c) =>
        {
            double scc1 = 0, scc2 = 0, scc3 = 0, sccun = 0, last = 0;
            long n = 0;
            bool first = true;
            if (!ForEachChunk(a, c, 0, data =>
            {
                foreach (byte b in data)
                {
                    if (first)
                    {
                        first = false;
                        sccun = b;
                    }
                    else
                    {
                        scc1 += last * b;
                    }

                    scc2 += b;
                    scc3 += (double)b * b;
                    last = b;
                    n++;
                }
            }) || n == 0)
            {
                return YValue.Undefined;
            }

            scc1 += last * sccun;
            double s2 = scc2 * scc2;
            double denom = n * scc3 - s2;
            return YValue.Float(denom == 0 ? -100000 : (n * scc1 - s2) / denom);
        }, 1, 2);
        m.Func("monte_carlo_pi", (a, c) =>
        {
            const double InCircle = 256.0 * 256.0 * 256.0 - 1;
            var six = new List<byte>(6);
            long tries = 0, hits = 0;
            if (!ForEachChunk(a, c, 0, data =>
            {
                foreach (byte b in data)
                {
                    six.Add(b);
                    if (six.Count == 6)
                    {
                        double x = 0, y = 0;
                        for (int j = 0; j < 3; j++)
                        {
                            x = x * 256 + six[j];
                            y = y * 256 + six[j + 3];
                        }

                        x /= InCircle;
                        y /= InCircle;
                        tries++;
                        if (x * x + y * y <= 1)
                        {
                            hits++;
                        }

                        six.Clear();
                    }
                }
            }) || tries == 0)
            {
                return YValue.Undefined;
            }

            double pi = 4.0 * hits / tries;
            return YValue.Float(Math.Abs((pi - Math.PI) / Math.PI));
        }, 1, 2);
        m.Func("in_range", (a, _) => a.Length == 3 && a.All(v => v.IsNumber)
            ? YValue.Bool(a[0].AsDouble >= a[1].AsDouble && a[0].AsDouble <= a[2].AsDouble) : YValue.Undefined, 3);
        m.Func("max", (a, _) => a.Length == 2 && a[0].Kind == YKind.Integer && a[1].Kind == YKind.Integer
            ? YValue.Int((long)Math.Max((ulong)a[0].I, (ulong)a[1].I)) : YValue.Undefined, 2);
        m.Func("min", (a, _) => a.Length == 2 && a[0].Kind == YKind.Integer && a[1].Kind == YKind.Integer
            ? YValue.Int((long)Math.Min((ulong)a[0].I, (ulong)a[1].I)) : YValue.Undefined, 2);
        m.Func("to_number", (a, _) => a.Length == 1 && !a[0].IsUndefined ? YValue.Int(a[0].Truthy ? 1 : 0) : YValue.Undefined, 1);
        m.Func("abs", (a, _) => a.Length == 1 && a[0].Kind == YKind.Integer ? YValue.Int(Math.Abs(a[0].I)) : YValue.Undefined, 1);
        m.Func("count", (a, c) =>
        {
            if (a.Length < 1 || a[0].Kind != YKind.Integer || a[0].I is < 0 or > 255)
            {
                return YValue.Undefined;
            }

            long[]? h = a.Length == 1 ? Histogram([YValue.Int(0), YValue.Int(c.FileSize)], c, 0) : Histogram(a, c, 1);
            return h is null ? YValue.Undefined : YValue.Int(h[a[0].I]);
        }, 1, 3);
        m.Func("percentage", (a, c) =>
        {
            if (a.Length < 1 || a[0].Kind != YKind.Integer || a[0].I is < 0 or > 255)
            {
                return YValue.Undefined;
            }

            long[]? h = a.Length == 1 ? Histogram([YValue.Int(0), YValue.Int(c.FileSize)], c, 0) : Histogram(a, c, 1);
            return h is null || h.Sum() == 0 ? YValue.Undefined : YValue.Float((double)h[a[0].I] / h.Sum());
        }, 1, 3);
        m.Func("mode", (a, c) =>
        {
            long[]? h = a.Length == 0 ? Histogram([YValue.Int(0), YValue.Int(c.FileSize)], c, 0) : Histogram(a, c, 0);
            if (h is null || h.Sum() == 0)
            {
                return YValue.Undefined;
            }

            int best = 0;
            for (int i = 1; i < 256; i++)
            {
                if (h[i] > h[best])
                {
                    best = i;
                }
            }

            return YValue.Int(best);
        }, 0, 2);
        m.Func("to_string", (a, _) =>
        {
            if (a.Length < 1 || a[0].Kind != YKind.Integer)
            {
                return YValue.Undefined;
            }

            long b = a.Length > 1 && a[1].Kind == YKind.Integer ? a[1].I : 10;
            return b switch
            {
                10 => YValue.Str(a[0].I.ToString(CultureInfo.InvariantCulture)),
                16 => YValue.Str(a[0].I.ToString("x", CultureInfo.InvariantCulture)),
                8 => YValue.Str(Convert.ToString(a[0].I, 8)),
                _ => YValue.Undefined,
            };
        }, 1, 2);
        return m;
    }

    // ---- hash ----

    private static YStruct HashModule()
    {
        var m = new YStruct();
        foreach ((string name, HashAlgorithmName algorithm) in new[] { ("md5", HashAlgorithmName.MD5), ("sha1", HashAlgorithmName.SHA1), ("sha256", HashAlgorithmName.SHA256) })
        {
            m.Func(name, (a, c) =>
            {
                using var hash = IncrementalHash.CreateHash(algorithm);
                return ForEachChunk(a, c, 0, hash.AppendData)
                    ? YValue.Str(Convert.ToHexStringLower(hash.GetHashAndReset())) : YValue.Undefined;
            }, 1, 2);
        }

        m.Func("checksum32", (a, c) =>
        {
            uint sum = 0;
            return ForEachChunk(a, c, 0, data =>
            {
                foreach (byte b in data)
                {
                    sum += b;
                }
            }) ? YValue.Int(sum) : YValue.Undefined;
        }, 1, 2);
        m.Func("crc32", (a, c) =>
        {
            uint state = Patches.PatchCrc32.Initial;
            return ForEachChunk(a, c, 0, data => state = Patches.PatchCrc32.Append(state, data))
                ? YValue.Int(Patches.PatchCrc32.Finish(state)) : YValue.Undefined;
        }, 1, 2);
        return m;
    }

    // ---- string・console ----

    private static YStruct StringModule() => new YStruct()
        .Func("to_int", (a, _) =>
        {
            if (a.Length < 1 || a[0].Kind != YKind.String)
            {
                return YValue.Undefined;
            }

            string s = a[0].AsText.Trim();
            int b = a.Length > 1 && a[1].Kind == YKind.Integer ? (int)a[1].I : 0;
            try
            {
                bool negative = s.StartsWith('-');
                if (negative || s.StartsWith('+'))
                {
                    s = s[1..];
                }

                if (b == 0)
                {
                    (b, s) = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? (16, s[2..]) : s.Length > 1 && s[0] == '0' ? (8, s[1..]) : (10, s);
                }

                long v = b == 10 ? long.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture) : Convert.ToInt64(s, b);
                return YValue.Int(negative ? -v : v);
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
            {
                return YValue.Undefined;
            }
        }, 1, 2)
        .Func("length", (a, _) => a.Length == 1 && a[0].Kind == YKind.String ? YValue.Int(a[0].S!.Length) : YValue.Undefined, 1);

    private static YStruct ConsoleModule() => new YStruct()
        .Func("log", (a, c) =>
        {
            c.ConsoleOutput.Add(string.Concat(a.Select(v => v.ToString())));
            return YValue.True;
        }, 1, 2)
        .Func("hex", (a, c) =>
        {
            string text = a.Length == 2 ? a[0].ToString() + "0x" + a[1].I.ToString("x", CultureInfo.InvariantCulture)
                : "0x" + a[0].I.ToString("x", CultureInfo.InvariantCulture);
            c.ConsoleOutput.Add(text);
            return YValue.True;
        }, 1, 2);

    // ---- pe ----

    private static YStruct Pe(ReadOnlySpan<byte> d)
    {
        var pe = new YStruct();
        foreach ((string n, long v) in PeConstants)
        {
            pe.Set(n, v);
        }

        string[] fields =
        [
            "is_pe", "machine", "number_of_sections", "timestamp", "pointer_to_symbol_table", "number_of_symbols", "size_of_optional_header",
            "characteristics", "entry_point", "entry_point_raw", "image_base", "number_of_rva_and_sizes", "opthdr_magic", "size_of_code",
            "size_of_image", "size_of_headers", "checksum", "subsystem", "dll_characteristics", "section_alignment", "file_alignment",
            "number_of_imports", "number_of_imported_functions", "number_of_exports", "dll_name", "base_of_code",
        ];
        foreach (string f in fields)
        {
            pe.Set(f, YValue.Undefined);
        }

        var section = new YStruct();
        foreach (string f in new[] { "name", "full_name", "characteristics", "virtual_address", "virtual_size", "raw_data_offset", "raw_data_size" })
        {
            section.Set(f, YValue.Undefined);
        }

        var directory = new YStruct().Set("virtual_address", YValue.Undefined).Set("size", YValue.Undefined);
        var sections = new YArray(section);
        var directories = new YArray(directory);
        pe.Set("sections", YValue.Obj(sections));
        pe.Set("data_directories", YValue.Obj(directories));
        pe.Set("linker_version", YValue.Obj(new YStruct().Set("major", YValue.Undefined).Set("minor", YValue.Undefined)));
        pe.Set("os_version", YValue.Obj(new YStruct().Set("major", YValue.Undefined).Set("minor", YValue.Undefined)));
        pe.Set("overlay", YValue.Obj(new YStruct().Set("offset", YValue.Undefined).Set("size", YValue.Undefined)));

        var imports = new List<(string Dll, string? Name, long Ordinal)>();
        var exports = new List<(string? Name, long Ordinal)>();
        bool is64 = false, isDll = false, parsed = false;
        var sectionList = new List<(uint Va, uint Vs, uint Raw, uint RawSize)>();
        try
        {
            parsed = ParsePe(d, pe, sections, directories, imports, exports, sectionList, out is64, out isDll);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            parsed = false;
        }

        if (parsed)
        {
            pe.Set("is_pe", 1);
            pe.Set("number_of_imports", imports.Select(i => i.Dll.ToLowerInvariant()).Distinct().Count());
            pe.Set("number_of_imported_functions", imports.Count);
            pe.Set("number_of_exports", exports.Count);
        }

        pe.Func("is_dll", (_, _) => parsed ? YValue.Bool(isDll) : YValue.Undefined, 0);
        pe.Func("is_32bit", (_, _) => parsed ? YValue.Bool(!is64) : YValue.Undefined, 0);
        pe.Func("is_64bit", (_, _) => parsed ? YValue.Bool(is64) : YValue.Undefined, 0);
        pe.Func("imports", (a, _) =>
        {
            if (!parsed || a.Length == 0 || a[0].Kind != YKind.String)
            {
                return parsed ? YValue.Int(0) : YValue.Undefined;
            }

            string dll = a[0].AsText;
            IEnumerable<(string Dll, string? Name, long Ordinal)> of = imports.Where(i => string.Equals(i.Dll, dll, StringComparison.OrdinalIgnoreCase));
            if (a.Length == 1)
            {
                return YValue.Int(of.Count());
            }

            return a[1].Kind == YKind.String
                ? YValue.Bool(of.Any(i => i.Name == a[1].AsText))
                : YValue.Bool(of.Any(i => i.Name is null && i.Ordinal == a[1].I));
        }, 1, 2);
        pe.Func("exports", (a, _) =>
        {
            if (!parsed || a.Length != 1)
            {
                return YValue.Undefined;
            }

            return a[0].Kind == YKind.String ? YValue.Bool(exports.Any(e => e.Name == a[0].AsText)) : YValue.Bool(exports.Any(e => e.Ordinal == a[0].I));
        }, 1);
        pe.Func("section_index", (a, _) =>
        {
            if (!parsed || a.Length != 1)
            {
                return YValue.Undefined;
            }

            for (int i = 0; i < sections.Items.Count; i++)
            {
                YValue s = sections.Items[i];
                if (a[0].Kind == YKind.String && s.O is YStruct st && st["name"].AsText == a[0].AsText)
                {
                    return YValue.Int(i);
                }

                if (a[0].Kind == YKind.Integer && s.O is YStruct st2 && st2["virtual_address"].I <= a[0].I
                    && a[0].I < st2["virtual_address"].I + st2["virtual_size"].I)
                {
                    return YValue.Int(i);
                }
            }

            return YValue.Undefined;
        }, 1);
        pe.Func("rva_to_offset", (a, _) => parsed && a.Length == 1 && a[0].Kind == YKind.Integer && RvaToOffset(sectionList, a[0].I) is long o
            ? YValue.Int(o) : YValue.Undefined, 1);
        pe.Func("imphash", (_, _) =>
        {
            if (!parsed)
            {
                return YValue.Undefined;
            }

            string text = string.Join(",", imports.Select(i =>
            {
                string dll = i.Dll.ToLowerInvariant();
                foreach (string ext in new[] { ".dll", ".ocx", ".sys" })
                {
                    if (dll.EndsWith(ext, StringComparison.Ordinal))
                    {
                        dll = dll[..^ext.Length];
                        break;
                    }
                }

                return dll + "." + (i.Name?.ToLowerInvariant() ?? "ord" + i.Ordinal.ToString(CultureInfo.InvariantCulture));
            }));
            return YValue.Str(Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes(text))));
        }, 0);
        return pe;
    }

    private static readonly (string Name, long Value)[] PeConstants =
    [
        ("MACHINE_UNKNOWN", 0), ("MACHINE_I386", 0x14C), ("MACHINE_AMD64", 0x8664), ("MACHINE_ARM", 0x1C0), ("MACHINE_ARMNT", 0x1C4),
        ("MACHINE_ARM64", 0xAA64), ("MACHINE_IA64", 0x200), ("MACHINE_THUMB", 0x1C2),
        ("SUBSYSTEM_UNKNOWN", 0), ("SUBSYSTEM_NATIVE", 1), ("SUBSYSTEM_WINDOWS_GUI", 2), ("SUBSYSTEM_WINDOWS_CUI", 3),
        ("SUBSYSTEM_EFI_APPLICATION", 10), ("SUBSYSTEM_EFI_BOOT_SERVICE_DRIVER", 11), ("SUBSYSTEM_EFI_RUNTIME_DRIVER", 12),
        ("SUBSYSTEM_WINDOWS_BOOT_APPLICATION", 16),
        ("RELOCS_STRIPPED", 0x1), ("EXECUTABLE_IMAGE", 0x2), ("LINE_NUMS_STRIPPED", 0x4), ("LOCAL_SYMS_STRIPPED", 0x8),
        ("LARGE_ADDRESS_AWARE", 0x20), ("MACHINE_32BIT", 0x100), ("DEBUG_STRIPPED", 0x200), ("SYSTEM", 0x1000), ("DLL", 0x2000),
        ("HIGH_ENTROPY_VA", 0x20), ("DYNAMIC_BASE", 0x40), ("FORCE_INTEGRITY", 0x80), ("NX_COMPAT", 0x100), ("NO_SEH", 0x400),
        ("GUARD_CF", 0x4000),
        ("SECTION_CNT_CODE", 0x20), ("SECTION_CNT_INITIALIZED_DATA", 0x40), ("SECTION_CNT_UNINITIALIZED_DATA", 0x80),
        ("SECTION_MEM_DISCARDABLE", 0x2000000), ("SECTION_MEM_SHARED", 0x10000000), ("SECTION_MEM_EXECUTE", 0x20000000),
        ("SECTION_MEM_READ", 0x40000000), ("SECTION_MEM_WRITE", 0x80000000),
        ("IMAGE_DIRECTORY_ENTRY_EXPORT", 0), ("IMAGE_DIRECTORY_ENTRY_IMPORT", 1), ("IMAGE_DIRECTORY_ENTRY_RESOURCE", 2),
        ("IMAGE_DIRECTORY_ENTRY_SECURITY", 4), ("IMAGE_DIRECTORY_ENTRY_BASERELOC", 5), ("IMAGE_DIRECTORY_ENTRY_DEBUG", 6),
        ("IMAGE_DIRECTORY_ENTRY_TLS", 9), ("IMAGE_DIRECTORY_ENTRY_IAT", 12), ("IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR", 14),
    ];

    private static long? RvaToOffset(List<(uint Va, uint Vs, uint Raw, uint RawSize)> sections, long rva)
    {
        foreach ((uint va, uint vs, uint raw, uint rawSize) in sections)
        {
            if (rva >= va && rva < va + Math.Max(vs, rawSize))
            {
                return raw + (rva - va);
            }
        }

        return sections.Count == 0 || rva < sections.Min(s => s.Va) ? rva : null;
    }

    private static bool ParsePe(ReadOnlySpan<byte> d, YStruct pe, YArray sections, YArray directories, List<(string, string?, long)> imports,
        List<(string?, long)> exports, List<(uint Va, uint Vs, uint Raw, uint RawSize)> sectionList, out bool is64, out bool isDll)
    {
        is64 = isDll = false;
        if (d.Length < 0x40 || d[0] != 'M' || d[1] != 'Z')
        {
            return false;
        }

        int lfanew = BinaryPrimitives.ReadInt32LittleEndian(d[0x3C..]);
        if (lfanew < 0 || lfanew + 24 > d.Length || BinaryPrimitives.ReadUInt32LittleEndian(d[lfanew..]) != 0x00004550)
        {
            return false;
        }

        int coff = lfanew + 4;
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(d[coff..]);
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(d[(coff + 2)..]);
        ushort optSize = BinaryPrimitives.ReadUInt16LittleEndian(d[(coff + 16)..]);
        ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(d[(coff + 18)..]);
        pe.Set("machine", machine).Set("number_of_sections", count).Set("timestamp", BinaryPrimitives.ReadUInt32LittleEndian(d[(coff + 4)..]))
            .Set("pointer_to_symbol_table", BinaryPrimitives.ReadUInt32LittleEndian(d[(coff + 8)..]))
            .Set("number_of_symbols", BinaryPrimitives.ReadUInt32LittleEndian(d[(coff + 12)..]))
            .Set("size_of_optional_header", optSize).Set("characteristics", characteristics);
        isDll = (characteristics & 0x2000) != 0;
        int opt = coff + 20;
        if (opt + 2 > d.Length)
        {
            return true;
        }

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(d[opt..]);
        is64 = magic == 0x20B;
        pe.Set("opthdr_magic", magic);
        if (opt + (is64 ? 112 : 96) <= d.Length)
        {
            pe.Set("linker_version", YValue.Obj(new YStruct().Set("major", d[opt + 2]).Set("minor", d[opt + 3])));
            pe.Set("size_of_code", BinaryPrimitives.ReadUInt32LittleEndian(d[(opt + 4)..]));
            uint entry = BinaryPrimitives.ReadUInt32LittleEndian(d[(opt + 16)..]);
            pe.Set("entry_point_raw", entry);
            pe.Set("base_of_code", BinaryPrimitives.ReadUInt32LittleEndian(d[(opt + 20)..]));
            pe.Set("image_base", is64 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(d[(opt + 24)..]) : BinaryPrimitives.ReadUInt32LittleEndian(d[(opt + 28)..]));
            pe.Set("section_alignment", BinaryPrimitives.ReadUInt32LittleEndian(d[(opt + 32)..]));
            pe.Set("file_alignment", BinaryPrimitives.ReadUInt32LittleEndian(d[(opt + 36)..]));
            pe.Set("os_version", YValue.Obj(new YStruct().Set("major", BinaryPrimitives.ReadUInt16LittleEndian(d[(opt + 40)..]))
                .Set("minor", BinaryPrimitives.ReadUInt16LittleEndian(d[(opt + 42)..]))));
            pe.Set("size_of_image", BinaryPrimitives.ReadUInt32LittleEndian(d[(opt + 56)..]));
            pe.Set("size_of_headers", BinaryPrimitives.ReadUInt32LittleEndian(d[(opt + 60)..]));
            pe.Set("checksum", BinaryPrimitives.ReadUInt32LittleEndian(d[(opt + 64)..]));
            pe.Set("subsystem", BinaryPrimitives.ReadUInt16LittleEndian(d[(opt + 68)..]));
            pe.Set("dll_characteristics", BinaryPrimitives.ReadUInt16LittleEndian(d[(opt + 70)..]));
            int rvaCountAt = opt + (is64 ? 108 : 92);
            uint rvaCount = BinaryPrimitives.ReadUInt32LittleEndian(d[rvaCountAt..]);
            pe.Set("number_of_rva_and_sizes", rvaCount);
            for (int i = 0; i < Math.Min(16, rvaCount) && rvaCountAt + 4 + i * 8 + 8 <= d.Length; i++)
            {
                int at = rvaCountAt + 4 + i * 8;
                directories.Items.Add(YValue.Obj(new YStruct().Set("virtual_address", BinaryPrimitives.ReadUInt32LittleEndian(d[at..]))
                    .Set("size", BinaryPrimitives.ReadUInt32LittleEndian(d[(at + 4)..]))));
            }

            // セクション表。
            int table = opt + optSize;
            long overlayStart = 0;
            for (int i = 0; i < count && table + i * 40 + 40 <= d.Length; i++)
            {
                ReadOnlySpan<byte> s = d.Slice(table + i * 40, 40);
                string name = Encoding.Latin1.GetString(s[..8]).TrimEnd('\0');
                uint vs = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]), va = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
                uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]), raw = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]);
                sectionList.Add((va, vs, raw, rawSize));
                overlayStart = Math.Max(overlayStart, (long)raw + rawSize);
                sections.Items.Add(YValue.Obj(new YStruct().Set("name", name).Set("full_name", name)
                    .Set("characteristics", BinaryPrimitives.ReadUInt32LittleEndian(s[36..])).Set("virtual_address", va).Set("virtual_size", vs)
                    .Set("raw_data_offset", raw).Set("raw_data_size", rawSize)));
            }

            if (RvaToOffset(sectionList, entry) is long entryOffset)
            {
                pe.Set("entry_point", entryOffset);
            }

            if (overlayStart > 0 && overlayStart <= d.Length)
            {
                pe.Set("overlay", YValue.Obj(new YStruct().Set("offset", overlayStart).Set("size", d.Length - overlayStart)));
            }

            ReadImports(d, directories, sectionList, is64, imports);
            ReadExports(d, directories, sectionList, exports, pe);
        }

        return true;
    }

    private static string? CString(ReadOnlySpan<byte> d, long offset)
    {
        if (offset < 0 || offset >= d.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> rest = d[(int)offset..];
        int end = rest.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? rest[..Math.Min(rest.Length, 256)] : rest[..Math.Min(end, 256)]);
    }

    private static void ReadImports(ReadOnlySpan<byte> d, YArray directories, List<(uint Va, uint Vs, uint Raw, uint RawSize)> sections, bool is64,
        List<(string, string?, long)> imports)
    {
        if (directories.Items.Count < 2 || directories.Items[1].O is not YStruct dir || dir["virtual_address"].I == 0
            || RvaToOffset(sections, dir["virtual_address"].I) is not long at)
        {
            return;
        }

        for (int n = 0; n < 4096 && at + 20 <= d.Length; n++, at += 20)
        {
            uint lookup = BinaryPrimitives.ReadUInt32LittleEndian(d[(int)at..]);
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(d[(int)(at + 12)..]);
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(d[(int)(at + 16)..]);
            if (nameRva == 0 && first == 0)
            {
                break;
            }

            string? dll = RvaToOffset(sections, nameRva) is long no ? CString(d, no) : null;
            if (dll is null || RvaToOffset(sections, lookup != 0 ? lookup : first) is not long thunk)
            {
                continue;
            }

            int size = is64 ? 8 : 4;
            for (int k = 0; k < 65536 && thunk + size <= d.Length; k++, thunk += size)
            {
                ulong value = is64 ? BinaryPrimitives.ReadUInt64LittleEndian(d[(int)thunk..]) : BinaryPrimitives.ReadUInt32LittleEndian(d[(int)thunk..]);
                if (value == 0)
                {
                    break;
                }

                bool byOrdinal = is64 ? (value & 0x8000000000000000UL) != 0 : (value & 0x80000000UL) != 0;
                if (byOrdinal)
                {
                    imports.Add((dll, null, (long)(value & 0xFFFF)));
                }
                else if (RvaToOffset(sections, (long)(value & 0x7FFFFFFF)) is long hint)
                {
                    imports.Add((dll, CString(d, hint + 2), 0));
                }
            }
        }
    }

    private static void ReadExports(ReadOnlySpan<byte> d, YArray directories, List<(uint Va, uint Vs, uint Raw, uint RawSize)> sections,
        List<(string?, long)> exports, YStruct pe)
    {
        if (directories.Items.Count < 1 || directories.Items[0].O is not YStruct dir || dir["virtual_address"].I == 0
            || RvaToOffset(sections, dir["virtual_address"].I) is not long at || at + 40 > d.Length)
        {
            return;
        }

        int a = (int)at;
        if (RvaToOffset(sections, BinaryPrimitives.ReadUInt32LittleEndian(d[(a + 12)..])) is long nameOffset)
        {
            pe.Set("dll_name", CString(d, nameOffset) ?? string.Empty);
        }

        uint ordinalBase = BinaryPrimitives.ReadUInt32LittleEndian(d[(a + 16)..]);
        uint functions = BinaryPrimitives.ReadUInt32LittleEndian(d[(a + 20)..]);
        uint names = BinaryPrimitives.ReadUInt32LittleEndian(d[(a + 24)..]);
        long? namesAt = RvaToOffset(sections, BinaryPrimitives.ReadUInt32LittleEndian(d[(a + 32)..]));
        long? ordinalsAt = RvaToOffset(sections, BinaryPrimitives.ReadUInt32LittleEndian(d[(a + 36)..]));
        var named = new HashSet<long>();
        for (int i = 0; i < Math.Min(names, 65536) && namesAt is long na && ordinalsAt is long oa && na + i * 4 + 4 <= d.Length && oa + i * 2 + 2 <= d.Length; i++)
        {
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(d[(int)(na + i * 4)..]);
            long ordinal = BinaryPrimitives.ReadUInt16LittleEndian(d[(int)(oa + i * 2)..]) + ordinalBase;
            named.Add(ordinal);
            exports.Add((RvaToOffset(sections, nameRva) is long o ? CString(d, o) : null, ordinal));
        }

        for (long i = 0; i < Math.Min(functions, 65536); i++)
        {
            if (!named.Contains(i + ordinalBase))
            {
                exports.Add((null, i + ordinalBase));
            }
        }
    }

    // ---- elf ----

    private static YStruct Elf(ReadOnlySpan<byte> d)
    {
        var elf = new YStruct();
        foreach ((string n, long v) in ElfConstants)
        {
            elf.Set(n, v);
        }

        foreach (string f in new[] { "type", "machine", "entry_point", "number_of_sections", "number_of_segments", "sh_offset", "sh_entry_size",
            "ph_offset", "ph_entry_size" })
        {
            elf.Set(f, YValue.Undefined);
        }

        var section = new YStruct();
        foreach (string f in new[] { "name", "type", "flags", "address", "size", "offset" })
        {
            section.Set(f, YValue.Undefined);
        }

        var segment = new YStruct();
        foreach (string f in new[] { "type", "flags", "offset", "virtual_address", "physical_address", "file_size", "memory_size", "alignment" })
        {
            segment.Set(f, YValue.Undefined);
        }

        var sections = new YArray(section);
        var segments = new YArray(segment);
        elf.Set("sections", YValue.Obj(sections));
        elf.Set("segments", YValue.Obj(segments));
        try
        {
            ParseElf(d, elf, sections, segments);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
        }

        return elf;
    }

    private static readonly (string Name, long Value)[] ElfConstants =
    [
        ("ET_NONE", 0), ("ET_REL", 1), ("ET_EXEC", 2), ("ET_DYN", 3), ("ET_CORE", 4),
        ("EM_NONE", 0), ("EM_M32", 1), ("EM_SPARC", 2), ("EM_386", 3), ("EM_68K", 4), ("EM_88K", 5), ("EM_860", 7), ("EM_MIPS", 8),
        ("EM_MIPS_RS3_LE", 10), ("EM_PPC", 20), ("EM_PPC64", 21), ("EM_ARM", 40), ("EM_X86_64", 62), ("EM_AARCH64", 183), ("EM_RISCV", 243),
        ("SHT_NULL", 0), ("SHT_PROGBITS", 1), ("SHT_SYMTAB", 2), ("SHT_STRTAB", 3), ("SHT_RELA", 4), ("SHT_HASH", 5), ("SHT_DYNAMIC", 6),
        ("SHT_NOTE", 7), ("SHT_NOBITS", 8), ("SHT_REL", 9), ("SHT_SHLIB", 10), ("SHT_DYNSYM", 11),
        ("SHF_WRITE", 1), ("SHF_ALLOC", 2), ("SHF_EXECINSTR", 4),
        ("PT_NULL", 0), ("PT_LOAD", 1), ("PT_DYNAMIC", 2), ("PT_INTERP", 3), ("PT_NOTE", 4), ("PT_SHLIB", 5), ("PT_PHDR", 6), ("PT_TLS", 7),
        ("PT_GNU_EH_FRAME", 0x6474E550), ("PT_GNU_STACK", 0x6474E551),
        ("PF_X", 1), ("PF_W", 2), ("PF_R", 4),
    ];

    private static void ParseElf(ReadOnlySpan<byte> d, YStruct elf, YArray sections, YArray segments)
    {
        if (d.Length < 52 || d[0] != 0x7F || d[1] != 'E' || d[2] != 'L' || d[3] != 'F' || d[4] is not 1 and not 2 || d[5] is not 1 and not 2)
        {
            return;
        }

        bool is64 = d[4] == 2, be = d[5] == 2;
        int w = is64 ? 8 : 4;
        elf.Set("type", (long)Rd(d, be, 16, 2)).Set("machine", (long)Rd(d, be, 18, 2));
        ulong entry = Rd(d, be, 24, w);
        ulong phoff = Rd(d, be, 24 + w, w), shoff = Rd(d, be, 24 + 2 * w, w);
        int tail = 24 + 3 * w + 4;
        int phentsize = (int)Rd(d, be, tail + 2, 2), phnum = (int)Rd(d, be, tail + 4, 2), shentsize = (int)Rd(d, be, tail + 6, 2), shnum = (int)Rd(d, be, tail + 8, 2);
        int shstrndx = (int)Rd(d, be, tail + 10, 2);
        elf.Set("number_of_sections", shnum).Set("number_of_segments", phnum).Set("sh_offset", (long)shoff).Set("sh_entry_size", shentsize)
            .Set("ph_offset", (long)phoff).Set("ph_entry_size", phentsize);

        var loads = new List<(ulong Va, ulong Offset, ulong Size)>();
        for (int i = 0; i < Math.Min(phnum, 4096); i++)
        {
            long at = (long)phoff + (long)i * phentsize;
            if (phentsize < (is64 ? 56 : 32) || at + phentsize > d.Length)
            {
                break;
            }

            int a = (int)at;
            ulong type = Rd(d, be, a, 4);
            ulong flags, offset, va, pa, filesz, memsz, align;
            if (is64)
            {
                flags = Rd(d, be, a + 4, 4);
                offset = Rd(d, be, a + 8, 8);
                va = Rd(d, be, a + 16, 8);
                pa = Rd(d, be, a + 24, 8);
                filesz = Rd(d, be, a + 32, 8);
                memsz = Rd(d, be, a + 40, 8);
                align = Rd(d, be, a + 48, 8);
            }
            else
            {
                offset = Rd(d, be, a + 4, 4);
                va = Rd(d, be, a + 8, 4);
                pa = Rd(d, be, a + 12, 4);
                filesz = Rd(d, be, a + 16, 4);
                memsz = Rd(d, be, a + 20, 4);
                flags = Rd(d, be, a + 24, 4);
                align = Rd(d, be, a + 28, 4);
            }

            if (type == 1)
            {
                loads.Add((va, offset, filesz));
            }

            segments.Items.Add(YValue.Obj(new YStruct().Set("type", (long)type).Set("flags", (long)flags).Set("offset", (long)offset)
                .Set("virtual_address", (long)va).Set("physical_address", (long)pa).Set("file_size", (long)filesz)
                .Set("memory_size", (long)memsz).Set("alignment", (long)align)));
        }

        long elfType = elf["type"].I;
        if (elfType == 1)
        {
            elf.Set("entry_point", (long)entry);
        }
        else
        {
            foreach ((ulong va, ulong offset, ulong size) in loads)
            {
                if (entry >= va && entry < va + size)
                {
                    elf.Set("entry_point", (long)(offset + (entry - va)));
                    break;
                }
            }
        }

        // 節の名前は節の名前の表 (e_shstrndx) から読む。
        long strtab = -1;
        if (shstrndx < shnum && shentsize >= (is64 ? 64 : 40))
        {
            long at = (long)shoff + (long)shstrndx * shentsize;
            if (at + shentsize <= d.Length)
            {
                strtab = (long)Rd(d, be, (int)at + (is64 ? 24 : 16), w);
            }
        }

        for (int i = 0; i < Math.Min(shnum, 4096); i++)
        {
            long at = (long)shoff + (long)i * shentsize;
            if (shentsize < (is64 ? 64 : 40) || at + shentsize > d.Length)
            {
                break;
            }

            int a = (int)at;
            uint nameIndex = (uint)Rd(d, be, a, 4);
            ulong type = Rd(d, be, a + 4, 4), flags = Rd(d, be, a + 8, w), addr = Rd(d, be, a + 8 + w, w), offset = Rd(d, be, a + 8 + 2 * w, w), size = Rd(d, be, a + 8 + 3 * w, w);
            string name = strtab >= 0 ? CString(d, strtab + nameIndex) ?? string.Empty : string.Empty;
            sections.Items.Add(YValue.Obj(new YStruct().Set("name", name).Set("type", (long)type).Set("flags", (long)flags).Set("address", (long)addr)
                .Set("offset", (long)offset).Set("size", (long)size)));
        }
    }

    private static ulong Rd(ReadOnlySpan<byte> d, bool be, int at, int size) => size switch
    {
        2 => be ? BinaryPrimitives.ReadUInt16BigEndian(d[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(d[at..]),
        4 => be ? BinaryPrimitives.ReadUInt32BigEndian(d[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(d[at..]),
        _ => be ? BinaryPrimitives.ReadUInt64BigEndian(d[at..]) : BinaryPrimitives.ReadUInt64LittleEndian(d[at..]),
    };
}
