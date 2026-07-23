# Changelog

All notable FluxFTP changes are documented here.

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
