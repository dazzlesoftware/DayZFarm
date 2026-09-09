# Install Guide

## Requirements

- Windows 11 **Pro**, **Enterprise**, or **Education** (Home does not support Hyper-V)
- Administrator access
- .NET 9 or .NET 10 SDK/runtime installed and on PATH
- PowerShell 7+ (`pwsh`) recommended; Windows PowerShell 5.1 works as a fallback
- A drive with room for `D:\DayZFarm` (adjust the path in config if you use a different drive)
- One legitimate Steam account (owning DayZ) per client VM you intend to run


- on cmd.exe run "winget install --id Microsoft.PowerShell --source winget"

## 1. Enable Hyper-V and prepare the host

From an elevated PowerShell prompt in the repo root:

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

```powershell
.\scripts\Install-Host.ps1 -RootDirectory D:\DayZFarm -VirtualSwitchName DayZFarmSwitch
```

This verifies Windows edition, PowerShell/.NET versions, enables Hyper-V if needed (reporting,
never forcing, a reboot), creates the `D:\DayZFarm\{Images,Instances,Config,Logs,Backups}`
layout, and creates the virtual switch. If Hyper-V needed enabling, **reboot manually** and
re-run the script to continue.

## 2. Build the solution

```bash
dotnet build DayZFarm.slnx
```

## 3. Configure the Controller

Copy `config/appsettings.example.json` values into
`src/DayZFarm.Controller/appsettings.json` (or set an `appsettings.Production.json` /
environment variables) — adjust `RootDirectory`, `MasterVhdx`, `VirtualSwitch` and the
concurrency/update-batch settings for your host.

## 4. Run the Controller

**Run this from an elevated (Administrator) terminal.** The Controller shells out to Hyper-V
PowerShell cmdlets to create/manage VMs, exactly like `Create-Client.ps1` does — from a
non-elevated terminal, VM creation from the dashboard fails with `New-VHD`/`New-VM: You do not
have the required permission to complete this task...`, even though the dashboard itself loads
and runs fine otherwise.

```bash
dotnet run --project src/DayZFarm.Controller
```

Browse to the URL Kestrel reports on startup (`http://localhost:5000` by default, though a
different `launchSettings.json` profile may use a different port — check the console output) for
the dashboard. **Use `dotnet run`, not the raw `.exe` under `bin\Debug\...`** — a debug build
doesn't copy `wwwroot` next to the exe, so the dashboard 404s if you run it directly; `dotnet
run` (or a proper `dotnet publish`, see below) handles this correctly.

To install it as a Windows Service instead of running interactively (services default to running
as `LocalSystem`, which already has full Hyper-V rights, so this elevation requirement doesn't
apply there):

```powershell
dotnet publish src/DayZFarm.Controller -c Release -o C:\DayZFarm\Controller
sc.exe create "DayZ Farm Controller" binPath= "C:\DayZFarm\Controller\DayZFarm.Controller.exe"
```

## 5. Create the master VM

See docs/MASTER-IMAGE.md.

## 6. Create client VMs

```powershell
.\scripts\Create-Clients.ps1 -Start 1 -Count 5 -StartVms
```

## 7. Install the guest agent in each client

See "How to install the guest agent" in the root README.md.

## 8. Log Steam into each client

See docs/STEAM-SETUP.md — this one step remains manual by design.

## Troubleshooting

See docs/TROUBLESHOOTING.md.
