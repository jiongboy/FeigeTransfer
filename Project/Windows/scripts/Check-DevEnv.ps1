<#
.SYNOPSIS
    Feige Windows 开发环境体检（只读，不修改任何配置）。

.DESCRIPTION
    Checks every prerequisite for building the WinUI 3 / Windows App SDK client
    on this machine and prints a PASS / WARN / FAIL table.

    Read-only: no installs, no registry writes, no service changes.

.PARAMETER Detailed
    Also prints paths and versions of every detected component.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Check-DevEnv.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Check-DevEnv.ps1 -Detailed

.NOTES
    Exit code 0 = no FAIL rows, 1 = at least one FAIL row.
    Kept ASCII-only on purpose: Windows PowerShell 5.1 mis-decodes UTF-8 files
    without a BOM, which would garble non-ASCII output.
#>
[CmdletBinding()]
param(
    [switch]$Detailed
)

$ErrorActionPreference = 'SilentlyContinue'

# ---------------------------------------------------------------- helpers
$results = New-Object System.Collections.ArrayList

function Add-Result {
    param(
        [Parameter(Mandatory = $true)][string]$Item,
        [Parameter(Mandatory = $true)][ValidateSet('OK', 'WARN', 'FAIL')][string]$Status,
        [string]$Detail = '',
        [switch]$Required
    )
    $null = $results.Add([pscustomobject]@{
            Item     = $Item
            Status   = $Status
            Detail   = $Detail
            Required = [bool]$Required
        })
}

function Get-ExePath {
    param([string]$Name)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

# ---------------------------------------------------------------- OS
$osKey = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$buildNumber = [int]$osKey.CurrentBuild
# Windows 11 still reports ProductName as "Windows 10 Pro" in the registry,
# so classify by build number instead of the (wrong) product name.
if ($buildNumber -ge 22000) { $osLabel = 'Windows 11' } else { $osLabel = 'Windows 10' }
$osDetail = '{0} build {1}.{2} ({3})' -f $osLabel, $buildNumber, $osKey.UBR, $osKey.DisplayVersion

if ($buildNumber -ge 19041) {
    Add-Result -Item 'OS version' -Status OK -Detail $osDetail -Required
} else {
    Add-Result -Item 'OS version' -Status FAIL -Detail ($osDetail + ' -- need Windows 10 2004 (19041) or newer') -Required
}

if ($env:PROCESSOR_ARCHITECTURE -eq 'AMD64') {
    Add-Result -Item 'CPU architecture' -Status OK -Detail 'x64 (also build/test ARM64 later)' -Required
} else {
    Add-Result -Item 'CPU architecture' -Status WARN -Detail $env:PROCESSOR_ARCHITECTURE -Required
}

# ---------------------------------------------------------------- privilege
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
    Add-Result -Item 'Elevation' -Status OK -Detail 'running as Administrator' -Required
} else {
    Add-Result -Item 'Elevation' -Status WARN -Detail 'not elevated -- installing toolchains / firewall rules will fail'
}

# ---------------------------------------------------------------- developer mode
$devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock').AllowDevelopmentWithoutDevLicense
if ($devMode -eq 1) {
    Add-Result -Item 'Developer Mode' -Status OK -Detail 'enabled (sideloading unsigned MSIX allowed)' -Required
} else {
    Add-Result -Item 'Developer Mode' -Status FAIL -Detail 'disabled -- enables sideloading + F5 deploy of packaged apps' -Required
}

# ---------------------------------------------------------------- long paths
$longPaths = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem').LongPathsEnabled
if ($longPaths -eq 1) {
    Add-Result -Item 'Long path support' -Status OK -Detail 'LongPathsEnabled = 1'
} else {
    Add-Result -Item 'Long path support' -Status WARN -Detail 'LongPathsEnabled = 0 -- deep MSIX / obj paths can exceed MAX_PATH'
}

# ---------------------------------------------------------------- .NET SDK
$dotnet = Get-ExePath 'dotnet'
$sdks = @()
if ($dotnet) {
    $sdks = @(& dotnet --list-sdks 2>$null | Where-Object { $_ -match '^\d+\.' })
}
if ($sdks.Count -eq 0) {
    Add-Result -Item 'dotnet SDK' -Status FAIL -Detail 'no SDK installed (runtime alone cannot build)' -Required
} else {
    $best = ($sdks | ForEach-Object { [version]($_ -split ' ')[0] } | Sort-Object -Descending | Select-Object -First 1)
    if ($best.Major -ge 10) {
        Add-Result -Item 'dotnet SDK' -Status OK -Detail ('{0} installed ({1} SDK(s))' -f $best, $sdks.Count) -Required
    } elseif ($best.Major -ge 8) {
        Add-Result -Item 'dotnet SDK' -Status WARN -Detail ('{0} installed -- project targets net10.0' -f $best) -Required
    } else {
        Add-Result -Item 'dotnet SDK' -Status FAIL -Detail ('{0} installed -- net10.0 SDK required' -f $best) -Required
    }
}

$runtimeList = @()
if ($dotnet) { $runtimeList = @(& dotnet --list-runtimes 2>$null | Where-Object { $_ -match 'Microsoft.WindowsDesktop.App' }) }
if ($runtimeList.Count -gt 0) {
    $rtBest = ($runtimeList | ForEach-Object { [version]($_ -split ' ')[1] } | Sort-Object -Descending | Select-Object -First 1)
    Add-Result -Item 'Desktop runtime' -Status OK -Detail ('Microsoft.WindowsDesktop.App ' + $rtBest)
} else {
    Add-Result -Item 'Desktop runtime' -Status WARN -Detail 'Microsoft.WindowsDesktop.App missing'
}

# ---------------------------------------------------------------- Windows SDK
# A C# WinUI 3 project does NOT need the standalone Windows SDK install. The
# projection (Microsoft.Windows.SDK.NET.Ref) and the packaging tools
# (Microsoft.Windows.SDK.BuildTools) arrive as NuGet packages during restore.
# Only the C++ / C#-with-C++-tooling workflows need the full SDK, so a missing
# standalone SDK is a WARN, not a FAIL.
$kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdkVersions = @()
if (Test-Path (Join-Path $kitsRoot 'Include')) {
    $sdkVersions = @(Get-ChildItem (Join-Path $kitsRoot 'Include') -Directory | Select-Object -ExpandProperty Name)
}

$nugetRoot = Join-Path $env:USERPROFILE '.nuget\packages'
$nugetBuildTools = @()
$btDir = Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools'
if (Test-Path $btDir) {
    $nugetBuildTools = @(Get-ChildItem $btDir -Directory | Select-Object -ExpandProperty Name)
}

if ($sdkVersions.Count -gt 0) {
    $has26100 = @($sdkVersions | Where-Object { $_ -like '10.0.26100*' }).Count -gt 0
    if ($has26100) {
        Add-Result -Item 'Windows SDK' -Status OK -Detail ('standalone 10.0.26100 installed (all: ' + ($sdkVersions -join ', ') + ')')
    } else {
        Add-Result -Item 'Windows SDK' -Status WARN -Detail ('standalone SDK present but no 10.0.26100 (found: ' + ($sdkVersions -join ', ') + ')')
    }
} elseif ($nugetBuildTools.Count -gt 0) {
    Add-Result -Item 'Windows SDK' -Status OK -Detail ('via NuGet BuildTools ' + ($nugetBuildTools -join ', ') + ' -- standalone SDK not required for C#')
} else {
    Add-Result -Item 'Windows SDK' -Status WARN -Detail 'neither standalone SDK nor NuGet BuildTools yet (appears after first restore)'
}

# ---------------------------------------------------------------- MSIX tooling
$makeappx = $null
$signtool = $null
$binRoot = Join-Path $kitsRoot 'bin'
if (Test-Path $binRoot) {
    $makeappx = Get-ChildItem $binRoot -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    $signtool = Get-ChildItem $binRoot -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
}
if ($nugetBuildTools.Count -gt 0 -and (-not $makeappx -or -not $signtool)) {
    $btBin = Join-Path $btDir ($nugetBuildTools[0] + '\bin')
    if (Test-Path $btBin) {
        if (-not $makeappx) {
            $makeappx = Get-ChildItem $btBin -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
        }
        if (-not $signtool) {
            $signtool = Get-ChildItem $btBin -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
        }
    }
}
if ($makeappx -and $signtool) {
    Add-Result -Item 'MSIX tooling' -Status OK -Detail 'makeappx.exe + signtool.exe found'
} elseif ($makeappx -or $signtool) {
    Add-Result -Item 'MSIX tooling' -Status WARN -Detail 'only one of makeappx / signtool found'
} else {
    Add-Result -Item 'MSIX tooling' -Status WARN -Detail 'not found yet -- supplied by NuGet BuildTools on first restore'
}

# ---------------------------------------------------------------- Visual Studio / MSBuild
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsInfo = $null
if (Test-Path $vswhere) {
    $vsInfo = & $vswhere -latest -products * -format json 2>$null | ConvertFrom-Json
}
if ($vsInfo -and $vsInfo.installationVersion) {
    $vsVer = $vsInfo.installationVersion
    $major = [int]($vsVer -split '\.')[0]
    if ($major -ge 18) {
        Add-Result -Item 'Visual Studio' -Status OK -Detail ('{0} {1} at {2}' -f $vsInfo.displayName, $vsVer, $vsInfo.installationPath) -Required
    } else {
        Add-Result -Item 'Visual Studio' -Status WARN -Detail ('{0} {1} -- VS 2026 (18.x) recommended for WinUI 3 / net10.0' -f $vsInfo.displayName, $vsVer) -Required
    }
} else {
    Add-Result -Item 'Visual Studio' -Status FAIL -Detail 'not installed -- WinUI 3 templates, XAML designer and F5 deploy need it' -Required
}

$msbuild = Get-ExePath 'msbuild'
if ($msbuild) {
    Add-Result -Item 'MSBuild on PATH' -Status OK -Detail $msbuild -Detailed
} elseif ($vsInfo -and $vsInfo.installationPath) {
    $msbuildPath = Join-Path $vsInfo.installationPath 'MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path $msbuildPath) {
        Add-Result -Item 'MSBuild on PATH' -Status WARN -Detail ('not on PATH, but present at ' + $msbuildPath) -Detailed
    } else {
        Add-Result -Item 'MSBuild on PATH' -Status FAIL -Detail 'MSBuild.exe not found in VS install' -Detailed
    }
}

# ---------------------------------------------------------------- Windows App Runtime
$war = @(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2*' -ErrorAction SilentlyContinue)
if ($war.Count -gt 0) {
    $warDetail = ($war | ForEach-Object { $_.Name + ' ' + $_.Version } | Sort-Object -Unique) -join '; '
    Add-Result -Item 'Windows App Runtime' -Status OK -Detail $warDetail -Required
} else {
    Add-Result -Item 'Windows App Runtime' -Status WARN -Detail 'no 2.x runtime package -- unpackaged runs need it (packaged MSIX pulls it in)'
}

# ---------------------------------------------------------------- WinApp CLI
$winapp = Get-ExePath 'winapp'
if ($winapp) {
    $winappVer = (& winapp --version 2>$null | Select-Object -First 1)
    Add-Result -Item 'WinApp CLI' -Status OK -Detail ('{0} ({1})' -f $winappVer, $winapp)
} else {
    Add-Result -Item 'WinApp CLI' -Status WARN -Detail 'winapp not on PATH -- optional, but lets you scaffold/package without VS'
}

# ---------------------------------------------------------------- dev signing cert
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -match 'Feige' } | Select-Object -First 1
if ($cert) {
    $expiresIn = [int]($cert.NotAfter - (Get-Date)).TotalDays
    if ($expiresIn -gt 0) {
        Add-Result -Item 'Dev signing cert' -Status OK -Detail ('{0} expires {1:yyyy-MM-dd}' -f $cert.Subject, $cert.NotAfter)
    } else {
        Add-Result -Item 'Dev signing cert' -Status FAIL -Detail ('{0} EXPIRED {1:yyyy-MM-dd}' -f $cert.Subject, $cert.NotAfter)
    }
} else {
    Add-Result -Item 'Dev signing cert' -Status WARN -Detail 'no self-signed Feige code signing cert -- needed to install MSIX locally'
}

# ---------------------------------------------------------------- network profile (LAN transfer)
$profiles = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue)
$privateCount = @($profiles | Where-Object { $_.NetworkCategory -eq 'Private' }).Count
if ($privateCount -gt 0) {
    Add-Result -Item 'Network profile' -Status OK -Detail ('{0} private network(s) -- inbound discovery/transfer allowed' -f $privateCount)
} else {
    Add-Result -Item 'Network profile' -Status WARN -Detail 'no Private network -- Public blocks inbound mDNS/transfer'
}

# ---------------------------------------------------------------- firewall
$rule = Get-NetFirewallRule -DisplayName 'Feige*' -ErrorAction SilentlyContinue
if ($rule) {
    Add-Result -Item 'Firewall rules' -Status OK -Detail ((@($rule).Count).ToString() + ' Feige rule(s) present')
} else {
    Add-Result -Item 'Firewall rules' -Status WARN -Detail 'none yet -- first debug run will prompt for inbound access'
}

# ---------------------------------------------------------------- git
$git = Get-ExePath 'git'
if ($git) {
    Add-Result -Item 'git' -Status OK -Detail $git
} else {
    Add-Result -Item 'git' -Status WARN -Detail 'git not on PowerShell PATH'
}

# ---------------------------------------------------------------- env hygiene
# Duplicate variables that differ only in casing (http_proxy + HTTP_PROXY) are a
# real blocker: .NET throws "An item with the same key has already been added"
# in Start-Process and in the VS bootstrapper (exit code 5002).
$envBroken = $false
try { $null = Get-ChildItem Env: } catch { $envBroken = $true }
if ($envBroken) {
    Add-Result -Item 'Env hygiene' -Status WARN -Detail 'duplicate case-variant proxy vars -- crashes Start-Process / VS setup; run Install-DevEnv.ps1 to repair'
} else {
    Add-Result -Item 'Env hygiene' -Status OK -Detail 'no duplicate case-variant environment variables'
}

# ---------------------------------------------------------------- report
$width = 30
Write-Output ''
Write-Output ('=' * 92)
Write-Output ' Feige Windows dev environment check'
Write-Output ('=' * 92)
Write-Output ('{0,-4} {1,-{2}} {3}' -f '', 'Item', $width, 'Detail')
Write-Output ('-' * 92)

foreach ($r in $results) {
    $tag = switch ($r.Status) {
        'OK' { '[OK]  ' }
        'WARN' { '[WARN]' }
        'FAIL' { '[FAIL]' }
    }
    $item = $r.Item.PadRight($width)
    Write-Output ('{0} {1} {2}' -f $tag, $item, $r.Detail)
}

$fails = @($results | Where-Object { $_.Status -eq 'FAIL' })
$warns = @($results | Where-Object { $_.Status -eq 'WARN' })
$oks = @($results | Where-Object { $_.Status -eq 'OK' })

Write-Output ('-' * 92)
Write-Output (' {0} OK / {1} WARN / {2} FAIL' -f $oks.Count, $warns.Count, $fails.Count)

if ($fails.Count -gt 0) {
    Write-Output ''
    Write-Output ' Blocking issues (required):'
    foreach ($f in $fails) { Write-Output ('   - ' + $f.Item + ': ' + $f.Detail) }
    Write-Output ''
    Write-Output ' Fix with: powershell -NoProfile -ExecutionPolicy Bypass -File Install-DevEnv.ps1'
    Write-Output ''
    exit 1
}

Write-Output ''
Write-Output ' No blocking issues. Ready to build.'
Write-Output ''
exit 0
