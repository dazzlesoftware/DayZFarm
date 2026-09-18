#Requires -Version 7.0
<#
.SYNOPSIS
    Reports VMware Workstation's configured host networks and every known client's current
    guest IP -- the VMware equivalent of scripts/Hyper-V/Configure-Network.ps1.

.DESCRIPTION
    Unlike Hyper-V, vmrun has no command to CREATE a network -- VMware Workstation's custom
    networks (vmnetN) are configured once via Edit -> Virtual Network Editor (as Administrator),
    not scripted. See docs/VMWARE-SETUP.md's network setup section. This script is read-only:
    it lists the host's existing networks and each known client's guest IP for convenience.

.EXAMPLE
    .\Configure-Network.ps1
#>
[CmdletBinding()]
param(
    [string] $RootDirectory = "D:\GameFarm",
    [string] $VmrunPath = "C:\Program Files\VMware\VMware Workstation\vmrun.exe",
    # Only needed if your clients' VMX files are encrypted -- see docs/VMWARE-SETUP.md.
    [string] $VmxPassword
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"
Assert-VmrunAvailable -VmrunPath $VmrunPath

Write-Host "Host networks (vmrun listHostNetworks):"
$networks = Invoke-Vmrun -VmrunPath $VmrunPath -Arguments @('listHostNetworks')
if ($networks.Success) {
    Write-Host $networks.StandardOutput
} else {
    Write-Warning "Failed to list host networks: $($networks.ErrorMessage)"
}
Write-Host "To add a new network: Edit -> Virtual Network Editor (as Administrator). See docs/VMWARE-SETUP.md."
Write-Host ""

$instancesDir = Join-Path $RootDirectory "Instances"
if (-not (Test-Path -LiteralPath $instancesDir)) {
    return
}

Write-Host "VM Name           Guest IP"
Write-Host "----------------  ---------------"
Get-ChildItem -Directory -LiteralPath $instancesDir | ForEach-Object {
    $vmxPath = Join-Path $_.FullName "$($_.Name).vmx"
    if (-not (Test-Path -LiteralPath $vmxPath)) { return }
    $ipResult = Invoke-Vmrun -VmrunPath $VmrunPath -VmxPassword $VmxPassword -Arguments @('getGuestIPAddress', $vmxPath) -TimeoutSeconds 8
    $ip = if ($ipResult.Success) { $ipResult.StandardOutput.Trim() } else { '-' }
    "{0,-16}  {1}" -f $_.Name, $ip
}
