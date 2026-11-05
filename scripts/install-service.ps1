#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes the DiskDrift Collector and installs it as the auto-start LocalSystem service "DiskDrift",
    then installs the Tray and starts it at logon for every user.

.DESCRIPTION
    1. dotnet publish -c Release -r win-x64 --self-contained for the Collector and the Tray.
    2. Stops the service if it is already installed, then copies the build to -InstallDir
       (Program Files by default, so only administrators can replace the binary SYSTEM runs).
       An existing appsettings.json there is kept, so local config survives a reinstall.
    3. Creates the service (or updates its path), sets restart-on-failure, and starts it.
    4. Stops any running Tray, copies it to -TrayInstallDir, and adds the machine-wide
       HKLM\Software\Microsoft\Windows\CurrentVersion\Run value "DiskDriftTray" so it starts at every
       user's logon. The Tray reads its colour thresholds from the Collector's appsettings.json.
       It is then started (un-elevated) for the current user.

    Run it again to upgrade in place. Undo with uninstall-service.ps1.

.EXAMPLE
    scripts/install-service.ps1
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'DiskDrift\Collector'),

    # The Tray's folder. Keep it next to -InstallDir: the Tray looks for ..\Collector\appsettings.json.
    [string] $TrayInstallDir = (Join-Path $env:ProgramFiles 'DiskDrift\Tray'),

    # Overwrite an existing appsettings.json in -InstallDir with the one from the repo.
    [switch] $ResetConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'DiskDrift'
$DisplayName = 'DiskDrift Collector'
$Description = 'Records free space, top writers and Probe sizes to explain disk-space Swings.'
$ExeName     = 'DiskDrift.Collector.exe'
$TrayExeName = 'DiskDrift.Tray.exe'
$RunKey      = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValue    = 'DiskDriftTray'

$repoRoot     = Split-Path -Parent $PSScriptRoot
$project      = Join-Path $repoRoot 'src\DiskDrift.Collector\DiskDrift.Collector.csproj'
$staging      = Join-Path $repoRoot 'artifacts\publish\collector'
$trayProject  = Join-Path $repoRoot 'src\DiskDrift.Tray\DiskDrift.Tray.csproj'
$trayStaging  = Join-Path $repoRoot 'artifacts\publish\tray'

function Invoke-Native {
    param([string] $What, [scriptblock] $Command)
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

function Wait-ServiceStatus {
    param([string] $Status, [int] $TimeoutSeconds = 30)
    $service = Get-Service -Name $ServiceName
    $service.WaitForStatus($Status, [TimeSpan]::FromSeconds($TimeoutSeconds))
}

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

Write-Host "Publishing the Collector (Release, win-x64, self-contained)..."
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Invoke-Native 'dotnet publish' {
    dotnet publish $project -c Release -r win-x64 --self-contained -o $staging --nologo
}

Write-Host "Publishing the Tray (Release, win-x64, self-contained)..."
if (Test-Path $trayStaging) { Remove-Item $trayStaging -Recurse -Force }
Invoke-Native 'dotnet publish (Tray)' {
    dotnet publish $trayProject -c Release -r win-x64 --self-contained -o $trayStaging --nologo
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host "Stopping the existing $ServiceName service..."
    Stop-Service -Name $ServiceName -Force
    Wait-ServiceStatus 'Stopped'
}
if (Test-Path $InstallDir) { Wait-FolderProcessesExit $InstallDir }

Write-Host "Copying to $InstallDir..."
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
$configPath = Join-Path $InstallDir 'appsettings.json'
$keepConfig = (Test-Path $configPath) -and -not $ResetConfig
Get-ChildItem -Path $staging | Where-Object { -not ($keepConfig -and $_.Name -eq 'appsettings.json') } |
    Copy-Item -Destination $InstallDir -Recurse -Force
if ($keepConfig) { Write-Host "Kept the existing $configPath (use -ResetConfig to replace it)." }

$exePath = Join-Path $InstallDir $ExeName
$binPath = "`"$exePath`""

if ($existing) {
    Write-Host "Updating the $ServiceName service..."
    # Written to the registry directly: sc.exe config mangles a quoted binPath under Windows PowerShell 5.1.
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    Set-ItemProperty -Path $key -Name ImagePath -Value $binPath
    Set-ItemProperty -Path $key -Name ObjectName -Value 'LocalSystem'
    Set-Service -Name $ServiceName -DisplayName $DisplayName -StartupType Automatic
} else {
    Write-Host "Creating the $ServiceName service..."
    # New-Service with no -Credential runs as LocalSystem, and quotes the path reliably on Windows PowerShell 5.1 too.
    New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName $DisplayName `
        -Description $Description -StartupType Automatic | Out-Null
}
Invoke-Native 'sc.exe description' { sc.exe description $ServiceName $Description }

# Restart 1 minute after any crash or non-zero exit; the failure count resets after a day.
Invoke-Native 'sc.exe failure' { sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 }
Invoke-Native 'sc.exe failureflag' { sc.exe failureflag $ServiceName 1 }

Write-Host "Starting $ServiceName..."
Start-Service -Name $ServiceName
Wait-ServiceStatus 'Running'

# The Tray: every signed-in user may be running a copy, which locks the files; stop them all first.
Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($TrayExeName)) -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
if (Test-Path $TrayInstallDir) { Wait-FolderProcessesExit $TrayInstallDir }

Write-Host "Copying the Tray to $TrayInstallDir..."
if (Test-Path $TrayInstallDir) { Remove-Item $TrayInstallDir -Recurse -Force }
New-Item -ItemType Directory -Path $TrayInstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $trayStaging '*') -Destination $TrayInstallDir -Recurse -Force

$trayExe = Join-Path $TrayInstallDir $TrayExeName
Write-Host "Adding the Tray to $RunKey ($RunValue) so it starts at every user's logon..."
New-ItemProperty -Path $RunKey -Name $RunValue -Value "`"$trayExe`"" -PropertyType String -Force | Out-Null

# This script runs elevated; starting the Tray through Explorer runs it as the signed-in user, un-elevated.
Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList "`"$trayExe`""

$dataDir = Join-Path $env:ProgramData 'DiskDrift'
Write-Host ""
Write-Host "DiskDrift is running as LocalSystem and starts with Windows; the Tray starts at every logon."
Write-Host "  Binaries: $InstallDir"
Write-Host "  Tray:     $TrayInstallDir"
Write-Host "  Database: $(Join-Path $dataDir 'diskdrift.db')"
Write-Host "  Logs:     $(Join-Path $dataDir 'logs')"
