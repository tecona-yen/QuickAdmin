# QuickAdmin

QuickAdmin is a lightweight mobile-first server management console for Windows Server 2022.
It ships as two services:

- **QuickAdmin Agent**: privileged worker + local IPC HTTP listener (`127.0.0.1:8610`)
- **QuickAdmin Web**: HTTP-only web UI and session/auth layer on **port 8600**

## Features

- HTTP-only console at `http://localhost:8600`
- Password-only login form (default password: `root`)
- Password stored securely using PBKDF2 hash + salt (no plaintext)
- Mobile-first tabbed UI:
  - Quick Management (shutdown/restart, WOL, adapters, custom commands)
  - Performance (1s live sparklines + process list)
  - Web Server Management (IIS status/control + binding command)
  - CMD terminal (streamed output, persistent per-session shell process)
  - PowerShell 7 terminal (streamed output)
- CSRF protection for state-changing requests
- Session cookie security (`HttpOnly`, `SameSite=Strict`, idle timeout)
- Brute-force lockout delay after repeated failed logins
- Audit logging for critical actions
- Localhost-only by default, with explicit LAN bind setting

## Prerequisites

- Windows Server 2022
- PowerShell 7 (`pwsh`) installed
- .NET 8 SDK/runtime
- Administrator privileges for service installation and privileged actions

## Install

From elevated PowerShell in repository root:

```powershell
./scripts/install.ps1
```

This publishes both projects to `C:\Program Files\QuickAdmin`, creates services:

- `QuickAdminAgent`
- `QuickAdminWeb`

and starts them.

## Service control

```powershell
./scripts/service.ps1 -Action status
./scripts/service.ps1 -Action restart
```

## Login

Open:

- `http://localhost:8600`

Default password:

- `root`

## Change password

```powershell
./scripts/set-password.ps1 -Password "YourNewStrongPassword"
./scripts/service.ps1 -Action restart
```

Or from UI Quick Management → Settings.

## Enable LAN access

Default is localhost bind only.

To allow LAN clients:

```powershell
./scripts/set-bind.ps1 -Mode lan
./scripts/service.ps1 -Action restart
```

To switch back:

```powershell
./scripts/set-bind.ps1 -Mode localhost
./scripts/service.ps1 -Action restart
```

## Config file

Default config is in `config/quickadmin.json` and copied to service directories.

Contains:

- `auth.passwordHash`
- `auth.passwordSalt`
- `server.bindAddress`
- `server.port` (fixed at 8600)
- `sessionTimeoutMinutes`
- `customCommands`
- `wolTargets`

## Troubleshooting

### Port 8600 unavailable

Check listener:

```powershell
netstat -ano | findstr :8600
```

If occupied, stop conflicting process or service.

### Firewall access (LAN mode)

```powershell
New-NetFirewallRule -DisplayName "QuickAdmin 8600" -Direction Inbound -Protocol TCP -LocalPort 8600 -Action Allow
```

### Agent unreachable

- Ensure `QuickAdminAgent` is running.
- Check `C:\Program Files\QuickAdmin\agent\logs\agent.log`.

### IIS actions fail

- Verify IIS is installed and `W3SVC` exists.
- Run services as LocalSystem or admin account.

## Security notes

- This tool is intentionally HTTP-only as requested; deploy on trusted management networks.
- Keep localhost-only mode for maximum safety.
- Restrict filesystem ACLs on `C:\Program Files\QuickAdmin\*\config\quickadmin.json`.
