#Requires -Version 7.0
<#
.SYNOPSIS
    Creates a single DayZ client VM as a VMware Workstation linked clone against the master
    VMX's snapshot -- the VMware equivalent of scripts/Hyper-V/Create-Client.ps1.

.DESCRIPTION
    Mirrors GameFarm.VMware.VMwareVirtualMachineProvider.CreateAsync exactly: same vmrun
    invocations, same .vmx edits (numvcpus/memsize/ethernet0.vnet/displayName), same
    cleanup-on-failure behavior (deletes what this attempt created rather than leaving a
    half-built clone that blocks a retry) -- so scripted and dashboard-created clients behave
    identically. Idempotent: refuses to overwrite an existing client directory/.vmx.

    Requires the master VMX to already have a snapshot named -MasterVmxSnapshot (default
    "Baseline") -- take one via New-MasterSnapshot.ps1 or VMware Workstation's own Snapshot menu
    first. See docs/VMWARE-SETUP.md.

.EXAMPLE
    .\Create-Client.ps1 -Name DayZ-001 -MasterVmx "D:\Virtual Machines\Windows 11 x64\Windows 11 x64.vmx" -CpuCount 4 -MemoryGB 6 -Start
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Name,
    [string] $RootDirectory = "D:\GameFarm",
    [Parameter(Mandatory)] [string] $MasterVmx,
    [string] $MasterVmxSnapshot = "Baseline",
    [int] $CpuCount = 4,
    [int] $MemoryGB = 6,
    [string] $VMwareNetworkName = "vmnet2",
    [string] $VmrunPath = "C:\Program Files\VMware\VMware Workstation\vmrun.exe",
    [switch] $Start
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"

Assert-VmrunAvailable -VmrunPath $VmrunPath

$logDirectory = Join-Path $RootDirectory 'Logs\Controller'

if ($Name -notmatch '^[A-Za-z0-9_-]+$') {
    throw "Invalid client name '$Name'. Use only letters, digits, hyphens and underscores."
}

if (-not (Test-Path -LiteralPath $MasterVmx)) {
    throw "Master VMX not found at '$MasterVmx'. VMware has no scripted master-creation step -- " +
        "build it through VMware Workstation's own New Virtual Machine wizard first. See docs/VMWARE-SETUP.md."
}

$instanceDir = Join-Path $RootDirectory "Instances\$Name"
$vmxPath = Join-Path $instanceDir "$Name.vmx"

if (Test-Path -LiteralPath $vmxPath) {
    throw "VM '$Name' already exists at '$vmxPath'. Use Remove-Client.ps1 first if you want to recreate it."
}

Write-FarmLog "Checking master snapshot '$MasterVmxSnapshot' on '$MasterVmx'..." -LogDirectory $logDirectory
$snapshots = Invoke-Vmrun -VmrunPath $VmrunPath -Arguments @('listSnapshots', $MasterVmx)
if (-not $snapshots.Success) {
    throw "Failed to list snapshots on '$MasterVmx': $($snapshots.StandardError.Trim())"
}
$hasSnapshot = ($snapshots.StandardOutput -split "`r?`n") | Where-Object { $_.Trim() -ieq $MasterVmxSnapshot }
if (-not $hasSnapshot) {
    throw "Master VMX has no snapshot named '$MasterVmxSnapshot'. Take one first -- New-MasterSnapshot.ps1, " +
        "or VMware Workstation -> master VM -> VM menu -> Snapshot -> Take Snapshot. See docs/VMWARE-SETUP.md."
}

New-Item -ItemType Directory -Force -Path $instanceDir | Out-Null

try {
    Write-FarmLog "Cloning '$Name' from '$MasterVmx' (snapshot '$MasterVmxSnapshot')..." -LogDirectory $logDirectory
    $clone = Invoke-Vmrun -VmrunPath $VmrunPath -Arguments @(
        'clone', $MasterVmx, $vmxPath, 'linked', "-snapshot=$MasterVmxSnapshot", "-cloneName=$Name")
    if (-not $clone.Success) {
        throw "Failed to clone VM '$Name': $($clone.StandardError.Trim())"
    }

    Write-FarmLog "Configuring '$Name' ($CpuCount vCPU, $MemoryGB GB RAM, network $VMwareNetworkName)..." -LogDirectory $logDirectory
    Set-VmxValues -Path $vmxPath -Updates @{
        numvcpus                   = $CpuCount
        memsize                    = ($MemoryGB * 1024)
        'ethernet0.connectionType' = 'custom'
        'ethernet0.vnet'           = $VMwareNetworkName
        displayName                = $Name
    }

    Write-FarmLog "Client VM '$Name' created successfully at '$vmxPath'." -LogDirectory $logDirectory

    if ($Start) {
        Write-FarmLog "Starting '$Name'..." -LogDirectory $logDirectory
        $startResult = Invoke-Vmrun -VmrunPath $VmrunPath -Arguments @('start', $vmxPath, 'nogui')
        if (-not $startResult.Success) {
            throw "Failed to start VM '$Name': $($startResult.StandardError.Trim())"
        }
        Write-FarmLog "Client VM '$Name' started." -LogDirectory $logDirectory
    }
}
catch {
    Write-FarmLog "Failed to create '$Name': $($_.Exception.Message). Rolling back..." -Level Error -LogDirectory $logDirectory
    try { Invoke-Vmrun -VmrunPath $VmrunPath -Arguments @('deleteVM', $vmxPath) | Out-Null } catch { }
    if (Test-Path -LiteralPath $instanceDir) {
        try { Remove-Item -LiteralPath $instanceDir -Recurse -Force } catch { }
    }
    throw
}
