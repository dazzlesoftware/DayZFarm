# DayZ Client Farm Management System

Runs multiple legitimate DayZ game clients simultaneously on Windows 11 + Hyper-V — each in its
own VM, with its own Steam account and its own Steam session — for private server QA, load
testing, and controlled client/server automation.

**This project does not, and will never, bypass Steam, bypass BattlEye, bypass licensing, or
emulate a Steam client.** Every client VM runs a real Steam client logged into a real,
legitimate Steam account that owns DayZ.

## Project structure

```
DayZFarm.slnx
src/
  DayZFarm.Shared/      Agent wire protocol (OS-agnostic)
  DayZFarm.Core/        Domain models, config, interfaces, pure logic (naming, batching, backoff)
  DayZFarm.HyperV/      IVirtualMachineProvider implementation (Hyper-V via PowerShell)
  DayZFarm.Controller/  ASP.NET Core host: REST API, SignalR, dashboard, SQLite, secrets
  DayZFarm.Agent/       Per-VM Scheduled Task (interactive session): Steam/DayZ process management, watchdog
scripts/                Install-Host, Create-Master, Create-Client(s), Create-SteamLibraryDisk, Remove-Client, Start/Stop-Client, Configure-Network
config/                 appsettings.example.json
docs/                   INSTALL, ARCHITECTURE, MASTER-IMAGE, STEAM-SETUP, TROUBLESHOOTING
tests/DayZFarm.Tests/   xUnit tests (no Hyper-V required)
```

## Requirements

- Windows 11 Pro / Enterprise / Education (Hyper-V is not available on Home)
- .NET 9 or .NET 10 SDK
- PowerShell 7+ (Windows PowerShell 5.1 works as a fallback)
- One legitimate Steam account per client, owning DayZ (App ID 221100)

## Build

```bash
dotnet build DayZFarm.slnx
dotnet test DayZFarm.slnx
```

## How to enable Hyper-V / prepare the host

```powershell
.\scripts\Install-Host.ps1 -RootDirectory D:\DayZFarm -VirtualSwitchName DayZFarmSwitch
```

Never reboots automatically — if Hyper-V needs enabling it tells you to reboot and re-run.

## Create the first master VM

```powershell
.\scripts\Create-Master.ps1 -CpuCount 4 -MemoryGB 6 -IsoPath C:\ISOs\Win11.iso
```

Then follow the manual Windows/Steam/DayZ/agent install steps in docs/MASTER-IMAGE.md. The
master's Steam is **never** logged into a personal account.

By default this also creates a second disk (`SteamLibrary-Master.vhdx`) that you install Steam +
DayZ into directly — every client then gets its own tiny differencing disk straight against that
one real file instead of a full ~40GB+ install each (see docs/MASTER-IMAGE.md). Pass
`-SkipSteamLibraryDisk` to opt out and install Steam/DayZ onto the OS disk as normal instead.

## Create DayZ-001

```powershell
.\scripts\Create-Client.ps1 -Name DayZ-001 -CpuCount 4 -MemoryGB 6 -Start
```

Or from the Controller once it's running: `POST /api/clients` with the client's name, VM name,
and target server.

## Install the guest agent

```powershell
dotnet publish src/DayZFarm.Agent -c Release -o C:\DayZFarmAgent
```

Copy the publish output into the client VM, run `DayZFarm.Agent.exe` once (generates its token),
then install it with `scripts/Install-Agent.ps1`:

```powershell
.\Install-Agent.ps1 -UserName Dayz-Master
```

This registers the agent as a **Scheduled Task** that runs directly in that account's
interactive logon session (and configures Windows auto-logon for it, so the VM boots straight
into that session) — **not** a LocalSystem Windows Service. That distinction matters: BattlEye
was found to consistently kick a session launched via a Session-0 service's
CreateProcessAsUser-based workaround ("Bad Packet"/"Game restart required"), since its
anti-tamper checks distrust a game process descending from a privileged service using a
duplicated security token — the same pattern real cheat-injection tooling uses. Running the
agent as a normal interactive process avoids that pattern entirely; see
docs/TROUBLESHOOTING.md. `-UserName` should be the same Windows account you log Steam into for
this VM (see docs/STEAM-SETUP.md below) — the whole point is for the agent to run in that same
session.

Do this once in the master image before creating client differencing disks, so every client
inherits the agent already installed.

## Log Steam into a client VM

Manual, once per VM — see docs/STEAM-SETUP.md. Steam Guard cannot be automated without either
storing credentials insecurely or bypassing Steam, both of which are out of scope.

## Start the controller

```bash
dotnet run --project src/DayZFarm.Controller
```

Dashboard: the URL Kestrel reports on startup (typically `http://localhost:5000`).

## Configure the DayZ server target

Either per-client via `POST /api/clients` (`serverAddress`/`serverPort`), or edit an existing
client's record. The controller builds `-connect=<address> -port=<port>` (and `-password=` if a
secret is configured) via `DayZLaunchArgumentBuilder` and sends it to that client's agent, which
launches DayZ through Steam's own `-applaunch 221100 ...` mechanism.

## Dashboard usage

- **+ Create Client** / **+ Create Clients (Range)**: create one client, or a numbered range
  (`DayZ-001`, `DayZ-002`, ...) in one form — the dashboard equivalent of
  `scripts/Create-Client(s).ps1`. Each still needs its Steam account logged in manually
  afterward (docs/STEAM-SETUP.md).
- Row actions: Start/Stop/Restart VM, Start/Stop/Restart DayZ/Steam, Join, Update, Details,
  **Register Token** (paste the agent's DPAPI-protected token after its first run in that VM),
  **Delete** (permanently removes the VM, disk, and client record — confirmation required).
- **Agent column** distinguishes three states that all otherwise look like "not responding":
  **Off** (VM isn't running), **Awaiting Token** (VM running, agent never registered — the
  expected state for a freshly created client until you hit Register Token), and
  **Unreachable** (token registered but the agent still isn't answering — a real problem; see
  docs/TROUBLESHOOTING.md).
- Bulk actions (select rows first): Start/Stop/Join/Restart DayZ/Update/Reboot Selected.
- Global actions (confirmation required): Start/Stop/Join/Disconnect/Update/Restart DayZ All.
- Client detail page (`/detail.html?id=<id>`): full status fields, all per-client actions, an
  **Edit Client** form (Steam account label, target server, RequiredMods, auto-start/reconnect/
  update flags), a **Mods** panel, and a **Logs** panel (DayZ log tail + action log tail).
- Live updates arrive via SignalR (`/hubs/farm-status`) — no manual refresh needed.

## Troubleshooting

- Disconnected agent → docs/TROUBLESHOOTING.md#a-client-shows-agent-offline
- Steam issues → docs/STEAM-SETUP.md, docs/TROUBLESHOOTING.md
- DayZ issues → docs/TROUBLESHOOTING.md#dayz-wont-launch--immediately-exits
- Reset/rebuild a single VM → docs/TROUBLESHOOTING.md#resettingrebuilding-a-single-vm
- Rebuild the master image → docs/MASTER-IMAGE.md

## Logging

- **Controller**: structured, rolling Serilog file logs under
  `D:\DayZFarm\Logs\Controller\controller-<date>.log` (30-day retention), plus console output
  and ASP.NET Core request logging.
- **Per-client action log**: `D:\DayZFarm\Logs\Clients\<name>\actions-<date>.log` — every VM
  action, agent command, join/update attempt, and their result, written by
  `ClientActionLogger`/`ClientOrchestrator`. Never contains secrets.
- **Agent**: its own local rolling log inside the guest, `C:\DayZFarmAgent\Logs\agent-<date>.log`
  (14-day retention) — DayZ launches/exits, Steam actions, watchdog decisions, errors.
- **`GET /api/clients/{id}/logs`** merges the client's action-log tail with the agent's live
  DayZ RPT/ADM log tail (proxied through the agent) into one response for the dashboard's Logs
  button / detail page.

## Workshop / mods

`IWorkshopManager` is implemented (`WindowsWorkshopManager`, agent-side): `GET
/api/agent/mods` reports installed Workshop content by scanning Steam's own
`steamapps/workshop/content/221100/` folder (no private API); `POST /api/agent/mods/ensure`
checks a client's `RequiredMods` against what's installed and, for anything missing, opens that
mod's Workshop page in Steam so an administrator can click **Subscribe** once per VM — mirroring
the Steam Guard login step. Programmatic subscription would require either the Steamworks API
(needs a running game session + API key) or an unattended steamcmd login (credential storage),
both out of scope.

## GPU virtualization

**GPU-P is required, not optional** — DayZ's Enfusion engine needs a real DirectX 11 GPU to
launch at all; the default Hyper-V synthetic display adapter has no 3D acceleration, so DayZ
fails outright without GPU-P (or Discrete Device Assignment). See docs/MASTER-IMAGE.md's GPU-P
section.

`IGpuVirtualizationProvider` is implemented (`ManualGpuVirtualizationProvider`, host-side):
`IsConfiguredAsync` queries `Get-VMGpuPartitionAdapter` to report whether a VM already has a GPU
partition. Actually provisioning one through this generic interface (`ConfigureAsync`) throws a
documented `NotSupportedException` — the exact driver package path under GPU-P varies per host
GPU model/driver, and guessing wrong can leave a VM's display adapter broken. For an **NVIDIA**
host, `scripts/Tools/Enable-GpuPartitionForVMs.ps1` is a tested, working automated alternative:
it loops over every VM (or specific ones), assigns a GPU-P adapter, configures the required VM
settings, and copies the host's NVIDIA driver payload directly into each guest's VHDX offline —
see docs/MASTER-IMAGE.md's GPU-P section for usage and partition-sizing guidance.

## Known limitations (v1)

- **Steam login is manual** per VM (Steam Guard cannot be safely automated).
- **Workshop mod *subscription* is manual** per VM (one click per mod, once) — installed-mod
  *detection* and the *ensure* check are automated; see "Workshop / mods" above.
- **DayZ connection-state detection is heuristic** (log-file scanning via
  `IDayZStatusProvider`/`DayZLogStatusProvider`) — DayZ has no supported state API. Isolated
  behind an interface so it can be improved without touching callers.
- **GPU-P provisioning through `IGpuVirtualizationProvider` itself is manual/out of scope for
  v1** — detection is automated; an NVIDIA-specific automated tool exists separately (see "GPU
  virtualization" above) since GPU-P turns out to be required, not optional, for DayZ to run at
  all in a Hyper-V VM.
- **Master rebuild requires recreating client differencing disks** — there is no automated
  parent-disk rebase; see docs/MASTER-IMAGE.md.
- **Future gameplay test-automation hook** (`IGameTestCommandProvider`) is an interface only —
  no in-game command execution ships in v1, and any future implementation must only use
  legitimate mod/API integration, never memory manipulation or anti-cheat bypasses.
- **Client detail page** covers the required fields/actions but is intentionally minimal.

The client detail page (`/detail.html?id=<id>`) now shows a **Mods** panel (RequiredMods vs.
installed, with an "Ensure Mods" button that triggers the agent's missing-mod Workshop-page
flow) and a **Logs** panel (DayZ RPT/ADM tail + the controller-side action log tail, with a
Refresh button), both backed by `GET /api/clients/{id}/mods`, `POST
/api/clients/{id}/mods/ensure`, and the merged `GET /api/clients/{id}/logs`.

## Recommended next implementation step

Replace the log-file heuristic in `DayZLogStatusProvider` with a small in-game reporting mod
once one exists — which would also be the natural home for a first real
`IGameTestCommandProvider` implementation.
