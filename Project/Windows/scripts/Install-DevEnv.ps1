<#
.SYNOPSIS
    Provisions this machine for Feige Windows native development
    (WinUI 3 / Windows App SDK 2.x / .NET 10).

.DESCRIPTION
    Idempotent setup script. Every step checks the current state first, so
    re-running it is safe.

    Steps:
      1. Enable Developer Mode            (MSIX sideloading + F5 deploy)
      2. Enable long path support         (deep MSIX / obj paths)
      3. Install .NET 10 SDK              (Microsoft.DotNet.SDK.10)
      4. Install WinApp CLI               (Microsoft.WinAppCli)
      5. Install Visual Studio 2026       (Community + WinUI workloads)   [skippable]
      6. Set the active Wi-Fi profile to Private                        [opt-in]

    Workload IDs below come from the official Microsoft configuration file
    https://aka.ms/winui-config (channel VisualStudio.18.Release).

.PARAMETER SkipVisualStudio
    Install only the CLI toolchain (dotnet SDK + WinApp CLI), no IDE.

.PARAMETER SetPrivateNetwork
    Flip the active Wi-Fi profile from Public to Private. Required for the app
    to receive inbound mDNS replies and transfer connections, but it also
    relaxes the firewall profile, so it is opt-in.

.PARAMETER DryRun
    Print the commands that would run, change nothing.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Install-DevEnv.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Install-DevEnv.ps1 -SkipVisualStudio -DryRun

.NOTES
    Must run elevated. ASCII-only on purpose (Windows PowerShell 5.1 mis-decodes
    UTF-8 without BOM).
#>
[CmdletBinding()]
param(
    [switch]$SkipVisualStudio,
    [switch]$SetPrivateNetwork,
    [switch]$DryRun
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Package / workload identifiers pinned here so the whole team builds the same thing.
$DotnetSdkId = 'Microsoft.DotNet.SDK.10'
$WinAppCliId = 'Microsoft.WinAppCli'
$VsId = 'Microsoft.VisualStudio.Community'
$VsComponents = @(
    'Microsoft.VisualStudio.Workload.ManagedDesktop'
    'Microsoft.VisualStudio.Workload.Universal'
    'Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cs'
)

function Write-Step {
    param([string]$Text)
    Write-Output ''
    Write-Output ('--- ' + $Text)
}

# Environment variables that differ only in casing (http_proxy + HTTP_PROXY) crash
# the VS bootstrapper with exit code 5002 ("An item with the same key has already
# been added"). Repair the process environment before handing off to winget.
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

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Winget {
    param([string[]]$Arguments)
    if ($DryRun) {
        Write-Output ('  [dry-run] winget ' + ($Arguments -join ' '))
        return $true
    }
    Write-Output ('  > winget ' + ($Arguments -join ' '))
    & winget @Arguments 2>&1 | ForEach-Object { Write-Output ('    ' + $_) }
    # winget exit codes: 0 ok, -1978335189 (0x8A15002B) already installed / no applicable upgrade
    return ($LASTEXITCODE -eq 0 -or $LASTEXITCODE -eq -1978335189)
}

if (-not (Test-Admin)) {
    Write-Output 'ERROR: this script must run elevated (Administrator).'
    exit 1
}

if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    Write-Output 'ERROR: winget (App Installer) not found. Install "App Installer" from the Microsoft Store first.'
    exit 1
}

Repair-DuplicateProxyEnvironment

# ---------------------------------------------------------------- 1. Developer Mode
Write-Step '1/6 Developer Mode'
$devKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
$current = (Get-ItemProperty $devKey -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($current -eq 1) {
    Write-Output '  already enabled, skipping'
} elseif ($DryRun) {
    Write-Output '  [dry-run] set AllowDevelopmentWithoutDevLicense = 1'
} else {
    if (-not (Test-Path $devKey)) { New-Item -Path $devKey -Force | Out-Null }
    New-ItemProperty -Path $devKey -Name AllowDevelopmentWithoutDevLicense -PropertyType DWord -Value 1 -Force | Out-Null
    New-ItemProperty -Path $devKey -Name AllowAllTrustedApps -PropertyType DWord -Value 1 -Force | Out-Null
    Write-Output '  enabled'
}

# ---------------------------------------------------------------- 2. Long paths
Write-Step '2/6 Long path support'
$fsKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem'
$lp = (Get-ItemProperty $fsKey).LongPathsEnabled
if ($lp -eq 1) {
    Write-Output '  already enabled, skipping'
} elseif ($DryRun) {
    Write-Output '  [dry-run] set LongPathsEnabled = 1'
} else {
    New-ItemProperty -Path $fsKey -Name LongPathsEnabled -PropertyType DWord -Value 1 -Force | Out-Null
    Write-Output '  enabled'
}

# ---------------------------------------------------------------- 3. .NET 10 SDK
Write-Step '3/6 .NET 10 SDK'
$sdks = @()
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) { $sdks = @(& dotnet --list-sdks 2>$null | Where-Object { $_ -match '^10\.' }) }
if ($sdks.Count -gt 0) {
    Write-Output ('  already installed: ' + ($sdks -join ' | '))
} else {
    $ok = Invoke-Winget -Arguments @('install', '--id', $DotnetSdkId, '--exact', '--source', 'winget', '--silent', '--accept-package-agreements', '--accept-source-agreements')
    if (-not $ok) { Write-Output '  WARNING: .NET SDK install returned a non-zero code' }
}

# ---------------------------------------------------------------- 4. WinApp CLI
Write-Step '4/6 WinApp CLI'
if (Get-Command winapp -ErrorAction SilentlyContinue) {
    Write-Output '  already installed, skipping'
} else {
    $ok = Invoke-Winget -Arguments @('install', '--id', $WinAppCliId, '--exact', '--source', 'winget', '--silent', '--accept-package-agreements', '--accept-source-agreements')
    if (-not $ok) { Write-Output '  WARNING: WinApp CLI install returned a non-zero code' }
}

# ---------------------------------------------------------------- 5. Visual Studio 2026
Write-Step '5/6 Visual Studio 2026 + WinUI workloads'
if ($SkipVisualStudio) {
    Write-Output '  skipped (-SkipVisualStudio)'
} else {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $installed = $null
    if (Test-Path $vswhere) {
        $installed = & $vswhere -latest -products * -requires $VsComponents[2] -property installationVersion 2>$null
    }
    if ($installed) {
        Write-Output ('  already installed with WinUI component group: ' + $installed)
    } else {
        # --override replaces winget's default args and hands the rest to the VS bootstrapper,
        # which is how the official aka.ms/winui-config ends up with exactly these components.
        $overrideArg = '--quiet --wait --norestart --add ' + ($VsComponents -join ' --add ')
        $ok = Invoke-Winget -Arguments @('install', '--id', $VsId, '--exact', '--source', 'winget', '--silent',
            '--accept-package-agreements', '--accept-source-agreements', '--override', $overrideArg)
        if (-not $ok) {
            Write-Output '  WARNING: Visual Studio install returned a non-zero code.'
            Write-Output '  Fallback: run "winget configure -f https://aka.ms/winui-config" or use the VS Installer GUI.'
        }
    }
}

# ---------------------------------------------------------------- 6. Network profile
Write-Step '6/6 Network profile'
if (-not $SetPrivateNetwork) {
    Write-Output '  skipped (pass -SetPrivateNetwork to switch Wi-Fi to Private)'
} else {
    $publicProfiles = @(Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq 'Public' })
    if ($publicProfiles.Count -eq 0) {
        Write-Output '  no Public profile, nothing to do'
    } elseif ($DryRun) {
        foreach ($p in $publicProfiles) { Write-Output ('  [dry-run] set ' + $p.Name + ' -> Private') }
    } else {
        foreach ($p in $publicProfiles) {
            Set-NetConnectionProfile -InterfaceIndex $p.InterfaceIndex -NetworkCategory Private
            Write-Output ('  ' + $p.Name + ' -> Private')
        }
    }
}

# ---------------------------------------------------------------- verify
Write-Step 'Verification'
$check = Join-Path $PSScriptRoot 'Check-DevEnv.ps1'
if (Test-Path $check) {
    if ($DryRun) {
        Write-Output ('  [dry-run] powershell -File ' + $check)
    } else {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $check
    }
} else {
    Write-Output ('  Check-DevEnv.ps1 not found next to this script (' + $PSScriptRoot + ')')
}

Write-Output ''
Write-Output 'Done. Open a NEW terminal before using dotnet / winapp (PATH changes need a fresh process).'
Write-Output ''
