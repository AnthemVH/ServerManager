# Server Launcher

A standalone Windows app that supervises game servers started by `.bat` and `.ps1`
scripts. Your scripts stay exactly as they are — the launcher wraps them with console
capture, crash recovery, resource monitoring and scheduled backups.

## What it does

- **Console and logs** — live stdout/stderr per server, colour-coded by stream, with an
  input box that sends commands to the running server's stdin. Full history is written
  to daily rolling log files.
- **Crash recovery** — restart on crash or on any exit, with exponential backoff, a
  failure counter that resets after stable uptime, and a hard cap so a broken server
  gets parked instead of thrashing forever. Stopping a server yourself never triggers
  a restart.
- **Resource monitoring** — CPU, memory, uptime and process count across the server's
  whole process tree, with a CPU sparkline.
- **Backups** — scheduled or on-demand zip archives with retention, in either *safe*
  mode (stop, archive, restart) or *live* mode (archive while running, skipping locked
  files).
- **Tray resident** — closing the window hides to the tray and servers keep running.
  Scheduled daily restarts and backups keep working while hidden.

## Requirements

- Windows 10/11
- .NET 8 SDK to build; nothing at all to run the self-contained build

## Building

```bash
dotnet build ServerLauncher.sln
```

Run it:

```bash
dotnet run --project src/ServerLauncher.App
```

Run the tests:

```bash
dotnet test
```

## Packaging

Self-contained — one file, no .NET install needed on the target machine (~155 MB):

```bash
dotnet publish src/ServerLauncher.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/self-contained
```

Framework-dependent — ~1 MB, but the machine needs the .NET 8 Desktop Runtime:

```bash
dotnet publish src/ServerLauncher.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist/framework-dependent
```

## How it works

### Process trees

The central problem: `cmd.exe /c start-server.bat` spawns the real server (`java.exe`
or similar) as a *child*. Killing the handle the launcher holds would leave that child
running, orphaned and invisible — and a later "restart" would then have two servers
writing to the same world files.

Every server therefore runs inside a **Windows job object** created with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. Every descendant joins the job automatically, so:

- stopping a server terminates the entire tree atomically, with no orphans;
- resource sampling can enumerate the tree directly rather than walking parent PIDs;
- if the launcher itself crashes, Windows closes the job handle and cleans up for us.

That last property is also why **exiting the app always stops your servers** — they are
supervised children, not detached services. Close the window instead; the launcher
stays in the tray and everything keeps running.

### Launch matrix

| Script | Command |
|--------|---------|
| `.bat` / `.cmd` | `cmd.exe /c "<path>"` (double-quoted, so paths with spaces work) |
| `.ps1` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<path>"` |
| `.exe` | invoked directly |

`-ExecutionPolicy Bypass` applies to that one process and never changes machine policy.
The PowerShell host is configurable in Settings if you install PowerShell 7 (`pwsh.exe`).

### Stopping a server

1. The configured stop command (e.g. `stop`) is written to the server's stdin.
2. The launcher waits up to the graceful timeout for a clean exit.
3. If it hasn't exited, the whole job is terminated.

Servers that don't read stdin just leave the stop command blank and get a short grace
period before termination.

## Where things live

| What | Where |
|------|-------|
| Server list | `%APPDATA%\ServerLauncher\servers.json` |
| App settings | `%APPDATA%\ServerLauncher\settings.json` |
| Console logs | `%LOCALAPPDATA%\ServerLauncher\logs\<server-id>\<date>.log` |

Config is written atomically (temp file + replace, keeping a `.bak`), so a crash
mid-save can't leave you with a truncated server list.

## Running on a dedicated server

### Which build to install

Use the **framework-dependent** build on the server, not the self-contained one. Install
the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) once, and
every update afterwards is a **1 MB** download instead of 155 MB.

Put it in a folder you own, such as `C:\ServerLauncher\`. **Not** `Program Files` — the
app replaces its own executable when updating, and that folder is not writable without
elevation.

### Surviving a reboot

The launcher supervises servers from inside an interactive session, so after a reboot
nothing starts until someone logs in. To close that gap:

1. In **Settings**, tick *Start Server Launcher when I log in* and *Start minimised to
   the tray*.
2. Configure Windows auto-logon, so a reboot reaches the desktop unattended.

For step 2, use [Sysinternals Autologon](https://learn.microsoft.com/sysinternals/downloads/autologon).
It stores the password as an LSA secret rather than in plain text under
`HKLM\...\Winlogon\DefaultPassword`, which is what the manual registry method does and
what the many "how to autologon" guides tell you to do. Either way, understand the
tradeoff: the machine sits at a logged-in desktop, so anyone with physical or remote
console access has your session.

If that tradeoff is unacceptable, the alternative is splitting the supervisor into a
Windows Service with the window as a client. That starts at boot with no login at all,
but is a real refactor of the supervision layer rather than a setting.

## Updating from your PC

Updates are pulled by the server from GitHub Releases. The server needs only **outbound**
HTTPS — nothing has to be exposed inbound, which matters for a rented box.

### One-time setup

Create the repository and push:

```bash
git init -b main
git add .
git commit -m "Initial commit"
git remote add origin https://github.com/AnthemVH/ServerManager.git
git push -u origin main
```

A **public** repo needs no credentials on the server. If you make it private, set a
`SERVERLAUNCHER_GITHUB_TOKEN` environment variable there with a fine-grained token that
has read access to the repo. It is deliberately read from the environment rather than
stored in `settings.json`, so no credential ends up in a plaintext config file.

Then in the launcher's **Settings** on the server, set **GitHub repository** to
`AnthemVH/ServerManager` (already the default). Pasting the full GitHub URL works too.

### Shipping a new version

From your PC:

```bash
git tag v1.1.0 && git push origin v1.1.0
```

The `Release` workflow builds, runs the full test suite, publishes the exe plus its
SHA-256, and creates the release. A failing test stops the release, so a broken build
never reaches the server.

Version tags must be `vMAJOR.MINOR.PATCH` — the launcher parses them to decide what is
newer, and the workflow rejects anything else.

### Installing it on the server

The launcher checks on startup and shows a banner when a newer release exists. Nothing is
installed until you click **Install and restart**. It then downloads, verifies the
SHA-256, stops your servers cleanly, swaps the executable, and relaunches.

**Updating restarts your game servers.** They are supervised children of the launcher, so
replacing it takes them down; the confirmation dialog lists exactly which ones will stop.
They do not come back automatically — start them again once the new version has loaded,
or set them to *Start this server when the launcher starts*.

### How the swap works

Windows will not let a running `.exe` be overwritten, but it will let one be renamed. So
the update moves the live executable to `.old`, drops the new build into its place, and
relaunches with `--after-update <pid>` so the new process waits for the old one to release
the single-instance mutex. The `.old` file is deleted on the next successful start, which
doubles as the rollback: if the new build will not run, rename it back.

If the second move fails, the first is undone — a failed update always leaves a working
app rather than an empty folder.

## Project layout

```
src/ServerLauncher.Core/   Supervision logic, no UI dependencies
  Processes/               Job objects, script launching, process wrapper
  Supervision/             Server state machine, restart policy, resource sampling
  Logging/                 Ring buffer + rolling file writer
  Backup/                  Archiving with retention
  Storage/                 Atomic JSON persistence
src/ServerLauncher.App/    WPF UI (MVVM)
tests/                     63 tests, including real process-tree kill verification
demo/                      A stand-in game server for trying things out
```

## Trying it out

`demo/demo-server.bat` behaves like a small game server: it prints startup lines, ticks,
responds to `players`, and shuts down cleanly on `stop`. See `demo/README.md`.
