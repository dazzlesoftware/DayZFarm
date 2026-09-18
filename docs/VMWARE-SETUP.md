# VMware Workstation Setup

The farm's virtualization backend is pluggable (`IVirtualMachineProvider` — see
docs/ARCHITECTURE.md); everything else (Controller, dashboard, guest Agent, Steam/DayZ
provisioning) works identically regardless of which one is active. This document covers the
VMware Workstation backend specifically — for Hyper-V, see docs/MASTER-IMAGE.md.

## Before you start: this is an either/or choice, not both

Hyper-V and VMware Workstation are **mutually exclusive in practice on a single Windows host**.
Enabling Hyper-V puts Windows itself into a hypervisor role (a "Type 1"-like arrangement) that
VMware Workstation then has to run nested under via the Windows Hypervisor Platform — with real
performance loss and some feature restrictions (no nested virtualization inside a VMware guest,
for one). To get VMware Workstation running with full native hardware virtualization, disable
the Hyper-V platform entirely:

```powershell
# Elevated PowerShell — requires a reboot
Disable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -NoRestart
```

Only do this if you're committing this host to VMware Workstation — you cannot run Hyper-V VMs
on this host at the same time with full performance either way. Set
`GameFarm:Hypervisor` to `"VMwareWorkstation"` in `appsettings.json` once you have (see
`config/appsettings.example.json`).

## What's supported in this first pass

- Core lifecycle: create, start, stop, shut down, restart, delete, status/IP polling.
- Linked clones off a snapshotted master VM — the VMware equivalent of Hyper-V's differencing
  disks; every client shares the master's base disk and only stores its own deltas.

**Not yet supported** (scoped out deliberately, not silently missing):
- A separate Steam Library base disk (Hyper-V's `SteamLibraryVhdx`/second-disk optimization) —
  `CreateAsync` throws `NotSupportedException` if you try to combine this with a VMware farm.
  Install Steam + DayZ directly onto each client's own disk for now, or into the master before
  cloning (every clone then already has DayZ installed, via the linked clone itself — see below).
- No automated GPU-P-style configuration through `IGpuVirtualizationProvider` — it resolves to a
  stub that always reports "not configured" and refuses to configure anything. This isn't a gap
  the way it is for Hyper-V, though: VMware Workstation doesn't need a GPU-P equivalent at all.
  Its virtual SVGA 3D device gives the guest real DirectX-accelerated graphics with **no host GPU
  passthrough/partitioning required** — just VMware Tools installed and "Accelerate 3D Graphics"
  enabled in the VM's display settings (see step 2 of "Build the master VM" below). That's the
  entire fix; there's nothing left for `IGpuVirtualizationProvider` to automate here.
- VM uptime tracking (`vmrun` has no equivalent to Hyper-V's `$vm.Uptime`) — always reports zero.

## 1. Install VMware Workstation

Download and install VMware Workstation Pro (or Player) normally — unlike Hyper-V, which is a
built-in Windows feature enabled in place (see docs/MASTER-IMAGE.md), VMware Workstation is a
separate product you install yourself; it isn't bundled with this project. Confirm `vmrun.exe`
exists at the path `VmrunPath` points to (default `C:\Program Files\VMware\VMware
Workstation\vmrun.exe`) — this is VMware's own command-line automation tool and is what this
farm's `IVirtualMachineProvider` implementation drives everything through, the same way the
Hyper-V provider only ever talks to the standard Hyper-V PowerShell cmdlets.

## 2. Set up a custom network

VMware Workstation's default networks (`vmnet0` NAT, `vmnet1` host-only, `vmnet8` NAT) are shared
with every other VM on the host; give the farm its own, via **Edit → Virtual Network Editor** (as
Administrator):
1. **Add Network...** and pick an unused `vmnetN` (the default config below assumes `vmnet2`).
2. Choose **Bridged** (for real LAN/internet access, mirroring Hyper-V's `External` switch type)
   or **NAT**/**Host-only** depending on how you want clients to reach the internet/each other.
3. Set `VMwareNetworkName` in `appsettings.json` to match whichever `vmnetN` you created.

## 3. Build the master VM

1. Create a new VM in VMware Workstation (**File → New Virtual Machine**) and install Windows —
   same base-OS setup as docs/MASTER-IMAGE.md's Hyper-V master, just created through VMware's own
   wizard. There is no scripted equivalent to `Create-Master.ps1` for this one step specifically —
   `vmrun` has no "create a new VM" command, only operations on VMs that already exist. Use a
   local account, not tied to any Steam account.
2. **Install VMware Tools inside the guest** (**VM menu → Install VMware Tools**, then run the
   installer it mounts) before doing anything else — this single step covers three separate
   requirements, not one:
   - **GPU acceleration.** Shut the VM down, enable **VM → Settings → Display → Accelerate 3D
     Graphics**, and boot it back up. This is VMware Workstation's equivalent of Hyper-V's
     GPU-P requirement (see the README's "GPU virtualization" section) — DayZ needs a real
     DirectX 11-capable adapter to launch at all, and VMware's virtual SVGA 3D device provides
     that with **no host GPU passthrough/partitioning needed**, but only once VMware Tools'
     guest driver is installed and this setting is on.
   - **Responsive stop/restart.** Every "soft" `vmrun stop`/`reset` this farm relies on (VM
     Stop/Restart from the dashboard, `Stop-Client.ps1`, `ShutdownAsync`) requires VMware Tools
     running in the guest to even respond — without it, `vmrun` hangs for the full 45-second
     timeout on every single one of those calls before falling back to a hard stop, instead of
     completing instantly.
   - **Guest IP reporting** (see Troubleshooting below).

   Confirm it's running (Task Manager → look for `vmtoolsd.exe`, or check **VM → Install VMware
   Tools** now reads "Reinstall VMware Tools") before continuing.
3. Install all Windows updates, then Steam (don't log in yet), then DayZ (App ID 221100) directly
   onto this VM's own disk — there's no separate Steam Library disk equivalent in this pass (see
   "Not yet supported" above), so DayZ genuinely lives on the master's disk here, and every
   client's linked clone inherits it via the clone's own copy-on-write delta.
4. Set `BEService` to auto-start (`Set-Service BEService -StartupType Automatic; Start-Service
   BEService`) and install the Game Farm Agent (`scripts\Install-Agent.ps1`) — identical steps to
   docs/MASTER-IMAGE.md and docs/TROUBLESHOOTING.md's BattlEye section; nothing about either is
   Hyper-V-specific.
5. Configure low graphics settings (see docs/MASTER-IMAGE.md's Graphics section — applies
   identically here).
6. Shut the master VM down cleanly (do **not** just suspend/pause it — linked clones need a
   clean, powered-off snapshot).
7. **Take a snapshot** named to match `MasterVmxSnapshot` in `appsettings.json` (default
   `"Baseline"`): **VM menu → Snapshot → Take Snapshot...**, or run
   `scripts\VMware\New-MasterSnapshot.ps1 -MasterVmx <path> -SnapshotName Baseline` — the one
   part of this whole master-build process `vmrun` actually can script (it refuses to snapshot a
   running VM, same "clean, powered-off" requirement as the manual route). This is the point
   every client links from — `CreateAsync` refuses to clone if this snapshot doesn't exist, with
   a clear error naming exactly what to create. Because VMware Tools was installed in step 2
   before this snapshot was taken, every client cloned from it inherits VMware Tools already
   running — no per-client step needed.
8. Set `MasterVmx` in `appsettings.json` to this VM's actual `.vmx` path (VMware Workstation
   shows this under the VM's own Settings, or check the folder you created it in).

> Just like the Hyper-V master (docs/MASTER-IMAGE.md), the Steam login must **not** happen on
> the master — each client gets its own Steam account, logged in individually after creation
> (see docs/STEAM-SETUP.md).
>
> Also just like the Hyper-V master: don't keep booting the master VM after this. Every client
> clone is linked against this exact snapshot's on-disk state — powering the master back on and
> changing anything invalidates that relationship for every existing client. Rebuild (a new
> snapshot, or a whole new master) rather than editing the running master in place.

## 4. Create clients from the dashboard, or from scripts

Once `Hypervisor` is set to `"VMwareWorkstation"` and steps 1–3 above are done, **Create Client**
on the dashboard works exactly as it does for Hyper-V — same form, same fields — except behind
the scenes it's now running `vmrun clone ... linked` against `MasterVmx`'s `MasterVmxSnapshot`
instead of `New-VHD -Differencing` against `MasterVhdx`.

`scripts\VMware\` also has standalone script equivalents, mirroring `scripts\Hyper-V\` exactly —
`Create-Client.ps1`/`Create-Clients.ps1`, `Remove-Client.ps1`, `Start-Client.ps1`/
`Stop-Client.ps1`, `Configure-Network.ps1` (read-only here — `vmrun` can't create a network, only
report host networks and guest IPs; add a network via **Edit → Virtual Network Editor**, per step
2 above), and `Install-Host.ps1` (checks `vmrun.exe` exists, warns if Hyper-V is still enabled,
creates the directory layout). They run the exact same `vmrun` invocations and `.vmx` edits as
`VMwareVirtualMachineProvider.CreateAsync`, including the same cleanup-on-failure behavior, so a
scripted client is indistinguishable from a dashboard-created one. Unlike Hyper-V's
`Remove-Client.ps1`, the VMware version doesn't need a "still in use as another VM's parent"
check — a linked clone's deltas live entirely in its own instance directory and never touch the
master.

## Rebuilding the master after a major DayZ update

Because every client is linked against the master's snapshot, there is no supported way to patch
the snapshot itself in place once clients exist against it — the VMware equivalent of
docs/MASTER-IMAGE.md's Hyper-V rebuild process:

1. Boot the master VMX (with no client clones running against it — they can stay powered off).
2. Let Steam update DayZ normally.
3. Re-apply the graphics settings (low-graphics + "Accelerate 3D Graphics", see step 2/4 above)
   if they were reset by the update.
4. Shut down the master cleanly.
5. Take a **new** snapshot — either overwrite `MasterVmxSnapshot`'s name (delete the old one
   first via `vmrun deleteSnapshot`, or the VMware Workstation Snapshot Manager) or give it a new
   name and update `MasterVmxSnapshot` in `appsettings.json` to match. Existing clients keep
   linking against the *old* snapshot's on-disk state either way — a snapshot's data doesn't
   change retroactively — so recreate each client against the new one:
   `scripts\VMware\Remove-Client.ps1` then `scripts\VMware\Create-Client.ps1` (or
   `Create-Clients.ps1` for a fresh range). Each client will need its Steam account logged in
   again, since a fresh linked clone has no saved Steam session.

## Troubleshooting

- **"Master VMX has no snapshot named '...'"**: take the snapshot from step 7 above, named
  exactly what `MasterVmxSnapshot` says.
- **A client shows as `Off` right after creation even with `StartAfterCreate`**: check the
  Controller log for the actual `vmrun start` error — a common cause is `VmrunPath` pointing at a
  Workstation Player install that doesn't support scripted linked clones the same way Pro does.
- **No guest IP ever appears**: VMware Tools wasn't installed before the master's snapshot was
  taken (step 2 above) — `vmrun getGuestIPAddress` returns nothing without it running in the
  guest. Every client inherits whatever state the master had at snapshot time, so this affects
  every client at once, not just one; fix the master (install VMware Tools, re-shut-down,
  re-snapshot) rather than each client individually.
- **Stop/Restart hangs on a client with no responsive VMware Tools**: confirmed directly during
  testing — a "soft" `vmrun stop`/`reset` against a guest with no VMware Tools installed/running
  hangs indefinitely; vmrun itself never times out on its own. `ProcessVmrunRunner` bounds every
  call to 45 seconds and kills a hung vmrun process automatically, falling back to a hard
  stop/reset the same way it would for any other failure — so this self-resolves within ~45s
  rather than hanging forever, but a client stuck this way will still visibly pause for that long
  before recovering. Install/start VMware Tools in that client to avoid the delay entirely.
- Everything else (BattlEye, Steam login, mods, Agent setup) — see docs/TROUBLESHOOTING.md; none
  of that is hypervisor-specific.
