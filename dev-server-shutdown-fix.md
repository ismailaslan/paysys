# Dev server shutdown — root cause and fix

## Problem
Stopping background `dotnet run` dev servers left orphaned processes holding
ports/file locks, recurring almost every session.

## Root cause (confirmed empirically, 2026-09-12)
Launching `dotnet run` via the Bash tool's `run_in_background` interposes
2-3 nested Git-Bash (MSYS2) `bash.exe` processes between the harness and the
real Windows process tree:

```
claude.exe → bash.exe → bash.exe → bash.exe → dotnet.exe → Paysys.Api.exe
```

Git Bash/MSYS2 does not use Windows Job Objects to manage its children. When
the tracked bash PID is killed, that termination never propagates down
through `dotnet.exe` to the actual apphost `.exe` — it's orphaned. This is
structural, not flaky: it happens every time a dev server is launched this
way.

## The fix — launch differently, not just detect-and-kill after
- **Paysys.Api** (has a native apphost): `dotnet build`, then launch
  `bin/Debug/net10.0/Paysys.Api.exe` directly via PowerShell
  `Start-Process -PassThru` (set `$env:ASPNETCORE_URLS` /
  `$env:ASPNETCORE_ENVIRONMENT` first, since bypassing `dotnet run` skips
  `launchSettings.json`). Single process, zero orphan risk. Stop with plain
  `Stop-Process -Id <pid> -Force`.
- **Paysys.Web** (Blazor WASM, no apphost `.exe`): launch via PowerShell
  `Start-Process -FilePath dotnet -ArgumentList "run","--no-build","--launch-profile","http" -PassThru`.
  Still a `dotnet.exe` parent → `dotnet.exe` child tree, but verified that
  plain `Stop-Process -Id <topPid> -Force` on the top-level PID correctly
  cascades and kills the child too — as long as Git Bash isn't in the
  ancestor chain.
- **Universal fallback**: `taskkill /F /T /PID <pid>` — the `/T` flag walks
  and kills the entire live descendant tree from any point, regardless of
  Job Objects. Verified it works even through the Git-Bash layers.
- Capture the launched PID (temp file or session state) so it can be
  retrieved and stopped later in a different tool call.
- **Never use the Bash tool's `run_in_background` to start `dotnet run` /
  dev servers going forward** — that's the actual leak mechanism. Foreground
  Bash commands (curl polling, one-shot builds) are unaffected.

## Why this matters
Recurred almost every session. This replaces "verify the port is free after
stopping" (a mitigation habit) with an actual fix at the source. A
`Get-NetTCPConnection` sanity check after stopping is still fine, but it
should no longer find anything if dev servers are launched this way.
