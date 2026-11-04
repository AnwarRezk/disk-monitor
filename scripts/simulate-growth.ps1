#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Creates (or, with -Cleanup, deletes) a file of a given size to produce test Swings for DiskMon.

.DESCRIPTION
    Creates diskmon-simulate.bin of -SizeGB with fsutil file createnew, which allocates the space at once, so C:
    free space drops in one step:
      -Location temp      your Temp folder (%LOCALAPPDATA%\Temp), which the "User Temp" Probe covers;
      -Location unprobed  C:\DiskMonSimulate, which no Probe covers, so the Swing shows up as Unaccounted.
    Run it again with -Cleanup to delete the file (a Release Swing). With -DeleteAfterMinutes the script waits and
    deletes it itself, which produces a Growth Swing followed by a Release Swing.

    Swings are judged over 15 minutes, so allow up to ~16 minutes for a Swing to settle and show as "attributing…".

.EXAMPLE
    scripts/simulate-growth.ps1 -SizeGB 2 -Location temp
    scripts/simulate-growth.ps1 -Location temp -Cleanup

.EXAMPLE
    # Create then delete within 10 minutes: two Swings, not one netted Swing.
    scripts/simulate-growth.ps1 -SizeGB 2 -Location temp -DeleteAfterMinutes 5

.EXAMPLE
    # A Swing across Downtime.
    sc.exe stop DiskMon
    scripts/simulate-growth.ps1 -SizeGB 2 -Location unprobed
    sc.exe start DiskMon
#>
[CmdletBinding()]
param(
    [ValidateRange(0.1, 1024)]
    [double] $SizeGB = 2,

    [ValidateSet('temp', 'unprobed')]
    [string] $Location = 'temp',

    # Delete the file this script created at -Location instead of creating one.
    [switch] $Cleanup,

    # After creating the file, wait this many minutes and delete it.
    [ValidateRange(0, 1440)]
    [double] $DeleteAfterMinutes = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$FileName = 'diskmon-simulate.bin'
$folder = switch ($Location) {
    'temp'     { Join-Path $env:LOCALAPPDATA 'Temp' }
    'unprobed' { 'C:\DiskMonSimulate' }
}
$path = Join-Path $folder $FileName

function Get-FreeGB {
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($folder))
    [math]::Round($drive.AvailableFreeSpace / 1GB, 2)
}

function Remove-SimulatedFile {
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host "Nothing to clean up: $path does not exist."
        return
    }
    Remove-Item -LiteralPath $path -Force
    Write-Host "Deleted $path. Free space now $(Get-FreeGB) GB (expect a Release Swing)."
    if ($Location -eq 'unprobed' -and -not (Get-ChildItem -LiteralPath $folder -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $folder -Force
    }
}

if ($Cleanup) {
    Remove-SimulatedFile
    return
}

if (Test-Path -LiteralPath $path) {
    throw "$path already exists. Delete it first with -Cleanup."
}

$bytes = [long]($SizeGB * 1GB)
$freeBefore = Get-FreeGB
if ($freeBefore - $SizeGB -lt 5) {
    throw "Only $freeBefore GB free; creating $SizeGB GB would leave less than 5 GB. Use a smaller -SizeGB."
}

New-Item -ItemType Directory -Path $folder -Force | Out-Null
& fsutil.exe file createnew $path $bytes | Out-Null
if ($LASTEXITCODE -ne 0) { throw "fsutil file createnew failed with exit code $LASTEXITCODE." }
Write-Host "Created $path ($SizeGB GB). Free space $freeBefore GB -> $(Get-FreeGB) GB (expect a Growth Swing)."

if ($DeleteAfterMinutes -gt 0) {
    Write-Host "Deleting it in $DeleteAfterMinutes minute(s)..."
    Start-Sleep -Seconds ([int]($DeleteAfterMinutes * 60))
    Remove-SimulatedFile
}
else {
    Write-Host "Undo with: scripts/simulate-growth.ps1 -Location $Location -Cleanup"
}
