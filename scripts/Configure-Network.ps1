#Requires -Version 7.0
<#
.SYNOPSIS
    Creates (or reports) the Hyper-V virtual switch used by the farm, and prints the current
    VM name / MAC / IP associations for every client VM (best-effort guest IP discovery via
    Hyper-V integration services).

.EXAMPLE
    .\Configure-Network.ps1 -VirtualSwitchName DayZFarmSwitch
#>
[CmdletBinding()]
param(
    [string] $VirtualSwitchName = "DayZFarmSwitch",
    [ValidateSet('External','Internal','Private')]
    [string] $SwitchType = 'External',
    [string] $ExternalAdapterName
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"
Assert-Administrator
Assert-HyperVAvailable

$switch = Get-VMSwitch -Name $VirtualSwitchName -ErrorAction SilentlyContinue
if (-not $switch) {
    Write-FarmLog "Creating virtual switch '$VirtualSwitchName'..."
    switch ($SwitchType) {
        'External' {
            if (-not $ExternalAdapterName) {
                $adapter = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.Virtual -eq $false } | Select-Object -First 1
                if (-not $adapter) { throw "No active physical adapter found; pass -ExternalAdapterName." }
                $ExternalAdapterName = $adapter.Name
            }
            New-VMSwitch -Name $VirtualSwitchName -NetAdapterName $ExternalAdapterName -AllowManagementOS $true | Out-Null
        }
        'Internal' { New-VMSwitch -Name $VirtualSwitchName -SwitchType Internal | Out-Null }
        'Private'  { New-VMSwitch -Name $VirtualSwitchName -SwitchType Private | Out-Null }
    }
} else {
    Write-FarmLog "Virtual switch '$VirtualSwitchName' already exists."
}

Write-Host ""
Write-Host "VM Name           MAC Address         Guest IP"
Write-Host "----------------  ------------------  ---------------"
Get-VM | ForEach-Object {
    $adapter = Get-VMNetworkAdapter -VM $_ | Select-Object -First 1
    $ip = ($adapter.IPAddresses | Where-Object { $_ -notmatch ':' } | Select-Object -First 1)
    "{0,-16}  {1,-18}  {2}" -f $_.Name, $adapter.MacAddress, $ip
}
