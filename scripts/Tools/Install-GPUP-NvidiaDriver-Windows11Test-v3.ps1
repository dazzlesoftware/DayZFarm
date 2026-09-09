#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Copies the host NVIDIA GPU-P driver files into the Hyper-V guest
    "Windows 11 Test".

.DESCRIPTION
    RUN THIS ON THE HYPER-V HOST, NOT INSIDE THE VM.

    This v3 version does NOT use Get-WindowsDriver and does NOT use
    Get-CimAssociatedInstance.

    It follows the proven Easy-GPU-PV style:
      - Resolve the NVIDIA kernel service (normally nvlddmkm)
      - Resolve its DriverStore\FileRepository package from the service path
      - Use Windows PowerShell 5.1 / legacy WMI only for the signed-driver
        file association, because that association is unreliable in pwsh 7
      - Copy DriverStore folders into:
          <Guest>\Windows\System32\HostDriverStore\FileRepository
      - Copy other associated NVIDIA files to matching guest Windows paths
      - Copy nvapi64.dll / nvapi.dll if present
      - Dismount the guest disk and start the VM

.PARAMETER VMName
    Hyper-V VM name. Default: Windows 11 Test

.PARAMETER DontStart
    Leave the VM powered off after copying the driver files.
#>

[CmdletBinding()]
param(
    [string]$VMName = "DayZ-Master",
    [switch]$DontStart
)

$ErrorActionPreference = "Stop"

$MountedVhdPath = $null
$AssignedLetters = @()
$ScriptFailed = $false

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-FreeDriveLetter {
    $used = @(
        Get-Volume -ErrorAction SilentlyContinue |
        Where-Object DriveLetter |
        ForEach-Object { [string]$_.DriveLetter }
    )

    foreach ($code in 90..68) { # Z down to D
        $letter = [string][char]$code
        if ($used -notcontains $letter) {
            return $letter
        }
    }

    throw "No free drive letter is available."
}

function Convert-SystemRootPath {
    param([string]$Path)

    if (-not $Path) {
        return $null
    }

    $p = $Path.Trim().Trim('"')

    if ($p -match '^(?i)\\SystemRoot\\') {
        return $p -replace '^(?i)\\SystemRoot', $env:SystemRoot
    }

    if ($p -match '^(?i)System32\\') {
        return Join-Path $env:SystemRoot $p
    }

    return $p
}

try {
    Import-Module Hyper-V -ErrorAction Stop

    Write-Step "Checking Hyper-V VM"
    $vm = Get-VM -Name $VMName -ErrorAction Stop
    Write-Host "Found VM: $($vm.Name)"

    Write-Step "Finding active NVIDIA display adapter on the host"
    $gpu = Get-PnpDevice -Class Display -PresentOnly |
        Where-Object {
            $_.InstanceId -like "PCI\VEN_10DE*" -and
            $_.Status -eq "OK"
        } |
        Select-Object -First 1

    if (-not $gpu) {
        throw "No active NVIDIA display adapter (VEN_10DE) was found on the host."
    }

    Write-Host "Host GPU : $($gpu.FriendlyName)" -ForegroundColor Green
    Write-Host "PNP ID   : $($gpu.InstanceId)"

    Write-Step "Finding installed NVIDIA driver"
    $signedDriver = Get-CimInstance Win32_PnPSignedDriver |
        Where-Object { $_.DeviceID -eq $gpu.InstanceId } |
        Select-Object -First 1

    if (-not $signedDriver) {
        throw "Could not find Win32_PnPSignedDriver for the NVIDIA GPU."
    }

    Write-Host "Version  : $($signedDriver.DriverVersion)"
    Write-Host "INF      : $($signedDriver.InfName)"

    Write-Step "Resolving NVIDIA kernel service and DriverStore package"

    $serviceName = $null

    try {
        $serviceName = (
            Get-PnpDeviceProperty `
                -InstanceId $gpu.InstanceId `
                -KeyName "DEVPKEY_Device_Service" `
                -ErrorAction Stop
        ).Data
    }
    catch {
        # Some builds expose Service directly on the PnP object.
        if ($gpu.PSObject.Properties.Name -contains "Service") {
            $serviceName = $gpu.Service
        }
    }

    if (-not $serviceName) {
        $serviceName = "nvlddmkm"
        Write-Host "Could not query DEVPKEY_Device_Service; trying default NVIDIA service: nvlddmkm" -ForegroundColor Yellow
    }

    Write-Host "Service  : $serviceName"

    $systemDriver = Get-CimInstance Win32_SystemDriver |
        Where-Object { $_.Name -eq $serviceName } |
        Select-Object -First 1

    if (-not $systemDriver) {
        throw "Could not find Win32_SystemDriver service '$serviceName'."
    }

    $servicePath = Convert-SystemRootPath $systemDriver.PathName
    Write-Host "Service path:"
    Write-Host "  $servicePath"

    $driverStoreFolders = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase
    )

    # The NVIDIA kernel driver normally lives inside:
    # C:\Windows\System32\DriverStore\FileRepository\<package>\nvlddmkm.sys
    if ($servicePath -match '(?i)^(.+?\\DriverStore\\FileRepository\\[^\\]+)\\') {
        $mainPackage = $matches[1]
        if (Test-Path -LiteralPath $mainPackage -PathType Container) {
            [void]$driverStoreFolders.Add($mainPackage)
            Write-Host "Main package:"
            Write-Host "  $mainPackage" -ForegroundColor Green
        }
    }

    Write-Step "Enumerating NVIDIA driver files using Windows PowerShell 5.1"

    $winPS = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"

    if (-not (Test-Path -LiteralPath $winPS)) {
        throw "Windows PowerShell 5.1 was not found at $winPS."
    }

    $helperPath = Join-Path $env:TEMP ("GPUP-WMI-" + [guid]::NewGuid().ToString("N") + ".ps1")

    $helper = @'
param(
    [Parameter(Mandatory=$true)]
    [string]$DeviceID,

    [Parameter(Mandatory=$true)]
    [string]$Hostname
)

$ErrorActionPreference = "Stop"

$ModifiedDeviceID = $DeviceID -replace "\\", "\\"
$Antecedent = "\\" + $Hostname + "\ROOT\cimv2:Win32_PNPSignedDriver.DeviceID=""" + $ModifiedDeviceID + """"

$items = Get-WmiObject Win32_PNPSignedDriverCIMDataFile |
    Where-Object { $_.Antecedent -eq $Antecedent }

foreach ($item in $items) {
    $dep = [string]$item.Dependent

    if ($dep -match 'Name="(.+)"$') {
        $path = $matches[1] -replace '\\\\', '\'
        Write-Output $path
    }
}
'@

    Set-Content -LiteralPath $helperPath -Value $helper -Encoding UTF8

    try {
        $associatedFiles = @(
            & $winPS `
                -NoProfile `
                -ExecutionPolicy Bypass `
                -File $helperPath `
                -DeviceID $gpu.InstanceId `
                -Hostname $env:COMPUTERNAME
        ) |
        ForEach-Object { [string]$_ } |
        Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
        Select-Object -Unique
    }
    finally {
        Remove-Item -LiteralPath $helperPath -Force -ErrorAction SilentlyContinue
    }

    Write-Host "Associated files found: $($associatedFiles.Count)"

    $driverStoreRoot = Join-Path $env:SystemRoot "System32\DriverStore\FileRepository"

    foreach ($path in $associatedFiles) {
        if ($path.StartsWith($driverStoreRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $relative = $path.Substring($driverStoreRoot.Length).TrimStart('\')

            if ($relative) {
                $packageName = $relative.Split('\')[0]
                $packagePath = Join-Path $driverStoreRoot $packageName

                if (Test-Path -LiteralPath $packagePath -PathType Container) {
                    [void]$driverStoreFolders.Add($packagePath)
                }
            }
        }
    }

    if ($driverStoreFolders.Count -eq 0) {
        throw @"
Could not resolve an NVIDIA DriverStore package.

Please run this on the HOST and send the output:

Get-CimInstance Win32_SystemDriver |
    Where-Object Name -eq 'nvlddmkm' |
    Format-List Name,PathName
"@
    }

    Write-Host ""
    Write-Host "DriverStore package(s) to copy:" -ForegroundColor Green
    foreach ($folder in $driverStoreFolders) {
        Write-Host "  $folder"
    }

    Write-Step "Stopping the VM"

    if ((Get-VM -Name $VMName).State -ne "Off") {
        Stop-VM -Name $VMName

        $deadline = (Get-Date).AddMinutes(2)

        while ((Get-VM -Name $VMName).State -ne "Off") {
            if ((Get-Date) -gt $deadline) {
                throw "VM did not turn off within two minutes."
            }

            Start-Sleep -Seconds 2
        }
    }

    Write-Host "VM is off." -ForegroundColor Green

    Write-Step "Locating VM VHD/VHDX"

    $hardDisks = @(
        Get-VMHardDiskDrive -VMName $VMName |
        Where-Object {
            $_.Path -and
            $_.Path -match '(?i)\.vhdx?$'
        }
    )

    if ($hardDisks.Count -eq 0) {
        throw "No VHD/VHDX was found for the VM."
    }

    # Prefer the first IDE/SCSI disk. The Windows partition is detected after mount.
    $vhdPath = $hardDisks[0].Path
    Write-Host "Using disk:"
    Write-Host "  $vhdPath"

    Write-Step "Mounting guest disk"

    $mounted = Mount-VHD -Path $vhdPath -Passthru -ErrorAction Stop
    $MountedVhdPath = $vhdPath
    $disk = $mounted | Get-Disk

    $guestRoot = $null

    foreach ($partition in @(
        Get-Partition -DiskNumber $disk.Number |
        Where-Object { $_.Size -gt 2GB }
    )) {
        $letter = $partition.DriveLetter

        if (-not $letter) {
            $letter = Get-FreeDriveLetter

            Set-Partition `
                -DiskNumber $disk.Number `
                -PartitionNumber $partition.PartitionNumber `
                -NewDriveLetter $letter `
                -ErrorAction Stop

            $AssignedLetters += [pscustomobject]@{
                DiskNumber      = $disk.Number
                PartitionNumber = $partition.PartitionNumber
                DriveLetter     = $letter
            }
        }

        $root = "$letter`:\"

        if (Test-Path -LiteralPath (Join-Path $root "Windows\System32")) {
            $guestRoot = $root
            break
        }
    }

    if (-not $guestRoot) {
        throw "Could not locate the Windows partition inside the VM disk."
    }

    Write-Host "Guest Windows volume: $guestRoot" -ForegroundColor Green

    $guestWindows = Join-Path $guestRoot "Windows"
    $guestHostStore = Join-Path $guestWindows "System32\HostDriverStore\FileRepository"

    New-Item -ItemType Directory -Path $guestHostStore -Force | Out-Null

    Write-Step "Copying NVIDIA DriverStore package(s)"

    foreach ($sourceFolder in $driverStoreFolders) {
        $packageName = Split-Path -Leaf $sourceFolder
        $destFolder = Join-Path $guestHostStore $packageName

        Write-Host ""
        Write-Host "FROM: $sourceFolder"
        Write-Host "TO  : $destFolder"

        if (Test-Path -LiteralPath $destFolder) {
            Remove-Item -LiteralPath $destFolder -Recurse -Force
        }

        Copy-Item `
            -LiteralPath $sourceFolder `
            -Destination $destFolder `
            -Recurse `
            -Force `
            -ErrorAction Stop
    }

    Write-Step "Copying NVIDIA files outside DriverStore"

    $copiedOutside = 0

    foreach ($source in $associatedFiles) {
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            continue
        }

        if ($source.StartsWith($driverStoreRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        if ($source.StartsWith($env:SystemRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $relative = $source.Substring($env:SystemRoot.Length).TrimStart('\')
            $dest = Join-Path $guestWindows $relative
            $destDir = Split-Path $dest -Parent

            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $dest -Force
            $copiedOutside++
        }
    }

    # Explicitly cover the NVIDIA API DLLs commonly required by GPU-P.
    $extraFiles = @(
        [pscustomobject]@{
            Source = (Join-Path $env:SystemRoot "System32\nvapi64.dll")
            Dest   = (Join-Path $guestWindows "System32\nvapi64.dll")
        },
        [pscustomobject]@{
            Source = (Join-Path $env:SystemRoot "SysWOW64\nvapi.dll")
            Dest   = (Join-Path $guestWindows "SysWOW64\nvapi.dll")
        }
    )

    foreach ($file in $extraFiles) {
        if (Test-Path -LiteralPath $file.Source -PathType Leaf) {
            New-Item -ItemType Directory -Path (Split-Path $file.Dest -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.Source -Destination $file.Dest -Force
            Write-Host "Copied: $($file.Source)"
        }
    }

    Write-Host "Associated files copied outside DriverStore: $copiedOutside"

    Write-Step "NVIDIA GPU-P driver payload copied"
}
catch {
    $ScriptFailed = $true

    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    foreach ($item in $AssignedLetters) {
        try {
            Remove-PartitionAccessPath `
                -DiskNumber $item.DiskNumber `
                -PartitionNumber $item.PartitionNumber `
                -AccessPath "$($item.DriveLetter):\" `
                -ErrorAction SilentlyContinue
        }
        catch {
        }
    }

    if ($MountedVhdPath) {
        try {
            Write-Host ""
            Write-Host "Unmounting guest disk..." -ForegroundColor Cyan
            Dismount-VHD -Path $MountedVhdPath -ErrorAction SilentlyContinue
        }
        catch {
        }
    }
}

if ($ScriptFailed) {
    exit 1
}

if (-not $DontStart) {
    Write-Step "Starting VM"
    Start-VM -Name $VMName -ErrorAction Stop
    Write-Host "$VMName started." -ForegroundColor Green
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host " NVIDIA GPU-P driver copy completed" -ForegroundColor Green
Write-Host " VM: $VMName" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Inside the VM, run:" -ForegroundColor Yellow
Write-Host ""
Write-Host 'Get-PnpDevice -Class Display | Format-Table Status,FriendlyName,Problem,InstanceId -AutoSize'
Write-Host ""
Write-Host 'Get-CimInstance Win32_VideoController | Format-Table Name,DriverVersion,Status -AutoSize'
Write-Host ""
Write-Host "If RTX 4090 still has a yellow warning:"
Write-Host "Device Manager -> NVIDIA GeForce RTX 4090 -> Properties -> General"
Write-Host "and send the exact Device status / Code number."
