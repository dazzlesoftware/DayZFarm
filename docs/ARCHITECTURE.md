# Architecture

## Overview

```
Windows 11 Host
|
|-- Game Farm Controller (ASP.NET Core, single process)
|     |-- REST API (/api/clients/...)
|     |-- SignalR hub (/hubs/farm-status)
|     |-- Static dashboard (wwwroot)
|     |-- IVirtualMachineProvider -> exactly ONE of:
|     |       HyperVVirtualMachineProvider (PowerShell/Hyper-V module), or
|     |       VMwareVirtualMachineProvider (vmrun.exe/VMware Workstation) -- see docs/VMWARE-SETUP.md
|     |     picked by GameFarmOptions.Hypervisor; the two are mutually exclusive on one host
|     |-- SQLite (Config/dayzfarm.db) - client records
|     |-- SecretStore (DPAPI) - agent tokens, server passwords
|     |-- StatusPollingService - background poll + SignalR broadcast
|
|-- Hyper-V Virtual Switch (GameFarmSwitch), or a VMware custom network (vmnetN)
|
|-- DayZ-Master (never booted with a child attached; template only)
|-- DayZ-001 .. DayZ-NNN (differencing disks / linked clones against DayZ-Master)
      |-- Game Farm Agent (ASP.NET Core minimal API, runs as a Scheduled Task in the
      |     VM's own interactive logon session -- deliberately NOT a Windows Service; see
      |     docs/TROUBLESHOOTING.md's BattlEye section for why)
            |-- SteamManager (discovers/starts/stops Steam, launches DayZ via -applaunch)
            |-- DayZGameLauncher (IGameLauncher)
            |-- DayZGameStatusProvider (IGameStatusProvider)
            |-- ReconnectWatchdogService (auto-reconnect state machine)
```

## Design goals

- **No Hyper-V lock-in.** All VM operations go through `IVirtualMachineProvider`
  (`src/GameFarm.Core/Models/VirtualMachineModels.cs`), whose request/response models
  (`VirtualMachineCreateRequest`, `VirtualMachineInfo`) are deliberately hypervisor-neutral —
  e.g. `ParentDiskPath`/`NetworkName`, not Hyper-V-specific names like the earlier
  `ParentVhdxPath`/`VirtualSwitchName`. Hyper-V-specific code lives in `GameFarm.HyperV`; a
  second, VMware Workstation-backed implementation lives in `GameFarm.VMware`
  (`VMwareVirtualMachineProvider`, via `vmrun.exe`) — see docs/VMWARE-SETUP.md. Exactly one is
  registered at startup, picked by `GameFarmOptions.Hypervisor`; nothing above this interface
  (Controller, API, dashboard) needs to know or care which.
- **No OS lock-in for the agent protocol.** `GameFarm.Shared` defines the wire contract
  (`AgentApiRoutes`, `AgentStatusResponse`, etc.) without any Windows-specific types. The
  Windows agent implementation lives in `GameFarm.Agent`; a Linux/Proton agent would implement
  the same routes and `IGameLauncher`/`IGameStatusProvider` interfaces.
- **No credential storage in JSON.** Client records store only non-sensitive identifiers
  (Steam account label, SteamID64, VM name). Agent tokens and server passwords are encrypted at
  rest with Windows DPAPI via `SecretStore` (Controller) / per-agent DPAPI-protected token file
  (Agent). Steam passwords are never touched by this system at all — see docs/STEAM-SETUP.md.
- **Testable orchestration.** `ClientOrchestrator` — the Controller's central coordination
  point — depends only on interfaces (`IClientRepository`, `IVirtualMachineProvider`,
  `IAgentClient`, `ISecretStore`), each with a concrete production implementation
  (`ClientRepository`/SQLite, whichever `IVirtualMachineProvider` is active —
  `HyperVVirtualMachineProvider` or `VMwareVirtualMachineProvider` — `AgentHttpClient`,
  `SecretStore`/DPAPI) and an in-memory fake used by `tests/GameFarm.Tests/ClientOrchestratorTests.cs`.
  This lets the bulk of the Controller's business logic (view building, summary counts, agent
  command routing, join/update flows) be unit-tested with no SQLite, DPAPI, Hyper-V, VMware
  Workstation, or network access at all.
- **Bounded concurrency everywhere.** `ConcurrencyGates` wraps three `SemaphoreSlim`s
  (`MaxConcurrentVmOperations`, `MaxConcurrentAgentCommands`, implicitly reused for updates) so a
  50-client "Start All" cannot become an unbounded command storm. `UpdateBatchPlanner` further
  batches Steam updates (`UpdateBatchSize`) so the WAN isn't hammered by 50 simultaneous
  downloads.

## Game modules

Just as exactly one `IVirtualMachineProvider` is active per host (picked by
`GameFarmOptions.Hypervisor`), exactly one game module is active per Agent, picked by
`AgentOptions.GameId` in `src/GameFarm.Agent/Program.cs`. **DayZ is the first, reference
implementation** (`src/GameFarm.Agent/Games/DayZ/`) — nothing about the Controller, dashboard,
REST API, or VM lifecycle knows or cares which game is running; they only ever talk in terms of
`IGameLauncher`/`IGameStatusProvider`/`GameLaunchOptions`.

A game module is **compiled C# code, not configuration** — deliberately so. Two games rarely
share a launch-argument syntax, a way to detect connection state, or even a consistent process
name, so there's no generic "launch template + log pattern" data format expressive enough to
cover that without effectively becoming its own small programming language. Writing a real class
is more work up front than editing a JSON file, but it's the same tradeoff `IVirtualMachineProvider`
already makes for hypervisors, and for the same reason.

**What a game module actually is**, using DayZ's as the template:

| Piece | DayZ's implementation | Purpose |
|---|---|---|
| `IGameLauncher` | `DayZGameLauncher` | Launch/stop the game's own process (via Steam's `-applaunch`, never a bare `.exe`), report whether it's running |
| `IGameStatusProvider` | `DayZGameStatusProvider` | Detect connection state from whatever evidence exists (DayZ: RPT/ADM log tailing — inherently best-effort, since DayZ exposes no connection-state API) |
| `IGameProfile` | `DayZGameProfile` | This game's identity (currently just its Steam App ID) — kept as its own tiny interface, separate from `IGameLauncher`, specifically so `IWorkshopManager` can depend on "which game is this" without depending on the launcher itself (which, for DayZ, depends on `IWorkshopManager` right back to resolve mod paths before launch — a real DI cycle if both pointed at each other) |
| its own argument builder | `DayZLaunchArgumentBuilder` | Builds this game's actual command-line syntax (DayZ: `-connect=`/`-port=`/`-mod=`) from the generic `GameLaunchOptions` shape — not part of any interface, since a different game's argument format has no reason to look anything like DayZ's |

**Adding a second game module**: create `src/GameFarm.Agent/Games/<Name>/`, implement
`IGameLauncher` (and `IGameStatusProvider`/`IGameProfile` if the game needs them — DayZ's is a
reasonable template for what each typically ends up doing), add a case for it in `Program.cs`'s
`GameId` switch, and set `Agent:GameId` in that VM's `appsettings.json`. Everything else —
`ReconnectWatchdogService`, the Controller, the dashboard, the REST API — already only depends on
the interfaces, so none of it changes.

## Hyper-V differencing disks

This section is Hyper-V-specific; for the VMware Workstation backend's equivalent (linked
clones off a snapshotted master), see docs/VMWARE-SETUP.md.

Every client VM's virtual disk is a **Hyper-V differencing disk** (`New-VHD -Differencing`)
whose parent is `DayZ-Master.vhdx`. This means:

- Disk space for N clients is roughly `sizeof(master) + N * (delta from base)`, not `N *
  sizeof(master)`.
- **The master VHDX must never be modified once differencing children exist.** Modifying it
  invalidates every child disk's chain. If you need to update the master (e.g. a major DayZ
  patch), see docs/MASTER-IMAGE.md's "Rebuilding the master" section (or docs/VMWARE-SETUP.md's
  equivalent for the VMware backend) — clients must be recreated (or rebased with `Set-VHD
  -ParentPath`, an advanced/manual operation not automated by this version) after a master
  rebuild.
- The master VM itself is only ever booted to build/update the image; it is never booted
  alongside differencing children pointing at the same file being open for write.

## Extensibility interfaces (reserved, not fully implemented in v1)

| Interface | Purpose | Status |
|---|---|---|
| `IVirtualMachineProvider` | Hypervisor abstraction | Implemented twice: Hyper-V (`HyperVVirtualMachineProvider`) and VMware Workstation (`VMwareVirtualMachineProvider`) — see docs/VMWARE-SETUP.md |
| `IGameLauncher` | Launch/stop the active game's client | Implemented for DayZ (`DayZGameLauncher`) — the first, reference game module; see "Game modules" above |
| `IGameStatusProvider` | Detect connection state for the active game | Implemented for DayZ (`DayZGameStatusProvider`, log heuristics) — documented as best-effort |
| `IGameProfile` | Active game's identity (Steam App ID) | Implemented for DayZ (`DayZGameProfile`) |
| `IWorkshopManager` | Mod/Workshop management | Implemented (`WindowsWorkshopManager`, agent-side) — detection + missing-mod handling automated, subscription click is manual |
| `IGpuVirtualizationProvider` | GPU-P configuration | Implemented for Hyper-V (`ManualGpuVirtualizationProvider`) — detection automated, provisioning manual by design; the VMware Workstation backend uses a stub (`UnsupportedGpuVirtualizationProvider`) instead — not a gap, since VMware doesn't need a GPU-P equivalent at all (its virtual SVGA 3D device gives guests DirectX acceleration with no host GPU passthrough/partitioning; see docs/VMWARE-SETUP.md) |
| `IAgentSelfUpdater` | Applies a pushed Agent build to this VM | Implemented for Windows (`WindowsAgentSelfUpdater`) — see docs/AGENT-UPDATES.md |
| `IGameTestCommandProvider` | Future server-mod-driven test automation | Interface only |

`GameFarm.Core.Plugins.PluginService` (not part of the extensibility-interfaces table above since
it's a concrete shared class, not an interface with swappable implementations) lets an
administrator add fixed, named, JSON-defined commands the Controller (host-side) or Agent
(guest-side) can run on demand — see docs/PLUGINS.md for the full design and its safety model
(only a plugin's *name* is ever accepted over HTTP; what it runs is fixed by a file already on
disk).

## Concurrency & update batching

- `MaxConcurrentVmOperations` bounds simultaneous hypervisor invocations (Hyper-V PowerShell, or
  VMware's `vmrun.exe` — see docs/VMWARE-SETUP.md).
- `MaxConcurrentAgentCommands` bounds simultaneous HTTP calls to guest agents.
- `UpdateBatchSize` splits "Update All" into sequential batches (e.g. 5 at a time) so Steam
  doesn't try to update 50 DayZ installs over the WAN simultaneously. See
  `GameFarm.Core.UpdateBatchPlanner`.
