# Architecture

The desktop UI depends on application contracts, never directly on a concrete FTP
library. Protocol transports implement the same session boundary and publish
capabilities discovered at runtime.

```text
IoFtp.Desktop
    -> browsing UI / transfer-job views
        -> IoFtp.Engine global scheduler (headless)
            -> site policies, reusable slot pools, scoring, allow/block rules
            -> transfer executor
        -> IoFtp.Core contracts
            -> FTP/FTPS provider
            -> SFTP provider
            -> FXP coordinator
        -> site profile + command providers
        -> durable queue store
```

Themes contain visual tokens only. Pane layout, open tabs and user preferences are
stored independently, allowing a theme change without resetting the workspace.

The engine has no dependency on WPF. A slot belongs to a site, not a pane or a
transfer direction, and is reserved only while one file is being transferred.
After every completion or site-state notification the global candidate list is
rescored and free source/destination slots are paired again.
