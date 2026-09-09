#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Copies the host NVIDIA GPU-P driver payload into every current Hyper-V VM.

.DESCRIPTION
    RUN THIS ON THE HYPER-V HOST, NOT INSIDE A VM.

    This script automatically enumerates the current Hyper-V VM names with Get-VM
    instead of using a hardcoded VM name.

    The NVIDIA host driver is resolved once, then each VM is processed in turn.

    For each VM it:
      - Records whether the VM was running
      - Checks whether a GPU-P adapter is attached
      - Stops the VM if necessary
      - Searches all attached VHD/VHDX files for the Windows system partition
      - Copies NVIDIA DriverStore package(s) into:
          <Guest>\Windows\System32\HostDriverStore\FileRepository
      - Copies associated NVIDIA files into matching guest Windows paths
      - Copies nvapi64.dll / nvapi.dll if present
      - Dismounts the VHD
      - Restarts the VM only if it was running before the script

    A VM that has no Windows partition or no VHD/VHDX is skipped and the script
    continues with the remaining VMs.

.PARAMETER VMName
    Optional list of VM names to process. If omitted, ALL current Hyper-V VMs
    are processed.

    Example:
      .\Install-GPUP-NvidiaDriver-AllVMs.ps1 -VMName "DayZ 01","DayZ 02"

.PARAMETER OnlyGpuP
    Process only VMs that currently have a GPU-P adapter attached.

.PARAMETER StartAll
    Start every successfully processed VM when finished, even if it was
    powered off before the script ran.

.PARAMETER LeaveOff
    Leave every processed VM powered off when finished.

.EXAMPLE
    .\Install-GPUP-NvidiaDriver-AllVMs.ps1

    Processes every current Hyper-V VM and preserves each VM's original
    running/off state.

.EXAMPLE
    .\Install-GPUP-NvidiaDriver-AllVMs.ps1 -OnlyGpuP

    Processes only VMs that already have GPU-P attached.

.EXAMPLE
    .\Install-GPUP-NvidiaDriver-AllVMs.ps1 -StartAll

    Processes all VMs, then starts all successfully processed VMs.
#>

[CmdletBinding()]
param(
    [string[]]$VMName,
    [switch]$OnlyGpuP,
    [switch]$StartAll,
    [switch]$LeaveOff
)

$ErrorActionPreference = "Stop"

if ($StartAll -and $LeaveOff) {
    throw "-StartAll and -LeaveOff cannot be used together."
}

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-VMHeader {
    param(
        [string]$Name,
        [int]$Index,
        [int]$Total
    )

    Write-Host ""
    Write-Host "================================================================" -ForegroundColor Magenta
    Write-Host " [$Index/$Total] Processing VM: $Name" -ForegroundColor Magenta
    Write-Host "================================================================" -ForegroundColor Magenta
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

function Remove-TemporaryDriveLetters {
    param([array]$Items)

    foreach ($item in $Items) {
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
}

function Find-WindowsVolumeInVhd {
    param(
        [Parameter(Mandatory=$true)]
        [string]$VhdPath
    )

    $assignedLetters = @()
    $mounted = $null

    try {
        $mounted = Mount-VHD -Path $VhdPath -Passthru -ErrorAction Stop
        $disk = $mounted | Get-Disk

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

                $assignedLetters += [pscustomobject]@{
                    DiskNumber      = $disk.Number
                    PartitionNumber = $partition.PartitionNumber
                    DriveLetter     = $letter
                }
            }

            $root = "$letter`:\"

            if (Test-Path -LiteralPath (Join-Path $root "Windows\System32")) {
                return [pscustomobject]@{
                    Found           = $true
                    GuestRoot       = $root
                    MountedVhdPath  = $VhdPath
                    AssignedLetters = $assignedLetters
                }
            }
        }

        Remove-TemporaryDriveLetters -Items $assignedLetters

        if ($mounted) {
            Dismount-VHD -Path $VhdPath -ErrorAction SilentlyContinue
        }

        return [pscustomobject]@{
            Found           = $false
            GuestRoot       = $null
            MountedVhdPath  = $null
            AssignedLetters = @()
        }
    }
    catch {
        Remove-TemporaryDriveLetters -Items $assignedLetters

        if ($mounted) {
            Dismount-VHD -Path $VhdPath -ErrorAction SilentlyContinue
        }

        throw
    }
}

Import-Module Hyper-V -ErrorAction Stop

# ---------------------------------------------------------------------------
# Resolve host NVIDIA driver ONCE
# ---------------------------------------------------------------------------

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
    if ($gpu.PSObject.Properties.Name -contains "Service") {
        $serviceName = $gpu.Service
    }
}

if (-not $serviceName) {
    $serviceName = "nvlddmkm"
    Write-Host "Using default NVIDIA service: nvlddmkm" -ForegroundColor Yellow
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

if ($servicePath -match '(?i)^(.+?\\DriverStore\\FileRepository\\[^\\]+)\\') {
    $mainPackage = $matches[1]

    if (Test-Path -LiteralPath $mainPackage -PathType Container) {
        [void]$driverStoreFolders.Add($mainPackage)
        Write-Host "Main package:"
        Write-Host "  $mainPackage" -ForegroundColor Green
    }
}

# Use Windows PowerShell 5.1 for the legacy WMI association.
Write-Step "Enumerating NVIDIA driver files"

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
    throw "Could not resolve an NVIDIA DriverStore package."
}

Write-Host ""
Write-Host "DriverStore package(s):" -ForegroundColor Green
foreach ($folder in $driverStoreFolders) {
    Write-Host "  $folder"
}

# ---------------------------------------------------------------------------
# Discover VMs
# ---------------------------------------------------------------------------

Write-Step "Scanning current Hyper-V VMs"

if ($VMName -and $VMName.Count -gt 0) {
    $vms = @()

    foreach ($name in $VMName) {
        try {
            $vms += Get-VM -Name $name -ErrorAction Stop
        }
        catch {
            Write-Warning "VM '$name' was not found and will be skipped."
        }
    }
}
else {
    $vms = @(Get-VM | Sort-Object Name)
}

if ($OnlyGpuP) {
    $filtered = @()

    foreach ($vm in $vms) {
        $adapter = Get-VMGpuPartitionAdapter `
            -VMName $vm.Name `
            -ErrorAction SilentlyContinue

        if ($adapter) {
            $filtered += $vm
        }
    }

    $vms = $filtered
}

if ($vms.Count -eq 0) {
    throw "No Hyper-V VMs matched the requested criteria."
}

Write-Host ""
Write-Host "VMs to process ($($vms.Count)):" -ForegroundColor Green
foreach ($vm in $vms) {
    $gpuP = Get-VMGpuPartitionAdapter `
        -VMName $vm.Name `
        -ErrorAction SilentlyContinue

    $gpuText = if ($gpuP) { "GPU-P attached" } else { "no GPU-P" }
    Write-Host "  $($vm.Name)  [$($vm.State), $gpuText]"
}

# ---------------------------------------------------------------------------
# Process every VM
# ---------------------------------------------------------------------------

$results = @()
$index = 0

foreach ($vm in $vms) {
    $index++
    $name = $vm.Name
    $originalState = $vm.State
    $wasRunning = ($originalState -eq "Running")
    $mountedVhdPath = $null
    $assignedLetters = @()
    $guestRoot = $null
    $success = $false
    $message = ""

    Write-VMHeader -Name $name -Index $index -Total $vms.Count

    try {
        $gpuP = Get-VMGpuPartitionAdapter `
            -VMName $name `
            -ErrorAction SilentlyContinue

        if (-not $gpuP) {
            Write-Warning "'$name' does not currently have a GPU-P adapter attached. Driver files will still be copied if it is a Windows VM."
        }
        else {
            Write-Host "GPU-P adapter detected." -ForegroundColor Green
        }

        # Stop VM if needed.
        if ((Get-VM -Name $name).State -ne "Off") {
            Write-Host "Stopping VM..."

            Stop-VM -Name $name

            $deadline = (Get-Date).AddMinutes(2)

            while ((Get-VM -Name $name).State -ne "Off") {
                if ((Get-Date) -gt $deadline) {
                    throw "VM did not turn off within two minutes."
                }

                Start-Sleep -Seconds 2
            }
        }

        Write-Host "VM is off."

        $hardDisks = @(
            Get-VMHardDiskDrive -VMName $name |
            Where-Object {
                $_.Path -and
                $_.Path -match '(?i)\.vhdx?$'
            }
        )

        if ($hardDisks.Count -eq 0) {
            throw "No VHD/VHDX is attached to this VM."
        }

        # Search every attached VHD/VHDX until we find Windows.
        foreach ($hardDisk in $hardDisks) {
            Write-Host "Checking disk:"
            Write-Host "  $($hardDisk.Path)"

            $found = Find-WindowsVolumeInVhd -VhdPath $hardDisk.Path

            if ($found.Found) {
                $guestRoot = $found.GuestRoot
                $mountedVhdPath = $found.MountedVhdPath
                $assignedLetters = $found.AssignedLetters
                break
            }
        }

        if (-not $guestRoot) {
            throw "No Windows system partition was found on the VM's attached VHD/VHDX files."
        }

        Write-Host "Guest Windows volume: $guestRoot" -ForegroundColor Green

        $guestWindows = Join-Path $guestRoot "Windows"
        $guestHostStore = Join-Path $guestWindows "System32\HostDriverStore\FileRepository"

        New-Item -ItemType Directory -Path $guestHostStore -Force | Out-Null

        Write-Host ""
        Write-Host "Copying NVIDIA DriverStore package(s)..." -ForegroundColor Cyan

        foreach ($sourceFolder in $driverStoreFolders) {
            $packageName = Split-Path -Leaf $sourceFolder
            $destFolder = Join-Path $guestHostStore $packageName

            Write-Host "  $packageName"

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

        Write-Host "Copying NVIDIA files outside DriverStore..." -ForegroundColor Cyan

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
            }
        }

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
                New-Item `
                    -ItemType Directory `
                    -Path (Split-Path $file.Dest -Parent) `
                    -Force | Out-Null

                Copy-Item `
                    -LiteralPath $file.Source `
                    -Destination $file.Dest `
                    -Force
            }
        }

        $success = $true
        $message = "NVIDIA GPU-P driver payload copied"
        Write-Host "Driver copy completed for '$name'." -ForegroundColor Green
    }
    catch {
        $message = $_.Exception.Message
        Write-Host ""
        Write-Host "FAILED: $message" -ForegroundColor Red
    }
    finally {
        if ($mountedVhdPath) {
            Remove-TemporaryDriveLetters -Items $assignedLetters

            try {
                Dismount-VHD `
                    -Path $mountedVhdPath `
                    -ErrorAction SilentlyContinue
            }
            catch {
            }
        }

        # Restore/start state.
        if ($success) {
            try {
                if ($LeaveOff) {
                    Write-Host "Leaving VM off."
                }
                elseif ($StartAll) {
                    Write-Host "Starting VM..."
                    Start-VM -Name $name -ErrorAction Stop
                }
                elseif ($wasRunning) {
                    Write-Host "Restoring original state: starting VM..."
                    Start-VM -Name $name -ErrorAction Stop
                }
                else {
                    Write-Host "Restoring original state: VM remains off."
                }
            }
            catch {
                $success = $false
                $message = "Driver copied, but VM start failed: $($_.Exception.Message)"
                Write-Host $message -ForegroundColor Red
            }
        }
        elseif ($wasRunning -and -not $LeaveOff) {
            # If copying failed after we stopped a previously-running VM,
            # make a best effort to put it back in its original state.
            try {
                if ((Get-VM -Name $name).State -eq "Off") {
                    Write-Host "Copy failed; attempting to restore original running state..."
                    Start-VM -Name $name -ErrorAction SilentlyContinue
                }
            }
            catch {
            }
        }
    }

    $results += [pscustomobject]@{
        VM            = $name
        OriginalState = $originalState
        Result        = if ($success) { "SUCCESS" } else { "FAILED" }
        Message       = $message
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

Write-Host ""
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host " SUMMARY" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""

$results | Format-Table VM,OriginalState,Result,Message -AutoSize

$failedCount = @($results | Where-Object Result -eq "FAILED").Count
$successCount = @($results | Where-Object Result -eq "SUCCESS").Count

Write-Host ""
Write-Host "Successful: $successCount"
Write-Host "Failed    : $failedCount"

if ($failedCount -gt 0) {
    exit 1
}

Write-Host ""
Write-Host "All selected VMs were processed successfully." -ForegroundColor Green
