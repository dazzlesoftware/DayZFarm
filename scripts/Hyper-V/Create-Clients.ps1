#Requires -Version 7.0
<#
.SYNOPSIS
    Bulk-creates a numbered range of DayZ client VMs (DayZ-001, DayZ-002, ...).

.EXAMPLE
    .\Create-Clients.ps1 -Start 1 -Count 20
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [int] $Start,
    [Parameter(Mandatory)] [int] $Count,
    [string] $RootDirectory = "D:\GameFarm",
    [string] $MasterVhdxPath,
    [int] $CpuCount = 4,
    [int] $MemoryGB = 6,
    [bool] $DynamicMemory = $false,
    [string] $VirtualSwitchName = "GameFarmSwitch",
    [string] $SteamLibraryMasterPath,
    [switch] $SkipSteamLibraryDisk,
    [switch] $StartVms
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"

$succeeded = @()
$failed = @()

# Only forward -SteamLibraryMasterPath if the caller actually specified it -- Create-Client.ps1
# treats an explicitly-passed path as required (throws if missing) but auto-detects and silently
# skips the conventional default path if it's simply not there. Splatting an unset value would
# incorrectly make every client treat auto-detection as a hard requirement.
$commonArgs = @{
    RootDirectory     = $RootDirectory
    MasterVhdxPath    = $MasterVhdxPath
    CpuCount          = $CpuCount
    MemoryGB          = $MemoryGB
    DynamicMemory     = $DynamicMemory
    VirtualSwitchName = $VirtualSwitchName
}
if ($PSBoundParameters.ContainsKey('SteamLibraryMasterPath')) {
    $commonArgs['SteamLibraryMasterPath'] = $SteamLibraryMasterPath
}
if ($SkipSteamLibraryDisk) {
    $commonArgs['SkipSteamLibraryDisk'] = $true
}

for ($i = $Start; $i -lt ($Start + $Count); $i++) {
    $name = Format-ClientName -Index $i
    try {
        & "$PSScriptRoot\Create-Client.ps1" -Name $name @commonArgs -Start:$StartVms
        $succeeded += $name
    } catch {
        Write-FarmLog "Failed to create ${name}: $($_.Exception.Message)" -Level Error -LogDirectory (Join-Path $RootDirectory 'Logs\Controller')
        $failed += $name
    }
}

Write-Host ""
Write-Host "Created: $($succeeded.Count)  Failed: $($failed.Count)"
if ($failed.Count -gt 0) {
    Write-Host "Failed clients: $($failed -join ', ')"
    exit 1
}
