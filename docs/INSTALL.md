# Install Guide

This project's hypervisor backend is pluggable — **Hyper-V** or **VMware Workstation** (see
README.md, docs/VMWARE-SETUP.md). Pick exactly one per host; they're mutually exclusive in
practice for full native virtualization performance. This guide's numbered steps default to
Hyper-V; each step that differs for VMware Workstation has a callout showing the alternative, and
steps with no callout are identical either way.

## Requirements

- Windows 11 — only **Pro**, **Enterprise**, or **Education** if using **Hyper-V** (Home does not
  support Hyper-V at all). **VMware Workstation** has no such edition restriction and runs on
  Home too.
- Administrator access (Hyper-V only — see step 4's note; VMware Workstation doesn't require an
  elevated Controller process)
- .NET 9 or .NET 10 SDK/runtime installed and on PATH
- PowerShell 7+ (`pwsh`) recommended; Windows PowerShell 5.1 works as a fallback
- A drive with room for `D:\GameFarm` (adjust the path in config if you use a different drive)
- One legitimate Steam account (owning DayZ) per client VM you intend to run
- **VMware Workstation only:** VMware Workstation Pro or Player installed separately (not
  included) — see docs/VMWARE-SETUP.md.


- on cmd.exe run "winget install --id Microsoft.PowerShell --source winget"

## 1. Enable Hyper-V and prepare the host

From an elevated PowerShell prompt in the repo root:

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

```powershell
.\scripts\Hyper-V\Install-Host.ps1 -RootDirectory D:\GameFarm -VirtualSwitchName GameFarmSwitch
```

This verifies Windows edition, PowerShell/.NET versions, enables Hyper-V if needed (reporting,
never forcing, a reboot), creates the `D:\GameFarm\{Images,Instances,Config,Logs,Backups}`
layout, and creates the virtual switch. If Hyper-V needed enabling, **reboot manually** and
re-run the script to continue.

> **VMware Workstation:** skip Hyper-V entirely.
> ```powershell
> .\scripts\VMware\Install-Host.ps1 -RootDirectory D:\GameFarm
> ```
> Checks `vmrun.exe` is installed, warns if Hyper-V is still enabled on this host, and creates
> the same directory layout minus `Images` (the VMware master `.vmx` lives wherever VMware
> Workstation's own wizard put it, not under `RootDirectory`). See docs/VMWARE-SETUP.md.

## 2. Build the solution

```bash
dotnet build GameFarm.slnx
```

## 3. Configure the Controller

Copy `config/appsettings.example.json` values into
`src/GameFarm.Controller/appsettings.json` (or set an `appsettings.Production.json` /
environment variables) — adjust `RootDirectory`, `MasterVhdx`, `VirtualSwitch` and the
concurrency/update-batch settings for your host.

> **VMware Workstation:** instead set `"Hypervisor": "VMwareWorkstation"`, `MasterVmx`,
> `MasterVmxSnapshot`, `VMwareNetworkName`, and `VmrunPath` — `MasterVhdx`/`VirtualSwitch` are
> then ignored. See `config/appsettings.example.json` and docs/VMWARE-SETUP.md.

## 4. Run the Controller

**Run this from an elevated (Administrator) terminal — Hyper-V only.** The Controller shells out
to Hyper-V PowerShell cmdlets to create/manage VMs, exactly like `scripts/Hyper-V/Create-Client.ps1`
does — from a non-elevated terminal, VM creation from the dashboard fails with `New-VHD`/`New-VM:
You do not have the required permission to complete this task...`, even though the dashboard
itself loads and runs fine otherwise.

> **VMware Workstation:** no elevation requirement here — `vmrun.exe` doesn't need Administrator
> rights for normal VM lifecycle operations, so an ordinary (non-elevated) terminal works fine.

```bash
dotnet run --project src/GameFarm.Controller
```

Browse to the URL Kestrel reports on startup (`http://localhost:5000` by default, though a
different `launchSettings.json` profile may use a different port — check the console output) for
the dashboard. **Use `dotnet run`, not the raw `.exe` under `bin\Debug\...`** — a debug build
doesn't copy `wwwroot` next to the exe, so the dashboard 404s if you run it directly; `dotnet
run` (or a proper `dotnet publish`, see below) handles this correctly. Identical for both
hypervisors.

To install it as a Windows Service instead of running interactively (services default to running
as `LocalSystem`, which already has full Hyper-V rights, so this elevation requirement doesn't
apply there — and is a non-issue for VMware either way, per the note above):

```powershell
dotnet publish src/GameFarm.Controller -c Release -o C:\GameFarm\Controller
sc.exe create "Game Farm Controller" binPath= "C:\GameFarm\Controller\GameFarm.Controller.exe"
```

## 5. Create the master VM

See docs/MASTER-IMAGE.md.

> **VMware Workstation:** see docs/VMWARE-SETUP.md's "Build the master VM" instead — there's no
> scripted equivalent to this step specifically, since `vmrun` has no "create a new VM" command;
> the master is always built through VMware Workstation's own **File → New Virtual Machine**
> wizard, then snapshotted with `scripts\VMware\New-MasterSnapshot.ps1`.

## 6. Create client VMs

```powershell
.\scripts\Hyper-V\Create-Clients.ps1 -Start 1 -Count 5 -StartVms
```

> **VMware Workstation:**
> ```powershell
> .\scripts\VMware\Create-Clients.ps1 -Start 1 -Count 5 -MasterVmx "D:\path\to\Master.vmx" -StartVms
> ```
> Or from the dashboard's Create Clients (Range) form once the Controller is running — same form
> either way. See docs/VMWARE-SETUP.md.

## 7. Install the guest agent in each client

See "How to install the guest agent" in the root README.md. Identical regardless of
hypervisor — the Agent runs inside the guest OS and doesn't know or care which one created it.

## 8. Log Steam into each client

See docs/STEAM-SETUP.md — this one step remains manual by design, and is also identical
regardless of hypervisor.

## Troubleshooting

See docs/TROUBLESHOOTING.md.
