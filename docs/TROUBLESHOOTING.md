# Troubleshooting

This file mixes hypervisor-agnostic issues (Steam, BattlEye, mods, agent connectivity) with
historical, Hyper-V-specific bug postmortems from building `HyperVVirtualMachineProvider`/
`ProcessPowerShellRunner` — those sections are marked accordingly below. VMware Workstation's
provider (`VMwareVirtualMachineProvider`/`ProcessVmrunRunner`) was built afterward with those
lessons already applied (bounded timeouts, cleanup-on-failure, etc. from day one), so it never
had most of these specific bugs; its own troubleshooting lives in docs/VMWARE-SETUP.md.

## `Install-Host.ps1` fails with "Class not registered" from Get-WindowsOptionalFeature (Hyper-V only)

This is a known Windows 11 issue with the DISM COM component `Get-WindowsOptionalFeature`
depends on — it is unrelated to whether Hyper-V itself can actually be enabled, and unrelated
to this project's code. `Get-HyperVFeatureState`/`Enable-HyperVFeatureWithFallback`
(scripts/Hyper-V/Common.ps1) automatically fall back to `dism.exe` (which uses a different code path)
when this happens, so a single occurrence usually doesn't block you — but if the `dism.exe`
fallback also fails ("Unknown" state reported), try, in order:

0. **Check whether you actually need to fix this at all.** `Get-HyperVFeatureState`/
   `Enable-HyperVFeatureWithFallback` (scripts/Hyper-V/Common.ps1) already fall back to `dism.exe`
   automatically and log a `WARNING: ... falling back to dism.exe` line when they do — if the
   script continues past that warning instead of reporting state `Unknown`, the fallback is
   working and you don't need to do anything below.
1. Re-register the DISM COM DLL (elevated). On current Windows 11 builds it lives under a
   `Dism` subfolder, not directly in `System32`:
   `regsvr32.exe $env:SystemRoot\System32\Dism\DismCore.dll`
   If that still reports "module failed to load / could not be found" on your build, find its
   real path first —
   `Get-ChildItem -Path $env:SystemRoot\System32 -Recurse -Filter dismcore.dll -ErrorAction SilentlyContinue`
   — then `regsvr32.exe` that path instead.
2. Restart the Windows Modules Installer service: `Start-Service TrustedInstaller`
3. Confirm you're running 64-bit PowerShell, not the SysWOW64 copy:
   `[Environment]::Is64BitProcess` should report `True`.
4. As a last resort, repair the Windows component store: `DISM /Online /Cleanup-Image
   /RestoreHealth`, then `sfc /scannow`.
5. If nothing else works, enable Hyper-V manually via **Turn Windows features on or off**
   (`optionalfeatures.exe`) or `Control Panel`, reboot, and re-run `Install-Host.ps1` — it will
   detect Hyper-V is already enabled and continue with directory/switch setup.

## `Remove-Client.ps1 -Name DayZ-Master` doesn't remove `SteamLibrary-Master.vhdx` (Hyper-V only)

Fixed as of the current version. The previous version only deleted disks it found **attached**
to the VM (`Get-VMHardDiskDrive`) — so if `SteamLibrary-Master.vhdx` was created as a file but
never actually got attached (the exact failure mode described above, from a script run before
`$ErrorActionPreference = 'Stop'` was added everywhere), the file was invisible to it as long as
the VM still existed, and got left behind.

`Remove-Client.ps1` now also checks every conventional path a client or master could plausibly
own — including `Images\SteamLibrary-Master.vhdx` — regardless of what's actually attached. To
keep this safe, it **refuses to delete any disk still in use by another VM**, whether that's
another VM's own live attached disk or another VM's differencing parent (`Test-DiskInUseAsParent`
in `scripts/Hyper-V/Common.ps1`) — so removing an ordinary client can never accidentally delete the
master's shared Steam Library disk out from under still-running clients, and removing the master
itself will only actually delete that shared disk once no client depends on it anymore. If it
skips a disk, it says so explicitly (`Skipping delete of '...' -- it is still in use...`) rather
than silently leaving it — that's not a bug, it means something else still needs it.

## `Create-Master.ps1` (or `Create-Client.ps1`) says a VM/disk "already exists" right after cleanup (Hyper-V only)

Fixed as of the current version, in two parts:

1. `-IsoPath` is validated *before* the VHDX/VM are created, so a bad path fails immediately
   without leaving anything behind.
2. `Remove-Client.ps1` retries a VHDX delete up to 5 times half a second apart (Hyper-V's
   management service can briefly hold a lock on a disk file right after `Remove-VM` returns,
   so a one-shot `Remove-Item` can lose that race), **and** — if you run it against a name whose
   VM record is already gone (e.g. from an even older copy of this script, or the VM was removed
   another way) — it now also checks for and cleans up an orphaned disk at the conventional
   `Instances\<name>\<name>.vhdx` or `Images\<name>.vhdx` paths, rather than just reporting
   "nothing to remove" and leaving it behind.

So simply re-running `Remove-Client.ps1 -Name DayZ-Master` (or the affected client's name) is
now enough to clear this. If it still somehow leaves a file behind, it's safe to delete directly
— nothing references it anymore:

```powershell
Remove-Item D:\GameFarm\Images\DayZ-Master.vhdx -Force   # or the client's Instances\<name>\<name>.vhdx
.\scripts\Hyper-V\Create-Master.ps1 -CpuCount 4 -MemoryGB 6 -SizeGB 80 -VirtualSwitchName GameFarmSwitch -IsoPath <correct path>
```

## `JsonException: ... could not be converted ... BytePositionInLine: 1` from status polling (Hyper-V only)

Fixed as of the current version, but it took two attempts. Windows PowerShell 5.1 defaults its
console output encoding to the system's OEM/ANSI codepage, not UTF-8 — but
`ProcessPowerShellRunner` used to tell .NET to decode the child process's redirected stdout *as*
UTF-8. That mismatch corrupted the captured text from its very first character, which is exactly
what a `JsonException` at `BytePositionInLine: 1` means. Forcing `[Console]::OutputEncoding =
[System.Text.Encoding]::UTF8` as the first line of the script did **not** reliably fix this — a
fully redirected, windowless child process (`CreateNoWindow = true`) often has no real console
handle for that property to reconfigure at all.

The actual fix: stop trusting the process's stdout pipe encoding for the payload entirely. The
runner now wraps every script so its pipeline result is written to a temporary file with an
explicit encoding (`[System.IO.File]::WriteAllText(...,  [System.Text.Encoding]::UTF8)`), and
reads that file back with the same explicit encoding — sidestepping pipe-encoding ambiguity
completely. stdout is still drained (so the child never blocks on a full pipe buffer) but no
longer used as the source of truth.

A related pile-up was also observed while this bug was active: many overlapping dashboard/status-
polling calls each spawn a real `powershell.exe` process, and without a cap they can pile up and
start timing out on each other (`TaskCanceledException` from `Process.WaitForExitAsync`). The
runner now caps concurrent PowerShell process spawns at 4 as a safety net, independent of the
Controller's own `ConcurrencyGates` (which don't cover every call path, e.g. status polling's VM
enumeration).

## `ConvertTo-Json : A parameter cannot be found that matches parameter name 'AsArray'` (Hyper-V only)

Fixed as of the current version. `ConvertTo-Json -AsArray` is a PowerShell 6.2+ feature — it
doesn't exist in Windows PowerShell 5.1's built-in `ConvertTo-Json`, and switching the preferred
Hyper-V PowerShell host to `powershell.exe` (see below) surfaced this. The fix simply drops
`-AsArray` — the C# JSON parsing (`HyperVVirtualMachineProvider.ParseJsonArray`) already handled
the resulting ambiguity (`ConvertTo-Json` without `-AsArray` collapses a single-item result down
to a bare JSON object instead of a one-element array) with a fallback parse path, so no other
change was needed. If you're writing a new PowerShell script invoked through
`IPowerShellRunner`/`ProcessPowerShellRunner`, avoid PowerShell 7+-only syntax generally (this
includes `-AsArray`, the `??`/`?:` operators, and `&&`/`||` pipeline chaining) — it targets
Windows PowerShell 5.1 for Hyper-V module compatibility (see below), and 5.1 will reject or
misbehave on all of these.

## `JsonException: The JSON value could not be converted to System.String. Path: $[0].IpAddress` (Hyper-V only)

Fixed as of the current version. This showed up after the fixes above got the VM-enumeration
script actually running and returning valid JSON — but `HyperVVirtualMachineProvider.ParseJsonArray`
was originally swallowing the real exception (its fallback single-object parse attempt threw its
own, unrelated error, which is what actually surfaced). `ParseJsonArray` now combines both parse
attempts' messages into one exception, which is what revealed the true cause here: for a VM with
no active network lease (e.g. `State = Off`), `$adapter.IPAddresses` is an *empty array*, not
`$null`. Piping an empty array through `Select-Object -First 1` produces PowerShell's internal
"AutomationNull" value, and `ConvertTo-Json` renders that as an empty JSON object `{}` — not
`null`, not a string. `RawVmInfo.IpAddress` is typed `string?`, so `System.Text.Json` rejects the
`{}` outright.

The fix replaces the ambiguous `IpAddress = ($adapter.IPAddresses | Select-Object -First 1)` with
explicit `if`/`else` logic that always yields a real string or an explicit `$null`:

```powershell
$ip = $adapter.IPAddresses | Where-Object { $_ -notmatch ':' } | Select-Object -First 1
...
IpAddress = if ($ip) { [string]$ip } else { $null }
```

(The `Where-Object { $_ -notmatch ':' }` filter also prefers an IPv4 address over an IPv6
link-local one when both are present — cosmetic, but keeps the dashboard's Guest IP column
readable.) If you add more properties to this script (or write a new one) that can come from an
empty PowerShell collection or pipeline, apply the same explicit-`if`/`else` pattern rather than a
bare parenthesized pipeline expression — the empty-array-to-`{}` behavior is general to
`ConvertTo-Json`, not specific to `IPAddresses`.

## Dashboard's VM actions (Start/Stop/Restart) silently do nothing at all (Hyper-V only)

Fixed as of the current version — and this was a serious one, so it's worth understanding.
`ProcessPowerShellRunner` used to pipe the script text into the PowerShell process's stdin and
pass `-Command -` to tell it to read the script from there. That convention is a documented
**PowerShell 7+ (`pwsh`)** feature; Windows PowerShell 5.1 does not reliably honor it the same
way. After the fix below (preferring `powershell.exe` over `pwsh` for Hyper-V compatibility), the
runner was still using the stdin-piping mechanism — so every single script silently executed
nothing at all: 0 bytes of stdout, 0 bytes of stderr, exit code 0 (success). No error surfaced
anywhere, because nothing failed — nothing ran. `Get-VM` enumeration returning empty and
`Start-VM`/`Stop-VM`/`Restart-VM` appearing to "succeed" (fast 200 responses) while doing nothing
were both this same root cause.

The runner now writes the script to a temporary `.ps1` file and runs it with `-File`, which both
PowerShell hosts understand identically — no more ambiguity about how a given host reads a
script handed to it. If you ever need to confirm this yourself: the Controller's console now
logs `Executing PowerShell script (...) via <temp path>` for every call at `Debug` level, and
`PowerShell script exited with code ...` at `Warning` level for any that fail with a non-zero
exit code.

## Dashboard shows a VM as "Unknown"/offline with no IP, even though it's actually Running (Hyper-V only)

Fixed as of the current version. `HyperVVirtualMachineProvider` shells out to PowerShell to
enumerate VMs (`Get-VM | ForEach-Object { ... Get-VMNetworkAdapter ... }`), and
`ProcessPowerShellRunner` previously *preferred* PowerShell 7 (`pwsh`) if it was installed. The
Hyper-V module is a legacy CDXML/WMI module that is only fully, natively compatible with Windows
PowerShell — under `pwsh` it can silently return empty or incomplete results for some cmdlets
(observed: VM enumeration with a nested `Get-VMNetworkAdapter` call returning nothing) instead of
throwing an error, so nothing looked broken in the logs — the dashboard just showed every field
at its zero/null default (`vmStatus: "Unknown"`, `guestIpAddress: null`, `cpuCount: 0`) forever,
even while `Get-VM` in your own terminal correctly showed the same VM as `Running`.

The runner now prefers Windows PowerShell (`powershell.exe`) instead, since Hyper-V management
is Windows-only anyway and there's no upside to `pwsh` here. It also now logs which PowerShell
host it resolved to, once, at startup (`Information` level, visible in the Controller's console
output) — if you ever need to check this again, look for a line like `Hyper-V PowerShell
operations will run via 'powershell'.`

Re-publish and restart the Controller to pick this up:
```powershell
dotnet publish H:\DayZProject\src\GameFarm.Controller -c Release -o C:\GameFarm\Controller
```

## Dashboard shows raw numbers ("0") instead of status text (VM State, Connection)

Fixed as of the current version. `VirtualMachineStatus`/`ConnectionStatus` are C# enums, and
System.Text.Json serializes enums as their raw numeric value by default — the dashboard's JS
looks up badge text/color by the string name (`'Running'`, `'Connected'`, etc.), so every such
lookup silently failed and fell back to displaying the bare number. `Program.cs` now configures
`JsonStringEnumConverter` globally so the API returns enum names as strings. Re-publish/restart
the Controller to pick this up.

## Dashboard shows correct text briefly, then VM State/Connection revert to raw numbers a few seconds later

Fixed as of the current version. This looks identical to the bug above but is a separate cause:
the dashboard gets its initial data from a plain HTTP `GET /api/clients` call (which the fix
above covers, via `ConfigureHttpJsonOptions`), but live updates after that arrive over SignalR
(`ClientsUpdated`) — and SignalR's `JsonHubProtocol` has its **own**, entirely separate
`JsonSerializerOptions` that `ConfigureHttpJsonOptions` never touches. So the first render showed
correct text, and the next `StatusPollingService` broadcast silently overwrote it with raw enum
numbers again. Fixed by also registering `JsonStringEnumConverter` on SignalR's own JSON protocol
in `Program.cs`: `builder.Services.AddSignalR().AddJsonProtocol(options =>
options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))`. If you add any
other ASP.NET Core JSON surface in the future (e.g. a second SignalR hub, a raw
`System.Text.Json.JsonSerializer.Serialize` call somewhere), remember it needs the same converter
applied explicitly — `ConfigureHttpJsonOptions` only covers minimal-API endpoint responses.

## Steam shows "Login needed" even though you're logged into Steam interactively in the VM

Fixed as of the current version. `SteamManager.GetLoginState()` originally read
`Registry.CurrentUser\Software\Valve\Steam\ActiveProcess\ActiveUser`. At the time this was
fixed, the agent ran as a `LocalSystem` Windows Service (Session 0) — see the "BattlEye" section
below for why it no longer does — while Steam ran in the VM's actual interactive desktop session
(whatever account you log into the VM as). `Registry.CurrentUser` from a Session-0 LocalSystem
process resolves to LocalSystem's own hive — a completely different, unrelated registry tree
from the interactive user's — so the `ActiveProcess` key was never found there, and the login
check always reported "not logged in" no matter what Steam's actual state was. This fix remains
correct and necessary even now that the agent runs interactively (as a Scheduled Task, in that
same account's session): it makes no assumption about which session is reading the registry, so
it works either way.

The fix scans every loaded user hive under `HKEY_USERS` instead of assuming
`Registry.CurrentUser`: any interactively logged-on user's hive is mounted under
`HKEY_USERS\<SID>` for as long as their session is active, regardless of which session the
*reading* process itself happens to run in. This needs no prior knowledge of which account is
logged into the VM. Re-publish and restart the agent inside each VM to pick this up:
```powershell
dotnet publish H:\DayZProject\src\GameFarm.Agent -c Release -o C:\GameFarmAgent
Get-Process GameFarm.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
Start-ScheduledTask -TaskName "Game Farm Agent"
```
If this still reports "not logged in" after that, check that the account you logged Steam into
interactively is actually still logged on (not just RDP-disconnected in a way that unloads its
profile) — a genuinely logged-off session unmounts its `HKEY_USERS` hive and this check has
nothing to find.

## Dashboard shows "Agent unreachable: ... HttpClient.Timeout of 15 seconds elapsing" on Start DayZ / Join

Fixed as of the current version. `DayZGameLauncher.LaunchAsync` blocks its HTTP response for
up to 30 seconds, polling once a second for the `DayZ_x64` process to appear after asking Steam
to launch it (`Steam -applaunch`, which itself forks the real game process asynchronously — see
the polling loop in that file). The Controller's `AgentHttpClient` was configured with a fixed
15-second `HttpClient.Timeout`, comfortably shorter than that — so any Start DayZ/Join call where
the game legitimately took anywhere close to or over 15 seconds to appear was aborted client-side
by the Controller itself with a `TaskCanceledException`, surfaced to the dashboard as "Agent
unreachable", even though the agent was reachable and the launch was still in progress (or had
even succeeded moments later, server-side, after the Controller had already given up on it).
Fixed by bumping `AgentHttpClient`'s timeout in `Program.cs` from 15 to 45 seconds — comfortably
above the agent's own 30-second ceiling. Re-publish/restart the Controller to pick this up.

## Pressing Start DayZ knocks Steam offline ("Login needed") instead of launching the game, and the VM needs a restart to recover

Fixed as of the current version. This was the deeper, related problem behind the two fixes
above: the agent runs as a `LocalSystem` Windows Service in **Session 0** (non-interactive),
while Steam runs in the VM's actual **interactive** desktop session (whatever you logged into the
VM's console/RDP as). `SteamManager.Start()`, its `-shutdown` call, and
`LaunchAppViaSteam` (`-applaunch`, used by Start DayZ/Join) all previously used a plain
`Process.Start(... UseShellExecute: true)`. On modern Windows, a process a Session-0 service
starts this way runs *in Session 0 itself*, not in the interactive session — it does not attach
to or hand off to the interactive user's already-running, already-logged-in Steam client at all.
Instead it silently starts a **second, isolated `steam.exe`** in Session 0, running as
LocalSystem with LocalSystem's own unrelated (never-logged-in) Steam config. Steam's
single-instance enforcement does not expect a second instance to appear in a different,
non-interactive session, and in practice this knocked the real interactive Steam instance
offline rather than leaving it alone or cleanly failing — hence "Login needed" right after
pressing Start DayZ, DayZ never actually launching, and needing a full VM restart to get back to
a working state.

Fixed by adding `InteractiveProcessLauncher`
(`src/GameFarm.Agent/Interop/InteractiveProcessLauncher.cs`), which uses the standard
"Session-0 service launches a process as the interactive user" Windows pattern
(`WTSQueryUserToken` + `DuplicateTokenEx` + `CreateProcessAsUser` with an explicit
`winsta0\default` desktop) so the process genuinely starts in the interactive session, as that
user, and reaches the Steam client that's actually running there. `SteamManager.Start()`,
`StopAsync()`, and `LaunchAppViaSteam()` now all go through this instead of `Process.Start`.

This requires an interactive session to actually be logged into the VM (console or RDP) before
Start Steam/Start DayZ/Join is used — if none is, `InteractiveProcessLauncher` throws a clear
"No interactive user session is currently logged on in this VM" error surfaced back to the
dashboard, rather than silently misbehaving. Re-publish and restart the agent inside each VM to
pick this up:
```powershell
dotnet publish H:\DayZProject\src\GameFarm.Agent -c Release -o C:\GameFarmAgent
Get-Process GameFarm.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
Start-ScheduledTask -TaskName "Game Farm Agent"
```
If a VM is currently in the wedged state described above, restart the VM once to clear it, then
apply this fix before trying Start DayZ again.

## Kicked with "Client is missing a mod which is on the server" even after setting Required Mods

`RequiredMods` (and the Mods panel's "Workshop ID" column) must be the mod's **numeric** Steam
Workshop ID (e.g. `1559212036`), not its display name. Entering the mod *names* shown in the
kick dialog (`CodeLock`, `DayZ-Expansion-Core`, ...) directly into `RequiredMods` looks
plausible but does nothing useful: `WindowsWorkshopManager` matches installed content by
scanning `steamapps/workshop/content/221100/<numeric id>/`, and `DayZLaunchArgumentBuilder`
passes whatever is in `RequiredMods` straight through as `-mod=<value1;value2;...>` — a mod name
matches no installed folder and means nothing to DayZ's `-mod=` argument, so every row shows
"Missing" and the client still launches with no mods loaded.

Fastest way to get the real numeric IDs for a given server's exact mod set: in the VM, when the
kick dialog offers it (or from the server browser before connecting), click **"SETUP DLCS AND
MODS AND JOIN"** — this has Steam auto-subscribe and download the exact set the server requires,
no manual Workshop-page hunting needed. Once that finishes, every installed mod's numeric ID is
the folder name under:
```
<SteamLibrary>\steamapps\workshop\content\221100\
```
List those folder names and put that comma-separated list of numbers into `RequiredMods` on the
client's Edit Client form, save, then **Ensure Mods** should show every row as Installed. Only
after that will Start DayZ/Join pass the matching `-mod=` argument.

The `Ensure Mods` button's own fallback (opening a missing mod's Workshop page for manual
Subscribe) also had the same Session-0-vs-interactive-session bug as Steam/DayZ launch (see
above) — `WindowsWorkshopManager.EnsureModsInstalledAsync` now goes through
`InteractiveProcessLauncher` too, for the same reason.

## Mods panel still shows every mod as "Missing" even though they're genuinely subscribed/downloaded in Steam

Fixed as of the current version. `WindowsWorkshopManager` originally assumed Workshop content
always lives right alongside `steam.exe` itself
(`<SteamInstallDir>\steamapps\workshop\content\221100\`). That's only true for a single-library
Steam install — this project's own design routinely puts DayZ on a **separate Steam Library
folder** (the `SteamLibrary-Master.vhdx` disk, often an entirely different drive letter — see
docs/MASTER-IMAGE.md), and Workshop content for an app is stored under whichever library folder
*that app* is installed to, not the Steam client's own folder. So the scan was silently looking
in the wrong (empty or irrelevant) directory and reporting every single mod "Missing" no matter
how many were actually installed.

Fixed by also consulting `steamapps/libraryfolders.vdf` (the file Steam itself maintains listing
every registered library folder) and scanning `steamapps/workshop/content/221100/` under **all**
of them, not just the one next to `steam.exe`. If mods still show "Missing" after this fix,
double check DayZ itself (and its Workshop content) is actually installed on a drive/folder that
is a registered Steam Library Folder for that Steam installation (Steam settings → Storage), not
just manually copied there.

This bug is also what caused Steam to visibly struggle/glitch when `Ensure Mods` was clicked with
a long `RequiredMods` list: with every mod falsely flagged "Missing", it tried to open dozens of
Workshop pages back-to-back. Now that detection is correct this shouldn't recur for mods that are
actually installed; the per-mod delay between opening Workshop pages for genuinely missing mods
was also bumped from 500ms to 2s as a defensive margin. If a client is missing dozens of mods,
install them once in the master image instead (see docs/MASTER-IMAGE.md) rather than relying on
`Ensure Mods` to open that many Workshop pages in one go.

## Still kicked for a missing mod even though the Mods panel shows every required mod as "Installed"

Fixed as of the current version. `RequiredMods`/the Mods panel work with bare numeric Workshop
IDs (e.g. `1559212036`) — but that's exactly what was being passed straight through into DayZ's
own `-mod=` launch argument. A bare numeric ID means nothing to DayZ's `-mod=` parameter; it
needs either a local `@ModName` folder name or, for Workshop-sourced content not copied/renamed
locally, the mod's actual absolute content folder path. So even with every mod genuinely
installed, the client launched with an effectively empty/meaningless mod list — same symptom as
the "empty RequiredMods" case above, but for a different, more subtle reason.

Fixed by resolving each Workshop ID to its actual installed content folder's absolute path
(`WindowsWorkshopManager.ResolveModPathsAsync`, using the same library-folder-aware scan as the
fix above) inside `DayZGameLauncher.LaunchAsync`, immediately before building the `-mod=`
argument — so `RequiredMods`/the dashboard/`Ensure Mods` all keep working with plain numeric IDs
(simple to read, edit, and match against Steam's own folder names), while the actual OS-level
launch command gets the full paths DayZ needs. Any ID with no installed folder found is logged
as a warning and simply omitted from the launch rather than failing the whole launch. Re-publish
and restart the agent inside each VM to pick this up:
```powershell
dotnet publish H:\DayZProject\src\GameFarm.Agent -c Release -o C:\GameFarmAgent
Get-Process GameFarm.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
Start-ScheduledTask -TaskName "Game Farm Agent"
```

## History: `-mod=` referencing mods via `!Workshop\<id>` instead of their absolute path

Attempted twice for the BattlEye kick described below, on the theory that Steam's own launcher
references a subscribed Workshop item via a `!Workshop\<id>` junction inside the game's own
install directory, and that keeping mod files inside that tree (rather than referencing them by
their raw absolute `steamapps/workshop/content/221100/<id>` path) would avoid whatever
BattlEye's periodic integrity re-check was objecting to.

The first attempt assumed Steam auto-maintains that junction regardless of launch method — it
doesn't for a direct `-applaunch` (only Steam's own "SETUP DLCS AND MODS AND JOIN" UI flow
creates it), so referencing a junction that didn't exist broke the connection entirely ("missing
a mod" kick, immediately). That version was reverted to the absolute path.

The second, current attempt has `WindowsWorkshopManager.ResolveModPathsAsync` **create the
junction itself** (`EnsureWorkshopJunction`, via `mklink /J`, idempotent) inside DayZ's own
install directory before referencing it — so the junction genuinely exists this time regardless
of how the game is launched, with a fallback to the absolute path if DayZ's install directory
can't be found or junction creation fails for any reason. Re-publish and restart the agent inside
each VM to pick this up:
```powershell
dotnet publish H:\DayZProject\src\GameFarm.Agent -c Release -o C:\GameFarmAgent
Get-Process GameFarm.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
Start-ScheduledTask -TaskName "Game Farm Agent"
```
If mods are still kicked as "missing" immediately after this, check the agent log for a "Failed
to create !Workshop junction" warning — that means junction creation itself failed (e.g. DayZ's
install directory wasn't found, or a permissions issue), and mods fell back to the absolute path
again. If the BattlEye kick still happens even with the junction genuinely in place, this theory
is likely wrong and the real cause lies elsewhere (see the idle/inactivity section below, or a
possible genuine BattlEye service issue).

## "BattlEye" kicks partway into every agent-launched session ("Game restart required", then "Bad Packet") — never when launched manually

This took three real, necessary layers to fully resolve. If you're hitting a BattlEye kick,
work through them in order and confirm each before moving to the next.

### Layer 1 (necessary but not sufficient): `BEService` never running

`BEService` (BattlEye's persistent Windows service — distinct from its per-session game client)
is installed by DayZ set to **Manual** start (`DEMAND_START`) and not actually running, and stays
that way on a freshly installed or cloned VM until something starts it at least once. Confirmed
via:
```powershell
Get-Service BEService     # Status: Stopped
sc.exe qc BEService        # START_TYPE: 3  DEMAND_START
```
and by the complete absence of any `BEClient_x64_<date>.log` under `<DayZ install>\battleye\`
(only the `BEClient_x64.dll` itself present) — that log is written fresh every session
BattlEye's client actually initializes. Without a running `BEService`, DayZ still connects
normally, but BattlEye never actually protects the session, and the server eventually kicks with
"kicked off by BattlEye: Game restart required" (server-side log: kick code `240`). Fix (inside
the VM, elevated PowerShell):
```powershell
Set-Service BEService -StartupType Automatic
Start-Service BEService
```
Do this once in the master image (see docs/MASTER-IMAGE.md) so every client inherits it. This
step is real and necessary — after it, the kick message changes from "Game restart required" to
"Bad Packet", confirming BattlEye's client is now actually attaching — but it wasn't the whole
story; see Layer 3 below for what was still wrong.

### Layer 2 (necessary, but must be re-applied on every boot): network checksum offload

"Bad Packet" is a real, distinct BattlEye kick reason, commonly caused by Hyper-V's virtual NIC
corrupting UDP checksums. Disabling checksum offload/VMQ on both the host's physical adapter and
the guest's synthetic adapter genuinely helps — but an earlier round of testing this
concluded it made no difference, which turned out to be a false negative: **Hyper-V's synthetic
network adapter does not persist a disabled checksum offload setting across a full VM reboot**
the way a physical NIC's driver normally would. It silently reverts to enabled on every boot,
confirmed directly:
```powershell
# Host (persists fine across reboots):
Get-NetAdapterChecksumOffload -Name "Ethernet","vEthernet (<switch name>)"   # stayed Disabled
# Guest (does NOT persist):
Get-NetAdapterChecksumOffload -Name "<guest adapter, e.g. 'Ethernet 2'>"      # back to RxTxEnabled after a reboot
```
So a test performed any time after the guest VM had rebooted (for any reason, including the
Layer 3 migration below) was silently running with offload re-enabled again, even though it had
been disabled earlier in that same VM's uptime.

**Fix**: `scripts/Install-Agent.ps1` now also registers a second Scheduled Task,
"Game Farm - Disable NIC Offload", triggered `AtStartup` (runs as SYSTEM, before any user logon),
which re-applies `Set-NetAdapterChecksumOffload -Disabled` on every network adapter every single
boot. This is idempotent and self-healing — it doesn't matter how many times the VM reboots
going forward. Also applied immediately when the script runs, so an already-running VM doesn't
need a reboot to pick it up. Re-run `Install-Agent.ps1` on any VM that predates this fix (pass
`-SkipAutoLogon` if that's already configured and you only need this part).

**On VMware Workstation:** `Install-Agent.ps1` is the same hypervisor-agnostic script (see
docs/VMWARE-SETUP.md), so this Scheduled Task gets registered on a VMware master/client exactly
the same way — it's harmless there even though the root cause diagnosed above (Hyper-V's
synthetic adapter specifically not persisting the setting) hasn't been confirmed to apply to
VMware's virtual NIC. If a VMware client gets "Bad Packet" kicks, it's worth checking whether the
same offload-reverts-on-reboot behavior occurs there — this fix would already cover it if so, but
that hasn't been specifically tested.

### Layer 3 (real, necessary, but on its own not sufficient): the agent's Session-0 → interactive-session launch trick

**Confirmed root cause**: every prior fix (Steam start/stop, DayZ launch, Workshop page opens)
routed through a Session-0 LocalSystem service using `CreateProcessAsUser` +
`DuplicateTokenEx` (`InteractiveProcessLauncher`) to reach the VM's interactive desktop session,
because the agent itself ran as a LocalSystem Windows Service. This technique is a legitimate,
standard Windows pattern, and it genuinely worked — Steam/DayZ launched and connected correctly
either way. But BattlEye's anti-tamper checks are specifically designed to distrust a game
process whose ancestry includes a privileged service using a manipulated/duplicated security
token, since that's also how real cheat-injection tooling gains elevated access alongside a
game process — and this was the one thing constant across every other failed attempt, matching
the exact, repeated observation that a manually-launched session (a human double-clicking Steam
themselves) never gets kicked, while every agent-launched one eventually does.

**Fix**: the agent no longer runs as a Windows Service at all. `scripts/Install-Agent.ps1`
instead registers it as a **Scheduled Task** that runs directly in the VM's own interactive
logon session (triggered "At log on"), with Windows auto-logon configured for that account. With
the agent's own process tree already living in that session, `SteamManager`/
`WindowsWorkshopManager` now launch Steam/DayZ via a plain, unmodified `Process.Start` — no
token duplication anywhere in the process tree, indistinguishable from a human launching them.
`InteractiveProcessLauncher` is no longer called anywhere (kept in the repo, clearly marked
unused, purely for reference/context — see its own doc comment before ever reintroducing it).

Migrating an existing client VM:
```powershell
# Inside the VM, elevated PowerShell:
.\Install-Agent.ps1 -UserName <the account Steam is logged into on this VM>
```
This removes any previous "Game Farm Agent" Windows Service install automatically, configures
auto-logon, and registers the Scheduled Task. Reboot (or log off/on as that user) afterward to
actually start the agent under the new setup. Do this once in the master image too (see
docs/MASTER-IMAGE.md) so every future client inherits it.

**This layer alone produced a large, real improvement** (survived ~9 minutes into a session,
versus kicks within 1-5 minutes before it), confirming it's a genuine, necessary fix — but
Layer 2's checksum-offload setting had silently reverted (due to the VM reboot this migration
itself required) by the time that longer test ran, so it isn't yet confirmed whether Layer 3 on
its own is fully sufficient, or whether the remaining "Bad Packet" kicks in that test were really
Layer 2 resurfacing. Retest with **both** layers genuinely active at once (Layer 2's Scheduled
Task now makes that durable across reboots) before concluding either one is incomplete.

All three layers were confirmed genuinely, simultaneously active on a real test (`BEService`
running, checksum offload actually `Disabled` on the guest adapter at that moment, agent running
via the Scheduled Task) — and "Bad Packet" still happened. A direct control test then confirmed
this is NOT a generic network-quality issue: a manually-launched (via Steam directly) session on
the exact same VM, same server, survived 10+ minutes with no problem. So a real, fourth cause
remained, specific to how the agent launches the game versus a human.

### Layer 4 (the actual remaining root cause): launching `DayZ_x64.exe` directly instead of through `DayZ_BE.exe`

**Confirmed root cause**: comparing a manual session's actual RPT-logged command line against an
agent-launched one revealed DayZ ships a **separate BattlEye-aware host executable**,
`DayZ_BE.exe`, alongside `DayZ_x64.exe` — confirmed directly from DayZ Launcher's own log
(`Launcher.log`): `GameLocator: 64-bit: DayZ_x64.exe, BE: DayZ_BE.exe`, and
`BattlEyeExecutablePath: D:\SteamLibrary\steamapps\common\DayZ\DayZ_BE.exe`. Clicking Play in the
DayZ Launcher actually starts the game via `DayZ_BE.exe`, which properly establishes BattlEye's
hooks before the real game process starts. The agent's launch used
`steam.exe -applaunch 221100 ... -nolauncher` — `-nolauncher` bypasses the DayZ Launcher
application entirely, and skipping the Launcher also means skipping its `DayZ_BE.exe` step:
Steam falls through to starting `DayZ_x64.exe` **directly**. The game still connects and plays
completely normally either way (BattlEye isn't required just to connect), but a directly-started
`DayZ_x64.exe` process never gets BattlEye's hooks correctly established the way `DayZ_BE.exe`
establishes them, and BattlEye's own periodic in-session validation eventually notices and kicks
it as anomalous ("Bad Packet") — something that never happened for the Launcher-routed manual
session.

(A secondary difference also visible in that comparison, not yet acted on: the manual session's
mod paths referenced each mod's real folder name inside `!Workshop\` — e.g. `!Workshop\@CF`,
`!Workshop\@BaseBuildingPlus` — rather than the numeric Workshop ID our junctions use, and it
included a `-password=` the agent's launch didn't. If Layer 4 alone doesn't fully resolve this,
revisit both: reading each mod's real name, likely from its local `steamapps/workshop/content/
221100/<id>/meta.cpp`, to name the junction after it instead of the numeric ID; and confirming
`RequiredMods`'/the client's `ServerPassword` matches whatever this server actually expects.)

**Fix**: `SteamManager.LaunchAppViaSteam` now launches `DayZ_BE.exe` directly (discovered via
`DiscoverDayZBattlEyeExePath()`, found alongside `DayZ_x64.exe` in DayZ's own install directory)
instead of going through `steam.exe -applaunch ... -nolauncher`, setting the
`SteamAppId`/`SteamGameId` environment variables the same way `DayZLauncher.exe` itself does
(from its own log) so Steamworks still initializes correctly for a process not started via
Steam's own `-applaunch` — this only requires the Steam client to already be running. Falls back
to the old `-applaunch` mechanism if `DayZ_BE.exe` can't be found (an unexpected install layout),
rather than failing outright, though that reintroduces the risk described above. Re-publish and
restart the agent inside each VM to pick this up:
```powershell
dotnet publish H:\DayZProject\src\GameFarm.Agent -c Release -o C:\GameFarmAgent
Get-Process GameFarm.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
Start-ScheduledTask -TaskName "Game Farm Agent"
```
If BattlEye still kicks after this, check the agent log for a "DayZ_BE.exe not found" warning
(meaning it fell back to the old mechanism), and check for a fresh
`BEClient_x64_<date>.log` under `<DayZ install>\battleye\` to confirm whether BattlEye's client
is actually attaching this time.

### Other ruled out / superseded investigations (kept for context)

Two other theories were pursued before the `BEService` finding above, in case they resurface as
contributing factors once BattlEye is actually running correctly:

- **Idle timers / screensaver**: nobody is physically touching the VM's mouse/keyboard once the
  dashboard starts a session, unlike a real player. `ReconnectWatchdogService` now calls
  `IdleActivitySuppressor.KeepSystemAndDisplayAwake()` every tick while DayZ is running
  (`SetThreadExecutionState`, prevents Windows' display/sleep power timers specifically — not a
  screensaver or a "Machine inactivity limit" policy, which key off actual input activity
  instead). Also worth doing at the OS level, ideally once in the master image:
  ```powershell
  powercfg /change monitor-timeout-ac 0
  powercfg /change standby-timeout-ac 0
  powercfg /change hibernate-timeout-ac 0
  Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name ScreenSaveActive -Value 0
  ```
  and check `secpol.msc` → Local Policies → Security Options → "Interactive logon: Machine
  inactivity limit" if the VM was provisioned from a template with Group Policy applied.
- **Mod load order**: `RequiredMods` was entered as Workshop IDs sorted in ascending numeric
  order (from a directory listing), not the server's actual required order — Workshop IDs have
  no relationship to dependency order. The RPT log showed a repeated `NULL pointer to instance`
  script exception (`DayZGame.GetBBPBuildTools`, called every frame from `AirborneAI`) right
  before a "Game restart required" fault, consistent with `BaseBuildingPlus` not being
  initialized before `AirborneAI` starts calling into it — a real mod-load-order sensitivity,
  independent of BattlEye. If kicks persist after fixing `BEService`, get the server's actual
  required mod order (its Discord/website, or a published Workshop Collection) and match
  `RequiredMods`'s order to it exactly.

## Create Client fails with "New-VHD ... The process cannot access the file because it is being used by another process" (Hyper-V only)

Almost always means the **master VM (`DayZ-Master`) is currently running**. `New-VHD -Differencing`
needs to open the parent VHDX (the master's OS disk, or `SteamLibrary-Master.vhdx`) to build the
new client's differencing disk against it, and Hyper-V holds that file locked while the master VM
is actively running. Per docs/MASTER-IMAGE.md, the master should never be booted except for a
deliberate rebuild — check for this first:
```powershell
Get-VM -Name "DayZ-Master"
```
If it shows `Running`, shut it down and retry:
```powershell
Stop-VM -Name "DayZ-Master"
```
(add `-Force` if it doesn't respond to a graceful shutdown). Once it's `Off`, the client creation
should succeed. Consider marking the master's disks read-only (`attrib +r`) once it's off, as a
safety net against this happening again by accident.

If `Get-VM` itself fails with "You do not have the required permission..." rather than showing
a VM list, that means the *checking* command wasn't run with Hyper-V administrator rights — not
that no VM is running. Don't take an empty/failed result at face value in that case; re-run from
an elevated PowerShell session.

## Create Client fails with `Set-VMMemory : Cannot convert 'System.String' to the type 'System.Boolean'` (`$$false`/`$$true` in the error) (Hyper-V only)

Fixed as of the current version. `HyperVVirtualMachineProvider`'s VM-creation script had a
duplicated `$` in its raw-string interpolation:
`-DynamicMemoryEnabled ${{(request.DynamicMemory ? "$true" : "$false")}}` — the C# ternary
already produces the string `"$true"`/`"$false"` (PowerShell's actual boolean literal syntax),
but a second, literal `$` right before the interpolation braces meant the generated script
always read `-DynamicMemoryEnabled $$false` (or `$$true`), which PowerShell parses as the
literal string `$false`/`$true`, not a boolean — hence the type-conversion error. Fixed by
removing the redundant leading `$`. No republish needed — this is Controller-side (HyperV
project), not something running inside any VM; just rebuild/republish the Controller.

If you hit this before the fix below existed, the `New-VM` step had already succeeded before
`Set-VMMemory` failed, leaving a half-configured VM behind that then blocks recreation with "VM
already exists" — see the next entry for both the manual cleanup and why it won't recur.

## Create Client fails with "VM '<name>' already exists" for a client you never successfully finished creating (Hyper-V only — see docs/VMWARE-SETUP.md for the VMware equivalent, which has the same cleanup-on-failure behavior already)

Fixed as of the current version. `CreateAsync`'s VM-creation script had no cleanup path: if any
step failed partway through (the `Set-VMMemory` bug above being one concrete example, but any
future mid-script failure has the same effect), `New-VM` had typically already succeeded, so a
half-configured VM (and possibly a disk file) was left behind — invisible to the dashboard
(the client record was never saved, since the whole `CreateAsync` call still threw), but very
much still real in Hyper-V, and blocking every subsequent retry of that same client name.

Manual cleanup for a client already stuck this way — `scripts/Hyper-V/Remove-Client.ps1` already handles
exactly this (it never touches the Controller's database, only Hyper-V/disk state, so it's safe
to run against a stuck VM that was never actually registered as a client):
```powershell
.\Remove-Client.ps1 -Name "<name>"
```
Fixed going forward by wrapping the whole creation script in `try`/`catch`: any failure now
best-effort tears down whatever *that same attempt* already created (the VM, if it exists, and
both possible disk files) before re-throwing the original error — never anything pre-existing,
so a genuinely-already-existing client's own VM is never touched. A failed Create Client is now
always safe to simply retry once the real underlying cause (whatever the error message says) is
fixed, without any manual Hyper-V cleanup step first.

## A client shows "Agent: Offline"

- Confirm the VM is actually running (`VM State` column). The agent can't respond if the VM is
  off/starting.
- Confirm the guest has a DHCP-assigned IP (`GuestIpAddress` in the client detail page). If
  blank: **Hyper-V** — the integration services (`Enable-VMIntegrationService`) may not be
  enabled, or the guest hasn't finished booting yet. **VMware Workstation** — VMware Tools may
  not be installed/running in the guest (`vmrun getGuestIPAddress` needs it — see
  docs/VMWARE-SETUP.md), or the guest hasn't finished booting yet.
- Check the agent's Scheduled Task status inside the guest: `Get-ScheduledTask -TaskName "Game Farm Agent"`
  (`State` should be `Running`), and confirm `GameFarm.Agent.exe` actually appears in
  `Get-Process`. The task only starts when the account it's registered for is logged on — if
  the VM booted but auto-logon isn't configured/working, the task never starts. See
  docs/TROUBLESHOOTING.md's BattlEye section for how the agent is installed. Identical regardless
  of hypervisor.
- Check the agent is listening on the configured port (`Agent:ListenPort`, default 5099) and
  that Windows Firewall inside the guest allows inbound TCP on that port from the farm's network
  — the Hyper-V switch subnet or the VMware `vmnetN` subnet, whichever backend is active (see
  "Firewall requirements" below).
- Verify the registered agent token matches the one in
  `%ProgramData%\GameFarmAgent\agent-token.secret` inside that VM — a mismatch returns 401 and
  is reported as the agent being unreachable/offline from the controller's perspective. Identical
  regardless of hypervisor.

### Firewall requirements

Inside each client VM, allow inbound TCP on the agent port from the farm's network subnet only
(the Hyper-V switch's subnet, or the VMware Workstation `vmnetN` subnet — same rule either way):

```powershell
New-NetFirewallRule -DisplayName "Game Farm Agent" -Direction Inbound -Protocol TCP -LocalPort 5099 -Action Allow
```

For tighter scoping, restrict `-RemoteAddress` to the controller host's IP on that network, and
set `Agent:ListenAddress` in the agent's `appsettings.json` to the guest's private adapter IP
rather than `0.0.0.0`.

## Steam won't log in / keeps asking for Steam Guard

This is expected the very first time an account is used on a fresh differencing disk — see
docs/STEAM-SETUP.md. If it happens repeatedly on a VM that was already logged in, the VM's disk
may have been recreated (differencing disk lost), or Steam's local cache was cleared. Re-do the
manual login step.

## DayZ won't launch / immediately exits

- Confirm DayZ shows as owned/installed on that Steam account (`steam://validate/221100` inside
  the VM console, or Steam library).
- Check `Get-Content <profile>\*.RPT | Select-Object -Last 100` for the newest log referenced by
  `GET /api/clients/{id}/logs` — BattlEye or missing-file errors usually appear there.
- Confirm the master image's DayZ install wasn't left on an incompatible graphics/driver
  configuration — see docs/MASTER-IMAGE.md's Graphics section.

## Connection status seems wrong / stuck on "Connecting"

`IGameStatusProvider`'s default implementation (`DayZGameStatusProvider`) is a **best-effort**
heuristic based on scanning DayZ's own RPT/ADM log files for known strings, because DayZ does not
expose a supported connection-state API. It can lag behind reality by a few seconds (poll
interval) and can occasionally misclassify an edge case. This is intentionally isolated behind
the `IGameStatusProvider` interface so a more precise mechanism can replace it later without
touching any caller.

## Resetting/rebuilding a single VM

```powershell
.\scripts\Hyper-V\Remove-Client.ps1 -Name DayZ-007
.\scripts\Hyper-V\Create-Client.ps1 -Name DayZ-007 -Start
```

> **VMware Workstation:**
> ```powershell
> .\scripts\VMware\Remove-Client.ps1 -Name DayZ-007
> .\scripts\VMware\Create-Client.ps1 -Name DayZ-007 -MasterVmx "D:\path\to\Master.vmx" -Start
> ```

Then re-register that client's Steam login (docs/STEAM-SETUP.md) and agent token — a fresh
differencing disk/linked clone has no saved Steam session or agent token file, regardless of
hypervisor.

## Rebuilding the master image

See docs/MASTER-IMAGE.md's "Rebuilding the master after a major DayZ update" section (Hyper-V),
or docs/VMWARE-SETUP.md's "Rebuilding the master after a major DayZ update" section (VMware
Workstation). Existing clients must be recreated against the rebuilt master/new snapshot either
way; there is no supported in-place patch of the differencing parent (Hyper-V) or a snapshot's
already-taken state (VMware).

## "Update All" seems slow / clients update in waves

This is intentional — see `UpdateBatchSize` in docs/ARCHITECTURE.md. Increase it (fewer, larger
batches) if your WAN connection can handle more simultaneous Steam updates, or decrease it if you
see contention.
