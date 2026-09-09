# Master Image Workflow

The master VM (`DayZ-Master`) is the parent of every client's differencing disk. It is built
**once**, then only booted again for maintenance/rebuilds — never alongside its differencing
children.

## Steam Library disk

Rather than installing Steam + DayZ (~40GB+) onto every client's OS disk from scratch, the
master gets a **second disk** — `SteamLibrary-Master.vhdx` — that you install Steam + DayZ into
directly, exactly like the OS disk. Every client then gets its **own tiny differencing disk
directly against that one real file** (never a copy, never the same file literally re-attached
to two VMs at once): the shared, unchanged data is read straight from
`SteamLibrary-Master.vhdx`, and each client's differencing child only ever stores its own deltas.
This is genuinely safe for many clients running **simultaneously**, and this disk is created
automatically by `Create-Master.ps1` below — there's no separate template-building step needed.

```powershell
.\scripts\Create-Master.ps1 -CpuCount 4 -MemoryGB 6 -SizeGB 80 -VirtualSwitchName DayZFarmSwitch -IsoPath D:\Windows.iso
```

This creates a Generation 2 VM with Secure Boot and the standard integration services enabled,
a blank NTFS-formatted `SteamLibrary-Master.vhdx` attached as SCSI 0:1, attaches the Windows ISO
if supplied, and reports the manual next steps. Pass `-SkipSteamLibraryDisk` to opt out
entirely — Steam/DayZ then just install onto the OS disk as normal, for both the master and
every client. (If you want to build the Steam Library disk separately instead — e.g. keeping the
master a pure OS image and populating the library via some other temporary VM — pass
`-SkipSteamLibraryDisk` here and use `scripts\Create-SteamLibraryDisk.ps1` on its own; every
`Create-Client.ps1` run auto-detects the conventional path either way.)

## Manual steps (cannot be automated)

1. **Start the VM and install Windows.** Use a local account or your own Microsoft account —
   this is the base OS, not tied to any Steam account.
2. **Install all Windows updates.** Reboot as required.
3. **Install Steam.** Download from https://store.steampowered.com and install normally. **Do
   not log in yet.** If using the Steam Library disk, in Steam go to Settings → Storage, add a
   Library Folder on the second disk's drive letter, and set it as the default — then install
   DayZ (App ID 221100) so it lands there instead of the OS disk.
4. **Set BattlEye's service to auto-start.** DayZ installs `BEService` (BattlEye's persistent
   Windows service, distinct from its per-session game client) set to **Manual** start and
   **not started** by default — it stays that way on a freshly installed/cloned VM until
   something starts it at least once. Every client differencing off this master inherits
   whatever state `BEService` is in here, so fix it once, in the master:
   ```powershell
   Set-Service BEService -StartupType Automatic
   Start-Service BEService
   ```
   If this is skipped, DayZ can still connect to a server, but BattlEye's client component
   never properly attaches (no `BEClient_x64_<date>.log` gets written under
   `<DayZ install>\battleye\` — check for that file as confirmation it's working), and the
   server eventually kicks with "BattlEye: Game restart required" partway into every session.
   See docs/TROUBLESHOOTING.md.
5. **Install DayZ Farm Agent.**
   - `dotnet publish src/DayZFarm.Agent -c Release -o C:\DayZFarmAgent` (run on the host, then
     copy the output into the VM, e.g. via a shared folder or a temporary network share).
   - Inside the VM: `C:\DayZFarmAgent\DayZFarm.Agent.exe` once to generate its token file
     (`%ProgramData%\DayZFarmAgent\agent-token.secret`), then install with
     `scripts\Install-Agent.ps1` (copy it into the VM too, or run it over a mapped drive):
     ```powershell
     .\Install-Agent.ps1 -UserName <the account created in step 1>
     ```
     This registers the agent as a **Scheduled Task** running directly in that account's
     interactive logon session, and configures Windows auto-logon for it — deliberately **not**
     a LocalSystem Windows Service; see docs/TROUBLESHOOTING.md for why (BattlEye rejects
     sessions launched via a Session-0 service's token-duplication workaround). Since every
     client differences off this master's OS disk, they all share this same Windows account —
     each client then only needs its own distinct Steam account logged into it (see
     docs/STEAM-SETUP.md), never a different Windows account.
6. **Configure low graphics settings** (see "Graphics" below) so the master's default DayZ
   config file is already reasonable for every client.
7. **Shut down the master VM.**
8. **Protect the master disks.** Mark both `DayZ-Master.vhdx` and (if used) `SteamLibrary-
   Master.vhdx` read-only at the filesystem level (`attrib +r`) as a safety net, and never boot
   `DayZ-Master` again except for a deliberate rebuild (see below). Differencing children break
   if either parent's on-disk bytes change.
9. **Create differencing clients** — see the root README / docs/INSTALL.md.
   `Create-Client.ps1`/`Create-Clients.ps1` auto-detect `SteamLibrary-Master.vhdx` at its
   conventional path and give each client its own differencing disk against it with no further
   setup; pass `-SkipSteamLibraryDisk` to opt a given client out.

> The Steam login must **not** happen in the master image. Each client VM gets its own Steam
> account, logged in individually after that client VM is created — see docs/STEAM-SETUP.md.

## Graphics recommendations (master + every client)

DayZ still needs a functional graphics device even when nobody is watching the screen. In the
master's DayZ video options, before shutting down:

- Resolution: 800x600 or the lowest supported
- Display mode: Windowed
- Overall graphics quality: Low / Very Low
- Shadows: Off / Low
- Post-processing: Off
- Anti-aliasing: Off
- VSync: Off
- FPS cap (`-maxFPS` launch parameter or in-game limiter, e.g. 30): reduces host CPU/GPU load
  across many simultaneous clients

**GPU virtualization (GPU-P) is required, not optional.** DayZ's Enfusion engine needs a real
DirectX 11-capable GPU to even launch — the default Hyper-V synthetic display adapter (WDDM) has
no 3D acceleration at all, so DayZ fails immediately with an *"Error creating enfusion engine"*
dialog (GPU not supported / drivers not up to date / DirectX not up to date) on any VM without
GPU-P or Discrete Device Assignment. This corrects earlier guidance in this doc that implied the
default adapter was sufficient for low-graphics clients — confirmed not to be the case.

`IGpuVirtualizationProvider` (`DayZFarm.Core`) still only automates *detection*
(`Get-VMGpuPartitionAdapter`) — actually provisioning GPU-P depends on host GPU vendor/driver
specifics that vary too much for a generic implementation to guess safely, so `ConfigureAsync`
still throws `NotSupportedException` there. For an **NVIDIA** host, though, a tested, working
automated tool now exists:

```powershell
.\scripts\Tools\Enable-GpuPartitionForVMs.ps1
```

Run on the Hyper-V host (not inside a VM), it loops over every current VM by default (or
`-VMName "DayZ-001","DayZ-002"` for specific ones) and, per VM: disables Dynamic Memory and
Automatic Checkpoints (both required for GPU-P), assigns a GPU-P adapter from the host's NVIDIA
GPU, sets the required MMIO space, and copies the host's NVIDIA driver payload directly into the
guest's VHDX offline (no matching driver install needed inside the guest first). See the
script's own help (`Get-Help .\scripts\Tools\Enable-GpuPartitionForVMs.ps1 -Full`) for sizing a
GPU partition down when sharing one GPU across many concurrent clients
(`-OptimalPartitionVRAM`/`-OptimalPartitionCompute`), and re-run it (optionally with `-OnlyGpuP
-SkipGpuPAssignment` to just refresh drivers) after any host NVIDIA driver update.

> **The Automatic Checkpoints trap:** Hyper-V cannot checkpoint a VM with a GPU partition
> attached at all (neither standard nor production checkpoints), but **Automatic Checkpoints**
> is enabled by default and silently tries to create one on every start — which then fails the
> VM with *"Checkpoint operation failed... Production checkpoints cannot be created... assigned
> one or more GPUP partitions."* `Create-Master.ps1`/`Create-Client.ps1` already disable
> automatic checkpoints on every VM they create, and `Enable-GpuPartitionForVMs.ps1` does too, so
> farm VMs shouldn't hit this; if you add GPU-P to some other VM by hand, disable it yourself
> first: `Set-VM -Name '<vm name>' -AutomaticCheckpointsEnabled $false`.

## Rebuilding the master after a major DayZ update

Because differencing children depend on the exact on-disk state of the parent, there is no
supported way to patch `DayZ-Master.vhdx` in place once clients exist against it. To rebuild:

1. Boot `DayZ-Master` (with no differencing children attached/running).
2. Let Steam update DayZ normally (`SteamManager`/Steam client auto-update, or manually) — this
   updates whichever disk DayZ actually lives on (`SteamLibrary-Master.vhdx` if you used it, the
   OS disk otherwise).
3. Re-apply the graphics settings above if they were reset by the update.
4. Shut down the master.
5. Recreate client VMs against the updated master (`Remove-Client.ps1` then `Create-Client.ps1`
   for each, or `Create-Clients.ps1` for a fresh range). Each client will need its Steam account
   logged in again the first time, since a fresh differencing disk has no saved Steam session.

This is a deliberate manual step — automating parent-disk rebase (`Set-VHD -ParentPath`) is
possible in principle but is easy to get wrong (it silently assumes the child's delta is still
valid against the new parent bytes) and is out of scope for v1.
