#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Fully configures NVIDIA GPU-P (GPU Partitioning) for one or more Hyper-V VMs in one pass:
    assigns the GPU partition, sets the required VM settings, and copies the host's NVIDIA
    driver payload into the guest -- everything a fresh VM needs to actually use the GPU.

.DESCRIPTION
    RUN THIS ON THE HYPER-V HOST, NOT INSIDE A VM.

    Combines two previously separate manual steps into one, looping over every current Hyper-V
    VM by default instead of a hardcoded name:

      1. GPU-P assignment (per VM, unless -SkipGpuPAssignment):
         - Disables Dynamic Memory and Automatic Checkpoints (both required/recommended for
           GPU-P -- see docs/MASTER-IMAGE.md's GPU-P note on Automatic Checkpoints).
         - Removes any existing GPU-P adapter, then attaches a fresh one from the host's
           partitionable NVIDIA GPU.
         - Sets GuestControlledCacheTypes and the MMIO space GPU-P requires.
      2. Driver payload copy (per VM, unless -SkipDriverCopy):
         - Resolves the host's NVIDIA DriverStore package via the loaded nvlddmkm service path
           (avoids the Get-WindowsDriver/CIM-association failures Get-CimAssociatedInstance and
           Get-WindowsDriver can hit on some PowerShell/Windows combinations).
         - Mounts each VM's attached VHD/VHDX offline, finds its Windows volume, and copies the
           driver package into <Guest>\Windows\System32\HostDriverStore\FileRepository plus the
           associated files (nvapi64.dll, nvapi.dll, etc.) into their matching guest paths.
         - Dismounts and restores/starts the VM per -StartAll/-LeaveOff.

    NVIDIA-only for now (matches VEN_10DE). This is the automated counterpart to the manual GPU-P
    steps docs/MASTER-IMAGE.md previously described as host-specific and out of scope -- it is
    still host-specific (GPU vendor/driver version dependent) and should be re-run after any host
    NVIDIA driver update, but no longer needs to be done by hand per VM.

.PARAMETER VMName
    Optional list of VM names to process. If omitted, ALL current Hyper-V VMs are processed.

.PARAMETER OnlyGpuP
    Restrict to VMs that already have a GPU-P adapter attached (e.g. to just refresh drivers
    after a host NVIDIA driver update, without touching VMs that were never GPU-P VMs).

.PARAMETER SkipGpuPAssignment
    Only copy driver files; don't touch GPU-P adapter assignment or VM settings. Use this to
    refresh drivers on VMs already correctly configured.

.PARAMETER SkipDriverCopy
    Only assign the GPU-P adapter and VM settings; don't copy driver files. Use this if the guest
    driver payload is already current, or will be handled separately.

.PARAMETER MinPartitionVRAM
.PARAMETER MaxPartitionVRAM
.PARAMETER OptimalPartitionVRAM
.PARAMETER MinPartitionCompute
.PARAMETER MaxPartitionCompute
.PARAMETER OptimalPartitionCompute
    Optional explicit GPU partition sizing (bytes for VRAM, percent 1-100 for Compute), passed
    to Set-VMGpuPartitionAdapter after attachment. Left unset, Add-VMGpuPartitionAdapter uses its
    own defaults (effectively a whole-GPU partition per VM) -- fine for one or two VMs, but for a
    farm sharing one GPU across many concurrent DayZ clients you almost certainly want to size
    these down explicitly (e.g. divide the GPU's total VRAM/compute by your intended concurrent
    VM count). Query `Get-VMHostPartitionableGpu | Format-List *` for the GPU's totals first.

.PARAMETER LowMemoryMappedIoSpaceGB
.PARAMETER HighMemoryMappedIoSpaceGB
    MMIO space GPU-P requires (default 1GB low / 32GB high, matching Microsoft's documented
    GPU-P guidance).

.PARAMETER StartAll
    Start every successfully processed VM when finished, even if it was powered off before.

.PARAMETER LeaveOff
    Leave every processed VM powered off when finished.

.EXAMPLE
    .\Enable-GpuPartitionForVMs.ps1

    Configures GPU-P and copies drivers for every current Hyper-V VM, preserving each VM's
    original running/off state.

.EXAMPLE
    .\Enable-GpuPartitionForVMs.ps1 -VMName "DayZ-001","DayZ-002" -OptimalPartitionVRAM 1GB -OptimalPartitionCompute 10

.EXAMPLE
    .\Enable-GpuPartitionForVMs.ps1 -OnlyGpuP -SkipGpuPAssignment

    Refreshes driver files only, on VMs already GPU-P-configured -- e.g. after a host NVIDIA
    driver update.
#>

[CmdletBinding()]
param(
    [string[]] $VMName,
    [switch] $OnlyGpuP,
    [switch] $SkipGpuPAssignment,
    [switch] $SkipDriverCopy,
    [int64] $MinPartitionVRAM,
    [int64] $MaxPartitionVRAM,
    [int64] $OptimalPartitionVRAM,
    [int] $MinPartitionCompute,
    [int] $MaxPartitionCompute,
    [int] $OptimalPartitionCompute,
    [double] $LowMemoryMappedIoSpaceGB = 1,
    [double] $HighMemoryMappedIoSpaceGB = 32,
    [switch] $StartAll,
    [switch] $LeaveOff
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\..\Common.ps1"

Assert-Administrator
Assert-HyperVAvailable

if ($StartAll -and $LeaveOff) {
    throw "-StartAll and -LeaveOff cannot be used together."
}
if ($SkipGpuPAssignment -and $SkipDriverCopy) {
    throw "-SkipGpuPAssignment and -SkipDriverCopy cannot both be set -- there would be nothing left to do."
}

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-VMHeader {
    param([string]$Name, [int]$Index, [int]$Total)
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
        if ($used -notcontains $letter) { return $letter }
    }
    throw "No free drive letter is available."
}

function Convert-SystemRootPath {
    param([string]$Path)
    if (-not $Path) { return $null }
    $p = $Path.Trim().Trim('"')
    if ($p -match '^(?i)\\SystemRoot\\') { return $p -replace '^(?i)\\SystemRoot', $env:SystemRoot }
    if ($p -match '^(?i)System32\\') { return Join-Path $env:SystemRoot $p }
    return $p
}

function Remove-TemporaryDriveLetters {
    param([array]$Items)
    foreach ($item in $Items) {
        try {
            Remove-PartitionAccessPath -DiskNumber $item.DiskNumber -PartitionNumber $item.PartitionNumber `
                -AccessPath "$($item.DriveLetter):\" -ErrorAction SilentlyContinue
        } catch { }
    }
}

function Find-WindowsVolumeInVhd {
    param([Parameter(Mandatory=$true)][string]$VhdPath)

    $assignedLetters = @()
    $mounted = $null
    try {
        $mounted = Mount-VHD -Path $VhdPath -Passthru -ErrorAction Stop
        $disk = $mounted | Get-Disk

        foreach ($partition in @(Get-Partition -DiskNumber $disk.Number | Where-Object { $_.Size -gt 2GB })) {
            $letter = $partition.DriveLetter
            if (-not $letter) {
                $letter = Get-FreeDriveLetter
                Set-Partition -DiskNumber $disk.Number -PartitionNumber $partition.PartitionNumber -NewDriveLetter $letter -ErrorAction Stop
                $assignedLetters += [pscustomobject]@{ DiskNumber = $disk.Number; PartitionNumber = $partition.PartitionNumber; DriveLetter = $letter }
            }

            $root = "$letter`:\"
            if (Test-Path -LiteralPath (Join-Path $root "Windows\System32")) {
                return [pscustomobject]@{ Found = $true; GuestRoot = $root; MountedVhdPath = $VhdPath; AssignedLetters = $assignedLetters }
            }
        }

        Remove-TemporaryDriveLetters -Items $assignedLetters
        if ($mounted) { Dismount-VHD -Path $VhdPath -ErrorAction SilentlyContinue }
        return [pscustomobject]@{ Found = $false; GuestRoot = $null; MountedVhdPath = $null; AssignedLetters = @() }
    } catch {
        Remove-TemporaryDriveLetters -Items $assignedLetters
        if ($mounted) { Dismount-VHD -Path $VhdPath -ErrorAction SilentlyContinue }
        throw
    }
}

Import-Module Hyper-V -ErrorAction Stop

# ---------------------------------------------------------------------------
# Resolve the host NVIDIA GPU (for partition assignment) and driver payload
# (for the guest driver copy) ONCE, up front.
# ---------------------------------------------------------------------------

$partitionableGpu = $null
if (-not $SkipGpuPAssignment) {
    Write-Step "Finding NVIDIA GPU-P capable GPU"
    $partitionableGpu = Get-VMHostPartitionableGpu | Where-Object { $_.Name -match "VEN_10DE" } | Select-Object -First 1
    if (-not $partitionableGpu) {
        throw "No NVIDIA GPU-P capable GPU was found. Get-VMHostPartitionableGpu did not return a VEN_10DE device."
    }
    Write-Host "Partitionable GPU: $($partitionableGpu.Name)" -ForegroundColor Green
}

$driverStoreFolders = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$associatedFiles = @()
$driverStoreRoot = Join-Path $env:SystemRoot "System32\DriverStore\FileRepository"

if (-not $SkipDriverCopy) {
    Write-Step "Finding active NVIDIA display adapter on the host"
    $gpu = Get-PnpDevice -Class Display -PresentOnly | Where-Object { $_.InstanceId -like "PCI\VEN_10DE*" -and $_.Status -eq "OK" } | Select-Object -First 1
    if (-not $gpu) { throw "No active NVIDIA display adapter (VEN_10DE) was found on the host." }
    Write-Host "Host GPU : $($gpu.FriendlyName)" -ForegroundColor Green
    Write-Host "PNP ID   : $($gpu.InstanceId)"

    Write-Step "Finding installed NVIDIA driver"
    $signedDriver = Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceID -eq $gpu.InstanceId } | Select-Object -First 1
    if (-not $signedDriver) { throw "Could not find Win32_PnPSignedDriver for the NVIDIA GPU." }
    Write-Host "Version  : $($signedDriver.DriverVersion)"
    Write-Host "INF      : $($signedDriver.InfName)"

    Write-Step "Resolving NVIDIA kernel service and DriverStore package"
    $serviceName = $null
    try {
        $serviceName = (Get-PnpDeviceProperty -InstanceId $gpu.InstanceId -KeyName "DEVPKEY_Device_Service" -ErrorAction Stop).Data
    } catch {
        if ($gpu.PSObject.Properties.Name -contains "Service") { $serviceName = $gpu.Service }
    }
    if (-not $serviceName) {
        $serviceName = "nvlddmkm"
        Write-Host "Using default NVIDIA service: nvlddmkm" -ForegroundColor Yellow
    }
    Write-Host "Service  : $serviceName"

    $systemDriver = Get-CimInstance Win32_SystemDriver | Where-Object { $_.Name -eq $serviceName } | Select-Object -First 1
    if (-not $systemDriver) { throw "Could not find Win32_SystemDriver service '$serviceName'." }
    $servicePath = Convert-SystemRootPath $systemDriver.PathName
    Write-Host "Service path:"
    Write-Host "  $servicePath"

    if ($servicePath -match '(?i)^(.+?\\DriverStore\\FileRepository\\[^\\]+)\\') {
        $mainPackage = $matches[1]
        if (Test-Path -LiteralPath $mainPackage -PathType Container) {
            [void]$driverStoreFolders.Add($mainPackage)
            Write-Host "Main package:"
            Write-Host "  $mainPackage" -ForegroundColor Green
        }
    }

    # Use Windows PowerShell 5.1 for the legacy WMI association -- Get-CimAssociatedInstance and
    # Get-WindowsDriver are both known to fail here on some PowerShell/Windows combinations.
    Write-Step "Enumerating NVIDIA driver files"
    $winPS = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    if (-not (Test-Path -LiteralPath $winPS)) { throw "Windows PowerShell 5.1 was not found at $winPS." }

    $helperPath = Join-Path $env:TEMP ("GPUP-WMI-" + [guid]::NewGuid().ToString("N") + ".ps1")
    $helper = @'
param(
    [Parameter(Mandatory=$true)][string]$DeviceID,
    [Parameter(Mandatory=$true)][string]$Hostname
)
$ErrorActionPreference = "Stop"
$ModifiedDeviceID = $DeviceID -replace "\\", "\\"
$Antecedent = "\\" + $Hostname + "\ROOT\cimv2:Win32_PNPSignedDriver.DeviceID=""" + $ModifiedDeviceID + """"
$items = Get-WmiObject Win32_PNPSignedDriverCIMDataFile | Where-Object { $_.Antecedent -eq $Antecedent }
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
            & $winPS -NoProfile -ExecutionPolicy Bypass -File $helperPath -DeviceID $gpu.InstanceId -Hostname $env:COMPUTERNAME
        ) | ForEach-Object { [string]$_ } | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -Unique
    } finally {
        Remove-Item -LiteralPath $helperPath -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Associated files found: $($associatedFiles.Count)"

    foreach ($path in $associatedFiles) {
        if ($path.StartsWith($driverStoreRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $relative = $path.Substring($driverStoreRoot.Length).TrimStart('\')
            if ($relative) {
                $packageName = $relative.Split('\')[0]
                $packagePath = Join-Path $driverStoreRoot $packageName
                if (Test-Path -LiteralPath $packagePath -PathType Container) { [void]$driverStoreFolders.Add($packagePath) }
            }
        }
    }

    if ($driverStoreFolders.Count -eq 0) { throw "Could not resolve an NVIDIA DriverStore package." }
    Write-Host ""
    Write-Host "DriverStore package(s):" -ForegroundColor Green
    foreach ($folder in $driverStoreFolders) { Write-Host "  $folder" }
}

# ---------------------------------------------------------------------------
# Discover VMs
# ---------------------------------------------------------------------------

Write-Step "Scanning current Hyper-V VMs"

if ($VMName -and $VMName.Count -gt 0) {
    $vms = @()
    foreach ($name in $VMName) {
        try { $vms += Get-VM -Name $name -ErrorAction Stop }
        catch { Write-Warning "VM '$name' was not found and will be skipped." }
    }
} else {
    $vms = @(Get-VM | Sort-Object Name)
}

if ($OnlyGpuP) {
    $vms = @($vms | Where-Object { Get-VMGpuPartitionAdapter -VMName $_.Name -ErrorAction SilentlyContinue })
}

if ($vms.Count -eq 0) { throw "No Hyper-V VMs matched the requested criteria." }

Write-Host ""
Write-Host "VMs to process ($($vms.Count)):" -ForegroundColor Green
foreach ($vm in $vms) {
    $gpuP = Get-VMGpuPartitionAdapter -VMName $vm.Name -ErrorAction SilentlyContinue
    Write-Host "  $($vm.Name)  [$($vm.State), $(if ($gpuP) { 'GPU-P attached' } else { 'no GPU-P' })]"
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
        if ((Get-VM -Name $name).State -ne "Off") {
            Write-Host "Stopping VM..."
            Stop-VM -Name $name
            $deadline = (Get-Date).AddMinutes(2)
            while ((Get-VM -Name $name).State -ne "Off") {
                if ((Get-Date) -gt $deadline) { throw "VM did not turn off within two minutes." }
                Start-Sleep -Seconds 2
            }
        }
        Write-Host "VM is off."

        if (-not $SkipGpuPAssignment) {
            Write-Host ""
            Write-Host "Configuring GPU-P..." -ForegroundColor Cyan

            # GPU-P requires static memory and cannot be checkpointed -- see
            # docs/MASTER-IMAGE.md's GPU-P note.
            Set-VMMemory -VMName $name -DynamicMemoryEnabled $false
            Set-VM -Name $name -AutomaticCheckpointsEnabled $false

            $existingAdapter = Get-VMGpuPartitionAdapter -VMName $name -ErrorAction SilentlyContinue
            if ($existingAdapter) {
                Remove-VMGpuPartitionAdapter -VMName $name
                Write-Host "Removed existing GPU-P adapter."
            }

            Set-VM -VMName $name -GuestControlledCacheTypes $true `
                -LowMemoryMappedIoSpace ([int64]($LowMemoryMappedIoSpaceGB * 1GB)) `
                -HighMemoryMappedIoSpace ([int64]($HighMemoryMappedIoSpaceGB * 1GB))

            Add-VMGpuPartitionAdapter -VMName $name -InstancePath $partitionableGpu.Name

            $partitionSizeArgs = @{}
            if ($MinPartitionVRAM) { $partitionSizeArgs['MinPartitionVRAM'] = $MinPartitionVRAM }
            if ($MaxPartitionVRAM) { $partitionSizeArgs['MaxPartitionVRAM'] = $MaxPartitionVRAM }
            if ($OptimalPartitionVRAM) { $partitionSizeArgs['OptimalPartitionVRAM'] = $OptimalPartitionVRAM }
            if ($MinPartitionCompute) { $partitionSizeArgs['MinPartitionCompute'] = $MinPartitionCompute }
            if ($MaxPartitionCompute) { $partitionSizeArgs['MaxPartitionCompute'] = $MaxPartitionCompute }
            if ($OptimalPartitionCompute) { $partitionSizeArgs['OptimalPartitionCompute'] = $OptimalPartitionCompute }
            if ($partitionSizeArgs.Count -gt 0) {
                Set-VMGpuPartitionAdapter -VMName $name @partitionSizeArgs
            }

            Write-Host "GPU-P adapter attached." -ForegroundColor Green
        }

        if (-not $SkipDriverCopy) {
            $hardDisks = @(Get-VMHardDiskDrive -VMName $name | Where-Object { $_.Path -and $_.Path -match '(?i)\.vhdx?$' })
            if ($hardDisks.Count -eq 0) { throw "No VHD/VHDX is attached to this VM." }

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
            if (-not $guestRoot) { throw "No Windows system partition was found on the VM's attached VHD/VHDX files." }
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
                if (Test-Path -LiteralPath $destFolder) { Remove-Item -LiteralPath $destFolder -Recurse -Force }
                Copy-Item -LiteralPath $sourceFolder -Destination $destFolder -Recurse -Force -ErrorAction Stop
            }

            Write-Host "Copying NVIDIA files outside DriverStore..." -ForegroundColor Cyan
            foreach ($source in $associatedFiles) {
                if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
                if ($source.StartsWith($driverStoreRoot, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
                if ($source.StartsWith($env:SystemRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $relative = $source.Substring($env:SystemRoot.Length).TrimStart('\')
                    $dest = Join-Path $guestWindows $relative
                    $destDir = Split-Path $dest -Parent
                    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
                    Copy-Item -LiteralPath $source -Destination $dest -Force
                }
            }

            $extraFiles = @(
                [pscustomobject]@{ Source = (Join-Path $env:SystemRoot "System32\nvapi64.dll"); Dest = (Join-Path $guestWindows "System32\nvapi64.dll") },
                [pscustomobject]@{ Source = (Join-Path $env:SystemRoot "SysWOW64\nvapi.dll"); Dest = (Join-Path $guestWindows "SysWOW64\nvapi.dll") }
            )
            foreach ($file in $extraFiles) {
                if (Test-Path -LiteralPath $file.Source -PathType Leaf) {
                    New-Item -ItemType Directory -Path (Split-Path $file.Dest -Parent) -Force | Out-Null
                    Copy-Item -LiteralPath $file.Source -Destination $file.Dest -Force
                }
            }

            Write-Host "Driver copy completed for '$name'." -ForegroundColor Green
        }

        $success = $true
        $message = if ($SkipDriverCopy) { "GPU-P configured" } elseif ($SkipGpuPAssignment) { "Driver payload copied" } else { "GPU-P configured and driver payload copied" }
    } catch {
        $message = $_.Exception.Message
        Write-Host ""
        Write-Host "FAILED: $message" -ForegroundColor Red
    } finally {
        if ($mountedVhdPath) {
            Remove-TemporaryDriveLetters -Items $assignedLetters
            try { Dismount-VHD -Path $mountedVhdPath -ErrorAction SilentlyContinue } catch { }
        }

        if ($success) {
            try {
                if ($LeaveOff) { Write-Host "Leaving VM off." }
                elseif ($StartAll) { Write-Host "Starting VM..."; Start-VM -Name $name -ErrorAction Stop }
                elseif ($wasRunning) { Write-Host "Restoring original state: starting VM..."; Start-VM -Name $name -ErrorAction Stop }
                else { Write-Host "Restoring original state: VM remains off." }
            } catch {
                $success = $false
                $message = "Configured, but VM start failed: $($_.Exception.Message)"
                Write-Host $message -ForegroundColor Red
            }
        } elseif ($wasRunning -and -not $LeaveOff) {
            try {
                if ((Get-VM -Name $name).State -eq "Off") {
                    Write-Host "Setup failed; attempting to restore original running state..."
                    Start-VM -Name $name -ErrorAction SilentlyContinue
                }
            } catch { }
        }
    }

    $results += [pscustomobject]@{ VM = $name; OriginalState = $originalState; Result = if ($success) { "SUCCESS" } else { "FAILED" }; Message = $message }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

Write-Host ""
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host " SUMMARY" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""
$results | Format-Table VM, OriginalState, Result, Message -AutoSize

$failedCount = @($results | Where-Object Result -eq "FAILED").Count
$successCount = @($results | Where-Object Result -eq "SUCCESS").Count
Write-Host ""
Write-Host "Successful: $successCount"
Write-Host "Failed    : $failedCount"

if ($failedCount -gt 0) { exit 1 }
Write-Host ""
Write-Host "All selected VMs were processed successfully." -ForegroundColor Green
