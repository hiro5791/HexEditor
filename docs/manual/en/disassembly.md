# Disassembly

The disassembly panel reads bytes as machine code and lists the instructions. It understands Windows (PE), Linux (ELF) and macOS (Mach-O) executables, .NET assemblies, and raw code for many processors. This page also covers symbol name demangling.

## Showing the disassembly {#show-disassembly}

- Select **Analysis > Disassemble**. The listing starts at the cursor, or at the start of the selection.
- Select **View > Panels > Disassembly**.
- Select bytes, right-click and choose **Disassemble**.
- Run **Analysis: Disassemble** or **Analysis: Disassemble from cursor** from the command palette.

The panel opens on the right. You can move it to the bottom when you need more width.

When the document is an executable, HexEditor reads its header and sets the architecture, mode, endianness and base address for you. The listing starts at the entry point. You can change any of these in the toolbar.

## The listing {#listing}

Each line shows the address, the bytes (up to 8 by default; longer instructions end with "…"), the instruction and a comment. Comments show symbol names, the names of branch targets and referenced strings. For executables, addresses are virtual addresses.

- Lines such as `main:` mark the entry point and symbols. Branch targets without a symbol get a name such as `sub_140001040` (the target of a call) or `loc_140001030` (the target of a jump).
- Bytes that are not a valid instruction are shown as `db 0x06`, one byte per line, and decoding continues with the next byte.
- Arrows left of the address show whether a branch goes forward or backward. When both ends are on screen, a line connects them.
- Point at an instruction to see its length, operands, the registers it reads and writes, and its instruction groups.
- If at least half of the lines are `db`, a notice says the bytes may not be code for this architecture.

The listing is always left to right, even when the display language is written right to left.

Move with the arrow keys, <kbd>Page Up</kbd>, <kbd>Page Down</kbd>, <kbd>Ctrl</kbd>+<kbd>Home</kbd> and <kbd>Ctrl</kbd>+<kbd>End</kbd>. The scroll bar follows the position in the file, so you can jump anywhere in a large file at once. Only the lines on screen are decoded.

### Working with the hex view {#hex-view-link}

- Select a line to select the bytes of that instruction in the hex view.
- With **Follow cursor** on (the default), moving the cursor in the hex view selects the instruction that contains it.
- The current instruction has a dashed outline in the hex view.
- After you edit the document, the listing is decoded again from the edited position.

### Copy and export {#copy-export}

- Press <kbd>Ctrl</kbd>+<kbd>C</kbd> or right-click a line and select **Copy** to copy it as tab-separated text.
- Right-click and select **Export as assembly...** to save a text file. It contains the selection in the hex view, or else the section of the selected line.

## Toolbar {#toolbar}

| Item | What it does |
| --- | --- |
| **Architecture** and **Mode** | The processor and its mode, such as x86 64 bit, ARM Thumb or RISC-V RV64C |
| **Endianness** | Little or big endian, for processors that have both |
| Universal binary list | For a Mach-O universal binary, choose x86_64, arm64 and so on |
| **Start** | Start from the cursor, the start of the selection, an offset, a virtual address or the entry point |
| Base address | The address of the first byte. Leave it empty to use the executable's value (0 for other files) |
| **Syntax (x86)** | Intel, AT&T or MASM |
| **Display options** | Number format, uppercase, columns (see [Display options](#display-options)) |
| **Follow cursor** | Keep the listing in step with the hex view cursor |
| **Find instructions** | Search for an instruction pattern (see [Find instructions](#find-instructions)) |

Supported architectures: x86 (16, 32 and 64 bit), ARM (ARM, Thumb, ARMv8, Cortex-M), ARM64, MIPS (MIPS32, MIPS64, microMIPS, MIPS32R6), PowerPC (32 bit, 64 bit, QPX), SPARC (SPARC, SPARC V9), SystemZ, XCore, 68K (68000 to 68060), RISC-V (RV32, RV64, and with compressed instructions), 6502 (6502, 65C02, W65C02, 65816), TMS320C64x, BPF (classic and eBPF) and WebAssembly. **Other** in the list has M680X, EVM and the architectures added by plug-ins.

## Sections and symbols {#sections-symbols}

For executables, the left side of the panel has two lists. When the panel is narrower than 600 pixels, the lists are above the listing.

- **Sections**: name, virtual address and size, file position and size, and flags (R, W, X). Select a section to disassemble from its start.
- **Symbols**: exports, imports (shown as `KERNEL32!CreateFileW`), the symbol tables, PLT entries (`puts@plt`) and stubs. C++, Rust and D names are shown demangled. Type in **Filter by name** to narrow the list. Select a symbol to disassemble from it.

If part of the header is broken, HexEditor uses what it can read and shows "Part of the header can't be interpreted" with the reason.

To go to a virtual address, run **Analysis: Go to virtual address**. Type an address (expressions work as in Go to). The dialog shows the matching offset and section, or says that the address isn't in any section. **Analysis: Go to entry point** goes to the entry point.

## Going to branch targets {#branch-targets}

Select a call or jump line and press <kbd>Enter</kbd> or <kbd>F12</kbd>, double-click it, or right-click and select **Go to branch target**. The listing and the hex view move to the target. Press <kbd>Alt</kbd>+<kbd>←</kbd> to come back.

- Instructions that refer to memory relative to the instruction pointer (such as `call qword ptr [rip + 0x1000]`) go to the referenced address.
- If the target is an imported function, the status bar shows its name, such as "External function: KERNEL32!CreateFileW", and nothing moves.
- If the target isn't in the file, the status bar says so.
- In the hex view, <kbd>F12</kbd> goes to the branch target of the instruction at the cursor when the disassembly panel is open and no template is applied.

### Finding references {#references}

Right-click a line and select **Find references** (or run **Disassembly: Find references**) to find instructions that call, jump to or refer to that address. HexEditor decodes all executable sections. For other files it decodes the selection, or the whole document. The results appear in the **Search results** panel with the address, instruction and kind (call, jump or data). The results are kept until you edit the document.

## Display options {#display-options}

Set these from **Display options** in the panel, or under **Settings > Disassembly**. They apply to all architectures and are saved.

- **x86 syntax**: Intel (default), AT&T or MASM. With AT&T, `48 89 E5` is shown as `movq %rsp, %rbp`.
- **Hexadecimal numbers**: `0x10` (default) or `10h`. **Uppercase hexadecimal digits** shows A to F in uppercase.
- **Signed immediates**: show negative immediate values with a minus sign (x86).
- **Uppercase mnemonics and registers**.
- **ARM register aliases**: `sp`, `lr` and `pc` (default) instead of `r13`, `r14` and `r15`.
- **Show the bytes column** and **Maximum bytes shown** (4 to 16).
- **Address column**: virtual address, file offset or both.
- **Branch lines** and **Comment column**.

## .NET assemblies {#dotnet}

When the document is a .NET assembly, a notice offers to show it as MSIL. Select **Show MSIL**, choose **MSIL (.NET)** under **Architecture**, or run **Analysis: Disassemble .NET method**.

1. The left side shows **Types and methods**: namespaces, types and methods. Expand a node to see what is inside.
2. Select a method. The listing shows the method header, the local variables, the IL instructions and the exception handling clauses.

Tokens are shown as names, such as `call void [System.Console]System.Console::WriteLine(string)` and `ldstr "Hello"`. Each line matches the bytes in the file, so selecting a line selects the bytes in the hex view. <kbd>Enter</kbd> on a branch goes to its target line. <kbd>Enter</kbd> on a call to a method in the same assembly opens that method. If the assembly was compiled with ReadyToRun, **Show native code** shows the x64 or ARM64 code of the method.

## Find instructions {#find-instructions}

Select **Find instructions** in the panel, or run **Disassembly: Find instruction pattern**, and type a pattern.

- Write one instruction, or several separated by `;`: `push rbp; mov rbp, rsp`.
- `*` matches any text and `?` matches one character: `call *`, `mov e?x, [rip+*]`.
- `{n}` or `{n-m}` in place of an instruction matches that many instructions: `cmp *; {0-3}; jz *`.
- Upper and lower case and the amount of white space don't matter. The pattern is compared with the instructions as shown with the current display options.
- Turn on **Regular expression** to use a .NET regular expression for each instruction.

Press <kbd>Enter</kbd> for the next match, <kbd>Shift</kbd>+<kbd>Enter</kbd> for the previous one, and <kbd>Alt</kbd>+<kbd>Enter</kbd> or **Find all** to list every match in the **Search results** panel. <kbd>↑</kbd> and <kbd>↓</kbd> go through your earlier patterns, and <kbd>Esc</kbd> closes the box. A pattern with an error, such as `{0-x}`, shows the column and the reason.

HexEditor searches all executable sections of an executable. For other files it searches the selection, or the whole document. When there are more than 100 MB to search, the status bar shows about how long it will take. You can cancel the search; the matches found so far stay.

## Changing instructions {#assemble}

For x86 and x64 you can type an instruction and write its machine code in place.

1. Right-click a line and select **Assemble...** (or run **Disassembly: Assemble instruction**).
2. Type the new instruction. Separate several with `;`. The bytes and their length appear as you type. Relative branches such as `jmp 0x401000` are calculated from the address of the line.
3. If the new code is shorter, choose **Fill the rest with NOP (0x90)** (the default) or **Keep the remaining bytes**. If it is longer, a warning lists the instructions after it that will be overwritten.
4. Select **Write**.

The bytes are always overwritten, even in insert mode, so the following instructions don't move. Press <kbd>Ctrl</kbd>+<kbd>Z</kbd> to undo the change in one step. If the instruction can't be assembled, the reason is shown and **Write** is not available. This feature is not available for read-only documents or for other architectures.

## Demangling symbol names {#demangle}

Compilers for C++, Rust and D change function names into a coded form. Demangling turns them back into readable names, such as `_ZN3foo3barEi` into `foo::bar(int)`.

1. Select **Analysis > Demangle...**, or run **Analysis: Demangle symbol names...**. To demangle text in the document, select it, right-click and choose **Demangle**.
2. Type or paste names into the upper box, one per line. The results appear in the lower box as you type.

HexEditor recognizes the Itanium C++ ABI (GCC and Clang), Microsoft Visual C++, Rust (legacy and v0) and D from the start of each name. Lines that are not mangled names are shown unchanged. Names that look mangled but can't be read are marked "(can't demangle)".

- **Short names** leaves out parameter and return types.
- **Show Rust hashes** keeps the hash at the end of legacy Rust names.
- **Copy** copies all results.

The symbol list of the disassembly panel and the template function `std::sys::demangle` use the same demangler.
