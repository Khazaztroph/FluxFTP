# Changelog

All notable FluxFTP changes are documented here.

## 1.0.39 — 2026-08-06

### Added

- Added single-instance protection so starting FluxFTP again restores the existing window instead of launching another process.

### Fixed

- Minimizing FluxFTP to the system tray now hides the active window from the Windows taskbar after the minimize transition has completed.
- Restoring FluxFTP from the tray now reliably brings the existing window to the foreground and gives it focus.

## 1.0.38 — 2026-08-05

### Added

- Added RushFTP-style Back, Forward, Up and Refresh navigation controls to both file panes.
- Replaced the plain path fields with editable path-history dropdowns for fast navigation in both Local and Remote mode.
- Local path dropdowns now include available drives, Desktop, Documents, Downloads and up to 20 recently visited folders.

### Changed

- Navigation history is maintained independently for the left and right panes and safely reset when switching between Local and Remote mode.

## 1.0.37 — 2026-08-05

### Added

- Added per-site proxy settings under Site Manager with `Use global proxy`, explicit `No proxy`, SOCKS5, SOCKS4 and HTTP CONNECT modes.
- Per-site proxy settings include host, port, credentials, proxy-side DNS resolution and optional proxying of passive FTP data connections.

### Changed

- Existing sites inherit Global Settings by default, while an explicit per-site proxy or direct-connection override takes precedence for browsing, transfers, worker slots, API jobs and reconnects.
- Per-site proxy passwords are stored in `FluxFTP-sites.ini` using Windows DPAPI protection for the current user.

## 1.0.36 — 2026-08-05

### Fixed

- Dark scrollbar tracks now bind correctly to the current value, maximum and viewport size, so dragging the thumb updates the list position accurately.
- Vertical scrollbars now use the full available height instead of being incorrectly constrained to 13 pixels; horizontal scrollbars retain a compact 13-pixel height.

## 1.0.35 — 2026-08-04

### Added

- Added a dark internal file viewer for local and remote files with automatic encoding detection, selectable UTF-8, Windows-1252, IBM437/DOS, ISO-8859-1, ASCII and UTF-16 encodings, plus optional word wrapping.
- Added a separate `Open externally` action that downloads remote files to the preview cache and opens them through their Windows file association.
- NFO files that are not valid UTF-8 now automatically use IBM437 so classic scene artwork is rendered correctly.

### Changed

- Reworked context menus to remove the standard white WPF icon gutter, prevent clipped labels and match the dark FluxFTP interface.
- Added dark themed horizontal and vertical scrollbars throughout the desktop client.

## 1.0.34 — 2026-08-03

### Added

- Connection Log now reports the negotiated TLS version, cipher suite and strength, certificate subject, issuer, validity period and validation result for the control connection and first protected data connection.

### Fixed

- Explicit and implicit FTPS now automatically reconnect with TLS 1.2 only when the initial TLS 1.2/1.3 negotiation returns an invalid or corrupted TLS frame, improving compatibility with affected glFTPd, OpenSSL and bouncer configurations.

## 1.0.33 — 2026-08-03

### Added

- Explicit FTPS now falls back from `AUTH TLS` to the legacy `AUTH SSL` command for compatible older FTP daemons while retaining TLS 1.2/1.3 SChannel encryption.

## 1.0.32 — 2026-08-03

### Fixed

- Secure FXP now falls back from an unsupported CEPR/EPSV command to CPSV when available, preserving the working ioFTPD TLS route before trying PASV/SSCN.
- Clear reverse FXP now falls back from an unsupported EPSV command to regular PASV instead of abandoning the direct route immediately.
- Dragging from an existing multi-selection now preserves every selected file and folder, allowing several remote folders to be queued through one drag-and-drop operation.

## 1.0.31 — 2026-08-03

### Added

- Added local drive selectors to both panes in Local mode for direct navigation between available Windows drives.
- Added drag-and-drop copying between two Local panes, including multiple files and recursive folders.
- Added Windows Explorer drag-and-drop into Local panes for copying and into Remote panes for queued uploads.

### Fixed

- Local directory views now list the complete directory instead of stopping after 100 entries.
- Drive-only paths such as `D:` now resolve to the drive root (`D:\`) consistently.
- Local directory entries remain sorted with folders before files after changing drives.

## 1.0.30 — 2026-08-03

### Changed

- Repositioned transfer progress, elapsed time, remaining time and queue duration at the right edge of the bottom status bar with responsive sizing for smaller windows.
- Transfer Queue columns now adapt to the available width so job state, paths and progress remain visible.
- Scrolling legend mode now uses the full width of the bottom bar instead of being limited to the left status field.

## 1.0.29 — 2026-08-03

### Changed

- Reworked the bottom transfer status into a FlashFXP-inspired FluxFTP bar with direction and filename, transferred bytes and speed, percentage, elapsed time, estimated remaining time and queue duration.

## 1.0.28 — 2026-08-03

### Added

- Desktop SFTP connections with password authentication, browsing, upload, download, resume and client-relay transfers.
- SHA256 SSH host-key verification with an explicit trust prompt and persistent per-site fingerprints.
- SFTP-backed create folder/file, rename, recursive delete, chmod, preview and queue operations.

### Changed

- Remote-to-remote transfers involving SFTP automatically use client relay because the FTP FXP protocol is not available over SSH.

## 1.0.27 — 2026-08-03

### Added

- Expanded both file-list context menus with Open/View, add-to-queue wording, create-and-enter folder, create-empty-file, and copy FTP/file URL actions.
- Expanded the connection log with timestamped raw FTP commands/server replies, automatic password masking, and Copy/Clear controls.

### Fixed

- UNIX symbolic links displayed as `name -> target` are now parsed with separate name and target metadata and can be navigated like directories.

## 1.0.26 — 2026-07-30

### Fixed

- FXP monitoring now accepts the same ioGuiExt activity rows as ioGUI3 instead of incorrectly discarding active status rows.
- A successful ioGuiExt command without an exact destination filename match no longer disables all fallback progress measurement.
- FluxFTP can read RETR activity from the source site when the destination does not expose matching STOR activity, such as ProFTPD-to-ioFTPD routes.
- The aggregate status progress bar estimates transferred bytes from live FXP speed when ioFTPD does not publish a usable `TRANSFERSIZE` value.

### Changed

- The main status bar now displays live FXP throughput next to the aggregate percentage.

## 1.0.25 — 2026-07-30

### Changed

- Multi-file transfers prewarm the required source/download and destination/upload workers in parallel before queue execution begins.
- Recently returned workers are reused immediately; `NOOP` health checks are now reserved for workers that have been idle for at least 30 seconds.
- Worker prewarming respects each site's login and directional upload/download slot limits and reports its preparation time in the connection log.
- Site Options now includes a cbftp-compatible `Broken PASV` checkbox that selects the site's PORT/active role immediately, without waiting for an initial `425` timeout; the setting is also exposed as `broken_pasv` through the API.

## 1.0.24 — 2026-07-30

### Added

- Learned reverse PASV/PORT routes are persisted per source/destination site pair and restored after FluxFTP restarts.
- Compact FXP phase timings now show CWD, PRET, PROT, PASV/EPSV/CPSV, PORT, SSCN, STOR/RETR acceptance and data-transfer duration.

### Changed

- Independent control commands on the source and destination sessions are issued concurrently where protocol ordering permits.
- A persisted reverse route is removed automatically when it later fails with a timeout or FTP `425`.

## 1.0.23 — 2026-07-30

### Changed

- Successful transfer sessions are retained as reusable site workers instead of logging out after every file.
- Reused workers are health-checked with `NOOP`; disconnected, failed, cancelled or stale-profile sessions are discarded safely.
- Idle worker pools are closed when a site disconnects, its profile changes or FluxFTP exits.

## 1.0.22 — 2026-07-30

### Fixed

- Remote file transfers now change to the source and destination parent directories before issuing relative `RETR` and `STOR` commands, matching FlashFXP behavior and ioFTPD pre-command script expectations.
- The relative-path command flow is used consistently for direct FXP, reversed PASV/PORT FXP, client relay, downloads and uploads.
- FXP failure diagnostics now display the actual `CWD`, relative `RETR` and relative `STOR` command sequence.

## 1.0.21 — 2026-07-29

### Added

- Failed direct FXP attempts now log the source and destination sites, actual connected endpoints, full `RETR` and `STOR` paths, destination parent, data protection, selected route and PRET state.
- Transfer diagnostics deliberately exclude usernames, passwords and authentication commands.

## 1.0.20 — 2026-07-29

### Added

- Optional Advanced Skiplist rules with wildcard or regex matching, File/Directory/Both selection, Allow/Deny actions, scope and ordered first-match evaluation.
- A filled aggregate progress bar in the main status area.

### Changed

- The default main-window size is reduced to fit smaller laptop displays while saved window layouts continue to be restored.
- About now reflects the current FTP, FTPS, FXP, automation, import and ioFTPD feature set.
- The application icon now uses a text-free modern server-and-gear symbol that remains clear at small Windows icon sizes.

### Fixed

- The Close button in the modeless Server Commands window now closes the window correctly.

## 1.0.19 — 2026-07-29

### Fixed

- Self-contained Windows releases once again include the native WPF libraries required before the main window can open.
- `SITE PRE` now runs from the selected release's parent directory, matching ioFTPD's expected working directory.
- Delayed ioFTPD CWD-script replies are skipped so the actual PRE result is displayed and the control channel remains synchronized.
- IRC formatting and other invisible control characters are removed from commands before they are sent.
- PRE script variables now use the release named in the command and its resolved release path.

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
