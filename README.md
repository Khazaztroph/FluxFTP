# FluxFTP

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
