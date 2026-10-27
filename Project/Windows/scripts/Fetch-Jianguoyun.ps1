<#
.SYNOPSIS
  Fetch a release set from Jianguoyun and verify it against the signed manifest.
.DESCRIPTION
  Companion to Publish-Jianguoyun.ps1. Pulls one build into a local folder, checks the
  installer against the SHA-256 recorded in the signed manifest, and optionally verifies the
  Authenticode publisher pin. Nothing is installed or uploaded here.
.PARAMETER Folder
  Release folder name on Jianguoyun, for example v1.2.1-b1301.
.PARAMETER Destination
  Local folder to receive the files. Defaults to Release/Windows/<version> based on the signed manifest.
.PARAMETER SourceConfig
  UpdateSource.json used for the publisher pin check.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Folder,
    [string]$Destination = '',
    [string]$SourceConfig = '',
    [string]$Remote = 'jianguoyun',
    [string]$RemoteBase = $(if ($env:FEIGE_JGY_BASE) { $env:FEIGE_JGY_BASE } else { '我的坚果云/feige-release' }),
    [string]$RclonePath = 'rclone'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$projectRoot = Join-Path $repoRoot 'Project'
$windowsRoot = Join-Path $projectRoot 'Windows'
if (-not $SourceConfig) { $SourceConfig = Join-Path $windowsRoot 'src\Feige.App\Updates\UpdateSource.json' }

$remoteTarget = "$Remote`:$RemoteBase/$Folder"
$downloadStageRoot = Join-Path $windowsRoot 'obj\.staging'
New-Item -ItemType Directory -Force -Path $downloadStageRoot | Out-Null
$downloadStage = Join-Path $downloadStageRoot ('fetch-' + [Guid]::NewGuid().ToString('N'))
$local = (New-Item -ItemType Directory -Force -Path $downloadStage).FullName

Write-Host "Fetching $remoteTarget"
& $RclonePath copy $remoteTarget $local --retries 3 --stats-one-line --stats 5s
if ($LASTEXITCODE -ne 0) { throw "Download failed for $remoteTarget." }

$manifest = Join-Path $local 'feige-windows-update.json'
if (-not (Test-Path -LiteralPath $manifest)) { throw 'Signed manifest missing after download.' }
$envelope = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
$payloadJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.payload))
$release = ($payloadJson | ConvertFrom-Json).releases | Select-Object -First 1
if (!$release -or [string]$release.version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Signed manifest contains an invalid product version.' }
if ([string]$release.architecture -notin @('x64','arm64')) { throw 'Signed manifest contains an unsupported architecture.' }
$releaseVersionRoot = Join-Path (Join-Path $repoRoot 'Release\Windows') ([string]$release.version)
$releaseDestination = if ($Destination) { [IO.Path]::GetFullPath($Destination) } else { [IO.Path]::GetFullPath($releaseVersionRoot) }
$manifest = Join-Path $local 'feige-windows-update.json'

$installer = Join-Path $local ([IO.Path]::GetFileName([Uri]::new($release.downloadUrl).LocalPath))
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer missing after download: $installer" }

$stream = [IO.File]::OpenRead($installer)
try { $sha = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream))).Replace('-', '') }
finally { $stream.Dispose() }

if ((Get-Item $installer).Length -ne $release.size) { throw 'Downloaded installer size does not match the signed manifest.' }
if ($sha -ne $release.sha256) { throw "Downloaded installer hash does not match the signed manifest: $sha" }

Write-Host "Verifying publisher pin"
$source = Get-Content -LiteralPath $SourceConfig -Raw | ConvertFrom-Json
. (Join-Path $windowsRoot 'src\Feige.App\Updates\VerifyPinnedAuthenticode.ps1')
Test-FeigePinnedAuthenticode -Path $installer -Pins $source.publisherCertificateSha256 | Out-Null
New-Item -ItemType Directory -Force -Path $releaseDestination | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $local -File) {
    $target = Join-Path $releaseDestination $file.Name
    if (Test-Path -LiteralPath $target) { throw "Refusing to overwrite existing release file: $target" }
    Copy-Item -LiteralPath $file.FullName -Destination $target
}

Write-Host ''
Write-Host "Verified $($release.version) build $($release.build)" -ForegroundColor Green
Write-Host "  installer : $installer"
Write-Host "  sha256    : $sha"
Write-Host "  next step : upload to the update server, then promote the manifest pointer"
