<#
.SYNOPSIS
  Upload a signed release set to Jianguoyun via WebDAV so another machine can fetch it.
.DESCRIPTION
  ROG builds and signs the installer; this script pushes the installer, its sidecar and the
  signed manifest into a per-build folder, then verifies the remote bytes with rclone check.
  Jianguoyun is used only as a transfer buffer - the update server (Mac mini) stays the
  distribution source, and git LFS remains the versioned fallback.
.PARAMETER BuildDir
  Directory containing the installer, sidecar and signed manifest (Windows/dist/build).
.PARAMETER RemoteBase
  Jianguoyun folder that holds all releases. Defaults to feige-release under the account root.
.PARAMETER KeepLocal
  Keep the temporary staging copy instead of deleting it.
.NOTES
  Requires rclone with a remote named "jianguoyun" configured:
    rclone config create jianguoyun webdav url=https://dav.jianguoyun.com/dav/ vendor=other \
      user=<account> pass=$(rclone obscure '<app password>')
  Jianguoyun free accounts allow 1 GB upload per month, so prune old release folders.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildDir,
    [string]$Remote = 'jianguoyun',
    [string]$RemoteBase = $(if ($env:FEIGE_JGY_BASE) { $env:FEIGE_JGY_BASE } else { '我的坚果云/feige-release' }),
    [string]$RclonePath = 'rclone',
    [switch]$KeepLocal
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose() }
}

$dir = (Resolve-Path -LiteralPath $BuildDir).Path
$installer = Get-ChildItem -LiteralPath $dir -Filter 'FeigeSetup-*-*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $installer) { throw "No installer found in $dir." }
$sidecar = "$($installer.FullName).json"
$manifest = Join-Path $dir 'feige-windows-update.json'
foreach ($required in @($sidecar, $manifest)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing release file: $required" }
}

$meta = Get-Content -LiteralPath $sidecar -Raw | ConvertFrom-Json
if (-not $meta.signed) { throw 'Refusing to upload an unsigned installer.' }
$sha = Get-Sha256 $installer.FullName
if ($sha -ne $meta.sha256) { throw "Installer hash changed after signing: $sha" }
if ($installer.Length -ne $meta.bytes) { throw 'Installer size changed after signing.' }

# The signed manifest must reference exactly this package.
$envelope = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
$payloadJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.payload))
$release = ($payloadJson | ConvertFrom-Json).releases | Where-Object { $_.build -eq $meta.build } | Select-Object -First 1
if (-not $release) { throw 'Signed manifest does not contain this build.' }
if ($release.sha256 -ne $sha -or $release.size -ne $installer.Length) { throw 'Signed manifest does not match the installer bytes.' }

$folder = "v$($meta.version)-b$($meta.build)"
$remoteTarget = "$Remote`:$RemoteBase/$folder"
Write-Host "Uploading $($installer.Name) ($([math]::Round($installer.Length / 1MB, 1)) MB) to $remoteTarget"

& $RclonePath mkdir "$Remote`:$RemoteBase" 2>&1 | Out-Null
& $RclonePath copy $installer.FullName "$remoteTarget/" --transfers 1 --retries 3 --stats-one-line --stats 5s
if ($LASTEXITCODE -ne 0) { throw "Upload failed for $($installer.Name)." }
& $RclonePath copy $sidecar "$remoteTarget/" --retries 3
if ($LASTEXITCODE -ne 0) { throw 'Upload failed for the sidecar.' }
& $RclonePath copy $manifest "$remoteTarget/" --retries 3
if ($LASTEXITCODE -ne 0) { throw 'Upload failed for the signed manifest.' }

Write-Host 'Verifying remote bytes'
# Compare the uploaded copy against the local file by listing the sizes on both sides;
# "rclone check file remote:dir" misparses the single-file form.
$remoteSize = (& $RclonePath size "$remoteTarget/$($installer.Name)" --json | ConvertFrom-Json).bytes
if ($remoteSize -ne $installer.Length) { throw "Remote installer size mismatch: $remoteSize vs $($installer.Length)." }

$remoteHash = (& $RclonePath hashsum sha256 "$remoteTarget/$($installer.Name)" | Out-String).Trim()
if ($remoteHash) {
    $remoteHashValue = ($remoteHash -split '\s+')[0]
    if ($remoteHashValue -and $remoteHashValue -ne $sha) { throw "Remote hash mismatch: $remoteHashValue" }
}
else {
    Write-Warning 'Jianguoyun did not report a SHA-256; size was verified instead.'
}

Write-Host ''
Write-Host "Uploaded to $remoteTarget" -ForegroundColor Green
Write-Host "  installer : $($installer.Name)"
Write-Host "  sha256    : $sha"
Write-Host "  fetch on the other machine with:"
Write-Host "    rclone copy `"$remoteTarget`" <local-folder>"
Write-Host ''
Write-Host 'Remember: Jianguoyun free tier allows 1 GB upload per month; prune old folders with:'
Write-Host "  rclone purge `"$Remote`:$RemoteBase/<older-build>`""
