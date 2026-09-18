#Requires -Version 7.0
<# .SYNOPSIS Starts a VMware Workstation client VM by name -- the VMware equivalent of
   scripts/Hyper-V/Start-Client.ps1. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Name,
    [string] $RootDirectory = "D:\GameFarm",
    [string] $VmrunPath = "C:\Program Files\VMware\VMware Workstation\vmrun.exe",
    # Only needed if this client's VMX is encrypted -- linked clones of an encrypted master
    # inherit its encryption. See docs/VMWARE-SETUP.md.
    [string] $VmxPassword
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"
Assert-VmrunAvailable -VmrunPath $VmrunPath

$vmxPath = Join-Path $RootDirectory "Instances\$Name\$Name.vmx"
if (-not (Test-Path -LiteralPath $vmxPath)) {
    throw "VM '$Name' not found at '$vmxPath'."
}

$result = Invoke-Vmrun -VmrunPath $VmrunPath -VmxPassword $VmxPassword -Arguments @('start', $vmxPath, 'nogui')
if (-not $result.Success) {
    throw "Failed to start '$Name': $($result.ErrorMessage)"
}
Write-FarmLog "Started VM '$Name'."
