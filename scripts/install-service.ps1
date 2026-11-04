#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes the DiskMon Collector and installs it as the auto-start LocalSystem service "DiskMon".

.DESCRIPTION
    1. dotnet publish -c Release -r win-x64 --self-contained into a staging folder.
    2. Stops the service if it is already installed, then copies the build to -InstallDir
       (Program Files by default, so only administrators can replace the binary SYSTEM runs).
       An existing appsettings.json there is kept, so local config survives a reinstall.
    3. Creates the service (or updates its path), sets restart-on-failure, and starts it.

    Run it again to upgrade in place. Undo with uninstall-service.ps1.

.EXAMPLE
    scripts/install-service.ps1
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'DiskMon\Collector'),

    # Overwrite an existing appsettings.json in -InstallDir with the one from the repo.
    [switch] $ResetConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'DiskMon'
$DisplayName = 'DiskMon Collector'
$Description = 'Records free space, top writers and Probe sizes to explain disk-space Swings.'
$ExeName     = 'DiskMon.Collector.exe'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project  = Join-Path $repoRoot 'src\DiskMon.Collector\DiskMon.Collector.csproj'
$staging  = Join-Path $repoRoot 'artifacts\publish\collector'

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

Write-Host "Publishing the Collector (Release, win-x64, self-contained)..."
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Invoke-Native 'dotnet publish' {
    dotnet publish $project -c Release -r win-x64 --self-contained -o $staging --nologo
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host "Stopping the existing $ServiceName service..."
    Stop-Service -Name $ServiceName -Force
    Wait-ServiceStatus 'Stopped'
}

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

$dataDir = Join-Path $env:ProgramData 'DiskMon'
Write-Host ""
Write-Host "DiskMon is running as LocalSystem and starts with Windows."
Write-Host "  Binaries: $InstallDir"
Write-Host "  Database: $(Join-Path $dataDir 'diskmon.db')"
Write-Host "  Logs:     $(Join-Path $dataDir 'logs')"
