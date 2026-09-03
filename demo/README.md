# Demo scripts

`demo-server.bat` is a stand-in game server for trying the launcher out. It prints
startup lines, echoes console commands, and shuts down cleanly when it receives `stop`.

Useful things to try with it:

- **Console** — type `players` and press Enter; the reply appears in the console.
- **Graceful stop** — press Stop and watch it save and exit rather than being killed.
- **Crash recovery** — set the restart policy to *On crash*, then end the `cmd.exe`
  process from Task Manager. The launcher notices and restarts it after a backoff.
- **Monitoring** — the CPU and memory figures cover the whole process tree.
