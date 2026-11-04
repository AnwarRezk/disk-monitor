<#
.SYNOPSIS
    The setup's service steps: stop DiskMon before files are replaced, register and start the service after,
    and remove it on uninstall. DiskMon-Setup.exe runs it elevated; it is not meant to be run by hand
    (use scripts/install-service.ps1 to install from source).

.PARAMETER Action
    Stop    - stop every running Tray and the DiskMon service, and wait until their files are free.
    Install - create the DiskMon service (or point the existing one at -AppDir), set restart-on-failure, start it.
    Remove  - Stop, then delete the service.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Stop', 'Install', 'Remove')] [string] $Action,
    [Parameter(Mandatory)] [string] $AppDir,

    # On failure the error is written here as well, so the setup can show it.
    [string] $ErrorFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'DiskMon'
$DisplayName = 'DiskMon Collector'
$Description = 'Records free space, top writers and Probe sizes to explain disk-space Swings.'
$CollectorDir = Join-Path $AppDir 'Collector'
$TrayDir      = Join-Path $AppDir 'Tray'

function Invoke-Native {
    param([string] $What, [scriptblock] $Command)
    & $Command | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

# A service reports Stopped a moment before its process exits and releases its files (clrjit.dll, ...),
# so wait for every process running from $Dir to be gone before touching it.
function Wait-FolderProcessesExit {
    param([string] $Dir, [int] $TimeoutSeconds = 30)
    if (-not (Test-Path $Dir)) { return }
    $prefix = (Join-Path ([IO.Path]::GetFullPath($Dir)) '')
    $running = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count -eq 0) { return }
    $running | Wait-Process -Timeout $TimeoutSeconds -ErrorAction SilentlyContinue
    $left = @($running | Where-Object { -not $_.HasExited })
    if ($left.Count -gt 0) {
        throw "Still running from ${Dir}: $(($left | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', ')."
    }
}

function Stop-DiskMon {
    # Every signed-in user may be running a Tray, which locks its files; as an administrator we can stop them all.
    Get-Process -Name 'DiskMon.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Wait-FolderProcessesExit $TrayDir

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Write-Host "Stopping $ServiceName..."
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Wait-FolderProcessesExit $CollectorDir
}

try {
switch ($Action) {
    'Stop' { Stop-DiskMon }

    'Install' {
        $binPath = "`"$(Join-Path $CollectorDir 'DiskMon.Collector.exe')`""
        if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
            Write-Host "Updating the $ServiceName service..."
            # Written to the registry directly: sc.exe config mangles a quoted binPath under Windows PowerShell 5.1.
            $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
            Set-ItemProperty -Path $key -Name ImagePath -Value $binPath
            Set-ItemProperty -Path $key -Name ObjectName -Value 'LocalSystem'
            Set-Service -Name $ServiceName -DisplayName $DisplayName -StartupType Automatic
        } else {
            Write-Host "Creating the $ServiceName service..."
            # New-Service with no -Credential runs as LocalSystem.
            New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName $DisplayName `
                -Description $Description -StartupType Automatic | Out-Null
        }
        Invoke-Native 'sc.exe description' { sc.exe description $ServiceName $Description }
        # Restart 1 minute after any crash or non-zero exit; the failure count resets after a day.
        Invoke-Native 'sc.exe failure' { sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 }
        Invoke-Native 'sc.exe failureflag' { sc.exe failureflag $ServiceName 1 }

        Write-Host "Starting $ServiceName..."
        Start-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    }

    'Remove' {
        Stop-DiskMon
        if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
            Write-Host "Removing the $ServiceName service..."
            Invoke-Native 'sc.exe delete' { sc.exe delete $ServiceName }
        }
    }
}
} catch {
    if ($ErrorFile) { "$_" | Out-File -FilePath $ErrorFile -Encoding utf8 }
    throw
}
