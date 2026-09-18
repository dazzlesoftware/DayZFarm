#Requires -Version 7.0
<#
.SYNOPSIS
    Takes a named snapshot of the master VMX -- the one part of docs/VMWARE-SETUP.md's master
    build steps vmrun can actually script.

.DESCRIPTION
    There is no vmrun equivalent to Hyper-V's New-VM -- building the master VM itself still
    requires VMware Workstation's own New Virtual Machine wizard (see docs/VMWARE-SETUP.md, step
    3). Once Windows/Steam/DayZ/the Agent are installed and the master is shut down cleanly,
    this script takes the snapshot every client is linked-cloned against
    (Create-Client.ps1 / GameFarm.VMware.VMwareVirtualMachineProvider.CreateAsync).

    Refuses to snapshot a running VM: linked clones need a clean, powered-off snapshot, not a
    suspended/running one.

.EXAMPLE
    .\New-MasterSnapshot.ps1 -MasterVmx "D:\Virtual Machines\Windows 11 x64\Windows 11 x64.vmx" -SnapshotName Baseline
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $MasterVmx,
    [string] $SnapshotName = "Baseline",
    [string] $VmrunPath = "C:\Program Files\VMware\VMware Workstation\vmrun.exe",
    # Only needed if the master VMX has VMware encryption enabled -- see docs/VMWARE-SETUP.md.
    [string] $VmxPassword
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"
Assert-VmrunAvailable -VmrunPath $VmrunPath

if (-not (Test-Path -LiteralPath $MasterVmx)) {
    throw "Master VMX not found at '$MasterVmx'."
}

if (Test-VmxRunning -VmxPath $MasterVmx -VmrunPath $VmrunPath) {
    throw "Master VMX '$MasterVmx' is currently running. Shut it down cleanly first -- linked clones need a clean, powered-off snapshot."
}

$result = Invoke-Vmrun -VmrunPath $VmrunPath -VmxPassword $VmxPassword -Arguments @('snapshot', $MasterVmx, $SnapshotName)
if (-not $result.Success) {
    throw "Failed to take snapshot '$SnapshotName' on '$MasterVmx': $($result.ErrorMessage)"
}

Write-Host "Took snapshot '$SnapshotName' on '$MasterVmx'. Clients can now be linked-cloned against it."
