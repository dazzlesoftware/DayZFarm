#Requires -Version 7.0
<#
.SYNOPSIS
    Verifies and assists with Windows 11 host setup for the DayZ Client Farm.

.DESCRIPTION
    Checks administrator rights, Windows edition, Hyper-V feature/tools, PowerShell version and
    .NET runtime, then creates the D:\DayZFarm directory layout and virtual switch. Never
    reboots automatically -- if enabling Hyper-V requires a reboot, this reports it and exits.

.PARAMETER RootDirectory
    Root storage directory for the farm (default D:\DayZFarm).

.PARAMETER VirtualSwitchName
    Name of the Hyper-V virtual switch to create if missing (default DayZFarmSwitch).

.PARAMETER SwitchType
    Hyper-V switch type: External (default, for internet/LAN access), Internal, or Private.

.EXAMPLE
    .\Install-Host.ps1 -RootDirectory D:\DayZFarm -VirtualSwitchName DayZFarmSwitch
#>
[CmdletBinding()]
param(
    [string] $RootDirectory = "D:\DayZFarm",
    [string] $VirtualSwitchName = "DayZFarmSwitch",
    [ValidateSet('External','Internal','Private')]
    [string] $SwitchType = 'External',
    [string] $ExternalAdapterName
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"

Assert-Administrator

Write-FarmLog "Starting host installation checks." -LogDirectory (Join-Path $RootDirectory 'Logs\Controller')

# --- Windows edition check ---
$os = Get-CimInstance -ClassName Win32_OperatingSystem
Write-FarmLog "Detected OS: $($os.Caption) ($($os.Version))"
if ($os.Caption -notmatch 'Pro|Enterprise|Education|Server') {
    Write-FarmLog "Windows Home edition does not support Hyper-V. Upgrade to Pro/Enterprise/Education." -Level Error
    exit 1
}

# --- PowerShell version ---
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-FarmLog "PowerShell 7+ is recommended. Detected $($PSVersionTable.PSVersion)." -Level Warning
}

# --- .NET runtime check ---
$dotnetVersion = & dotnet --version 2>$null
if (-not $dotnetVersion) {
    Write-FarmLog ".NET SDK/runtime not found on PATH. Install .NET 9 or 10 from https://dotnet.microsoft.com/download" -Level Error
    exit 1
}
Write-FarmLog "Detected .NET SDK: $dotnetVersion"

# --- Hyper-V feature ---
# Uses Get-HyperVFeatureState (Common.ps1), which falls back to dism.exe if
# Get-WindowsOptionalFeature's DISM COM dependency is broken (a known Windows 11 issue —
# "Class not registered" — unrelated to whether Hyper-V itself is actually available; see
# docs/TROUBLESHOOTING.md).
$featureState = Get-HyperVFeatureState
if ($featureState -eq 'Unknown') {
    Write-FarmLog "Could not query Hyper-V feature state via Get-WindowsOptionalFeature OR dism.exe. See docs/TROUBLESHOOTING.md for DISM repair steps (regsvr32 dismcore.dll, restart TrustedInstaller, sfc /scannow)." -Level Error
    exit 1
}

if ($featureState -ne 'Enabled') {
    Write-FarmLog "Hyper-V is not enabled. Enabling now (this may require a reboot)..." -Level Warning
    $restartNeeded = Enable-HyperVFeatureWithFallback
    if ($restartNeeded) {
        Write-FarmLog "Hyper-V was enabled but a REBOOT IS REQUIRED before continuing. Reboot manually, then re-run this script." -Level Warning
        exit 2
    }
} else {
    Write-FarmLog "Hyper-V feature is already enabled."
}

if (-not (Get-Module -ListAvailable -Name Hyper-V)) {
    Write-FarmLog "Hyper-V PowerShell module not found even though the feature is enabled. Ensure 'Hyper-V Management Tools' is installed." -Level Error
    exit 1
}

# --- Directory layout ---
$dirs = @('Images', 'Instances', 'Config', 'Logs\Controller', 'Logs\Clients', 'Backups')
foreach ($d in $dirs) {
    $path = Join-Path $RootDirectory $d
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}
Write-FarmLog "Directory layout ready under $RootDirectory"

# --- Virtual switch ---
$existingSwitch = Get-VMSwitch -Name $VirtualSwitchName -ErrorAction SilentlyContinue
if ($existingSwitch) {
    Write-FarmLog "Virtual switch '$VirtualSwitchName' already exists (Type: $($existingSwitch.SwitchType))."
} else {
    Write-FarmLog "Creating virtual switch '$VirtualSwitchName' (Type: $SwitchType)..."
    switch ($SwitchType) {
        'External' {
            if (-not $ExternalAdapterName) {
                $adapter = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.Virtual -eq $false } | Select-Object -First 1
                if (-not $adapter) { throw "No active physical network adapter found. Pass -ExternalAdapterName explicitly." }
                $ExternalAdapterName = $adapter.Name
            }
            New-VMSwitch -Name $VirtualSwitchName -NetAdapterName $ExternalAdapterName -AllowManagementOS $true | Out-Null
        }
        'Internal' { New-VMSwitch -Name $VirtualSwitchName -SwitchType Internal | Out-Null }
        'Private'  { New-VMSwitch -Name $VirtualSwitchName -SwitchType Private | Out-Null }
    }
    Write-FarmLog "Virtual switch '$VirtualSwitchName' created."
}

Write-FarmLog "Host installation checks complete. Next: create the master VM (see docs/MASTER-IMAGE.md)."
