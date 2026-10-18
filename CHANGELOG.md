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
