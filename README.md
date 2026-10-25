# AutoStart

Windows tray application that runs a selected order-service executable in timed cycles.

## Background windows

Leave **Keep target in background** enabled (existing `MinimizeTarget` settings remain compatible).
AutoStart watches top-level windows belonging to the exact selected executable in the current
Windows session. Initial windows, delayed popups and later restores are minimized without
activation. The watcher runs for the entire session, including when the target was already running.
It never closes a popup to hide it and never matches an unrelated application by its title alone.
Turn the option off in Settings when you need to interact with the target window.

Windows delivers window events asynchronously: a third-party application that deliberately restores
itself may still appear briefly before its event is delivered. This is background-window management,
not a modification of the order application. A target that launches a differently named executable
or runs at a higher privilege level needs separate verification; select its actual running EXE.

## Updates and recovery

Auto-update checks the latest stable release after one minute and then every six hours. Toggling
auto-update takes effect without restarting AutoStart. Manual checking remains available in the tray.
Only `AutoStart.zip` or `AutoStart.exe` from this repository's release is accepted. GitHub's SHA-256
asset digest, executable format and file version are checked before the running application exits.
The ZIP must contain a single root `AutoStart.exe`.

A hidden native installer waits for the old process to exit, preserves `AutoStart_backup.exe`,
replaces the executable, verifies the copied bytes, and restarts with `--updated`. Copy/restart
failures attempt to restore and restart the previous binary; diagnostics are written beside the EXE
as `AutoStart.exe.update-log.txt`. A crash occurring after the new process has successfully started
is not automatically detected. Keep a manually accessible previous release for that case.
The installation directory must be writable by the current user; no elevation or policy changes
are performed. Configuration stays in `%ProgramData%\AutoStart\config.json`.

**Upgrading from 1.0.18:** the old updater can fail to exit its message loop and can lose a staged
file after a failed copy. A successful version check alone does not prove installation works.
If it remains on 1.0.18, exit AutoStart from its tray menu and replace the executable manually with
the approved new build, then reopen it. Preserve the configuration and previous EXE. Do not run
from inside a ZIP or rename the executable to `AutoStart_update_helper.exe`.

## Build and test

Requires Windows and the .NET 8 SDK:

```powershell
dotnet run --project tests/AutoStart.Tests.csproj -c Release
dotnet publish src/AutoStart.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish
```

Tests cover process identity, configuration bounds, release selection, digest/ZIP validation,
successful installation and failed-restart rollback. Native window tests use a new non-input
desktop and synthetic forms; they do not open the actual order program, register startup,
change real configuration, or stop an installed AutoStart instance.

The build workflow runs for pull requests. Published release tags must match the version in
`src/AutoStart.csproj`; changing source or opening a PR does not publish an update to existing users.
