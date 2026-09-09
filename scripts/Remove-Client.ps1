#Requires -Version 7.0
<#
.SYNOPSIS
    Stops and removes a client VM and its disks. Never deletes a disk that's still in use as the
    differencing parent of another VM (e.g. a master's OS/Steam Library disk while clients still
    exist against it) -- see docs/TROUBLESHOOTING.md.

.DESCRIPTION
    Also cleans up orphaned disk files left behind by an earlier partial/failed run even when no
    matching VM exists anymore, or when a disk was created but never actually attached to the VM
    (both documented failure modes -- see docs/TROUBLESHOOTING.md). Checks every disk this could
    plausibly be responsible for -- attached disks, the conventional per-client paths, and the
    shared Steam Library master disk -- and skips (with a clear warning, not a silent no-op) any
    of them still in use as another VM's differencing parent.

.EXAMPLE
    .\Remove-Client.ps1 -Name DayZ-001
    .\Remove-Client.ps1 -Name DayZ-Master
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)] [string] $Name,
    [string] $RootDirectory = "D:\DayZFarm"
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"

Assert-Administrator
Assert-HyperVAvailable

$logDir = Join-Path $RootDirectory 'Logs\Controller'

<#
.SYNOPSIS
    Deletes a disk file, retrying briefly to ride out Hyper-V's management service holding a
    handle on it right after a VM is removed. Returns $true if the file no longer exists
    afterward (including if it was already gone or is protected).
#>
function Remove-DiskFileWithRetry {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $true }

    if (Test-DiskInUseAsParent -Path $Path -ExcludingVm $Name) {
        Write-FarmLog "Skipping delete of '$Path' -- it is still in use as another VM's differencing parent. Remove that VM first if you really want to delete this disk." -Level Warning -LogDirectory $logDir
        return $false
    }

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
            Write-FarmLog "Deleted disk '$Path'." -LogDirectory $logDir
            return $true
        } catch {
            if ($attempt -eq 5) {
                Write-FarmLog "Failed to delete disk '$Path' after 5 attempts (still locked?): $($_.Exception.Message). Delete it manually once nothing has it open." -Level Error -LogDirectory $logDir
                return $false
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

$vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
$attachedDisks = @()

if ($vm) {
    if ($PSCmdlet.ShouldProcess($Name, "Stop and permanently remove VM")) {
        if ($vm.State -ne 'Off') {
            Write-FarmLog "Stopping VM '$Name'..." -LogDirectory $logDir
            Stop-VM -Name $Name -Force
        }

        $attachedDisks = (Get-VMHardDiskDrive -VMName $Name).Path
        Remove-VM -Name $Name -Force
        Write-FarmLog "Removed VM '$Name'." -LogDirectory $logDir
    } else {
        Write-FarmLog "Removal of '$Name' was not confirmed; nothing was changed." -Level Warning -LogDirectory $logDir
        return
    }
} else {
    Write-FarmLog "VM '$Name' does not exist." -Level Warning -LogDirectory $logDir
}

# Beyond whatever was actually attached to the VM, also check every conventional path this
# client/master could plausibly own -- covers disks created but never attached (see
# docs/TROUBLESHOOTING.md) and disks left behind because the VM was already gone before this run.
$candidatePaths = @(
    (Join-Path $RootDirectory "Instances\$Name\$Name.vhdx"),
    (Join-Path $RootDirectory "Instances\$Name\$Name-SteamLibrary.vhdx"),
    (Join-Path $RootDirectory "Images\$Name.vhdx"),
    (Join-Path $RootDirectory "Images\SteamLibrary-Master.vhdx")
)

$allDisks = @($attachedDisks + $candidatePaths) | Where-Object { $_ } | Select-Object -Unique

if (-not $allDisks) {
    Write-FarmLog "No disks found for '$Name'; nothing to remove." -LogDirectory $logDir
    return
}

foreach ($disk in $allDisks) {
    if (-not (Test-Path -LiteralPath $disk)) { continue }
    if ($PSCmdlet.ShouldProcess($disk, "Delete disk file")) {
        [void](Remove-DiskFileWithRetry -Path $disk)
    }
}

$instanceDir = Join-Path $RootDirectory "Instances\$Name"
if (Test-Path -LiteralPath $instanceDir) {
    Remove-Item -LiteralPath $instanceDir -Recurse -Force -ErrorAction SilentlyContinue
}
