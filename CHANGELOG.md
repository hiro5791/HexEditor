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
