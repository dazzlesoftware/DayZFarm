#Requires -Version 7.0
<#
.SYNOPSIS
    Bulk-creates a numbered range of DayZ client VMs as VMware Workstation linked clones -- the
    VMware equivalent of scripts/Hyper-V/Create-Clients.ps1.

.EXAMPLE
    .\Create-Clients.ps1 -Start 1 -Count 20 -MasterVmx "D:\Virtual Machines\Windows 11 x64\Windows 11 x64.vmx" -StartVms
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [int] $Start,
    [Parameter(Mandatory)] [int] $Count,
    [string] $RootDirectory = "D:\GameFarm",
    [Parameter(Mandatory)] [string] $MasterVmx,
    [string] $MasterVmxSnapshot = "Baseline",
    [int] $CpuCount = 4,
    [int] $MemoryGB = 6,
    [string] $VMwareNetworkName = "vmnet2",
    [string] $VmrunPath = "C:\Program Files\VMware\VMware Workstation\vmrun.exe",
    [switch] $StartVms
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"

$succeeded = @()
$failed = @()

$commonArgs = @{
    RootDirectory     = $RootDirectory
    MasterVmx         = $MasterVmx
    MasterVmxSnapshot = $MasterVmxSnapshot
    CpuCount          = $CpuCount
    MemoryGB          = $MemoryGB
    VMwareNetworkName = $VMwareNetworkName
    VmrunPath         = $VmrunPath
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
