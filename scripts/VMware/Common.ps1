#Requires -Version 7.0
# Shared helpers dot-sourced by the other VMware Workstation farm scripts. Deliberately
# self-contained (does not dot-source scripts/Hyper-V/Common.ps1) so this folder has no
# dependency on the Hyper-V-only scripts.

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must be run from an elevated (Administrator) PowerShell session."
    }
}

function Assert-VmrunAvailable {
    param([Parameter(Mandatory)] [string] $VmrunPath)
    if (-not (Test-Path -LiteralPath $VmrunPath)) {
        throw "vmrun.exe not found at '$VmrunPath'. Install VMware Workstation, or pass -VmrunPath explicitly. See docs/VMWARE-SETUP.md."
    }
}

function Write-FarmLog {
    param(
        [Parameter(Mandatory)] [string] $Message,
        [string] $LogDirectory = "D:\GameFarm\Logs\Controller",
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
    return "Client-{0:D3}" -f $Index
}

<#
.SYNOPSIS
    Runs vmrun.exe (always with "-T ws", VMware Workstation's host type flag) with a bounded
    timeout, returning a result object instead of throwing on failure or hang.

.DESCRIPTION
    Mirrors GameFarm.VMware.ProcessVmrunRunner exactly: a "soft" vmrun stop/reset against a
    guest with no responsive VMware Tools was confirmed during testing to hang indefinitely --
    vmrun itself never times out on its own. This kills the process (and any children) and
    returns a synthetic failure result after -TimeoutSeconds instead of hanging the caller
    forever. See docs/VMWARE-SETUP.md's troubleshooting section.
#>
<#
.SYNOPSIS
    Adds an ErrorMessage property to a vmrun result -- vmrun does not consistently write its
    error messages to stderr (confirmed directly: "Cannot open VM: ..., A password is required
    for this operation" and "Cannot read the virtual machine configuration file" both land on
    STDOUT), so a script that only ever reads .StandardError silently shows a blank error for
    those. Prefers .StandardError when it has content, otherwise falls back to .StandardOutput.
    Mirrors DayZFarm.VMware.VmrunResult.ErrorMessage on the C# side.
#>
function Add-VmrunErrorMessage {
    param([Parameter(Mandatory)] $Result)
    $message = if ([string]::IsNullOrWhiteSpace($Result.StandardError)) { $Result.StandardOutput.Trim() } else { $Result.StandardError.Trim() }
    $Result | Add-Member -MemberType NoteProperty -Name ErrorMessage -Value $message
}

function Invoke-Vmrun {
    param(
        [Parameter(Mandatory)] [string[]] $Arguments,
        [Parameter(Mandatory)] [string] $VmrunPath,
        # Only needed against an encrypted master VMX (vmrun refuses everything else on it
        # without this -- see docs/VMWARE-SETUP.md's "Encrypted master VM" section). Passed as a
        # plain CLI argument since that's what vmrun itself requires either way -- it's visible
        # via process command-line inspection (Task Manager, Get-Process) regardless of how it's
        # stored in this script, the same disclosure tradeoff the dashboard's own -vp plumbing
        # (ProcessVmrunRunner) has.
        [string] $VmxPassword,
        [int] $TimeoutSeconds = 45
    )

    $authFlags = @('-T', 'ws')
    if ($VmxPassword) { $authFlags += @('-vp', $VmxPassword) }

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $VmrunPath
    foreach ($a in ($authFlags + $Arguments)) { $psi.ArgumentList.Add($a) }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()

    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        try { $proc.Kill($true) } catch { }
        $result = [PSCustomObject]@{
            ExitCode       = -1
            StandardOutput = ''
            StandardError  = "vmrun timed out after ${TimeoutSeconds}s and was killed."
            Success        = $false
        }
        Add-VmrunErrorMessage $result
        return $result
    }

    $result = [PSCustomObject]@{
        ExitCode       = $proc.ExitCode
        StandardOutput = $stdoutTask.GetAwaiter().GetResult()
        StandardError  = $stderrTask.GetAwaiter().GetResult()
        Success        = ($proc.ExitCode -eq 0)
    }
    Add-VmrunErrorMessage $result
    return $result
}

<#
.SYNOPSIS
    Reads a .vmx file's "key = "value"" lines into a hashtable -- the PowerShell equivalent of
    GameFarm.VMware.VmxFile.Read.
#>
function Get-VmxSettings {
    param([Parameter(Mandatory)] [string] $Path)
    $settings = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*([\w.]+)\s*=\s*"(.*)"\s*$') {
            $settings[$Matches[1]] = $Matches[2]
        }
    }
    return $settings
}

<#
.SYNOPSIS
    Replaces (or appends) "key = "value"" lines in a .vmx file, preserving every other line and
    its order -- the PowerShell equivalent of GameFarm.VMware.VmxFile.Set.
#>
function Set-VmxValues {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [hashtable] $Updates
    )

    $remainingKeys = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]$Updates.Keys, [System.StringComparer]::OrdinalIgnoreCase)

    $newLines = foreach ($line in (Get-Content -LiteralPath $Path)) {
        $replaced = $false
        if ($line -match '^\s*([\w.]+)\s*=') {
            $key = $Matches[1]
            if ($Updates.ContainsKey($key)) {
                "$key = `"$($Updates[$key])`""
                [void]$remainingKeys.Remove($key)
                $replaced = $true
            }
        }
        if (-not $replaced) { $line }
    }

    foreach ($key in $remainingKeys) {
        $newLines += "$key = `"$($Updates[$key])`""
    }

    Set-Content -LiteralPath $Path -Value $newLines
}

<#
.SYNOPSIS
    True if the given .vmx's full path appears in "vmrun list" (currently running VMs).
#>
function Test-VmxRunning {
    param(
        [Parameter(Mandatory)] [string] $VmxPath,
        [Parameter(Mandatory)] [string] $VmrunPath
    )

    $running = Invoke-Vmrun -VmrunPath $VmrunPath -Arguments @('list')
    if (-not $running.Success) { return $false }

    $fullPath = (Resolve-Path -LiteralPath $VmxPath -ErrorAction SilentlyContinue).Path
    if (-not $fullPath) { return $false }

    foreach ($line in ($running.StandardOutput -split "`r?`n" | Where-Object { $_.Trim() })) {
        $candidate = (Resolve-Path -LiteralPath $line.Trim() -ErrorAction SilentlyContinue).Path
        if ($candidate -and $candidate -eq $fullPath) { return $true }
    }
    return $false
}
