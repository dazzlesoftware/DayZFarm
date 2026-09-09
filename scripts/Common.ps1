#Requires -Version 7.0
# Shared helpers dot-sourced by the other DayZ Farm scripts.

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must be run from an elevated (Administrator) PowerShell session."
    }
}

function Assert-HyperVAvailable {
    $state = Get-HyperVFeatureState
    if ($state -ne 'Enabled') {
        throw "Hyper-V does not appear to be enabled (state: $state). Run Install-Host.ps1 first."
    }
    if (-not (Get-Module -ListAvailable -Name Hyper-V)) {
        throw "The Hyper-V PowerShell module is not available. Install the 'Hyper-V Management Tools' Windows feature."
    }
}

<#
.SYNOPSIS
    Returns 'Enabled', 'Disabled', or 'Unknown' for the Microsoft-Hyper-V-All Windows feature.

.DESCRIPTION
    Get-WindowsOptionalFeature depends on a DISM COM component that is known to occasionally
    fail on Windows 11 with "Class not registered" even when Hyper-V itself is fine and
    DISM.exe works perfectly well. When that happens, fall back to parsing `dism.exe
    /online /get-featureinfo` output instead of hard-failing the whole script on a broken
    cmdlet. See docs/TROUBLESHOOTING.md.
#>
function Get-HyperVFeatureState {
    try {
        $feature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -ErrorAction Stop
        return $feature.State.ToString()
    } catch {
        # Known, harmless DISM COM issue (see docs/TROUBLESHOOTING.md) -- the dism.exe fallback
        # below handles it every time, so this is informational rather than a real warning.
        Write-FarmLog "Get-WindowsOptionalFeature unavailable (DISM COM registration issue) -- using dism.exe instead." -Level Info
    }

    $output = & dism.exe /online /get-featureinfo /featurename:Microsoft-Hyper-V-All 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-FarmLog "dism.exe fallback also failed (exit $LASTEXITCODE): $($output -join ' ')" -Level Warning
        return 'Unknown'
    }

    $stateLine = $output | Where-Object { $_ -match '^State\s*:\s*(.+)$' }
    if (-not $stateLine) { return 'Unknown' }
    $value = ($stateLine -replace '^State\s*:\s*', '').Trim()
    # dism.exe reports "Enabled"/"Disabled" in English installs; treat anything else as Unknown
    # rather than guessing on a localized string.
    if ($value -in @('Enabled', 'Disabled')) { return $value }
    return 'Unknown'
}

<#
.SYNOPSIS
    Enables the Microsoft-Hyper-V-All feature, preferring Enable-WindowsOptionalFeature and
    falling back to dism.exe on the same "Class not registered" COM failure as
    Get-HyperVFeatureState. Returns $true if a reboot is required.
#>
function Enable-HyperVFeatureWithFallback {
    try {
        $result = Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -All -NoRestart -ErrorAction Stop
        return [bool]$result.RestartNeeded
    } catch {
        Write-FarmLog "Enable-WindowsOptionalFeature failed ($($_.Exception.Message)); falling back to dism.exe." -Level Warning
    }

    $output = & dism.exe /online /enable-feature /featurename:Microsoft-Hyper-V-All /all /norestart 2>&1
    # DISM uses exit code 3010 for "success, but a reboot is required" — that's still success.
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 3010) {
        throw "dism.exe fallback failed to enable Hyper-V (exit $LASTEXITCODE): $($output -join ' ')"
    }
    return $LASTEXITCODE -eq 3010 -or (($output -join ' ') -match 'restart')
}

function Write-FarmLog {
    param(
        [Parameter(Mandatory)] [string] $Message,
        [string] $LogDirectory = "D:\DayZFarm\Logs\Controller",
        [ValidateSet('Info','Warning','Error')] [string] $Level = 'Info'
    )
    New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
    $line = "[{0:yyyy-MM-dd HH:mm:ss}] [{1}] {2}" -f (Get-Date), $Level, $Message
    Add-Content -Path (Join-Path $LogDirectory "scripts.log") -Value $line
    switch ($Level) {
        'Warning' { Write-Warning $Message }
        'Error'   { Write-Error $Message }
        default   { Write-Host $line }
    }
}

function Format-ClientName {
    param([Parameter(Mandatory)][int]$Index)
    return "DayZ-{0:D3}" -f $Index
}

<#
.SYNOPSIS
    Checks whether any VM's attached disk (other than -ExcludingVm) is a Hyper-V differencing
    disk whose parent resolves to -Path.

.DESCRIPTION
    Safety check before ever deleting a shared parent disk like SteamLibrary-Master.vhdx or a
    master OS VHDX: deleting one while a client's differencing disk still points at it corrupts
    that client's disk chain. Used by Remove-Client.ps1 before it will delete anything that isn't
    unambiguously that one VM's own exclusive disk. See docs/TROUBLESHOOTING.md.
#>
function Test-DiskInUseAsParent {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [string] $ExcludingVm
    )

    $fullPath = (Resolve-Path -LiteralPath $Path -ErrorAction SilentlyContinue).Path
    if (-not $fullPath) { return $false }

    foreach ($vm in Get-VM) {
        if ($vm.Name -eq $ExcludingVm) { continue }

        foreach ($diskPath in (Get-VMHardDiskDrive -VMName $vm.Name).Path) {
            if (-not $diskPath -or -not (Test-Path -LiteralPath $diskPath)) { continue }

            # Directly attached to a VM we're not removing -- e.g. the master's own live Steam
            # Library disk, which isn't anyone's "parent" in the differencing sense but is
            # absolutely still in active use and must never be deleted as a side effect of
            # removing some unrelated client.
            if ((Resolve-Path -LiteralPath $diskPath).Path -eq $fullPath) {
                return $true
            }

            $parentPath = (Get-VHD -Path $diskPath -ErrorAction SilentlyContinue).ParentPath
            if ($parentPath -and (Resolve-Path -LiteralPath $parentPath -ErrorAction SilentlyContinue).Path -eq $fullPath) {
                return $true
            }
        }
    }
    return $false
}

<#
.SYNOPSIS
    Creates and NTFS-formats a blank fixed or dynamic VHDX -- used to build a real, shared parent
    disk (e.g. SteamLibrary-Master.vhdx) that VMs install directly into and every client then
    creates its own differencing disk against. See docs/MASTER-IMAGE.md's Steam Library section.
#>
function New-SharedDataDisk {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [int] $SizeGB,
        [bool] $Dynamic = $true,
        [string] $VolumeLabel = "SteamLibrary"
    )

    # Defense-in-depth: every caller now sets this globally too, but a non-terminating error
    # from any of New-VHD/Mount-VHD/Initialize-Disk/New-Partition/Format-Volume must never be
    # silently swallowed here -- that previously let this function "succeed" having created
    # nothing, while the calling script carried on to attach a disk that was never actually
    # built. See docs/TROUBLESHOOTING.md.
    $ErrorActionPreference = 'Stop'

    if (Test-Path $Path) {
        throw "A disk already exists at '$Path'. Remove it first if you intend to recreate it."
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $Path -Parent) | Out-Null

    if ($Dynamic) {
        New-VHD -Path $Path -SizeBytes ([int64]$SizeGB * 1GB) -Dynamic | Out-Null
    } else {
        New-VHD -Path $Path -SizeBytes ([int64]$SizeGB * 1GB) -Fixed | Out-Null
    }

    # Mount on the host just long enough to partition/format it, then detach -- the disk itself
    # is never left attached to the host once this returns.
    $mounted = Mount-VHD -Path $Path -Passthru
    try {
        $disk = $mounted | Get-Disk
        Initialize-Disk -Number $disk.Number -PartitionStyle GPT
        $partition = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
        Format-Volume -Partition $partition -FileSystem NTFS -NewFileSystemLabel $VolumeLabel -Confirm:$false | Out-Null
    } finally {
        Dismount-VHD -Path $Path
    }
}
