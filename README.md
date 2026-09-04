# ServerManager

A Windows desktop app that supervises game servers started by `.bat` and `.ps1` scripts.

Your existing start scripts stay exactly as they are. ServerManager wraps them with
console capture, crash recovery, resource monitoring and scheduled backups, and keeps
running in the tray so your servers stay up.

---

## Features

**Dashboard** — every server on one screen, each with its status, CPU, memory, uptime,
process count and CPU graph, plus start/stop/restart on each card. A summary strip totals
CPU, memory and processes across all of them and shows how many are running. This is the
landing view; **Servers** switches to the per-server console and settings.

**Console and logs** — live stdout and stderr per server, colour-coded by stream, with an
input box that sends commands straight to the running server. Full history is written to
daily rolling log files, so nothing is lost when the on-screen buffer scrolls.

**Launcher scripts** — scripts that start the server and then exit, rather than staying
alive for its lifetime, are supported. ServerManager notices the script left processes
behind and supervises those instead. Arma 3 server scripts work this way.

**Crash recovery** — restart on crash or on any exit, with exponential backoff
(5s → 10s → 30s → 60s). The failure counter resets once a server has been up a while, so a
server that crashes once a week never accumulates its way into being given up on. A hard
cap parks a crash-looping server instead of thrashing forever. Stopping a server yourself
never triggers a restart.

**Resource monitoring** — CPU, memory, uptime and process count measured across the
server's whole process tree, with a CPU history graph.

**Self-monitoring** — ServerManager also reports its own CPU, memory, managed heap,
thread and handle counts. It supervises everything else, so if it falls over the servers
go with it; a handle or memory count that climbs and never settles is the earliest sign of
trouble. A compact readout sits in the status bar at all times, with the full breakdown
under Monitoring.

**Backups** — scheduled or on-demand zip archives with retention. *Safe* mode stops the
server, archives, and restarts it, which is always restorable at the cost of a short
outage. *Live* mode archives while running and skips files the server holds locked.

**Scheduling** — a daily restart time and a daily backup time per server.

**Tray resident** — closing the window hides to the tray and servers keep running.
Schedules keep firing while hidden. Launching ServerManager again brings the running
copy's window back rather than reporting that it is already running.

**Self-updating** — checks GitHub for new releases and installs them on your approval.

**Phone control** — an Android app to view your servers, start and stop them, read their
consoles and send commands, from anywhere over Tailscale. Off unless you turn it on.

---

## Installing

1. Install the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Download `ServerLauncher.exe` from the
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
| **Clean exit codes** | Extra exit codes that mean "stopped", not "crashed". Closing a server's own window is already recognised, so doing that never triggers a restart. |
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

The previous executable is kept beside the new one as `ServerLauncher.exe.old` until
the update starts successfully. If a build misbehaves, delete the new exe and rename that
file back.

---

## Controlling it from your phone

Off by default. The API can start, stop, restart and inspect **servers you have already
configured**, and deliberately has no way to create one or change a script path — this app
launches arbitrary scripts, so an endpoint that could set one would turn a stolen phone
token into remote code execution rather than just an unwanted restart.

### From a browser, with nothing to install

ServerManager serves a phone-sized web interface on the same address as the API. Once
remote access is on, **Settings &rarr; Remote access &rarr; Open in browser** opens it on
the server itself, and the tray menu has **Open browser interface** for when the window is
hidden. Both go to:

```
http://127.0.0.1:8787/
```

To reach it from a phone, publish that port with Tailscale Serve (below) and use the
address Tailscale prints.

Pair it with the same code the desktop shows, and it works exactly like the app: the
dashboard, start/stop/restart, consoles and commands. The browser appears in the paired
device list and is revoked the same way. On Android or iOS, *Add to home screen* gives it
an icon and its own window.

Nothing about it weakens the API. The page itself is served without a token — it has to
be, since pairing happens on it — but it holds no data, and every request behind it still
needs a device token.

### Setting it up

1. Install [Tailscale](https://tailscale.com/) on the server and your phone, signed into
   the same tailnet.
2. In **Settings → Remote access**, tick *Allow remote control* and press Save.
3. Run this once on the server, to publish the local port onto your tailnet:

   ```
   tailscale serve --bg 8787
   ```

   The Settings screen has a button that copies this command.
4. Put the address Tailscale gives you into **Address phones should connect to**.
5. Install the APK from the [latest release](https://github.com/AnthemVH/ServerManager/releases/latest)
   on your phone, press **Pair a phone** on the desktop, and scan the QR code.

The API listens on `127.0.0.1` only. Tailscale Serve is what makes it reachable, and it
terminates TLS with a real Tailscale-issued certificate, so nothing is ever exposed to the
open internet and no port is forwarded.

### How access is controlled

Security comes from a secret each install generates for itself, never from anything in this
repository — your install and anyone else's have completely independent credentials.

- Pairing needs a **single-use code** that expires in five minutes, is rate limited, and
  exists only while the pairing dialog is open.
- Device tokens are 256 bits and stored **only as hashes**; the plaintext never touches disk.
- Every device is listed in Settings and **revocable individually**, taking effect at once.
- **Sending console commands is a separate permission**, off by default, granted per device.
  It is arbitrary input to a game server, so it is not handed out at pairing.
- Every remote action is written to an audit log and appears in the desktop console.

### The Android app

Sideloaded, not on the Play Store: download the APK from the release page and install it.
It is built by CI, so nothing has to be installed on your development machine.

The app polls every few seconds rather than holding a live connection, because Android
suspends sockets as soon as an app leaves the foreground. That also means **it cannot give
you instant crash alerts** — there is no cloud service to push from. Real push would need
Firebase and a component the server talks out to, which is the hosting and trust question
Tailscale was chosen to avoid.

The released APK is **debug-signed**, which is fine for sideloading onto your own phone but
means it is marked debuggable. If you would rather it were not, add a release keystore to
the repository secrets and switch the workflow to `assembleRelease`.

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

Closing a server's own window is **not** treated as a crash. Windows ends such a process
with `STATUS_CONTROL_C_EXIT`, and reading that as a failure meant a server you had just
closed by hand restarted itself. That code and `DBG_CONTROL_BREAK` now count as a clean
stop, and any further codes your server reports on shutdown can be added per server.

### Scripts that launch and exit

Some scripts start the real server and return immediately instead of running for its
lifetime — the Arma 3 server scripts call `$p.Start()` and fall off the end of the file.

Taking the script's exit as the server's exit would dispose the job object, and
`KILL_ON_JOB_CLOSE` would then kill the server that had just started. So when a script
exits leaving processes behind, ServerManager keeps the job and supervises those instead.
The server counts as stopped when the job empties.

The decision waits a couple of seconds first: Windows leaves a console host in the job for
a moment after any script exits, and treating that straggler as a launched server would
stop crashes being detected at all.

Two things are unavailable for a server started this way, because the process that owned
the console is gone: **live console output** and **stop commands**. Stopping still
terminates everything the script launched. To get console and stdin back, have the script
wait for the server instead of exiting — in PowerShell, `$p.WaitForExit()` after starting
it.

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
  Remote/                  Device pairing, tokens, the HTTP API, audit log
src/ServerLauncher.App/    WPF interface (MVVM)
android/                   Android app (Kotlin, Jetpack Compose), built by CI
tests/                     132 tests
demo/                      A stand-in game server for trying things out
```

The tests cover the parts that are easy to get quietly wrong: real process-tree kills
verified by checking no orphan survives, restart policy and backoff, backups against
locked files, update version comparison and the rename-and-rollback swap, theme contrast,
and WPF data bindings.

> The app ships as `ServerLauncher.exe`. The project folders and namespaces keep the
> `ServerLauncher.App` / `ServerLauncher.Core` names they were created with; only the
> output binary is renamed.
