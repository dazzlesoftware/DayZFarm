#Requires -Version 7.0
<#
.SYNOPSIS
    Creates the DayZ-Master Generation 2 VM used as the differencing-disk parent for all clients.

.DESCRIPTION
    Creates a blank OS VHDX (installed into directly -- Windows/Steam/DayZ/Agent all go here,
    exactly like today) and, unless -SkipSteamLibraryDisk is passed, a second real (non-
    differencing) disk: SteamLibrary-Master.vhdx, blank and NTFS-formatted, attached as SCSI 0:1.

    Both disks are real files you install directly into -- there is no separate "template" that
    gets copied. Steam + DayZ get installed onto the master's own OS disk as normal, and (if
    using the second disk) Steam's Library Folder gets pointed at SteamLibrary-Master.vhdx's
    drive letter so the actual game files land there instead. Every client then gets its own
    differencing child directly against THIS SAME master disk for both the OS and (if present)
    the Steam Library -- one real shared parent, never copied, never duplicated. See
    docs/MASTER-IMAGE.md.

.PARAMETER SizeGB
    Size of the master OS VHDX in GB (default 80).

.PARAMETER SteamLibrarySizeGB
    Size of the Steam Library disk in GB (default 120 -- DayZ alone is ~40GB; leave headroom for
    Steam itself, updates, and mods). Ignored if -SkipSteamLibraryDisk is passed.

.PARAMETER SkipSteamLibraryDisk
    Skip creating the second Steam Library disk entirely -- Steam/DayZ then install onto the OS
    disk like any normal VM, and clients get no second disk either.

.EXAMPLE
    .\Create-Master.ps1 -CpuCount 4 -MemoryGB 6 -VirtualSwitchName GameFarmSwitch -IsoPath D:\Windows.iso
#>
[CmdletBinding()]
param(
    [string] $RootDirectory = "D:\GameFarm",
    [string] $Name = "DayZ-Master",
    [int] $CpuCount = 4,
    [int] $MemoryGB = 6,
    [bool] $DynamicMemory = $false,
    [int] $SizeGB = 80,
    [int] $SteamLibrarySizeGB = 120,
    [switch] $SkipSteamLibraryDisk,
    [string] $VirtualSwitchName = "GameFarmSwitch",
    [string] $IsoPath
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"

Assert-Administrator
Assert-HyperVAvailable

$logDirectory = Join-Path $RootDirectory 'Logs\Controller'

# Validate everything we can BEFORE creating any state (VHDX/VM). A late failure here would
# otherwise leave a half-built master behind that then blocks a corrected retry via the
# idempotency checks below -- see docs/TROUBLESHOOTING.md.
if ($Name -notmatch '^[A-Za-z0-9_-]+$') {
    throw "Invalid master VM name '$Name'. Use only letters, digits, hyphens and underscores."
}

if ($IsoPath -and -not (Test-Path -LiteralPath $IsoPath)) {
    throw "ISO not found at '$IsoPath'. Fix the path and try again -- nothing has been created yet."
}

if (Get-VM -Name $Name -ErrorAction SilentlyContinue) {
    throw "A VM named '$Name' already exists. Remove it first if you intend to recreate the master."
}

$imagesDir = Join-Path $RootDirectory "Images"
New-Item -ItemType Directory -Force -Path $imagesDir | Out-Null

$vhdxPath = Join-Path $imagesDir "$Name.vhdx"
$steamLibraryPath = Join-Path $imagesDir "SteamLibrary-Master.vhdx"

if (Test-Path -LiteralPath $vhdxPath) {
    throw "A disk already exists at '$vhdxPath'. Remove it before recreating the master."
}

if (-not $SkipSteamLibraryDisk -and (Test-Path -LiteralPath $steamLibraryPath)) {
    throw "A Steam Library disk already exists at '$steamLibraryPath'. Remove it before recreating the master, or pass -SkipSteamLibraryDisk."
}

Write-FarmLog "Creating master OS VHDX at $vhdxPath ($SizeGB GB)..." -LogDirectory $logDirectory
New-VHD -Path $vhdxPath -SizeBytes ([int64]$SizeGB * 1GB) -Dynamic | Out-Null

if (-not $SkipSteamLibraryDisk) {
    Write-FarmLog "Creating shared Steam Library disk at $steamLibraryPath ($SteamLibrarySizeGB GB)..." -LogDirectory $logDirectory
    New-SharedDataDisk -Path $steamLibraryPath -SizeGB $SteamLibrarySizeGB -VolumeLabel "SteamLibrary"
}

Write-FarmLog "Creating master VM '$Name' (Gen2, $CpuCount vCPU, $MemoryGB GB RAM, switch $VirtualSwitchName)..." -LogDirectory $logDirectory
New-VM `
    -Name $Name `
    -Generation 2 `
    -MemoryStartupBytes ([int64]$MemoryGB * 1GB) `
    -VHDPath $vhdxPath `
    -SwitchName $VirtualSwitchName | Out-Null

if (-not $SkipSteamLibraryDisk) {
    # This IS the shared master disk -- not a differencing child of anything. Every client will
    # later create its own differencing disk directly against this exact file.
    Add-VMHardDiskDrive `
        -VMName $Name `
        -ControllerType SCSI `
        -ControllerNumber 0 `
        -ControllerLocation 1 `
        -Path $steamLibraryPath

    Write-FarmLog "Attached Steam Library disk to $Name as SCSI 0:1 ($steamLibraryPath)." -LogDirectory $logDirectory
}

# VM Processor
Set-VMProcessor -VMName $Name -Count $CpuCount

# VM Memory
Set-VMMemory `
    -VMName $Name `
    -DynamicMemoryEnabled $DynamicMemory

# Windows 11 Secure Boot requirement.
Set-VMFirmware `
    -VMName $Name `
    -EnableSecureBoot On `
    -SecureBootTemplate MicrosoftWindows

# Windows 11 virtual TPM 2.0 requirement.
# A local key protector must exist before the vTPM can be enabled.
Set-VMKeyProtector `
    -VMName $Name `
    -NewLocalKeyProtector

Enable-VMTPM `
    -VMName $Name

# VM Integration Services
Enable-VMIntegrationService `
    -VMName $Name `
    -Name 'Guest Service Interface',
          'Heartbeat',
          'Key-Value Pair Exchange',
          'Shutdown',
          'Time Synchronization',
          'VSS'

# A VM with a GPU partition (GPU-P) attached cannot be checkpointed at all, but Hyper-V's
# automatic checkpoints (on by default) silently try to create one on every start -- which then
# fails to start the VM with "Production checkpoints cannot be created". Disabling this up front
# means adding GPU-P later never hits that trap.
Set-VM -Name $Name -AutomaticCheckpointsEnabled $false

if ($IsoPath) {
    Add-VMDvdDrive -VMName $Name -Path $IsoPath
    Write-FarmLog "Attached installer ISO: $IsoPath" -LogDirectory $logDirectory
}
else {
    Write-FarmLog "No -IsoPath supplied. Attach a Windows installer ISO manually via Hyper-V Manager before first boot." -Level Warning -LogDirectory $logDirectory
}

Write-FarmLog "Master VM '$Name' created successfully." -LogDirectory $logDirectory
Write-FarmLog "  OS disk          : $vhdxPath" -LogDirectory $logDirectory
if (-not $SkipSteamLibraryDisk) {
    Write-FarmLog "  Steam Library disk: $steamLibraryPath (shared -- clients difference directly against this file)" -LogDirectory $logDirectory
}
Write-FarmLog "  Secure Boot      : Enabled (MicrosoftWindows)" -LogDirectory $logDirectory
Write-FarmLog "  Virtual TPM 2.0  : Enabled" -LogDirectory $logDirectory
Write-FarmLog "Next steps (see docs/MASTER-IMAGE.md):" -LogDirectory $logDirectory
Write-FarmLog "  1. Start the VM and install Windows." -LogDirectory $logDirectory
Write-FarmLog "  2. Install Windows updates." -LogDirectory $logDirectory
Write-FarmLog "  3. Install Steam (do NOT log in with a personal account)." -LogDirectory $logDirectory
if (-not $SkipSteamLibraryDisk) {
    Write-FarmLog "  4. In Steam, add a Library Folder on the second disk's drive letter and set it as default, then install DayZ there." -LogDirectory $logDirectory
} else {
    Write-FarmLog "  4. Install DayZ normally onto the OS disk." -LogDirectory $logDirectory
}
Write-FarmLog "  5. Install Game Farm Agent (build output from src/GameFarm.Agent)." -LogDirectory $logDirectory
Write-FarmLog "  6. Configure low graphics settings." -LogDirectory $logDirectory
Write-FarmLog "  7. Shut down the VM and treat these disks as read-only before creating clients." -LogDirectory $logDirectory
