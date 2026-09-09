#Requires -Version 5.1
<#
.SYNOPSIS
    Decrypts and prints the DayZ Farm Agent's token so it can be pasted into the Controller
    dashboard's "Register Token" prompt.

.DESCRIPTION
    RUN THIS INSIDE THE CLIENT VM ITSELF (not on the Hyper-V host) -- the agent token file
    (%ProgramData%\DayZFarmAgent\agent-token.secret) is encrypted with Windows DPAPI at
    machine scope (see AgentTokenStore.cs), so it can only be decrypted by a process running on
    that same machine. Opening the file directly in Notepad (or anywhere else) will only ever
    show encrypted bytes -- that's expected, not a bug.

    Uses Windows PowerShell 5.1 (powershell.exe), which ships with every Windows install and has
    native support for System.Security.Cryptography.ProtectedData -- no extra install needed.

.EXAMPLE
    .\Show-AgentToken.ps1
#>
[CmdletBinding()]
param(
    [string] $TokenFilePath = (Join-Path $env:ProgramData "DayZFarmAgent\agent-token.secret")
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    # PowerShell 7's .NET runtime doesn't ship System.Security.Cryptography.ProtectedData by
    # default the way Windows PowerShell 5.1 (.NET Framework) does -- re-invoke under
    # powershell.exe instead of trying to Add-Type an assembly that may not be present.
    $winPS = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    & $winPS -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -TokenFilePath $TokenFilePath
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $TokenFilePath)) {
    throw "No agent token file found at '$TokenFilePath'. Has the DayZ Farm Agent run at least once on this VM?"
}

Add-Type -AssemblyName System.Security

$protectedBytes = [System.IO.File]::ReadAllBytes($TokenFilePath)
$plainBytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
    $protectedBytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)

$token = [Convert]::ToBase64String($plainBytes)

Write-Host ""
Write-Host "Agent token (paste this into the dashboard's Register Token prompt):" -ForegroundColor Cyan
Write-Host $token -ForegroundColor Green
Write-Host ""

try {
    Set-Clipboard -Value $token
    Write-Host "(Also copied to clipboard.)" -ForegroundColor DarkGray
} catch {
    # Set-Clipboard can fail in some remote/console-only sessions -- the printed value above is
    # still there to copy manually either way.
}
