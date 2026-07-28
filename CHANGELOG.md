# Changelog

All notable FluxFTP changes are documented here.

## 1.0.18 — 2026-07-29

### Added

- Per-site FXP data role selection: Auto, PASV or PORT.
- The selected FXP data role is persisted in `FluxFTP-sites.ini` and exposed through the API as `fxp_data_role`.

### Fixed

- Clear FXP can retry with the reverse PASV/PORT topology when the standard route times out.
- A successful reverse route is remembered for the remaining queued files, avoiding repeated timeout delays.
- FXP starts RETR and STOR together for compatibility with servers that wait for the peer before sending a preliminary reply.

## 1.0.17 — 2026-07-28

### Added

- Clearly visible drag handles for independently resizing Transfer Queue and Connection Log.
- Resized queue and log heights continue to be restored on the next start.

### Fixed

- Auto FXP now uses clear PASV/PORT transfers when either site explicitly uses plain FTP, while two FTPS sites remain secure by default.
- Unix `LIST` summary rows such as `total 10690` are no longer mistaken for files and queued for transfer.
- Window layout persistence now rejects invalid non-finite dimensions instead of failing while the application closes.

## 1.0.16 — 2026-07-28

### Fixed

- Opening the embedded transfer queue no longer crashes while jobs are active.
- The Transfer Jobs progress bars now use the same safe one-way progress binding.

## 1.0.15 — 2026-07-27

### Added

- Multi-selection with Ctrl/Shift in both file panes for batching files and folders.
- Recursive local-folder uploads, matching the existing recursive download and FXP folder handling.
- Multi-item support for Transfer, Queue, drag-and-drop and context-menu transfers.

## 1.0.14 — 2026-07-26

### Fixed

- Transfer slots now reload the latest Site Manager profile before every file, so switching a connected FTP/FTPS site from Auto to Clear FXP takes effect without reconnecting.
- Clarified that Clear mode supports fully plain FTP control connections and PASV/PORT FXP without data TLS.

## 1.0.13 — 2026-07-26

### Added

- RaceTrade-compatible REST spreadjob endpoints for starting and monitoring races.
- RaceTrade/cbftp aliases for site settings including PRET, CEPR, XDUPE, binary mode and named priorities.
- Spreadjob tracking tied to the underlying FluxFTP FXP queue.
- Automatic probing of eligible race sites to locate the announced release source.

### Changed

- The `/sites` and `/sections` collection endpoints now return cbftp-compatible name arrays; their detail endpoints continue to expose full configuration objects.

## 1.0.12 — 2026-07-26

### Added

- FTPRush import guide with separate **Import Sites** and **Import Bookmarks** choices.
- Mutually exclusive **Replace existing** and **Skip existing** conflict handling.
- Import of global bookmarks from `core_setting.json` and per-site bookmarks from `site.json`.
- Persistent `FluxFTP-bookmarks.json` storage.
- Bookmark selectors in both panes for local and site-specific remote navigation.
- Explicit **Kill ghost login (/username)** option in Connection details for ioFTPD accounts.

### Fixed

- Local downloads now close and flush the `.ioftp-part` stream before the final rename, preventing Windows file-lock failures.

## 1.0.11 — 2026-07-26

### Added

- Automatic nuke detection for common ioFTPD/glFTPD directory and marker-file names.
- A Status column and red highlighting for nuked entries in both file panes.
- Nuke status fields in the `/path` API response.
- A manual warning and override before transferring a nuked remote folder.
- Safe blocking of nuked releases before API/d-tool FXP or download jobs are queued.
- Per-site direct FXP protection mode: Auto keeps TLS mandatory, while Clear explicitly permits PASV/PORT FXP without data-channel TLS.
- `fxp_protection` support in the sites API for DrFTPD and other compatibility workflows.
- Fixed an application crash when DrFTPD returns `550` while a remote directory is queued recursively; inaccessible subfolders are now logged and skipped.

## 1.0.10 — 2026-07-23

### Added

- Per-section release validation (Wanker check) with Disabled, Warning and Block modes.
- Allow and deny rules using wildcards or `regex:` patterns.
- Interactive precheck testing in the Sections window.
- Validation before manual `SITE PRE`, raw API PRE and section-based API/d-tool race or FXP jobs.
- Clear precheck logging and manual override for Warning mode.

## 1.0.9 — 2026-07-23

### Added

- Reusable Spread presets that remember section, source site and target sites.
- Apply, save, update and delete controls for presets in the Spread Jobs window.
- Optional unique site descriptions in Connection details and Site Manager.
- `description` in the cbftp-compatible sites API.
- API and UDP downloads can resolve a site by either name or description.

## 1.0.8 — 2026-07-23

### Added

- Per-site **Affiliates (affils)** field in Site Options.
- cbftp-compatible `affils` synchronization through the sites API for d-tool.

### Fixed

- Preserve affiliate values when Site Options are edited and saved.

## 1.0.7 — 2026-07-23

### Added

- cbftp-compatible UDP listener for d-tool `raw`, `fxp`, `race` and `download` commands.
- Headless API and UDP transfers using saved sites and reusable transfer slots.
- cbftp-compatible `/spreadjobs` endpoint.
- Additional site API fields for sections, transfer policies, affiliates and binary mode.
- Support for both standard and compact cbftp FXP command formats.

### Fixed

- Match cbftp's `/raw` response structure and connection behavior for d-tool.
- Remove ANSI color codes from raw FTP command responses.
- Add safe API request diagnostics without logging credentials.
