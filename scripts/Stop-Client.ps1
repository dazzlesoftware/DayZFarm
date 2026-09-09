#Requires -Version 7.0
<# .SYNOPSIS Gracefully stops a client VM, forcing only if it doesn't shut down cleanly. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Name,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"
Assert-Administrator
Assert-HyperVAvailable

if ($Force) {
    Stop-VM -Name $Name -Force
} else {
    Stop-VM -Name $Name -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 5
    if ((Get-VM -Name $Name).State -ne 'Off') {
        Write-FarmLog "Graceful shutdown of '$Name' did not complete; forcing." -Level Warning
        Stop-VM -Name $Name -Force
    }
}
Write-FarmLog "Stopped VM '$Name'."
