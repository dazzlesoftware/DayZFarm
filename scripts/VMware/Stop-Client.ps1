#Requires -Version 7.0
<#
.SYNOPSIS
    Gracefully stops a VMware Workstation client VM, forcing a hard power-off if the graceful
    stop fails -- the VMware equivalent of scripts/Hyper-V/Stop-Client.ps1.

.DESCRIPTION
    A "soft" vmrun stop needs VMware Tools running in the guest; without it, vmrun itself can
    hang indefinitely rather than failing outright (confirmed during testing -- see
    docs/VMWARE-SETUP.md's troubleshooting section). Invoke-Vmrun (Common.ps1) bounds every call
    to 45 seconds and kills a hung vmrun process automatically, mirroring
    GameFarm.VMware.ProcessVmrunRunner's own fix, so this always completes one way or another.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Name,
    [string] $RootDirectory = "D:\GameFarm",
    [string] $VmrunPath = "C:\Program Files\VMware\VMware Workstation\vmrun.exe",
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"
Assert-VmrunAvailable -VmrunPath $VmrunPath

$vmxPath = Join-Path $RootDirectory "Instances\$Name\$Name.vmx"
if (-not (Test-Path -LiteralPath $vmxPath)) {
    throw "VM '$Name' not found at '$vmxPath'."
}

if ($Force) {
    $result = Invoke-Vmrun -VmrunPath $VmrunPath -Arguments @('stop', $vmxPath, 'hard')
    if (-not $result.Success) {
        throw "Failed to stop '$Name': $($result.StandardError.Trim())"
    }
} else {
    $soft = Invoke-Vmrun -VmrunPath $VmrunPath -Arguments @('stop', $vmxPath, 'soft')
    if (-not $soft.Success) {
        Write-FarmLog "Graceful stop of '$Name' failed ($($soft.StandardError.Trim())); forcing a hard stop." -Level Warning
        $hard = Invoke-Vmrun -VmrunPath $VmrunPath -Arguments @('stop', $vmxPath, 'hard')
        if (-not $hard.Success) {
            throw "Failed to stop '$Name': $($hard.StandardError.Trim())"
        }
    }
}
Write-FarmLog "Stopped VM '$Name'."
