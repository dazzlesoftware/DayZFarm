#Requires -Version 7.0
<#
.SYNOPSIS
    Stops (if running) and deletes a VMware Workstation client VM and its linked-clone
    directory -- the VMware equivalent of scripts/Hyper-V/Remove-Client.ps1.

.DESCRIPTION
    Unlike the Hyper-V version, there is no "shared parent disk still in use" check needed here:
    a linked clone's own delta files live entirely inside its own instance directory, and
    deleting them never touches the master VMX or its snapshot -- vmrun's own deleteVM already
    refuses to delete a VMX that still has other clones linked against it.

.EXAMPLE
    .\Remove-Client.ps1 -Name DayZ-001
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)] [string] $Name,
    [string] $RootDirectory = "D:\GameFarm",
    [string] $VmrunPath = "C:\Program Files\VMware\VMware Workstation\vmrun.exe"
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"

$logDir = Join-Path $RootDirectory 'Logs\Controller'
$instanceDir = Join-Path $RootDirectory "Instances\$Name"
$vmxPath = Join-Path $instanceDir "$Name.vmx"

if (-not (Test-Path -LiteralPath $vmxPath)) {
    Write-FarmLog "No VMX found for '$Name' at '$vmxPath'; nothing to remove." -Level Warning -LogDirectory $logDir
    return
}

if (-not $PSCmdlet.ShouldProcess($Name, "Stop and permanently remove VMware client")) {
    Write-FarmLog "Removal of '$Name' was not confirmed; nothing was changed." -Level Warning -LogDirectory $logDir
    return
}

if (Test-VmxRunning -VmxPath $vmxPath -VmrunPath $VmrunPath) {
    Write-FarmLog "Stopping '$Name' before removal..." -LogDirectory $logDir
    Invoke-Vmrun -VmrunPath $VmrunPath -Arguments @('stop', $vmxPath, 'hard') | Out-Null
}

$delete = Invoke-Vmrun -VmrunPath $VmrunPath -Arguments @('deleteVM', $vmxPath)
if (-not $delete.Success) {
    Write-FarmLog "vmrun deleteVM failed for '$Name' ($($delete.StandardError.Trim())); falling back to deleting its directory directly." -Level Warning -LogDirectory $logDir
}

# Belt-and-braces: deleteVM sometimes leaves the directory behind (e.g. a lingering lock) --
# always also remove it directly if still there, mirroring Remove-Client.ps1's Hyper-V twin.
if (Test-Path -LiteralPath $instanceDir) {
    Remove-Item -LiteralPath $instanceDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-FarmLog "Removed VMware client '$Name'." -LogDirectory $logDir
