<#
.SYNOPSIS
  Test an EXE installer: install, launch, reinstall, verify identity preservation, uninstall.
.NOTES
  Run in a clean test Windows account. Refuses to replace an existing EXE installation.
  Run in an interactive desktop; UI Automation completes the first-run guide using its defaults.
  Kills only processes started by this test. User data and received files are retained.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InstallerPath,
    [int]$TimeoutSeconds = 60
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$InstallerPath = (Resolve-Path $InstallerPath).Path
$installedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{1BF6149A-D679-44ED-92D0-ACD061BB4707}_is1'
if (Test-Path $installedKey) { throw 'An EXE installation already exists. Use a clean test Windows account; existing installations will not be removed.' }
try {
    $mutex = [Threading.Mutex]::OpenExisting('Local\Songli.FeigeTransfer')
    $mutex.Dispose()
    throw 'An EXE app is running. Exit it from its menu before this test.'
}
catch [Threading.WaitHandleCannotBeOpenedException] { }
$testRoot = Join-Path $env:TEMP ('FeigeInstallerTest-' + [guid]::NewGuid().ToString('N'))
$destination = Join-Path $testRoot 'app'
$data = Join-Path $env:LOCALAPPDATA 'Songli\FeigeTransfer'
$log = Join-Path $data 'app.log'
$identity = Join-Path $data 'identity.pkcs8.dpapi'
$started = $null
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
New-Item $testRoot -ItemType Directory -Force | Out-Null

function Run-Setup {
    param([string]$LogName)
    $process = Start-Process $InstallerPath -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$destination`"", "/LOG=`"$(Join-Path $testRoot $LogName)`"") -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { $process.Kill(); throw 'Installer timed out.' }
    if ($process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode). Logs: $testRoot" }
}
function Start-TestApp {
    $before = if (Test-Path $log) { @(Get-Content $log).Count } else { 0 }
    $process = Start-Process (Join-Path $destination 'Feige.App.exe') -WorkingDirectory $env:TEMP -PassThru
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $completedGuideActions = @{}
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) { throw "Application exited during startup: $($process.ExitCode). Logs: $log" }
        # Scope automation to this process. Never operate another running application's window.
        $process.Refresh()
        $guideVisible = $false
        if ($process.MainWindowHandle -ne 0) {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SetupPrimaryButton')
            $button = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            $guideVisible = $button -and -not $button.Current.IsOffscreen
            if ($button -and $button.Current.IsEnabled -and -not $button.Current.IsOffscreen) {
                $action = $button.Current.Name
                if ($action -in @('保存并继续', '开始使用') -and -not $completedGuideActions.ContainsKey($action)) {
                    ([System.Windows.Automation.InvokePattern]$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
                    $completedGuideActions[$action] = $true
                }
            }
        }
        $lines = if (Test-Path $log) { @(Get-Content $log | Select-Object -Skip $before) } else { @() }
        if (($lines -match 'application: packaged=False').Count -gt 0 -and ($lines -match 'started: protocol=3').Count -gt 0 -and -not $guideVisible) {
            $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $process.Id -ErrorAction SilentlyContinue)
            if ($listeners.Count -gt 0) { return $process }
        }
        Start-Sleep -Milliseconds 250
    }
    $process.Kill()
    throw "Application readiness timed out. Logs: $log"
}
try {
    Run-Setup 'install.log'
    if (-not (Test-Path $installedKey)) { throw 'Uninstall registration was not created.' }
    $started = Start-TestApp
    if (-not (Test-Path $identity)) { throw 'DPAPI identity file was not created.' }
    $identityHash = (Get-FileHash $identity -Algorithm SHA256).Hash
    $retained = @{}
    foreach ($name in @('settings.json', 'conversations.json', 'history.json')) {
        $path = Join-Path $data $name
        if (Test-Path $path) { $retained[$name] = (Get-FileHash $path -Algorithm SHA256).Hash }
    }
    Write-Output 'PASS: installed EXE starts without package identity and owns a TCP listener'
    $started.Kill(); $started.WaitForExit(); $started = $null
    Run-Setup 'reinstall.log'
    $started = Start-TestApp
    if ((Get-FileHash $identity -Algorithm SHA256).Hash -ne $identityHash) { throw 'Reinstallation changed the device identity.' }
    foreach ($name in $retained.Keys) {
        if ((Get-FileHash (Join-Path $data $name) -Algorithm SHA256).Hash -ne $retained[$name]) { throw "Reinstallation changed $name" }
    }
    Write-Output 'PASS: reinstallation and relaunch preserve identity and existing user files'
    $started.Kill(); $started.WaitForExit(); $started = $null
    $uninstall = Start-Process (Join-Path $destination 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -PassThru
    if (-not $uninstall.WaitForExit($TimeoutSeconds * 1000)) { $uninstall.Kill(); throw 'Uninstaller timed out.' }
    if ($uninstall.ExitCode -ne 0 -or (Test-Path $installedKey) -or (Test-Path (Join-Path $destination 'Feige.App.exe'))) { throw 'Uninstall did not complete.' }
    if ((Get-FileHash $identity -Algorithm SHA256).Hash -ne $identityHash) { throw 'Uninstall removed or changed retained identity data.' }
    Write-Output 'PASS: uninstall removes program and registration while retaining user data'
    Write-Output "Acceptance passed. Logs: $testRoot"
}
finally {
    if ($started -and -not $started.HasExited) { $started.Kill(); $started.WaitForExit() }
    # Leave failed installations and logs for investigation; never remove another installation here.
}
