<#
.SYNOPSIS
  Publishes the signed Windows update manifest to the matching GitHub Release.
.DESCRIPTION
  The GitHub origin (layout "github-release") reads <repo>/releases/latest/download/feige-windows-update.json
  and downloads <repo>/releases/download/v<version>/<installer>. Clients rebase the LAN addresses inside the
  signed manifest onto GitHub, so the very manifest published by Publish-LanUpdate.ps1 is uploaded unchanged.

  The script checks that release v<version> already holds every installer named by the manifest with the
  same size and SHA-256, uploads the manifest (replacing an older copy only with -Force), and finally reads
  it back through latest/download when that release is Latest.
.EXAMPLE
  .\Publish-GitHubUpdate.ps1 -ManifestPath ..\..\..\Release\Windows\1.3.2\feige-windows-update.json -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)] [string] $ManifestPath,
    [string] $Repository = 'jiongboy/FeigeTransfer',
    [switch] $Force
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) is required.' }
$manifest = (Resolve-Path -LiteralPath $ManifestPath).Path
$envelope = Get-Content -LiteralPath $manifest -Raw -Encoding utf8 | ConvertFrom-Json
$payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.payload)) | ConvertFrom-Json
$releases = @($payload.releases)
if ($releases.Count -eq 0) { throw 'The manifest lists no releases.' }
$versions = @($releases | ForEach-Object { $_.version } | Sort-Object -Unique)
if ($versions.Count -ne 1) { throw "The manifest mixes versions: $($versions -join ', ')." }
$tag = "v$($versions[0])"

$view = gh release view $tag --repo $Repository --json assets,isLatest,isDraft,isPrerelease | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "GitHub release $tag was not found in $Repository." }
if ($view.isDraft -or $view.isPrerelease) { throw "GitHub release $tag must be published and not a pre-release." }
$assets = @{}
foreach ($asset in $view.assets) { $assets[$asset.name] = $asset }

$work = Join-Path ([IO.Path]::GetTempPath()) ("feige-gh-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    foreach ($release in $releases) {
        $file = [IO.Path]::GetFileName(([Uri]$release.downloadUrl).AbsolutePath)
        if (-not $assets.ContainsKey($file)) { throw "Release $tag has no asset $file." }
        if ([long]$assets[$file].size -ne [long]$release.size) { throw "Asset $file size differs from the manifest." }
        gh release download $tag --repo $Repository --pattern $file --dir $work --clobber
        if ($LASTEXITCODE -ne 0) { throw "Could not download $file from $tag." }
        $hash = (Get-FileHash -LiteralPath (Join-Path $work $file) -Algorithm SHA256).Hash
        if ($hash -ne ([string]$release.sha256).ToUpperInvariant()) { throw "Asset $file SHA-256 differs from the manifest." }
        Write-Host "verified $file ($($release.architecture), build $($release.build))"
    }

    $name = 'feige-windows-update.json'
    if ($assets.ContainsKey($name) -and -not $Force) { throw "Release $tag already has $name; pass -Force to replace it." }
    $upload = Join-Path $work $name
    Copy-Item -LiteralPath $manifest -Destination $upload
    if ($PSCmdlet.ShouldProcess("$Repository $tag", "upload $name")) {
        if ($Force) { gh release upload $tag $upload --repo $Repository --clobber }
        else { gh release upload $tag $upload --repo $Repository }
        if ($LASTEXITCODE -ne 0) { throw "Uploading $name failed." }

        $expected = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
        $check = Join-Path $work 'check.json'
        $url = if ($view.isLatest) { "https://github.com/$Repository/releases/latest/download/$name" }
               else { "https://github.com/$Repository/releases/download/$tag/$name" }
        Invoke-WebRequest -Uri $url -OutFile $check -UseBasicParsing
        if ((Get-FileHash -LiteralPath $check -Algorithm SHA256).Hash -ne $expected) { throw "Read-back from $url does not match." }
        Write-Host "published $name to $tag; read back from $url"
        if (-not $view.isLatest) { Write-Warning "$tag is not the Latest release, so clients do not see it yet." }
    }
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
