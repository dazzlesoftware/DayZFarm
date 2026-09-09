# Troubleshooting

## `Install-Host.ps1` fails with "Class not registered" from Get-WindowsOptionalFeature

This is a known Windows 11 issue with the DISM COM component `Get-WindowsOptionalFeature`
depends on — it is unrelated to whether Hyper-V itself can actually be enabled, and unrelated
to this project's code. `Get-HyperVFeatureState`/`Enable-HyperVFeatureWithFallback`
(scripts/Common.ps1) automatically fall back to `dism.exe` (which uses a different code path)
when this happens, so a single occurrence usually doesn't block you — but if the `dism.exe`
fallback also fails ("Unknown" state reported), try, in order:

0. **Check whether you actually need to fix this at all.** `Get-HyperVFeatureState`/
   `Enable-HyperVFeatureWithFallback` (scripts/Common.ps1) already fall back to `dism.exe`
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

## `Remove-Client.ps1 -Name DayZ-Master` doesn't remove `SteamLibrary-Master.vhdx`

Fixed as of the current version. The previous version only deleted disks it found **attached**
to the VM (`Get-VMHardDiskDrive`) — so if `SteamLibrary-Master.vhdx` was created as a file but
never actually got attached (the exact failure mode described above, from a script run before
`$ErrorActionPreference = 'Stop'` was added everywhere), the file was invisible to it as long as
the VM still existed, and got left behind.

`Remove-Client.ps1` now also checks every conventional path a client or master could plausibly
own — including `Images\SteamLibrary-Master.vhdx` — regardless of what's actually attached. To
keep this safe, it **refuses to delete any disk still in use by another VM**, whether that's
another VM's own live attached disk or another VM's differencing parent (`Test-DiskInUseAsParent`
in `scripts/Common.ps1`) — so removing an ordinary client can never accidentally delete the
master's shared Steam Library disk out from under still-running clients, and removing the master
itself will only actually delete that shared disk once no client depends on it anymore. If it
skips a disk, it says so explicitly (`Skipping delete of '...' -- it is still in use...`) rather
than silently leaving it — that's not a bug, it means something else still needs it.

## `Create-Master.ps1` (or `Create-Client.ps1`) says a VM/disk "already exists" right after cleanup

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
Remove-Item D:\DayZFarm\Images\DayZ-Master.vhdx -Force   # or the client's Instances\<name>\<name>.vhdx
.\scripts\Create-Master.ps1 -CpuCount 4 -MemoryGB 6 -SizeGB 80 -VirtualSwitchName DayZFarmSwitch -IsoPath <correct path>
```

## `JsonException: ... could not be converted ... BytePositionInLine: 1` from status polling

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

## `ConvertTo-Json : A parameter cannot be found that matches parameter name 'AsArray'`

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

## `JsonException: The JSON value could not be converted to System.String. Path: $[0].IpAddress`

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

## Dashboard's VM actions (Start/Stop/Restart) silently do nothing at all

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

## Dashboard shows a VM as "Unknown"/offline with no IP, even though it's actually Running

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
dotnet publish H:\DayZProject\src\DayZFarm.Controller -c Release -o C:\DayZFarm\Controller
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
`Registry.CurrentUser\Software\Valve\Steam\ActiveProcess\ActiveUser`. The agent runs as a
`LocalSystem` Windows Service (Session 0 — see `sc.exe create` in the root README's guest-agent
install steps), while Steam runs in the VM's actual interactive desktop session (whatever
account you log into the VM as, e.g. via Hyper-V console or RDP). `Registry.CurrentUser` from a
Session-0 LocalSystem process resolves to LocalSystem's own hive — a completely different,
unrelated registry tree from the interactive user's — so the `ActiveProcess` key was never
found there, and the login check always reported "not logged in" no matter what Steam's actual
state was.

The fix scans every loaded user hive under `HKEY_USERS` instead of assuming
`Registry.CurrentUser`: any interactively logged-on user's hive is mounted under
`HKEY_USERS\<SID>` for as long as their session is active, regardless of which session the
*reading* process itself happens to run in. This needs no prior knowledge of which account is
logged into the VM. Re-publish and restart the agent inside each VM to pick this up:
```powershell
dotnet publish H:\DayZProject\src\DayZFarm.Agent -c Release -o C:\DayZFarmAgent
Restart-Service "DayZ Farm Agent"
```
If this still reports "not logged in" after that, check that the account you logged Steam into
interactively is actually still logged on (not just RDP-disconnected in a way that unloads its
profile) — a genuinely logged-off session unmounts its `HKEY_USERS` hive and this check has
nothing to find.

## Dashboard shows "Agent unreachable: ... HttpClient.Timeout of 15 seconds elapsing" on Start DayZ / Join

Fixed as of the current version. `WindowsDayZLauncher.LaunchAsync` blocks its HTTP response for
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
(`src/DayZFarm.Agent/Interop/InteractiveProcessLauncher.cs`), which uses the standard
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
dotnet publish H:\DayZProject\src\DayZFarm.Agent -c Release -o C:\DayZFarmAgent
Restart-Service "DayZ Farm Agent"
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
fix above) inside `WindowsDayZLauncher.LaunchAsync`, immediately before building the `-mod=`
argument — so `RequiredMods`/the dashboard/`Ensure Mods` all keep working with plain numeric IDs
(simple to read, edit, and match against Steam's own folder names), while the actual OS-level
launch command gets the full paths DayZ needs. Any ID with no installed folder found is logged
as a warning and simply omitted from the launch rather than failing the whole launch. Re-publish
and restart the agent inside each VM to pick this up:
```powershell
dotnet publish H:\DayZProject\src\DayZFarm.Agent -c Release -o C:\DayZFarmAgent
Restart-Service "DayZ Farm Agent"
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
dotnet publish H:\DayZProject\src\DayZFarm.Agent -c Release -o C:\DayZFarmAgent
Restart-Service "DayZ Farm Agent"
```
If mods are still kicked as "missing" immediately after this, check the agent log for a "Failed
to create !Workshop junction" warning — that means junction creation itself failed (e.g. DayZ's
install directory wasn't found, or a permissions issue), and mods fell back to the absolute path
again. If the BattlEye kick still happens even with the junction genuinely in place, this theory
is likely wrong and the real cause lies elsewhere (see the idle/inactivity section below, or a
possible genuine BattlEye service issue).

## "BattlEye: Game restart required" kicks partway into every agent-launched session

**Confirmed root cause**: `BEService` (BattlEye's persistent Windows service — distinct from its
per-session game client) is installed by DayZ set to **Manual** start (`DEMAND_START`) and not
actually running, and stays that way on a freshly installed or cloned VM until something starts
it at least once. Confirmed via:
```powershell
Get-Service BEService     # Status: Stopped
sc.exe qc BEService        # START_TYPE: 3  DEMAND_START
```
and by the complete absence of any `BEClient_x64_<date>.log` under
`<DayZ install>\battleye\` (only the `BEClient_x64.dll` itself is present) — that log is written
fresh every session BattlEye's client actually initializes, so a total absence of it across every
session means the client never properly attaches. Without a running `BEService`, DayZ can still
connect to a server normally, but BattlEye is never actually protecting the session — the server
eventually notices and kicks with "kicked off by BattlEye: Game restart required" (server-side
log shows this as kick code `240`). This reproduced identically regardless of mod load order or
launch method once actually tested carefully — it is not related to the mod-path/order
investigations above, and not related to idle timers; both were reasonable hypotheses at the
time but were superseded by this more direct evidence.

**Fix** (inside the VM, elevated PowerShell):
```powershell
Set-Service BEService -StartupType Automatic
Start-Service BEService
Get-Service BEService     # should now show Running
```
Since every client differences off the master image, this should be fixed **once, in the master
image itself** (see docs/MASTER-IMAGE.md's "Manual steps" — a step for this was added there) so
every new client inherits `BEService` already set to auto-start, rather than needing this
per-client. For clients already created before this fix existed, run the two commands above on
each one (or accept the current session's disk and re-run `Create-Client.ps1` after fixing the
master, for a future client).

If BattlEye still kicks after confirming `BEService` is genuinely `Running`, check for a fresh
`BEClient_x64_<date>.log` under `<DayZ install>\battleye\` as direct confirmation the client
component is actually attaching, and if it now exists, look inside it for a specific rejection
reason rather than assuming the same root cause is still at play.

### Ruled out / superseded investigations (kept for context)

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

## A client shows "Agent: Offline"

- Confirm the VM is actually running (`VM State` column). The agent can't respond if the VM is
  off/starting.
- Confirm the guest has a DHCP-assigned IP (`GuestIpAddress` in the client detail page). If
  blank, the Hyper-V integration services (`Enable-VMIntegrationService`) may not be enabled, or
  the guest hasn't finished booting yet.
- Check the agent's Windows Service status inside the guest: `Get-Service "DayZ Farm Agent"`.
- Check the agent is listening on the configured port (`Agent:ListenPort`, default 5099) and
  that Windows Firewall inside the guest allows inbound TCP on that port from the Hyper-V switch
  subnet (see "Firewall requirements" below).
- Verify the registered agent token matches the one in
  `%ProgramData%\DayZFarmAgent\agent-token.secret` inside that VM — a mismatch returns 401 and
  is reported as the agent being unreachable/offline from the controller's perspective.

### Firewall requirements

Inside each client VM, allow inbound TCP on the agent port from the Hyper-V switch's subnet
only:

```powershell
New-NetFirewallRule -DisplayName "DayZ Farm Agent" -Direction Inbound -Protocol TCP -LocalPort 5099 -Action Allow
```

For tighter scoping, restrict `-RemoteAddress` to the controller host's IP on the virtual
switch, and set `Agent:ListenAddress` in the agent's `appsettings.json` to the guest's private
adapter IP rather than `0.0.0.0`.

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

`IDayZStatusProvider`'s default implementation (`DayZLogStatusProvider`) is a **best-effort**
heuristic based on scanning DayZ's own RPT/ADM log files for known strings, because DayZ does not
expose a supported connection-state API. It can lag behind reality by a few seconds (poll
interval) and can occasionally misclassify an edge case. This is intentionally isolated behind
the `IDayZStatusProvider` interface so a more precise mechanism can replace it later without
touching any caller.

## Resetting/rebuilding a single VM

```powershell
.\scripts\Remove-Client.ps1 -Name DayZ-007
.\scripts\Create-Client.ps1 -Name DayZ-007 -Start
```

Then re-register that client's Steam login (docs/STEAM-SETUP.md) and agent token — a fresh
differencing disk has no saved Steam session or agent token file.

## Rebuilding the master image

See docs/MASTER-IMAGE.md's "Rebuilding the master after a major DayZ update" section. Existing
clients must be recreated against the rebuilt master; there is no supported in-place patch of the
differencing parent.

## "Update All" seems slow / clients update in waves

This is intentional — see `UpdateBatchSize` in docs/ARCHITECTURE.md. Increase it (fewer, larger
batches) if your WAN connection can handle more simultaneous Steam updates, or decrease it if you
see contention.
