<#
.SYNOPSIS
    Builds DiskMon-Setup-<version>.exe in artifacts\installer: publishes the Collector and the Tray
    (Release, win-x64, self-contained), then compiles installer\DiskMon.iss with Inno Setup 6.

.EXAMPLE
    installer/build.ps1 -Version 0.2.0
#>
[CmdletBinding()]
param(
    [string] $Version = '0.0.0',

    # ISCC.exe; found in the usual Inno Setup 6 folders when not given (winget install JRSoftware.InnoSetup).
    [string] $Iscc
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publish  = Join-Path $repoRoot 'artifacts\publish'

function Invoke-Native {
    param([string] $What, [scriptblock] $Command)
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

if (-not $Iscc) {
    $Iscc = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { throw 'Inno Setup 6 not found. Install it (winget install JRSoftware.InnoSetup) or pass -Iscc.' }
}

foreach ($app in @(@{ Name = 'collector'; Project = 'src\DiskMon.Collector\DiskMon.Collector.csproj' },
                   @{ Name = 'tray';      Project = 'src\DiskMon.Tray\DiskMon.Tray.csproj' })) {
    $out = Join-Path $publish $app.Name
    Write-Host "Publishing $($app.Name) $Version..."
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Invoke-Native "dotnet publish ($($app.Name))" {
        dotnet publish (Join-Path $repoRoot $app.Project) -c Release -r win-x64 --self-contained -o $out --nologo `
            "-p:Version=$Version"
    }
}

Write-Host "Compiling the setup with $Iscc..."
Invoke-Native 'ISCC' { & $Iscc "/DAppVersion=$Version" "/DPublishDir=$publish" (Join-Path $PSScriptRoot 'DiskMon.iss') }

$setup = Join-Path $repoRoot "artifacts\installer\DiskMon-Setup-$Version.exe"
Write-Host "Built $setup"
