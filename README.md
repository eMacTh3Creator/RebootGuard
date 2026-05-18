# RebootGuard

A small Windows tool that **blocks reboot / shutdown / logoff until a password is entered**.
Intended for servers where an accidental or unattended reboot is unacceptable.

When Windows tries to end the session (Start menu restart, `shutdown` command,
Windows Update restart prompt, etc.), RebootGuard pops a password prompt:

- **Correct password** → the shutdown/reboot is allowed to proceed.
- **Wrong password or Cancel** → the shutdown/reboot is blocked and Windows shows
  *"RebootGuard is preventing shutdown."*

It runs as a hidden process with a **system-tray icon** (shield). The tray menu lets
you check status, change the password, or exit — exit and password change both
require the password.

## Build

No .NET SDK needed — uses the in-box .NET Framework compiler.

```powershell
.\build.ps1
```

Output: `dist\RebootGuard.exe`. A prebuilt copy is committed in `dist\` for convenience.

## First run / setting the password

Run `dist\RebootGuard.exe`. On first launch (no password configured yet) it asks
you to set one, then starts guarding.

To change the password later, either use the tray menu **Change password**, or run:

```powershell
.\dist\RebootGuard.exe --set-password
```

(Changing requires the current password.)

The password is stored as a **salted SHA-256 hash** at:

```
%ProgramData%\RebootGuard\config.cfg
```

The plaintext password is never written to disk.

## Run automatically at logon (recommended)

RebootGuard must run in an **interactive desktop session** (it has to show the
password prompt), so run it at user logon — not as a Windows service.

Scheduled Task that starts at logon and runs with highest privileges:

```powershell
$exe = "C:\Path\To\RebootGuard\dist\RebootGuard.exe"
$action  = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtLogOn
$set     = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries `
             -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0
Register-ScheduledTask -TaskName "RebootGuard" -Action $action `
  -Trigger $trigger -Settings $set -RunLevel Highest -Force
```

## How it works

RebootGuard owns a hidden top-level window and handles `WM_QUERYENDSESSION`.
Returning `FALSE` from that message tells Windows to abort the session end;
it also calls `ShutdownBlockReasonCreate` so Windows surfaces a clear reason and
gives the prompt time to be answered.

## Limitations (read this)

- **Forced shutdowns bypass it.** `shutdown /r /f`, `shutdown /r /t 0` with force,
  `Restart-Computer -Force`, power loss, hard reset, or a hypervisor "reset" do
  **not** honor `WM_QUERYENDSESSION`. This guards against *graceful* reboots
  (the common accidental case), not a determined admin or a power cut.
- **Must be in an interactive session.** No one logged on / session 0 = no prompt
  can be shown, so it cannot guard. Use the logon Scheduled Task above and keep a
  session active (or use autologon on a locked console) if you need 24/7 coverage.
- **Not tamper-proof.** A local admin can kill the process (`taskkill /F`),
  disable the task, or delete the config. It is an accident/oversight guard, not
  a security control against a hostile administrator.
- A logged-on user could simply enter the password — it stops *unattended/automated*
  and *accidental* reboots, gated on knowing the secret.

## Files

| Path | Purpose |
|------|---------|
| `src/RebootGuard.cs` | Single-file C# source |
| `build.ps1` | Builds the exe with in-box csc.exe |
| `dist/RebootGuard.exe` | Prebuilt binary |

## License

MIT — see `LICENSE`.
