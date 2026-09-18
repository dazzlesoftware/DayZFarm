# Plugins

Both the Controller (host-side) and the guest Agent (per-client, inside each VM) can run
admin-configured commands through a small, shared plugin system (`GameFarm.Core.Plugins`). This
document covers what it is, the safety model behind it, and how to add a plugin.

## What this is (and isn't)

A **plugin** is one JSON file, placed on disk by an administrator, that names a fixed command:

```json
{
  "name": "list-network-adapters",
  "description": "Lists network adapters and their status",
  "executable": "powershell.exe",
  "arguments": "-NoProfile -Command \"Get-NetAdapter | Format-Table -AutoSize | Out-String -Width 200\"",
  "timeoutSeconds": 30
}
```

The dashboard and API can only ever trigger a plugin **by name** — `POST
/api/plugins/{name}/run` (host) or `POST /api/clients/{id}/plugins/{name}/run` (a specific
client's guest). **Neither route, nor anything reachable from the browser, accepts command text,
arguments, or an executable path.** What a plugin actually runs is entirely fixed by whoever has
filesystem access to author its JSON file on that machine — the same trust boundary as, say,
editing `appsettings.json` or a Scheduled Task.

This is deliberate: the Controller's dashboard/API in this project has no authentication of its
own (it relies on network-level trust — see docs/ARCHITECTURE.md). A generic "run this arbitrary
command string" endpoint reachable from the browser would be an open remote-code-execution
surface to anyone who can reach that port. A fixed, named, admin-authored command is not — it's
no more of a risk than any of the other actions already on the dashboard (Start VM, Update, etc.),
which are equally "the browser tells a privileged process to do something," just with a smaller,
hardcoded menu. Plugins extend that same menu without requiring a rebuild, while keeping the same
safety property: **the browser picks from a menu; it never writes the menu.**

## Where plugins run, and with what privileges

Plugins run with whatever privileges the calling process already has — nothing here elevates
anything:

- **Host plugins** (`GameFarmOptions.PluginsDirectory`, default `<RootDirectory>\Config\Plugins\`)
  run as the Controller itself — the same account already managing Hyper-V/VMware Workstation.
- **Guest plugins** (`AgentOptions.PluginsDirectory`, default `C:\GameFarmAgent\Plugins\`) run as
  the Agent itself — the interactive account it's installed for (see
  scripts/Install-Agent.ps1 and docs/TROUBLESHOOTING.md's BattlEye section for why the Agent
  runs interactively rather than as a Windows Service).

A plugin JSON file with no matching `.json` extension, or one that fails to parse, is skipped
(logged as a warning) rather than crashing the whole list — one bad file never takes down every
other plugin.

## Shipped examples

Two ready-made plugin definitions ship in the repo (not auto-installed — copy them to the real
plugin directory yourself, per their comments):

- `config/Plugins/host-info.json` — a host plugin; prints basic OS info/uptime. Copy to
  `GameFarmOptions.PluginsDirectory` (default `D:\GameFarm\Config\Plugins\`) to try the system
  out risk-free.
- `config/AgentPlugins/initialize-vm.json` — a guest plugin named `initialize-vm`: sets
  `BEService` to auto-start and starts it (see docs/TROUBLESHOOTING.md's BattlEye section for
  why every fresh client needs this once). Copy to `AgentOptions.PluginsDirectory` (default
  `C:\GameFarmAgent\Plugins\`) **in the master image**, so every client inherits it already
  configured — matching how `BEService`'s own fix is documented in docs/MASTER-IMAGE.md and
  docs/VMWARE-SETUP.md.

  The client detail page has a dedicated **Initialize VM** button (in the main actions row,
  alongside Start VM/Join Server/etc.) that runs this exact plugin by name — it's just the
  generic `rowActions` mechanism in `detail.js` pointed at `plugins/initialize-vm/run`, since
  that route already fits the same `/api/clients/{id}/{route}` shape every other button uses; no
  separate code path was needed. The plugin still also appears in that client's generic **Guest
  Plugins** panel like any other.

  **Why `Install-Agent.ps1` isn't a plugin, and can't be triggered this way**: a guest plugin
  only runs once the Agent is already up — but `Install-Agent.ps1` is what installs the Agent in
  the first place. There's no agent to ask to install itself. It's also fundamentally a
  **master-image**, one-time step (every client inherits the Agent through its clone/differencing
  disk, per docs/MASTER-IMAGE.md/docs/VMWARE-SETUP.md), not a per-client action — so even setting
  the chicken-and-egg problem aside, it doesn't fit "run this on client X" the way `initialize-vm`
  does. It remains a manual step run once on the master, same as before.

## Adding a plugin

1. Create a `.json` file (any filename) under the relevant `Plugins` directory.
2. Fields:
   - `name` (required) — unique, case-insensitive key used to trigger it. Keep it short and
     URL-safe (it appears in a route path).
   - `description` (optional) — shown on the dashboard.
   - `executable` (required) — an absolute path, or anything resolvable on `PATH` (e.g.
     `powershell.exe`, `cmd.exe`).
   - `arguments` (optional) — the exact, fixed argument string passed to `executable`.
   - `workingDirectory` (optional).
   - `timeoutSeconds` (optional, default `120`) — the plugin is killed and reported as a failure
     if it runs longer than this (mirrors `GameFarm.VMware.ProcessVmrunRunner`'s own defensive
     timeout — see docs/VMWARE-SETUP.md's troubleshooting section for why this matters: a
     command waiting on something that never responds must never hang the caller forever).
3. No restart needed — plugin definitions are read fresh from disk every time the list/run
   endpoints are called, so adding, editing, or removing a `.json` file takes effect immediately.

## API surface

| Route | What it does |
|---|---|
| `GET /api/plugins` | Lists host plugins (name + description only — never the command itself) |
| `POST /api/plugins/{name}/run` | Runs one host plugin, returns `{ name, success, exitCode, standardOutput, standardError, durationSeconds }` |
| `GET /api/clients/{id}/plugins` | Lists that client's guest-side plugins (proxied through its Agent) |
| `POST /api/clients/{id}/plugins/{name}/run` | Runs one guest-side plugin on that client's VM |

The dashboard's home page has a **Host Plugins** panel; each client's detail page has a **Guest
Plugins** panel — both list configured plugins with a Run button and show the last run's
stdout/stderr inline.

## Why JSON-defined commands instead of DLL plugins

Plugins are a fixed JSON description of a command (`executable` + `arguments`), not a loaded
`.dll` running arbitrary code inside the Controller/Agent process. This was a deliberate choice,
not a missing feature:

- **Blast radius.** A JSON file is inert data — at worst it names a process to spawn with fixed
  arguments. A `.dll` plugin runs *inside* the host process, with full access to everything that
  process can reach (the secret store, the VM provider, the database) — a much bigger risk if that
  file is ever tampered with or sourced from somewhere untrusted, especially given the dashboard
  has no authentication of its own (see above).
- **No loader complexity.** DLL plugins need `AssemblyLoadContext` isolation, ABI/version
  compatibility with whatever .NET the host is running, and careful unload handling to avoid
  leaking memory or file handles across reloads. A JSON plugin is just `File.ReadAllText` +
  `JsonSerializer.Deserialize`, reloaded fresh on every call — see `PluginService.ListPlugins`.
- **It matches what plugins are for.** They exist to let an administrator add "run this
  approved script/command" without a rebuild — not to extend the application's own logic.
  Anything a DLL plugin could do that a JSON one can't (call internal APIs, manipulate VM state
  directly, return structured data beyond stdout/exitcode) is already better served by a real
  method on `ClientOrchestrator`/`IVirtualMachineProvider` plus an API route — a deliberate,
  reviewed code change, not something droppable into a folder at runtime.

If a real need for in-process extensibility ever comes up (not just "run a process"), that's the
point to revisit this — a `.dll`/MEF-style model is a reasonable answer to that specific problem,
but it isn't one today.

## Design notes / what's NOT built yet

- **No plugin arguments from the caller.** A plugin's `arguments` are entirely fixed in its JSON
  file. If a plugin needs to act on a specific client (e.g. "restart Explorer on VM X"), that's
  already implicit in which route was called (`/api/clients/{id}/plugins/...` targets that one
  client's own guest Agent) — there's no mechanism for the dashboard to pass extra parameters
  into a plugin's command line, by design.
- **No per-plugin authorization/role model.** Anyone who can reach the dashboard can run any
  configured plugin — matches every other action already there. If you need finer-grained
  control, that's a bigger change (the Controller doesn't have a user/role system today at all).
- **No audit trail beyond the existing logs.** A guest plugin run is written to that client's
  action log the same way any other agent command is (`ClientOrchestrator`'s `_actionLog`); a
  host plugin run is only in the Controller's own Serilog output today — a dedicated
  plugins-specific log file would be a reasonable follow-up if this sees heavy use.
