#Requires -Version 7.0
<#
.SYNOPSIS
    Creates a blank, NTFS-formatted Steam Library VHDX at the conventional path clients
    auto-detect (D:\GameFarm\Images\SteamLibrary-Master.vhdx).

.DESCRIPTION
    Create-Master.ps1 already creates and attaches this disk to the master by default -- you
    only need this script if you passed -SkipSteamLibraryDisk to Create-Master.ps1 and want to
    build the shared Steam Library disk separately (e.g. install Steam/DayZ onto it via some
    other temporary VM, keeping the master purely an OS image).

    This only creates and formats a blank disk -- it does NOT install Steam or DayZ onto it,
    since that requires a running Windows OS to install into. After running this:

      1. Attach the resulting VHDX as a second disk to any Windows VM and boot it.
      2. Install Steam, add a Library Folder on this disk's drive letter, set it as default, and
         install DayZ into it.
      3. Shut the VM down and detach the disk.

    Every Create-Client.ps1 run auto-detects this file at the conventional path and gives that
    client its own differencing disk directly against it -- nothing is ever copied. See
    docs/MASTER-IMAGE.md.

.PARAMETER SizeGB
    Size of the disk in GB. DayZ alone is ~40GB; leave headroom for Steam itself, updates, and
    mods. Default 120.

.EXAMPLE
    .\Create-SteamLibraryDisk.ps1 -SizeGB 150
#>
[CmdletBinding()]
param(
    [string] $RootDirectory = "D:\GameFarm",
    [string] $Path,
    [int] $SizeGB = 120
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"

Assert-Administrator
Assert-HyperVAvailable

if (-not $Path) {
    $Path = Join-Path $RootDirectory "Images\SteamLibrary-Master.vhdx"
}

Write-FarmLog "Creating blank Steam Library disk at '$Path' ($SizeGB GB)..."
New-SharedDataDisk -Path $Path -SizeGB $SizeGB -VolumeLabel "SteamLibrary"

Write-FarmLog "Steam Library disk created at '$Path'."
Write-FarmLog "Next: attach it to a Windows VM, install Steam + DayZ onto it, then shut down and detach -- see docs/MASTER-IMAGE.md." -Level Warning
