#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the DayZ Farm Agent inside a client VM as a Scheduled Task that runs directly in
    the interactive logon session, and (optionally) configures Windows auto-logon for that user.

.DESCRIPTION
    Run this INSIDE the client VM (or the master image, before creating differencing clients),
    not on the Hyper-V host.

    An earlier version of this agent ran as a LocalSystem Windows Service (Session 0) and used a
    CreateProcessAsUser/DuplicateTokenEx trick to reach the interactive session in order to
    launch Steam/DayZ. That worked -- Steam/DayZ genuinely launched and connected -- but
    BattlEye consistently kicked every session launched that way ("Bad Packet" / "Game restart
    required"), and never a manually-launched one. BattlEye's anti-tamper checks are known to
    distrust a game process descending from a privileged service using a duplicated security
    token, since that's also how real cheat-injection tooling operates. The fix is to stop
    needing that pattern at all: this script instead registers the agent as a Scheduled Task
    that runs directly as the VM's own interactive user, triggered "At log on" -- Steam/DayZ
    then launch via a plain, unmodified process creation, indistinguishable from a human
    starting them. See docs/TROUBLESHOOTING.md.

    This should be the SAME Windows account you interactively log Steam into for this VM (see
    docs/STEAM-SETUP.md) -- the whole point is for the agent to run in that same session.

    Also registers a second, separate Scheduled Task that disables network checksum offload on
    every network adapter at every boot. BattlEye was observed to eventually kick a session with
    "Bad Packet" when checksum offload is left enabled on this VM's virtual NIC (a known Hyper-V
    synthetic-adapter/anti-cheat interaction). Disabling it via Set-NetAdapterChecksumOffload
    fixes this, but Hyper-V's synthetic adapter does NOT persist that setting across a full VM
    reboot the way a physical NIC driver normally would -- it silently reverts to enabled on
    every boot, so this must be re-applied every boot, not just once. See docs/TROUBLESHOOTING.md.

.PARAMETER AgentExePath
    Path to the published DayZFarm.Agent.exe (default C:\DayZFarmAgent\DayZFarm.Agent.exe).
    Publish it first: dotnet publish src/DayZFarm.Agent -c Release -o C:\DayZFarmAgent

.PARAMETER UserName
    The local Windows account the agent's Scheduled Task should run as (and, unless
    -SkipAutoLogon is passed, that Windows auto-logon will be configured for).

.PARAMETER Password
    That account's password, as a SecureString. Prompted for interactively if not supplied and
    -SkipAutoLogon is not passed. Windows' own AutoAdminLogon mechanism stores this in the
    registry in PLAINTEXT (HKLM\...\Winlogon\DefaultPassword) -- a well-known trade-off of that
    feature. Acceptable for an isolated, single-purpose client VM with its own dedicated Windows
    account and Steam account, but worth knowing about; use -SkipAutoLogon if you'd rather
    configure logon some other way (e.g. it's already configured).

.PARAMETER SkipAutoLogon
    Skip configuring Windows auto-logon. The Scheduled Task is still registered either way -- it
    only actually starts once that user is logged on, by whatever means.

.PARAMETER RunElevated
    Run the Scheduled Task with the account's full/highest privileges instead of its normal
    (limited/standard) token. Only meaningful if -UserName is a local Administrator. Needed only
    if the agent's Reboot/Shutdown commands (`shutdown /r`, `shutdown /s`) fail with access
    denied for that account under its normal token -- most Windows installs grant the "Shut down
    the system" right to all users by default, so this usually isn't necessary. Still a same-
    session elevation (the account's own split token, if it's an admin), NOT a cross-session
    token duplication -- so it does not reintroduce the BattlEye issue this script exists to fix.

.PARAMETER SkipChecksumOffloadFix
    Skip registering the boot-time network checksum offload fix (see DESCRIPTION). Pass this if
    you've already handled it some other way.

.EXAMPLE
    .\Install-Agent.ps1 -UserName Dayz-Master
    (prompts for the password, configures auto-logon, registers the Scheduled Task)

.EXAMPLE
    .\Install-Agent.ps1 -UserName Dayz-Master -SkipAutoLogon
    (auto-logon already configured separately; just registers the Scheduled Task)
#>
[CmdletBinding()]
param(
    [string] $AgentExePath = "C:\DayZFarmAgent\DayZFarm.Agent.exe",
    [Parameter(Mandatory)] [string] $UserName,
    [System.Security.SecureString] $Password,
    [switch] $SkipAutoLogon,
    [switch] $RunElevated,
    [switch] $SkipChecksumOffloadFix
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run from an elevated (Administrator) PowerShell session."
}

if (-not (Test-Path $AgentExePath)) {
    throw "Agent executable not found at '$AgentExePath'. Publish it first: dotnet publish src/DayZFarm.Agent -c Release -o C:\DayZFarmAgent"
}

if (-not (Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue)) {
    Write-Warning "No local user named '$UserName' was found. If this is a domain account, ignore this warning."
}

# --- Remove any earlier LocalSystem-service install, if present -- it and the Scheduled Task
# registered below would otherwise both try to run the agent and fight over its Kestrel port. ---
$existingService = Get-Service -Name "DayZ Farm Agent" -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "Removing previous 'DayZ Farm Agent' Windows Service install..."
    if ($existingService.Status -eq 'Running') {
        Stop-Service -Name "DayZ Farm Agent" -Force
    }
    sc.exe delete "DayZ Farm Agent" | Out-Null
}

# --- Auto-logon ---
if (-not $SkipAutoLogon) {
    if (-not $Password) {
        $Password = Read-Host -Prompt "Password for '$UserName' (for Windows auto-logon)" -AsSecureString
    }
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
    try {
        $plainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    } finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }

    $winlogonPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    Set-ItemProperty -Path $winlogonPath -Name AutoAdminLogon -Value '1'
    Set-ItemProperty -Path $winlogonPath -Name DefaultUserName -Value $UserName
    Set-ItemProperty -Path $winlogonPath -Name DefaultPassword -Value $plainPassword
    Set-ItemProperty -Path $winlogonPath -Name DefaultDomainName -Value $env:COMPUTERNAME
    Remove-Variable plainPassword -ErrorAction SilentlyContinue

    Write-Host "Configured Windows auto-logon for '$UserName'. This VM will now boot straight to that desktop."
} else {
    Write-Host "Skipped auto-logon configuration (-SkipAutoLogon). Make sure '$UserName' is actually logged on interactively for the Scheduled Task to start."
}

# --- Scheduled Task, running directly in that user's interactive session ---
$action = New-ScheduledTaskAction -Execute $AgentExePath
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $UserName
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
$runLevel = if ($RunElevated) { 'Highest' } else { 'Limited' }
$principal = New-ScheduledTaskPrincipal -UserId $UserName -LogonType Interactive -RunLevel $runLevel

Register-ScheduledTask -TaskName "DayZ Farm Agent" -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null

Write-Host "Registered Scheduled Task 'DayZ Farm Agent' (runs as '$UserName', at logon, $runLevel privileges)."
Write-Host "Reboot this VM (or log off/on as '$UserName') to start the agent for the first time."

# --- Network checksum offload fix, re-applied at every boot (see DESCRIPTION) ---
if (-not $SkipChecksumOffloadFix) {
    $offloadCommand = "Get-NetAdapter | Set-NetAdapterChecksumOffload -IpIPv4 Disabled -TcpIPv4 Disabled -UdpIPv4 Disabled -TcpIPv6 Disabled -UdpIPv6 Disabled -ErrorAction SilentlyContinue"

    # Apply immediately too, so this VM doesn't need a reboot to be fixed right now.
    Invoke-Expression $offloadCommand
    Write-Host "Disabled network checksum offload on all adapters (applied immediately)."

    $offloadAction = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -Command `"$offloadCommand`""
    $offloadTrigger = New-ScheduledTaskTrigger -AtStartup
    $offloadPrincipal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName "DayZ Farm - Disable NIC Offload" -Action $offloadAction -Trigger $offloadTrigger -Principal $offloadPrincipal -Force | Out-Null

    Write-Host "Registered Scheduled Task 'DayZ Farm - Disable NIC Offload' (runs as SYSTEM, at every boot)."
} else {
    Write-Host "Skipped the network checksum offload fix (-SkipChecksumOffloadFix)."
}
