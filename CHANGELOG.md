# Changelog

All notable changes to HexEditor are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses [Semantic Versioning](https://semver.org/) (see docs/spec/10-packaging.md, PKG-28).
The release workflow uses the section for the tagged version as the release notes and fails if the section is missing (PKG-24, PKG-29).

## [Unreleased]

### Added

- Opening, viewing, editing (overwrite and insert), searching and saving files of any size without loading them into memory.
- Undo and redo, copy and paste as bytes or hex text, go to offset.
- Recovery of unsaved changes after a crash, and local crash reports.
- Three distributions from one code base: installer (Velopack), portable zip and Microsoft Store (MSIX).
- UI in 23 languages.
- Command line `hexed` (open, hash, dump, compare, find, convert, patch, template, run): results on standard output, exit codes, JSON output, `--wait`, `--unregister`.
- Branching undo history: a tree in the history panel, named and pinned points, and saving and loading the history (`.hexhist`).
- Patches: create IPS, IPS32, UPS, BPS and HexEditor patches (`.hexpatch`) from unsaved changes, two tabs, a comparison or two points in the history; apply them with a check of the original; self-applying patches (EXE). `hexed patch` also applies UPS, BPS and `.hexpatch`.
- Transform view: show and edit the data through XOR and other transformations without changing the file.
- Calculator with programmer mode, base and data type converter, floating-point visualizer, character code table, color picker and date and time converter (Tools menu).
- Checksum files: create (GNU, BSD, per-file, SFV) and verify, with an InfoBar when you open one; folder comparison.
- Printing with a live preview, and PDF export with searchable text and embedded font subsets (File menu).
- File tools: split, join, interleave and deinterleave files of any size, and secure delete (Tools > File tools).
- NTFS alternate data streams: list, open as documents, create, rename, delete, export and import.
- Disassembly panel: x86 / x64 (with an assembler), ARM, ARM64, MIPS, PowerPC, SPARC, RISC-V, 6502, BPF, WebAssembly and .NET IL; sections and symbols of PE, ELF and Mach-O files, cross references, instruction pattern search, export as assembly; C++, Rust and D name demangling.
- Encryption and decryption (AES, ChaCha20-Poly1305, 3DES, DES and RC4) and compression and decompression (deflate, zlib, gzip, Brotli, zstd, LZ4, LZMA and XZ) of a range, with derived documents that you edit and write back to the original range.
- YARA rule scan of a document, a selection or a folder (built-in engine).
- Process snapshots and minidumps, GDB remote debugging targets, SFTP files and UDP capture with a packets panel.
- Template visualizers (images, tables, colors and charts in the structure view) and an online template repository with signed indexes.
- Automation through a JSON-RPC named pipe (off by default; current user only).
