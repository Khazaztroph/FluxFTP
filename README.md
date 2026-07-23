               
# FluxFTP

See [CHANGELOG.md](CHANGELOG.md) for release history.

FluxFTP is a modern dual-pane FTP/FXP client prototype. The first milestone focuses
on the desktop workflow, a persistent transfer queue boundary, and a theme system
with ioGUI3 as the default visual identity.

## Run the prototype

From PowerShell inside the `C:\ioftp` directory:

```powershell
.\run.cmd
```

`run.cmd` can also be launched by double-clicking it. To use the PowerShell
script explicitly, invoke it with the call operator so Windows does not use the
`.ps1` file association:

```powershell
& 'C:\ioftp\run.ps1'
```

The equivalent direct command is:

```powershell
dotnet run --project .\src\IoFtp.Desktop\IoFtp.Desktop.csproj
```

FluxFTP supports FTP/FTPS connections, dual remote sessions, resumable transfers,
and secure FXP with automatic client-relay fallback.

Per-site affiliate/group names can be edited in Site Options and are exposed as
`affils` through the cbftp-compatible API for automatic d-tool synchronization.
Spread Jobs also supports reusable presets for section, source and target routes.
Optional unique site descriptions are exposed through the sites API and can be
used in API or d-tool download commands instead of the technical site name.
Sections can enforce release-name prechecks with wildcard or `regex:` allow and
deny rules. Warning mode allows a manual override, while Block mode prevents PRE
and section-based API/d-tool race or FXP jobs.

Transfer Queue and Transfer Jobs provide graphical per-file progress bars, queued
and started timestamps, live speed information, and an aggregate activity bar in
the main status area. FluxFTP can also check GitHub Releases for updates at startup
or manually from Global Settings.

Remote folders can be downloaded recursively to either Local pane. FluxFTP creates
the local directory tree and queues each file through the configured reusable
download slots while applying the Skiplist and Priority List.

FluxFTP can minimize and close to the Windows system tray so transfers and API
automation continue in the background. Use **Exit FluxFTP** from the tray menu to
stop the application; an additional warning is shown while the HTTPS/JSON API is active.

### mIRC and d-tool automation

When the HTTPS/JSON API is enabled, FluxFTP also exposes a cbftp-compatible UDP
command listener on the same numeric port. TCP is used for HTTPS and UDP is used
for autotrader commands, so both listeners can run together. This supports the
d-tool `raw`, `fxp`, `race` and `download` commands without the FTPRush DLL.

Example UDP payloads (the configured API password is the first field):

```text
password raw SITE1 site stat
password fxp SITE1 /path/Release SITE2 /target/Release
password fxp SITE1 /path Release SITE2 /target
password race MOVIES Release.Name SITE1,SITE2,SITE3
password download SITE1 /path/Release
```

API/UDP FXP jobs use saved site profiles and reusable slots and do not require
the sites to be open in the two visible Remote panes. Keep **Localhost only**
enabled when mIRC and FluxFTP run on the same computer.

d-tool's current line-oriented `/raw` parser also prints standalone JSON
delimiters. The compatible parser fix is available in
[scriptzteam/d-tool#1](https://github.com/scriptzteam/d-tool/pull/1).

## Screenshots

### Dual-pane workspace

![FluxFTP dual-pane main window](docs/screenshots/main-window.png)

| Transfer Jobs | Site Manager |
| --- | --- |
| ![FluxFTP Transfer Jobs](docs/screenshots/transfer-jobs.png) | ![FluxFTP Site Manager](docs/screenshots/site-manager.png) |

### Global Settings

![FluxFTP Global Settings](docs/screenshots/global-settings.png)

Saved sites may contain multiple addresses or FTP bouncers in the **Address(es)** field, separated by spaces. FluxFTP tries the first address immediately and starts the remaining attempts after one second. The first successful address is promoted to the front of the saved list for future connections.

Site Manager can import saved sites from FTPRush 3 `site.json`, legacy FTPRush
`RushSite.xml`, and FlashFXP `Sites.ftp` XML exports. A preview lets you select
profiles and leaves detected duplicates unchecked. Imported passwords are saved
using the same Windows DPAPI protection as manually created FluxFTP profiles.

## Currently supported FTP commands

FluxFTP sends the following commands automatically when required by browsing, transfers and connection setup:

- Connection and TLS: `USER`, `PASS`, `AUTH TLS`, `PBSZ`, `PROT`, `FEAT`, `TYPE` and `QUIT`
- Navigation and file management: `CWD`, `MKD`, `RMD`, `DELE`, `RNFR`, `RNTO` and `SITE CHMOD`
- Directory and file information: automatic `MLSD` → `LIST` → `STAT -l` fallback, plus `SIZE`
- Data connections and resume: `EPSV`, CEPR address replies, `PASV`, `PORT`, `EPRT` and `REST`
- File transfers: `RETR` and `STOR`
- Secure server-to-server FXP: `CPSV`, `SSCN ON` and `SSCN OFF`
- Distributed FTP servers: `PRET LIST`, `PRET RETR` and `PRET STOR` when **Needs PRET** is enabled for a site
- Multi-file duplicate handling: `SITE XDUPE 3` and X-DUPE `553` reply processing when **Use XDUPE** is enabled

The Commands window currently includes presets for:

- Common ioFTPD/glFTPD commands including `SITE TAGLINE`, `SITE NFO`, `SITE WHO`, `SITE RULES`, `SITE SEARCH`, `SITE NUKES`, `SITE NEW` and `SITE HELP`
- ioFTPD commands including `SITE PRE` and `SITE REQUESTS`
- glFTPD file, group, user, log, statistics and miscellaneous administration commands
- Optional pzs-ng and custom glFTPD commands, clearly identified in the preset name
- `SITE ioGuiExt who` is used internally for ioFTPD FXP speed monitoring

**Load SITE HELP** can add commands advertised by the connected server. FTPRush `RushCmd.xml` command packs can also be imported. The raw-command field accepts one FTP command at a time; credential commands `USER`, `PASS` and `ACCT` are blocked there to avoid exposing or replacing the active login.

## Project boundaries

- `IoFtp.Core`: protocol-neutral connection, browsing and transfer contracts.
- `IoFtp.Desktop`: Windows desktop shell and theme resources.
- `docs`: product scope and architecture decisions.

## Initial milestones

1. Usable dual-pane shell and ioGUI3 theme foundation.
2. Persistent queue and resumable local copy reference implementation.
3. FTP/FTPS transport with capability detection.
4. Server-to-server FXP orchestration.
5. Site profiles and cbftpd-aware commands.
6. SFTP transport as an optional provider.
