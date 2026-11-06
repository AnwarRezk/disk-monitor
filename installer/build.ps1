<#
.SYNOPSIS
    Builds DiskDrift-Setup-<version>.exe in artifacts\installer: publishes the Collector and the Tray
    (Release, win-x64, self-contained), then compiles installer\DiskDrift.iss with Inno Setup 6.

.EXAMPLE
    installer/build.ps1 -Version 0.2.0
#>
[CmdletBinding()]
param(
    [string] $Version = '0.0.0',

    # ISCC.exe; found in the usual Inno Setup 6 folders when not given (winget install JRSoftware.InnoSetup).
    [string] $Iscc,

    # Optional sign command template containing $f (e.g. 'signtool.exe sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 $f').
    # Signs the published Collector and Tray binaries, the Inno Setup uninstaller, and the setup itself.
    [string] $SignCommand,

    [switch] $SkipPublish,
    [switch] $SkipCompile
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

if (-not $SkipPublish) {
    foreach ($app in @(@{ Name = 'collector'; Project = 'src\DiskDrift.Collector\DiskDrift.Collector.csproj'; Exe = 'DiskDrift.Collector.exe' },
                       @{ Name = 'tray';      Project = 'src\DiskDrift.Tray\DiskDrift.Tray.csproj';           Exe = 'DiskDrift.Tray.exe' })) {
        $out = Join-Path $publish $app.Name
        Write-Host "Publishing $($app.Name) $Version..."
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }
        Invoke-Native "dotnet publish ($($app.Name))" {
            dotnet publish (Join-Path $repoRoot $app.Project) -c Release -r win-x64 --self-contained -o $out --nologo `
                "-p:Version=$Version"
        }
        if ($SignCommand) {
            $exe = Join-Path $out $app.Exe
            $cmd = $SignCommand.Replace('$f', "`"$exe`"")
            Write-Host "Signing $exe..."
            Invoke-Native "sign ($($app.Exe))" { cmd.exe /c $cmd }
        }
    }
}

if (-not $SkipCompile) {
    if (-not $Iscc) {
        $Iscc = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $Iscc) { throw 'Inno Setup 6 not found. Install it (winget install JRSoftware.InnoSetup) or pass -Iscc.' }
    }

    Write-Host "Compiling the setup with $Iscc..."
    $isccArgs = @("/DAppVersion=$Version", "/DPublishDir=$publish")
    if ($SignCommand) {
        $isccArgs += "/Ssign=$SignCommand"
        $isccArgs += '/DSignCommand=1'
    }
    $isccArgs += (Join-Path $PSScriptRoot 'DiskDrift.iss')
    Invoke-Native 'ISCC' { & $Iscc @isccArgs }

    $setup = Join-Path $repoRoot "artifacts\installer\DiskDrift-Setup-$Version.exe"
    Write-Host "Built $setup"
}
