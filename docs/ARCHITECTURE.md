# Architecture

## Overview

```
Windows 11 Host
|
|-- DayZ Farm Controller (ASP.NET Core, single process)
|     |-- REST API (/api/clients/...)
|     |-- SignalR hub (/hubs/farm-status)
|     |-- Static dashboard (wwwroot)
|     |-- IVirtualMachineProvider -> HyperVVirtualMachineProvider (PowerShell/Hyper-V module)
|     |-- SQLite (Config/dayzfarm.db) - client records
|     |-- SecretStore (DPAPI) - agent tokens, server passwords
|     |-- StatusPollingService - background poll + SignalR broadcast
|
|-- Hyper-V Virtual Switch (DayZFarmSwitch)
|
|-- DayZ-Master (never booted with a differencing child attached; template only)
|-- DayZ-001 .. DayZ-NNN (differencing disks against DayZ-Master)
      |-- DayZ Farm Agent (ASP.NET Core minimal API, runs as a Scheduled Task in the
      |     VM's own interactive logon session -- deliberately NOT a Windows Service; see
      |     docs/TROUBLESHOOTING.md's BattlEye section for why)
            |-- SteamManager (discovers/starts/stops Steam, launches DayZ via -applaunch)
            |-- WindowsDayZLauncher (IDayZLauncher)
            |-- DayZLogStatusProvider (IDayZStatusProvider)
            |-- ReconnectWatchdogService (auto-reconnect state machine)
```

## Design goals

- **No Hyper-V lock-in.** All VM operations go through `IVirtualMachineProvider`
  (`src/DayZFarm.Core/Models/VirtualMachineModels.cs`). The only Hyper-V-specific code lives in
  `DayZFarm.HyperV`; a `VMwareVirtualMachineProvider` (or other backend) can be added later
  without touching the Controller, API, or dashboard.
- **No OS lock-in for the agent protocol.** `DayZFarm.Shared` defines the wire contract
  (`AgentApiRoutes`, `AgentStatusResponse`, etc.) without any Windows-specific types. The
  Windows agent implementation lives in `DayZFarm.Agent`; a Linux/Proton agent would implement
  the same routes and `IDayZLauncher`/`IDayZStatusProvider` interfaces.
- **No credential storage in JSON.** Client records store only non-sensitive identifiers
  (Steam account label, SteamID64, VM name). Agent tokens and server passwords are encrypted at
  rest with Windows DPAPI via `SecretStore` (Controller) / per-agent DPAPI-protected token file
  (Agent). Steam passwords are never touched by this system at all — see docs/STEAM-SETUP.md.
- **Testable orchestration.** `ClientOrchestrator` — the Controller's central coordination
  point — depends only on interfaces (`IClientRepository`, `IVirtualMachineProvider`,
  `IAgentClient`, `ISecretStore`), each with a concrete production implementation
  (`ClientRepository`/SQLite, `HyperVVirtualMachineProvider`, `AgentHttpClient`,
  `SecretStore`/DPAPI) and an in-memory fake used by `tests/DayZFarm.Tests/ClientOrchestratorTests.cs`.
  This lets the bulk of the Controller's business logic (view building, summary counts, agent
  command routing, join/update flows) be unit-tested with no SQLite, DPAPI, Hyper-V, or network
  access at all.
- **Bounded concurrency everywhere.** `ConcurrencyGates` wraps three `SemaphoreSlim`s
  (`MaxConcurrentVmOperations`, `MaxConcurrentAgentCommands`, implicitly reused for updates) so a
  50-client "Start All" cannot become an unbounded command storm. `UpdateBatchPlanner` further
  batches Steam updates (`UpdateBatchSize`) so the WAN isn't hammered by 50 simultaneous
  downloads.

## Hyper-V differencing disks

Every client VM's virtual disk is a **Hyper-V differencing disk** (`New-VHD -Differencing`)
whose parent is `DayZ-Master.vhdx`. This means:

- Disk space for N clients is roughly `sizeof(master) + N * (delta from base)`, not `N *
  sizeof(master)`.
- **The master VHDX must never be modified once differencing children exist.** Modifying it
  invalidates every child disk's chain. If you need to update the master (e.g. a major DayZ
  patch), see docs/MASTER-IMAGE.md's "Rebuilding the master" section — clients must be recreated
  (or rebased with `Set-VHD -ParentPath`, an advanced/manual operation not automated by this
  version) after a master rebuild.
- The master VM itself is only ever booted to build/update the image; it is never booted
  alongside differencing children pointing at the same file being open for write.

## Extensibility interfaces (reserved, not fully implemented in v1)

| Interface | Purpose | Status |
|---|---|---|
| `IVirtualMachineProvider` | Hypervisor abstraction | Implemented (Hyper-V) |
| `IDayZLauncher` | Launch/stop the game client | Implemented (Windows/Steam) |
| `IDayZStatusProvider` | Detect connection state | Implemented (log heuristics) — documented as best-effort |
| `IWorkshopManager` | Mod/Workshop management | Implemented (`WindowsWorkshopManager`, agent-side) — detection + missing-mod handling automated, subscription click is manual |
| `IGpuVirtualizationProvider` | GPU-P configuration | Implemented (`ManualGpuVirtualizationProvider`) — detection automated, provisioning manual by design |
| `IGameTestCommandProvider` | Future server-mod-driven test automation | Interface only |

## Concurrency & update batching

- `MaxConcurrentVmOperations` bounds simultaneous Hyper-V PowerShell invocations.
- `MaxConcurrentAgentCommands` bounds simultaneous HTTP calls to guest agents.
- `UpdateBatchSize` splits "Update All" into sequential batches (e.g. 5 at a time) so Steam
  doesn't try to update 50 DayZ installs over the WAN simultaneously. See
  `DayZFarm.Core.UpdateBatchPlanner`.
