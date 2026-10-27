<#
.SYNOPSIS
    Install the built MSIX, prove it launches and listens, then uninstall it.

.DESCRIPTION
    A sideload acceptance test for the packaged app: clean install, launch with
    package identity, confirm the expected network footprint (one TCP listener
    plus mDNS on 5353), then remove the package and verify nothing is left.

    The package is always removed first so the run is a real "clean install"
    instead of an upgrade. Without -KeepInstalled the machine is left the way it
    was found.

.PARAMETER MsixPath
    Package to install. Defaults to the newest .msix under Release\Windows\<version>.

.PARAMETER CerPath
    Certificate to trust before installing. Defaults to .certs\feige-dev.cer when
    it exists. Ignored when the package is already trusted.

.PARAMETER KeepInstalled
    Leave the package installed (and the app running stopped) after a pass.

.PARAMETER TimeoutSeconds
    How long to wait for the app's sockets to appear. Default 30.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Test-MsixPackage.ps1

.NOTES
    Exit code 0 = install/launch/ports/uninstall all passed, 1 = failure.
    ASCII-only output on purpose (Windows PowerShell 5.1 mis-decodes UTF-8 without BOM).
#>
[CmdletBinding()]
param(
    [string]$MsixPath = "",
    [string]$CerPath = "",
    [switch]$KeepInstalled,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# The AppX deployment progress bar re-renders on every callback and floods a
# redirected log with duplicated lines, so keep it quiet.
$ProgressPreference = "SilentlyContinue"

$windowsRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent $windowsRoot
$repoRoot = Split-Path -Parent $projectRoot
$packageName = "FeigeTransfer.Windows"
$processName = "Feige.App"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "== $Message" -ForegroundColor Cyan
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
    Write-Host "PASS: $Message" -ForegroundColor Green
}

function Test-Elevated {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Note: the comma before the return value stops PowerShell from unrolling the
# array. Without it an empty result becomes $null and $x.Count throws under
# Set-StrictMode, and a single item loses .Count / [0] semantics.
#
# UDP 5353 is probed with netstat rather than Get-NetUdpEndpoint on purpose.
# mDNS sockets bind 5353 with SO_REUSEADDR, so several processes legitimately
# share the port (Windows' own responder runs in svchost). Get-NetUdpEndpoint
# then reports only one of the sharers and the check becomes flaky; netstat
# lists every binding with its PID.
function Get-ListeningSockets {
    param([int]$ProcessId)

    $items = @(netstat -ano -p UDP |
        Where-Object { $_ -match '^\s*UDP\s+\S*:5353\s' } |
        ForEach-Object {
            $fields = ($_ -replace '^\s+', '') -split '\s+'
            $owner = 0

            if ($fields.Count -ge 4 -and [int]::TryParse($fields[$fields.Count - 1], [ref]$owner) -and $owner -eq $ProcessId) {
                $fields[1]
            }
        })

    return ,$items
}

function Get-TcpListeners {
    param([int]$ProcessId)
    $items = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -eq $ProcessId })
    return ,$items
}

function Get-AppProcess {
    # The packaged executable is Feige.App.exe, but fall back to a prefix match
    # so a rename in the manifest cannot silently break the acceptance test.
    $exact = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
    if ($exact.Count -gt 0) {
        return ,$exact
    }
    $fuzzy = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like "Feige*" })
    return ,$fuzzy
}

if ($MsixPath -eq "") {
    [xml]$projectXml = Get-Content (Join-Path $windowsRoot "src\Feige.App\Feige.App.csproj") -Raw
    $packageVersion = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    $versionedMsixRoot = Join-Path (Join-Path $repoRoot "Release\Windows") $packageVersion
    $candidate = Get-ChildItem -Path $versionedMsixRoot -Filter "*.msix" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $candidate) {
        throw "No .msix found under $versionedMsixRoot. Run Build-Msix.ps1 first."
    }

    $MsixPath = $candidate.FullName
}

if ($CerPath -eq "") {
    $defaultCer = Join-Path $windowsRoot ".certs\feige-dev.cer"
    if (Test-Path $defaultCer) {
        $CerPath = $defaultCer
    }
}

Write-Step "Trusting signing certificate"
if ($CerPath -ne "" -and (Test-Path $CerPath)) {
    $cerThumbprint = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CerPath)).Thumbprint
    $store = if (Test-Elevated) { "Cert:\LocalMachine\TrustedPeople" } else { "Cert:\CurrentUser\TrustedPeople" }
    $trusted = Get-ChildItem $store | Where-Object { $_.Thumbprint -eq $cerThumbprint }

    if ($null -eq $trusted) {
        Import-Certificate -FilePath $CerPath -CertStoreLocation $store | Out-Null
        Write-Host "Imported $CerPath into $store"
    }
    else {
        Write-Host "Already trusted in $store"
    }
}
else {
    Write-Host "No certificate given; assuming the package is already trusted."
}

Write-Step "Removing any previous installation"
$existing = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    foreach ($item in (Get-AppProcess)) {
        Stop-Process -Id $item.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-AppxPackage -Package $existing.PackageFullName
    Write-Host "Removed $($existing.PackageFullName)"
}

Write-Step "Installing $MsixPath"
Add-AppxPackage -Path $MsixPath

$package = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
Assert-True ($null -ne $package) "package registered"
Write-Host "Package: $($package.PackageFullName)"
Write-Host "Publisher: $($package.Publisher)"

Write-Step "Launching"
$aumid = "$($package.PackageFamilyName)!App"
Start-Process "shell:AppsFolder\$aumid"

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$process = $null
$tcpListeners = @()
$mdnsSockets = @()
$launchPid = 0

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500

    $running = Get-AppProcess
    if ($running.Count -eq 0) {
        continue
    }

    $process = $running[0]
    if ($process.Id -ne $launchPid) {
        $launchPid = $process.Id
        Write-Host "Process appeared: $($process.ProcessName) (PID $launchPid)"
    }

    $tcpListeners = Get-TcpListeners -ProcessId $launchPid
    $mdnsSockets = Get-ListeningSockets -ProcessId $launchPid

    if ($tcpListeners.Count -gt 0 -and $mdnsSockets.Count -gt 0) {
        break
    }
}

Assert-True ($null -ne $process) "app process is running"

if ($null -ne $process) {
    Write-Host "PID: $($process.Id)"
    Write-Host "Image: $($process.ProcessName)"
}

if ($tcpListeners.Count -eq 0 -or $mdnsSockets.Count -eq 0) {
    Write-Host "Diagnostic: every socket owned by PID $launchPid" -ForegroundColor Yellow

    $all = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -eq $launchPid })

    if ($all.Count -eq 0 -and $mdnsSockets.Count -eq 0) {
        Write-Host "  (none)"
    }
    else {
        foreach ($item in $all) {
            Write-Host "  TCP $($item.LocalAddress):$($item.LocalPort)"
        }

        foreach ($item in $mdnsSockets) {
            Write-Host "  UDP $item"
        }
    }

    # The app writes start-up progress and failures here; a missing socket is
    # usually explained by a line in this file.
    $log = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState\app.log"

    if (Test-Path $log) {
        Write-Host "App log ($log):" -ForegroundColor Yellow
        Get-Content $log -Tail 15 | ForEach-Object { Write-Host "  $_" }
    }
}

$tcpPort = 0
if ($tcpListeners.Count -gt 0) {
    $tcpPort = $tcpListeners[0].LocalPort
}

Assert-True ($tcpListeners.Count -gt 0) "TCP listener is open (port $tcpPort)"
Assert-True ($mdnsSockets.Count -gt 0) "mDNS socket 5353 is bound ($($mdnsSockets[0]))"

Write-Step "Stopping app"
$running = Get-AppProcess
foreach ($item in $running) {
    Stop-Process -Id $item.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

if ($KeepInstalled) {
    Write-Host "Package kept installed (-KeepInstalled)."
    Write-Host ""
    Write-Host "Sideload acceptance passed." -ForegroundColor Green
    exit 0
}

Write-Step "Uninstalling"
Remove-AppxPackage -Package $package.PackageFullName
Start-Sleep -Seconds 2

$leftover = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
Assert-True ($null -eq $leftover) "package removed"

$leftoverProcess = Get-AppProcess
Assert-True ($leftoverProcess.Count -eq 0) "no process left"

Write-Host ""
Write-Host "Sideload acceptance passed." -ForegroundColor Green
exit 0
