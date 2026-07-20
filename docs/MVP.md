# ioFTP MVP

## Product promise

A new user can connect to a site and transfer files without understanding raw FTP,
while an experienced user can inspect commands, manage multiple sites and use FXP.

## Included in the first usable release

- Dual local/remote browser with keyboard-friendly navigation.
- Site manager and encrypted credential storage through Windows facilities.
- FTP and explicit/implicit FTPS.
- Persistent transfer queue with retry, pause, resume and crash recovery.
- Connection log with friendly messages and an optional raw protocol view.
- ioGUI3 default theme plus light, dark and high-contrast theme slots.
- Per-site capability detection and configurable custom site commands.
- FXP when both endpoints advertise and successfully negotiate support.

## Deferred

- SFTP is a provider after the FTP/FTPS queue is stable.
- cbftpd automation is layered on site profiles rather than embedded in FTP code.
- Plugin distribution and public theme marketplace are post-MVP.

## Safety rules

- Passwords never appear in logs or theme/configuration files.
- Destructive remote actions require explicit confirmation by default.
- Queue state is written atomically and can be recovered after interruption.
- Server replies are retained separately from user-facing explanations.

