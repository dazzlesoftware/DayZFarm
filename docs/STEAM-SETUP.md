# Steam Account Setup

This system never stores Steam passwords, never bypasses Steam authentication, and never
emulates the Steam client. Every client VM uses one real, legitimate Steam account that owns
DayZ, logged in **manually** by an administrator, once per VM.

## Why this step is manual

Steam Guard (email or mobile authenticator confirmation) cannot be automated without either
storing credentials insecurely or working around Steam's own security — both are explicitly out
of scope. Steam does, however, remember a successful login per Windows user profile, so this is
a **one-time** step per client VM.

## Procedure (per client VM)

1. Start the client VM (`scripts\Hyper-V\Start-Client.ps1 -Name DayZ-001` or
   `scripts\VMware\Start-Client.ps1 -Name DayZ-001`, whichever backend is active — or via the
   dashboard, same either way).
2. Connect to its console — **Hyper-V:** Hyper-V Manager → Connect (or Enhanced Session if
   enabled). **VMware Workstation:** the VM's own window is already a console — just click into
   it, or **VM menu → Send Ctrl+Alt+Del** if it's not focused.
3. Start Steam (or use the dashboard's "Start Steam" button, which starts the Steam client
   process — it does not log anyone in).
4. Log in with that client's dedicated Steam account credentials.
5. Complete Steam Guard (email code or mobile authenticator) if prompted.
6. Check **"Remember my password"** / stay logged in.
7. Confirm the DayZ library entry is visible and, ideally, let Steam finish its first download of
   DayZ once (this can be scheduled off-hours; see update batching in docs/ARCHITECTURE.md for
   avoiding N simultaneous downloads across many VMs).
8. In the Controller, register the client's identifiers (Steam account label / SteamID64 — never
   the password) via `POST /api/clients` when creating the client record.

After this, Steam keeps that account's session inside the VM's own user profile and differencing
disk — the controller and agent never see or store the password.

## Reducing repeated downloads (Steam Local Network Game Transfers)

Steam supports **Local Network Game Transfers**, letting one Steam client on the LAN seed
updates to others without each downloading from Steam's CDN independently. To use it:

- Ensure all client VMs are on the same virtual network / LAN segment — the Hyper-V virtual
  switch (`GameFarmSwitch` by default) or the VMware Workstation custom network
  (`VMwareNetworkName`, e.g. `vmnet2`), whichever backend is active.
- In each VM's Steam client: Settings → Downloads → enable "Stream games from other computers on
  a local network" / ensure local network discovery isn't blocked by VM firewall rules (allow
  Steam's ports, typically UDP 27036 and the dynamic transfer ports Steam negotiates).
- This is a standard, supported Steam feature — nothing here modifies the Steam protocol. It is
  not guaranteed to eliminate all redundant downloads, but meaningfully reduces WAN usage for a
  farm of many clients.
- Combine with `UpdateBatchSize` (docs/ARCHITECTURE.md) so at most a handful of clients update
  concurrently, giving Local Network Transfers time to actually help before the next batch
  starts.

## Registering the agent token

After the agent's first run inside a client VM, it generates a random token at
`%ProgramData%\GameFarmAgent\agent-token.secret`, encrypted at rest with Windows DPAPI (machine
scope). **Opening that file directly (Notepad, etc.) will only ever show encrypted bytes — that's
expected, not a bug.** It can only be decrypted by a process running on that same VM, so retrieve
it from inside the VM console using the provided helper script:

```powershell
.\scripts\Tools\Show-AgentToken.ps1
```

(copy `scripts/Tools/Show-AgentToken.ps1` into the VM first, or run it via a mapped/shared
folder). It prints the plaintext token and copies it to the clipboard. Then register it with the
controller — either paste it into the dashboard's **Register Token** button on that client's row,
or call the API directly:

```http
POST /api/clients/{id}/agent/register-token
{ "clientName": "DayZ-001", "token": "<paste the token here>" }
```

The controller then encrypts it at rest via DPAPI (on the *controller's* machine) and uses it for
all subsequent agent calls to that client.
