#Requires -Version 7.0
<#
.SYNOPSIS
    Verifies and assists with Windows host setup for the DayZ Client Farm's VMware Workstation
    backend -- the VMware equivalent of scripts/Hyper-V/Install-Host.ps1.

.DESCRIPTION
    Checks that vmrun.exe exists (VMware Workstation is installed), warns if the Hyper-V Windows
    feature is still enabled (the two are mutually exclusive for full native virtualization
    performance -- see docs/VMWARE-SETUP.md's "Before you start"), then creates the
    D:\GameFarm\{Instances,Config,Logs,Backups} directory layout. Never touches the Hyper-V
    feature itself and never reboots.

.PARAMETER RootDirectory
    Root storage directory for the farm (default D:\GameFarm). Note this layout has no "Images"
    directory the way the Hyper-V backend does -- the master .vmx typically lives wherever
    VMware Workstation's New Virtual Machine wizard put it, not under this root.

.EXAMPLE
    .\Install-Host.ps1 -RootDirectory D:\GameFarm -VmrunPath "C:\Program Files\VMware\VMware Workstation\vmrun.exe"
#>
[CmdletBinding()]
param(
    [string] $RootDirectory = "D:\GameFarm",
    [string] $VmrunPath = "C:\Program Files\VMware\VMware Workstation\vmrun.exe"
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"

$logDirectory = Join-Path $RootDirectory 'Logs\Controller'
Write-FarmLog "Starting VMware Workstation host installation checks." -LogDirectory $logDirectory

Assert-VmrunAvailable -VmrunPath $VmrunPath
Write-FarmLog "Found vmrun.exe at '$VmrunPath'." -LogDirectory $logDirectory

try {
    $feature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -ErrorAction Stop
    if ($feature.State -eq 'Enabled') {
        Write-FarmLog ("Hyper-V is currently enabled on this host. VMware Workstation will run nested " +
            "under Windows Hypervisor Platform with reduced performance and some feature restrictions " +
            "until it's disabled (Disable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All). " +
            "See docs/VMWARE-SETUP.md.") -Level Warning -LogDirectory $logDirectory
    } else {
        Write-FarmLog "Hyper-V is not enabled -- VMware Workstation will use full native virtualization." -LogDirectory $logDirectory
    }
} catch {
    Write-FarmLog "Could not query the Hyper-V feature state ($($_.Exception.Message)); skipping that check." -Level Warning -LogDirectory $logDirectory
}

$dirs = @('Instances', 'Config', 'Logs\Controller', 'Logs\Clients', 'Backups')
foreach ($d in $dirs) {
    $path = Join-Path $RootDirectory $d
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}
Write-FarmLog "Directory layout ready under $RootDirectory" -LogDirectory $logDirectory

Write-FarmLog ("Host installation checks complete. Next: build the master VM through VMware Workstation's " +
    "own New Virtual Machine wizard, then take a snapshot -- see docs/VMWARE-SETUP.md (steps 2-3), or run " +
    "New-MasterSnapshot.ps1 once the master is ready and shut down.") -LogDirectory $logDirectory
