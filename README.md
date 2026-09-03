# ServerManager

A Windows desktop app that supervises game servers started by `.bat` and `.ps1` scripts.

Your existing start scripts stay exactly as they are. ServerManager wraps them with
console capture, crash recovery, resource monitoring and scheduled backups, and keeps
running in the tray so your servers stay up.

---

## Features

**Console and logs** — live stdout and stderr per server, colour-coded by stream, with an
input box that sends commands straight to the running server. Full history is written to
daily rolling log files, so nothing is lost when the on-screen buffer scrolls.

**Crash recovery** — restart on crash or on any exit, with exponential backoff
(5s → 10s → 30s → 60s). The failure counter resets once a server has been up a while, so a
server that crashes once a week never accumulates its way into being given up on. A hard
cap parks a crash-looping server instead of thrashing forever. Stopping a server yourself
never triggers a restart.

**Resource monitoring** — CPU, memory, uptime and process count measured across the
server's whole process tree, with a CPU history graph.

**Backups** — scheduled or on-demand zip archives with retention. *Safe* mode stops the
server, archives, and restarts it, which is always restorable at the cost of a short
outage. *Live* mode archives while running and skips files the server holds locked.

**Scheduling** — a daily restart time and a daily backup time per server.

**Tray resident** — closing the window hides to the tray and servers keep running.
Schedules keep firing while hidden.

**Self-updating** — checks GitHub for new releases and installs them on your approval.

---

## Installing

1. Install the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Download `ServerLauncher.App.exe` from the
   [latest release](https://github.com/AnthemVH/ServerManager/releases/latest).
3. Put it in a folder you own, such as `C:\ServerManager\`.

**Not `Program Files`.** The app replaces its own executable when updating, and that
folder is not writable without elevation.

A self-contained build needing no .NET runtime can be produced with the packaging command
below, but it is ~155 MB and makes every update that size. The framework-dependent build
published with each release is ~1 MB.

---

## Adding a server

Click **Add server** and point it at the `.bat`, `.cmd`, `.ps1` or `.exe` that starts your
server. The file is never modified.

The settings that matter most:

| Setting | What it does |
|---------|--------------|
| **Stop command** | Written to the server's console for a clean shutdown — `stop` for Minecraft, `quit` for many source engine servers. Leave empty if the server does not read commands; it is terminated after a short grace period instead. |
| **Restart policy** | *Never*, *On crash* (non-zero exit only), or *Always*. |
| **Environment variables** | One `KEY=VALUE` per line, e.g. `JAVA_OPTS=-Xmx4G`. |
| **Working directory** | Leave empty to run from the script's own folder, which is what most server scripts expect. |

---

## Running on a dedicated server

The app supervises servers from inside an interactive session, so after a reboot nothing
starts until someone logs in. To close that gap:

1. In **Settings**, tick *Start ServerManager when I log in* and *Start minimised to the
   tray*.
2. Set each server to *Start this server when the launcher starts*.
3. Configure Windows auto-logon so a reboot reaches the desktop unattended.

For step 3 use [Sysinternals Autologon](https://learn.microsoft.com/sysinternals/downloads/autologon),
which stores the password as an LSA secret rather than in plain text under
`HKLM\...\Winlogon\DefaultPassword` as the manual registry method does. Either way the
machine sits at a logged-in desktop, so anyone with console access has that session.

---

## Versioning and updates

Releases are versioned `vMAJOR.MINOR.PATCH`. The tag drives everything:

```bash
git tag v1.1.0 && git push origin v1.1.0
```

That tag becomes the build's assembly version, so a copy built from `v1.1.0` reports
itself as `1.1.0`. The app compares its own version against the newest release tag to
decide whether an update exists — the version shown in the status bar is the same number
that comparison uses. The release workflow rejects any tag that is not
`vMAJOR.MINOR.PATCH`, because a tag it cannot parse is a tag the app cannot compare.

Pushing the tag builds the app, runs the full test suite, and publishes the executable
with its SHA-256. A failing test stops the release, so a broken build never reaches the
server.

### How an update reaches the server

The server pulls; nothing is pushed to it. It needs only outbound HTTPS, so nothing has to
be exposed inbound.

1. ServerManager checks the releases API on startup, and whenever you click
   **Check for updates**.
2. A banner appears when a newer release exists. **Nothing is installed until you click
   Install and restart.**
3. It downloads the new build, verifies it against the SHA-256 published with the release,
   stops your servers cleanly, swaps the executable, and relaunches.

> **Updating restarts your game servers.** They are supervised children of ServerManager,
> so replacing it takes them down. The confirmation dialog lists exactly which servers will
> stop. They do not come back automatically unless set to start with the launcher.

Point **Settings → GitHub repository** at `AnthemVH/ServerManager` (already the default).
For a private repository, set a `SERVERLAUNCHER_GITHUB_TOKEN` environment variable on the
server holding a read-only token — it is read from the environment rather than settings, so
no credential is written to a config file.

### Rolling back

The previous executable is kept beside the new one as `ServerLauncher.App.exe.old` until
the update starts successfully. If a build misbehaves, delete the new exe and rename that
file back.

---

## How it works

### Process trees

`cmd.exe /c start-server.bat` spawns the real server — `java.exe` or similar — as a
*child*. Killing only the handle the app holds would leave that child running, orphaned and
invisible, and a later "restart" would then have two servers writing to the same world
files.

Every server therefore runs inside a **Windows job object** created with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. Every descendant joins the job automatically, so
stopping a server terminates the entire tree atomically with no orphans, resource sampling
can enumerate the tree directly, and if the app crashes Windows closes the job handle and
cleans up regardless.

That last property is also why **exiting always stops your servers** — they are supervised
children, not detached services. Close the window instead; the app stays in the tray.

### Launching

| Script | Command |
|--------|---------|
| `.bat` / `.cmd` | `cmd.exe /c "<path>"` (double-quoted, so paths with spaces work) |
| `.ps1` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<path>"` |
| `.exe` | invoked directly |

`-ExecutionPolicy Bypass` applies to that one process and never changes machine policy. The
PowerShell host is configurable if you install PowerShell 7 (`pwsh.exe`).

### Stopping

The stop command is written to the server's stdin, the app waits up to the graceful
timeout, and only then terminates the job.

---

## Where things live

| What | Where |
|------|-------|
| Server list | `%APPDATA%\ServerLauncher\servers.json` |
| App settings | `%APPDATA%\ServerLauncher\settings.json` |
| Console logs | `%LOCALAPPDATA%\ServerLauncher\logs\<server-id>\<date>.log` |

Config is written atomically (temp file, then replace, keeping a `.bak`), so a crash
mid-save cannot leave a truncated server list.

---

## Building

Requires the .NET 8 SDK and Windows 10/11.

```bash
dotnet build
```

```bash
dotnet test
```

Package a release build:

```bash
dotnet publish src/ServerLauncher.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:Version=1.0.0 -o dist
```

### Layout

```
src/ServerLauncher.Core/   Supervision logic, no UI dependencies
  Processes/               Job objects, script launching, process wrapper
  Supervision/             State machine, restart policy, resource sampling
  Logging/                 Ring buffer and rolling file writer
  Backup/                  Archiving with retention
  Updates/                 Release checking, download verification, self-install
  Storage/                 Atomic JSON persistence
src/ServerLauncher.App/    WPF interface (MVVM)
tests/                     132 tests
demo/                      A stand-in game server for trying things out
```

The tests cover the parts that are easy to get quietly wrong: real process-tree kills
verified by checking no orphan survives, restart policy and backoff, backups against
locked files, update version comparison and the rename-and-rollback swap, theme contrast,
and WPF data bindings.

> The executable and assemblies are still named `ServerLauncher.*` from the project's
> original name. Renaming them would change the release asset name and the installed file
> path, so it has been left alone.
