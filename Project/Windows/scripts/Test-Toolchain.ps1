<#
.SYNOPSIS
    End-to-end toolchain smoke test: scaffold a blank WinUI 3 app, build it,
    launch it with MSIX package identity, confirm a real window appeared, then
    clean everything up.

.DESCRIPTION
    Proves that .NET SDK, Windows App SDK and the MSIX debug-deployment path all
    work together on this machine. Nothing is left behind: the temporary project
    folder and the debug package registration are both removed.

    Run this after Install-DevEnv.ps1, and in CI on a clean image.

.PARAMETER Keep
    Keep the temporary project folder for inspection (package still unregistered).

.PARAMETER TimeoutSeconds
    How long to wait for the app window before giving up. Default 60.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Test-Toolchain.ps1

.NOTES
    Exit code 0 = build and launch both succeeded, 1 = failure.
    ASCII-only on purpose (Windows PowerShell 5.1 mis-decodes UTF-8 without BOM).
#>
[CmdletBinding()]
param(
    [switch]$Keep,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Environment variables that differ only in casing (http_proxy + HTTP_PROXY) make
# .NET throw "An item with the same key has already been added" whenever it builds
# a case-insensitive dictionary -- Start-Process and the VS bootstrapper both do.
# Collapse them to a single casing before launching anything.
function Repair-DuplicateProxyEnvironment {
    $canonical = @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY')
    $broken = $false
    try { $null = Get-ChildItem Env: } catch { $broken = $true }
    if (-not $broken) { return }
    $saved = @{}
    foreach ($n in $canonical) { $saved[$n] = [Environment]::GetEnvironmentVariable($n, 'Process') }
    foreach ($n in @('http_proxy', 'https_proxy', 'all_proxy', 'no_proxy')) {
        [Environment]::SetEnvironmentVariable($n, $null, 'Process')
    }
    foreach ($n in $canonical) {
        if ($saved[$n]) { [Environment]::SetEnvironmentVariable($n, $saved[$n], 'Process') }
    }
    Write-Output '  repaired duplicate proxy environment variables'
}
Repair-DuplicateProxyEnvironment

$projectName = 'FeigeToolchainSmoke'
$tempRoot = Join-Path $env:TEMP 'feige-toolchain-smoke'
$projectDir = Join-Path $tempRoot $projectName

function Write-Step {
    param([string]$Text)
    Write-Output ''
    Write-Output ('--- ' + $Text)
}

function Fail {
    param([string]$Text)
    Write-Output ''
    Write-Output ('FAILED: ' + $Text)
    exit 1
}

# ---------------------------------------------------------------- preflight
Write-Step 'Preflight'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Fail 'dotnet not on PATH. Open a new terminal or re-run Install-DevEnv.ps1.' }
$dotnetVersion = (& dotnet --version)
Write-Output ('  dotnet ' + $dotnetVersion)
if ([int]($dotnetVersion -split '\.')[0] -lt 10) { Fail ('need .NET 10 SDK or later, found ' + $dotnetVersion) }

if (-not ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense -eq 1)) {
    Fail 'Developer Mode is disabled -- packaged launch will fail. Run Install-DevEnv.ps1.'
}
Write-Output '  Developer Mode enabled'

# ---------------------------------------------------------------- clean slate
if (Test-Path $tempRoot) { Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

# ---------------------------------------------------------------- templates
Write-Step 'WinUI templates'
& dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates --force 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Fail 'could not install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates' }
Write-Output '  templates ready'

# ---------------------------------------------------------------- scaffold
Write-Step 'Scaffold'
Push-Location $tempRoot
& dotnet new winui -n $projectName 2>&1 | Out-Null
$scaffoldExit = $LASTEXITCODE
Pop-Location
if ($scaffoldExit -ne 0 -or -not (Test-Path (Join-Path $projectDir ($projectName + '.csproj')))) {
    Fail 'dotnet new winui did not produce a project'
}
$csproj = Join-Path $projectDir ($projectName + '.csproj')
$tfm = (Select-String -Path $csproj -Pattern '<TargetFramework>([^<]+)</TargetFramework>').Matches.Groups[1].Value
Write-Output ('  project created, TargetFramework = ' + $tfm)

# ---------------------------------------------------------------- build
Write-Step 'Build'
$buildOut = & dotnet build $csproj -c Debug 2>&1
$buildExit = $LASTEXITCODE
# Rely on the exit code, not on localized build text (the SDK output is localized).
$buildOut | Select-Object -Last 8 | ForEach-Object { Write-Output ('  ' + $_) }
if ($buildExit -ne 0) { Fail 'build failed' }

# ---------------------------------------------------------------- launch
Write-Step 'Launch with package identity'
$stdoutFile = Join-Path $tempRoot 'run.out'
$stderrFile = Join-Path $tempRoot 'run.err'
$runner = Start-Process -FilePath $dotnet.Source -ArgumentList @('run', '--project', $csproj, '--no-build') `
    -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile

$appPid = 0
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $text = ''
    if (Test-Path $stdoutFile) { $text = (Get-Content $stdoutFile -Raw -ErrorAction SilentlyContinue) }
    if ($text -and $text -match 'PID:\s*(\d+)') {
        $appPid = [int]$Matches[1]
        break
    }
    if ($runner.HasExited -and -not $appPid) { break }
}

if ($appPid -eq 0) {
    Write-Output '  --- run stdout ---'
    if (Test-Path $stdoutFile) { Get-Content $stdoutFile | Select-Object -Last 20 | ForEach-Object { Write-Output ('  ' + $_) } }
    Write-Output '  --- run stderr ---'
    if (Test-Path $stderrFile) { Get-Content $stderrFile | Select-Object -Last 20 | ForEach-Object { Write-Output ('  ' + $_) } }
    if (-not $Keep) {
        Stop-Process -Id $runner.Id -Force -ErrorAction SilentlyContinue
        Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    Fail 'app did not report a launch PID'
}

$proc = Get-Process -Id $appPid -ErrorAction SilentlyContinue
if (-not $proc) { Fail ('launched PID ' + $appPid + ' exited immediately') }
Write-Output ('  running: ' + $proc.ProcessName + ' (PID ' + $appPid + ')')

$hasWindow = $false
$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline) {
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { $hasWindow = $true; break }
    Start-Sleep -Milliseconds 500
}
if ($hasWindow) {
    Write-Output ('  window handle: ' + $proc.MainWindowHandle)
} else {
    Write-Output '  WARNING: process alive but no top-level window detected'
}

# ---------------------------------------------------------------- teardown
Write-Step 'Teardown'
Stop-Process -Id $appPid -Force -ErrorAction SilentlyContinue
Stop-Process -Id $runner.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

$stale = @(Get-AppxPackage | Where-Object { $_.InstallLocation -and $_.InstallLocation -like ($tempRoot + '*') })
foreach ($pkg in $stale) {
    Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction SilentlyContinue
    Write-Output ('  unregistered ' + $pkg.Name)
}
if (-not $Keep) {
    Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Output '  temp folder removed'
} else {
    Write-Output ('  temp folder kept at ' + $tempRoot)
}

Write-Output ''
if ($hasWindow) {
    Write-Output 'SMOKE TEST PASSED: build, MSIX identity launch and window creation all work.'
    exit 0
}
Write-Output 'SMOKE TEST PARTIAL: build and launch work, but no window was detected.'
exit 1
