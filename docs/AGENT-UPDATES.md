# Agent Updates

The Controller can push a new build of the guest Agent out to every client from the dashboard,
instead of manually re-publishing and copying files into each VM by hand.

## What this is (and isn't)

Upload a zip of `dotnet publish src/GameFarm.Agent`'s output once, from the dashboard's **Agent
Package** panel. From then on, each client row (and the client detail page) has an **Update
Agent** button, plus toolbar-level **Update Agent Selected**/**Update Agent All** actions, that
push that same package to one or more clients. Each client applies it to itself and restarts.

This is a full binary replacement, not a fixed named command — different from the plugin system
(see docs/PLUGINS.md), which deliberately only ever lets the dashboard trigger a pre-authored
command by name. An Agent package genuinely *is* arbitrary code, but that's fine here: it's the
Controller pushing its own trusted build to its own agents, the same trust relationship as every
other action the Controller already performs against a VM it manages (creating it, deleting it,
running plugins on it). Nothing about this widens what the dashboard can do to a client that
wasn't already true.

## How it works

1. **Upload** (`POST /api/agent-package`, multipart): the Controller stores the zip at
   `<RootDirectory>\Config\AgentPackage\agent-package.zip`, and computes its own SHA256 hash as
   the package's "version" — automatically, rather than asking an administrator to remember to
   type one in. Only one package is kept at a time; uploading a new one replaces it (no
   history/rollback in v1 — keep a copy of a previous build yourself if you want one).
2. **Push** (`POST /api/clients/{id}/agent/update-package`): the Controller reads the stored zip
   and POSTs its raw bytes to that client's Agent (`AgentApiRoutes.UpdatePackage`), the same way
   it reaches that Agent for any other command — same bearer token, same endpoint resolution.
3. **Apply** (agent-side, `WindowsAgentSelfUpdater`): Windows won't let a running process
   overwrite its own `.exe`/`.dll` files, so the Agent can't just unzip over itself. Instead it:
   - Extracts the package to a staging folder under `%TEMP%`.
   - Writes a small, detached PowerShell script and launches it (not waited on) — then this HTTP
     request returns "update staged" immediately.
   - That script waits ~3 seconds (letting the HTTP response actually reach the Controller),
     kills the Agent process by PID, copies the staged files into the live install directory
     (**never deleting anything not present in the new package** — so admin-authored
     `Plugins\` and this VM's own `Logs\` survive untouched), writes a small
     `agent-version.txt` marker file next to the exe, and restarts the **"Game Farm Agent"**
     Scheduled Task.
4. **Report** (`GET /api/agent/status`): the Agent reads `agent-version.txt` (if present) and
   includes it as `AgentVersion` in its status response. The Controller compares that against the
   currently uploaded package's hash to flag `AgentUpdateAvailable` per client on the dashboard —
   `null`/`false` (not an error) for an agent that's never been updated this way, or one that's
   currently unreachable.

## Uploading a package

```powershell
dotnet publish src/GameFarm.Agent -c Release -o C:\Temp\agent-publish
Compress-Archive -Path C:\Temp\agent-publish\* -DestinationPath C:\Temp\agent-publish.zip
```

Upload `agent-publish.zip` via the dashboard's **Agent Package** panel (or `POST
/api/agent-package` directly, as multipart form data with a `file` field). The panel then shows
the package's version (its SHA256, truncated for display), original filename, size, and upload
time.

## Pushing it out

- **Per-client**: the **Update Agent** button on that client's row, or on its detail page. If the
  Controller's uploaded package differs from what that client last reported, the button is
  flagged (⭗ on the dashboard, "Agent update available: Yes" on the detail page) — but nothing
  stops you from pushing it anyway even without that flag (e.g. to force-reapply the same build).
- **Bulk**: select rows, then **Update Agent Selected**.
- **All clients**: **Update Agent All** (confirmation required, like every other "All" action).

Each push is independent — a client that's offline or has no registered token fails cleanly with
a clear message (same as any other agent command), without blocking the others.

## Safety / failure behavior

- **No package uploaded yet**: pushing fails cleanly with "No agent package has been uploaded
  yet" rather than doing nothing silently.
- **A client is offline/unreachable**: fails the same way any other agent command does — VM not
  running, no known guest IP, or no registered token, each with its own specific message.
- **The update itself fails mid-script** (e.g. `Copy-Item` hits a locked file): the detached
  script logs the failure to `apply-update.log` in its (self-cleaning) staging folder and stops;
  the version marker is only written *after* the copy succeeds, so a failed update never falsely
  reports itself as applied. The Scheduled Task's own restart policy (`-RestartCount 3
  -RestartInterval 1 minute`, set by `Install-Agent.ps1`) still recovers the Agent process itself
  even if the copy step failed, so a client isn't left completely dead — just not updated.
- **Large packages**: both Kestrel's request body limit and the ASP.NET Core multipart form
  parser's own separate limit are raised to 300MB on both the Controller and the Agent (defaults
  are ~28.6MB and 128MB respectively) — comfortably above a typical framework-dependent publish
  output, but this is a hard ceiling if you ever publish a self-contained build instead.
- **Package transfer timeout**: `AgentHttpClient`'s shared `HttpClient` has a 45-second timeout
  (set in `Program.cs`, shared with every other agent call). If your package is large enough
  that pushing it over your farm's network genuinely takes longer than that, raise it there.

## What's NOT built yet

- **No rollback/version history.** Uploading a new package discards the previous one entirely.
  Keep your own copy of a build if you might need to revert to it.
- **No per-client version pinning.** Every client is compared against the one currently uploaded
  package; there's no way to keep one client intentionally on an older build while updating
  others.
- **No signature/integrity check on the package itself** beyond the SHA256 used as its version
  label — this is a hash for identity/comparison, not a cryptographic authenticity check. As with
  plugins (see docs/PLUGINS.md), the trust boundary is "whoever can reach the dashboard and has a
  package file to upload," which is already the same level of trust the Controller extends to
  someone able to create/delete/reconfigure a client.
- **Linux/Proton agent**: `IAgentSelfUpdater` is the extension point (see
  `GameFarm.Core.Interfaces.ExtensibilityInterfaces`) — a future non-Windows agent would need an
  entirely different mechanism (no Scheduled Task, no PowerShell), not just a different file path.
