#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stops and removes the DiskMon service, the Tray, its HKLM Run entry and the binaries. Reverses install-service.ps1.

.DESCRIPTION
    The data folder (%ProgramData%\DiskMon: database and logs) is kept unless -RemoveData is given,
    so reinstalling continues the same timeline.

.EXAMPLE
    scripts/uninstall-service.ps1
    scripts/uninstall-service.ps1 -RemoveData
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'DiskMon\Collector'),

    [string] $TrayInstallDir = (Join-Path $env:ProgramFiles 'DiskMon\Tray'),

    # Also delete %ProgramData%\DiskMon (the database and logs). This cannot be undone.
    [switch] $RemoveData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'DiskMon'
$RunKey      = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValue    = 'DiskMonTray'
$dataDir     = Join-Path $env:ProgramData 'DiskMon'

# A service reports Stopped a moment before its process exits and releases its files (clrjit.dll, ...),
# so wait for every process running from $Dir to be gone before touching it.
function Wait-FolderProcessesExit {
    param([string] $Dir, [int] $TimeoutSeconds = 30)
    $prefix = (Join-Path ([IO.Path]::GetFullPath($Dir)) '')
    $running = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count -eq 0) { return }
    Write-Host "Waiting for $($running.Count) process(es) running from $Dir to exit..."
    $running | Wait-Process -Timeout $TimeoutSeconds -ErrorAction SilentlyContinue
    $left = @($running | Where-Object { -not $_.HasExited })
    if ($left.Count -gt 0) {
        throw "Still running from ${Dir}: $(($left | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', ')."
    }
}

if (Get-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue) {
    Write-Host "Removing the Tray's Run entry ($RunValue)..."
    Remove-ItemProperty -Path $RunKey -Name $RunValue
}

$trays = Get-Process -Name 'DiskMon.Tray' -ErrorAction SilentlyContinue
if ($trays) {
    Write-Host "Stopping $(@($trays).Count) running Tray process(es)..."
    $trays | Stop-Process -Force -ErrorAction SilentlyContinue
}
if (Test-Path $TrayInstallDir) { Wait-FolderProcessesExit $TrayInstallDir }

if (Test-Path $TrayInstallDir) {
    Write-Host "Removing $TrayInstallDir..."
    Remove-Item $TrayInstallDir -Recurse -Force
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host "Stopping $ServiceName..."
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    if (Test-Path $InstallDir) { Wait-FolderProcessesExit $InstallDir }
    Write-Host "Removing the $ServiceName service..."
    sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }
} else {
    Write-Host "The $ServiceName service is not installed."
}

if (Test-Path $InstallDir) {
    Write-Host "Removing $InstallDir..."
    Remove-Item $InstallDir -Recurse -Force
    $parent = Split-Path -Parent $InstallDir
    if ((Split-Path -Leaf $parent) -eq 'DiskMon' -and -not (Get-ChildItem $parent -Force)) {
        Remove-Item $parent -Force
    }
}

if ($RemoveData) {
    if (Test-Path $dataDir) {
        Write-Host "Removing $dataDir (database and logs)..."
        Remove-Item $dataDir -Recurse -Force
    }
} else {
    Write-Host "Kept $dataDir (database and logs). Use -RemoveData to delete it."
}

Write-Host "DiskMon is uninstalled."
