# FluxTelemetry bridge

FluxTelemetry is an optional local API layer for ioFTPD. One persistent FTP session polls a lightweight `client who` command and shares the result with FluxFTP, ioGUI3, bots and plugins.

## ioFTPD installation

1. Copy `extras/ioftpd/FluxTelemetry.itcl` to `C:\ioFTPD\scripts\FluxTelemetry.itcl`.
2. Add this command under `[FTP_Custom_Commands]` in `system/ioFTPD.ini`:

   ```ini
   fluxwho = TCL ..\scripts\FluxTelemetry.itcl
   ```

3. Restrict it under `[FTP_SITE_Permissions]` to the trusted telemetry account or an appropriate admin flag:

   ```ini
   fluxwho = M
   ```

4. Rehash or restart ioFTPD and verify `SITE FLUXWHO` with the telemetry account.

## Bridge configuration

Run `FluxTelemetry.exe` once to create `FluxTelemetry.json`, then set the ioFTPD host, port, username and password. At the next start a plaintext password is automatically protected with Windows DPAPI for the current user. The bridge binds to loopback by default.

If the telemetry username is empty, FluxTelemetry automatically imports host, port and credentials from `C:\ioFTPD\ioGUI\sites.ini`, then immediately protects the imported password with DPAPI. `LegacySitesIni` can be changed in the JSON configuration.

Endpoints:

- `GET /info`
- `GET /health`
- `GET /activity`
- `GET /metrics`
- `GET /events` (Server-Sent Events)

The latest snapshot is also written atomically to `FluxTelemetry-activity.json` for Eggdrop/Tcl and other local consumers. Default refresh interval is 500 ms. Decimal ioFTPD `TRANSFERSPEED` values are normalized from KiB/s to bytes/s.

The bridge filters its own `SITE FLUXWHO` command from activity snapshots and metrics. Other sessions belonging to the telemetry account remain visible.

Routine HTTP polling is intentionally not logged. The console remains quiet during normal operation and reports only startup details, warnings and errors.

FluxTelemetry is an optimization only. Consumers should retain their existing FTP/server-specific method as fallback when the bridge is unavailable.
