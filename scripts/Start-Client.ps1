#Requires -Version 7.0
<# .SYNOPSIS Starts a client VM by name. #>
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Name)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Common.ps1"
Assert-Administrator
Assert-HyperVAvailable

Start-VM -Name $Name
Write-FarmLog "Started VM '$Name'."
