#Requires -Version 7.0
<#
.SYNOPSIS
    Creates a single DayZ client VM as a Hyper-V differencing disk against the master image.

.DESCRIPTION
    Idempotent: refuses to overwrite an existing VM or an existing disk file. Never modifies the
    master VHDX or the shared Steam Library disk (if used) -- every client's disks are
    differencing children that only ever read unchanged blocks from those two shared parents;
    nothing is ever copied. Logs every step to D:\DayZFarm\Logs\Controller\scripts.log.

    If a Steam Library disk exists at the conventional path (built by Create-Master.ps1, or
    passed explicitly via -SteamLibraryMasterPath), this client gets its own differencing child
    of it too, attached as a second SCSI disk -- see docs/MASTER-IMAGE.md.

.EXAMPLE
    .\Create-Client.ps1 -Name DayZ-001 -CpuCount 4 -MemoryGB 6
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Name,
    [string] $RootDirectory = "D:\DayZFarm",
    [string] $MasterVhdxPath,
    [int] $CpuCount = 4,
    [int] $MemoryGB = 6,
    [bool] $DynamicMemory = $false,
    [string] $VirtualSwitchName = "DayZFarmSwitch",
    [string] $SteamLibraryMasterPath,
    [switch] $SkipSteamLibraryDisk,
    [switch] $Start
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"

Assert-Administrator
Assert-HyperVAvailable

$logDirectory = Join-Path $RootDirectory 'Logs\Controller'

if ($Name -notmatch '^[A-Za-z0-9_-]+$') {
    throw "Invalid client name '$Name'. Use only letters, digits, hyphens and underscores."
}

if (-not $MasterVhdxPath) {
    $MasterVhdxPath = Join-Path $RootDirectory "Images\DayZ-Master.vhdx"
}
if (-not (Test-Path -LiteralPath $MasterVhdxPath)) {
    throw "Master VHDX not found at '$MasterVhdxPath'. Run Create-Master.ps1 first."
}

# Steam Library disk is auto-detected at the conventional path Create-Master.ps1 uses. If you
# pass -SteamLibraryMasterPath explicitly, it must exist (you clearly meant to use one); if
# using the default and it's simply not there, silently skip -- not every farm uses this disk.
$useSteamLibrary = -not $SkipSteamLibraryDisk
if ($useSteamLibrary) {
    $explicitSteamLibraryPath = $PSBoundParameters.ContainsKey('SteamLibraryMasterPath')
    if (-not $SteamLibraryMasterPath) {
        $SteamLibraryMasterPath = Join-Path $RootDirectory "Images\SteamLibrary-Master.vhdx"
    }
    if (Test-Path -LiteralPath $SteamLibraryMasterPath) {
        Write-FarmLog "Using shared Steam Library disk at '$SteamLibraryMasterPath'." -LogDirectory $logDirectory
    } elseif ($explicitSteamLibraryPath) {
        throw "Steam Library master disk not found at '$SteamLibraryMasterPath'."
    } else {
        Write-FarmLog "No Steam Library disk found at '$SteamLibraryMasterPath'; this client will install Steam/DayZ on its OS disk as usual." -LogDirectory $logDirectory
        $useSteamLibrary = $false
    }
}

if (Get-VM -Name $Name -ErrorAction SilentlyContinue) {
    throw "VM '$Name' already exists. Use Remove-Client.ps1 first if you want to recreate it."
}

$instanceDir = Join-Path $RootDirectory "Instances\$Name"
$diskPath = Join-Path $instanceDir "$Name.vhdx"
$steamDiskPath = Join-Path $instanceDir "$Name-SteamLibrary.vhdx"

if (Test-Path -LiteralPath $diskPath) {
    throw "A disk already exists at '$diskPath'. Remove it manually before recreating this client."
}
if ($useSteamLibrary -and (Test-Path -LiteralPath $steamDiskPath)) {
    throw "A Steam Library disk already exists at '$steamDiskPath'. Remove it manually before recreating this client."
}

New-Item -ItemType Directory -Force -Path $instanceDir | Out-Null

Write-FarmLog "Creating differencing OS disk for $Name against $MasterVhdxPath..." -LogDirectory $logDirectory
New-VHD -Path $diskPath -ParentPath $MasterVhdxPath -Differencing | Out-Null

if ($useSteamLibrary) {
    Write-FarmLog "Creating Steam Library differencing disk for $Name against $SteamLibraryMasterPath..." -LogDirectory $logDirectory
    New-VHD -Path $steamDiskPath -ParentPath $SteamLibraryMasterPath -Differencing | Out-Null
}

Write-FarmLog "Creating VM $Name (Gen2, $CpuCount vCPU, $MemoryGB GB RAM, switch $VirtualSwitchName)..." -LogDirectory $logDirectory
New-VM `
    -Name $Name `
    -Generation 2 `
    -MemoryStartupBytes ([int64]$MemoryGB * 1GB) `
    -VHDPath $diskPath `
    -SwitchName $VirtualSwitchName | Out-Null

if ($useSteamLibrary) {
    Add-VMHardDiskDrive `
        -VMName $Name `
        -ControllerType SCSI `
        -ControllerNumber 0 `
        -ControllerLocation 1 `
        -Path $steamDiskPath

    Write-FarmLog "Attached Steam Library disk to $Name as SCSI 0:1 ($steamDiskPath)." -LogDirectory $logDirectory
}

# VM Processor
Set-VMProcessor -VMName $Name -Count $CpuCount

# VM Memory
Set-VMMemory `
    -VMName $Name `
    -DynamicMemoryEnabled $DynamicMemory

# Windows 11 requirements
Set-VMFirmware `
    -VMName $Name `
    -EnableSecureBoot On `
    -SecureBootTemplate MicrosoftWindows

# Create local key protector required by vTPM
Set-VMKeyProtector `
    -VMName $Name `
    -NewLocalKeyProtector

# Enable virtual TPM 2.0
Enable-VMTPM `
    -VMName $Name

# VM Integration Service
Enable-VMIntegrationService `
    -VMName $Name `
    -Name 'Guest Service Interface',
          'Heartbeat',
          'Key-Value Pair Exchange',
          'Shutdown',
          'Time Synchronization',
          'VSS'

# See the matching comment in Create-Master.ps1: GPU-P VMs can't be checkpointed at all, and
# automatic checkpoints (on by default) will otherwise block the VM from starting once GPU-P is
# added.
Set-VM -Name $Name -AutomaticCheckpointsEnabled $false

Write-FarmLog "Client VM '$Name' created successfully." -LogDirectory $logDirectory

if ($Start) {
    Start-VM -Name $Name
    Write-FarmLog "Client VM '$Name' started." -LogDirectory $logDirectory
}
