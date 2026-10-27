<#
.SYNOPSIS
  Publish verified Windows update assets to the configured Mac mini LAN static server.
.DESCRIPTION
  Uploads one immutable version directory, then atomically promotes the signed
  manifest pointer. No installer build and no manifest signing happens here.
  Use -WhatIf to preview; a real run requires confirmation (ConfirmImpact High).
.PARAMETER ResumePointer
  The version directory was already promoted by an earlier failed run. Verify the
  existing files against the signed manifest and only promote the pointer.
.NOTES
  Requires Windows PowerShell 5.1, .NET 10, OpenSSH ssh/scp on PATH, and an SSH
  host alias named songli-macmini in the release user's ssh config.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [string]$DotnetPath = 'dotnet',
    [ValidatePattern('^[A-Za-z0-9._-]{1,64}$')][string]$SshTarget = 'songli-macmini',
    [string]$RemoteRoot = '/usr/local/var/www/feige-updates',
    [switch]$ResumePointer
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# -WhatIf propagates to every ShouldProcess cmdlet inside the script (Get-FileHash
# included), which would break the read-only validation. Capture it, disable it for
# the checks, and return explicitly before any remote write.
$dryRun = [bool]$WhatIfPreference
$WhatIfPreference = $false
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$projectRoot = Join-Path $repoRoot 'Project'
$windowsRoot = Join-Path $projectRoot 'Windows'
$sourcePath = Join-Path $windowsRoot 'src\Feige.App\Updates\UpdateSource.json'
$source = Get-Content -LiteralPath $sourcePath -Raw | ConvertFrom-Json
if ($RemoteRoot -ne '/usr/local/var/www/feige-updates') { throw 'RemoteRoot is fixed to the configured update directory.' }
if ($source.channel -notin @('stable', 'preview')) { throw 'Configured update channel is invalid.' }
# Packages are built into the ignored Windows/dist/build staging folder; publish is the
# only step that copies a verified installer into the repository-adjacent Release/ directory.
$builds = [IO.Path]::GetFullPath((Join-Path $windowsRoot 'dist\build')) + [IO.Path]::DirectorySeparatorChar
$manifest = [IO.Path]::GetFullPath($ManifestPath)
if (!$manifest.StartsWith($builds, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest must be under Windows/dist/build.' }
if ($source.publisherCertificateSha256.Count -eq 0) { throw 'Expected Authenticode publisher certificates are not configured.' }
$configuredOrigin = [Uri]$source.baseUri
if ($configuredOrigin.AbsolutePath.TrimEnd('/') -ne '/updates' -or $configuredOrigin.Host -ne '192.168.50.229' -or
    $configuredOrigin.Port -ne 8080 -or $configuredOrigin.Scheme -ne 'http' -or !$source.allowInsecurePrivateNetwork -or
    $source.trustedHosts -notcontains '192.168.50.229') {
    throw 'LAN update source does not match the explicitly configured Mac mini origin.'
}
$toolDirectory = Join-Path $windowsRoot 'dist\update-tool'
$verificationDirectory = Join-Path $windowsRoot ('dist\.verification\lan-update-' + [Guid]::NewGuid().ToString('N'))
& $DotnetPath build (Join-Path $windowsRoot 'src\Feige.UpdateTool\Feige.UpdateTool.csproj') -c Release -o $toolDirectory --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Update tool build failed.' }
$tool = Join-Path $toolDirectory 'Feige.UpdateTool.dll'
$verified = & $DotnetPath $tool verify $sourcePath $manifest
if ($LASTEXITCODE -ne 0) { throw 'Signed manifest verification failed.' }
$releases = @(($verified | ConvertFrom-Json).releases)
if (!$releases.Count) { throw 'No release assets.' }
. (Join-Path $windowsRoot 'src\Feige.App\Updates\VerifyPinnedAuthenticode.ps1')

$releaseRoot = [Uri]::new(([string]$source.baseUri).TrimEnd('/') + '/')
$expectedVersionPrefix = "/updates/windows/$($source.channel)/versions/"
$tags = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$releaseVersions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$releaseArchitectures = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$files = @()
$expected = @{}
foreach ($release in $releases) {
    if ($release.channel -ne $source.channel -or $release.installation -ne 'exe') { throw 'Release channel/installation mismatch.' }
    if ([string]$release.version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Manifest contains an invalid package version.' }
    if ([string]$release.architecture -notin @('x64','arm64')) { throw 'Manifest contains an unsupported architecture.' }
    [void]$releaseVersions.Add([string]$release.version)
    [void]$releaseArchitectures.Add([string]$release.architecture)
    $uri = [Uri]$release.downloadUrl
    if ($uri.Scheme -ne 'http' -or $uri.Port -ne 8080 -or !$uri.Host.Equals($configuredOrigin.Host, [StringComparison]::OrdinalIgnoreCase) -or
        $uri.Query -or $uri.Fragment -or $uri.UserInfo -or $uri.AbsolutePath.Contains('..') -or $uri.AbsolutePath.Contains('%')) {
        throw 'Package URL has an untrusted LAN origin.'
    }
    if (!$uri.AbsolutePath.StartsWith($expectedVersionPrefix, [StringComparison]::Ordinal)) { throw 'Invalid version asset path.' }
    $parts = $uri.AbsolutePath.Substring($expectedVersionPrefix.Length).Split('/')
    if ($parts.Count -ne 2 -or $parts[0] -notmatch '^[A-Za-z0-9._-]+$' -or $parts[1] -notmatch '^[A-Za-z0-9._-]+\.exe$') {
        throw 'Invalid version asset URL.'
    }
    [void]$tags.Add($parts[0])
    $file = Join-Path $builds $parts[1]
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or
        (Get-Item -LiteralPath $file).Length -ne $release.size -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $release.sha256) {
        throw 'Package bytes differ from signed metadata.'
    }
    . (Join-Path $windowsRoot 'src\Feige.App\Updates\VerifyPinnedAuthenticode.ps1')
    $verification = Test-FeigePinnedAuthenticode -Path $file -Pins $source.publisherCertificateSha256
    if ($verification.Sha256 -notin $source.publisherCertificateSha256) { throw 'Unexpected Authenticode publisher.' }
    $expectedNotes = ([Uri]::new($releaseRoot, "windows/$($source.channel)/versions/$($parts[0])/notes")).AbsoluteUri
    if ($release.notesUrl -ne $expectedNotes) { throw 'Release notes URL does not match the configured version path.' }
    if ($expected.ContainsKey($parts[1])) { throw 'Duplicate release asset filename.' }
    $expected[$parts[1]] = $release
    $files += $file
}
if ($tags.Count -ne 1) { throw 'Publish exactly one immutable version tag at a time.' }
if ($releaseVersions.Count -ne 1) { throw 'A release manifest must contain exactly one product version.' }
$tag = @($tags)[0]
$releaseVersion = @($releaseVersions)[0]
$downloads = [IO.Path]::GetFullPath((Join-Path (Join-Path $repoRoot 'Release\Windows') $releaseVersion)) + [IO.Path]::DirectorySeparatorChar

# Remote paths are POSIX; never use Join-Path (it would emit backslashes on Windows).
$remoteBase = "$RemoteRoot/windows/$($source.channel)"
$remoteVersions = "$remoteBase/versions"
$versionPath = "$remoteVersions/$tag"
$staging = [Guid]::NewGuid().ToString('N')
$stageRemote = "$remoteBase/.staging-$staging"
$pointerTemp = "$remoteBase/.feige-windows-update-$staging.json"
$pointerPath = "$remoteBase/feige-windows-update.json"
$pointerBackup = "$remoteBase/feige-windows-update.previous.json"
# Windows PowerShell binds Uri(string, string) to the wrong overload (returns the base
# alone), so always pass a real Uri as the base.
$destination = ([Uri]::new([Uri](([string]$source.baseUri).TrimEnd('/') + '/'), "windows/$($source.channel)/feige-windows-update.json")).AbsoluteUri

if ($dryRun) {
    Write-Output "WhatIf: validated $tag and would promote $destination"
    return
}
if (!$PSCmdlet.ShouldProcess($SshTarget, "publish immutable $tag and promote the signed manifest at $destination")) { return }

# Preflight: required directories exist and the tag is either absent or explicitly resumed.
& ssh $SshTarget "test -d '$remoteVersions' && test -d '$remoteBase' && { test ! -e '$versionPath' || test -d '$versionPath'; }"
if ($LASTEXITCODE -ne 0) { throw 'Remote update directories are missing.' }
& ssh $SshTarget "test -e '$versionPath'"
$versionExists = $LASTEXITCODE -eq 0
if ($versionExists -and !$ResumePointer) {
    throw "Version $tag already exists. If a previous run failed after promotion, rerun with -ResumePointer."
}
if (!$versionExists -and $ResumePointer) { throw 'ResumePointer requires an existing immutable version directory.' }

$promoted = $versionExists
$pointerPromoted = $false
$stageCreated = $false
try {
    if (!$versionExists) {
        & ssh $SshTarget "mkdir '$stageRemote'"
        if ($LASTEXITCODE -ne 0) { throw 'Cannot create remote staging directory.' }
        $stageCreated = $true
        foreach ($file in $files) {
            & scp -- $file "${SshTarget}:$stageRemote/$(Split-Path -Leaf $file)"
            if ($LASTEXITCODE -ne 0) { throw 'Package upload to staging failed.' }
        }
        $remoteHashLines = & ssh $SshTarget "cd '$stageRemote' && shasum -a 256 *"
        if ($LASTEXITCODE -ne 0) { throw 'Cannot verify staged package hashes.' }
        foreach ($name in $expected.Keys) {
            $line = @($remoteHashLines | Where-Object { $_ -match ("\s" + [Regex]::Escape($name) + '$') }) | Select-Object -First 1
            if (!$line -or ($line -split '\s+')[0] -ne $expected[$name].sha256) { throw "Staged package hash mismatch: $name" }
        }
        # Rename a sibling directory into versions/<tag>: single atomic operation on the same filesystem.
        & ssh $SshTarget "mv '$stageRemote' '$versionPath'"
        if ($LASTEXITCODE -ne 0) { throw 'Immutable package promotion failed; pointer remains unchanged.' }
        $stageCreated = $false
        $promoted = $true
    }

    # Verify promoted bytes over the real HTTP endpoint before touching the pointer.
    New-Item -ItemType Directory -Path $verificationDirectory -Force | Out-Null
    foreach ($name in $expected.Keys) {
        $release = $expected[$name]
        $probe = & ssh $SshTarget "shasum -a 256 '$versionPath/$name'"
        if ($LASTEXITCODE -ne 0 -or ($probe -split '\s+')[0] -ne $release.sha256) { throw "Promoted package hash mismatch: $name" }
        $download = Join-Path $verificationDirectory $name
        Invoke-WebRequest -Uri $release.downloadUrl -OutFile $download -UseBasicParsing
        if ((Get-Item $download).Length -ne $release.size -or
            (Get-FileHash $download -Algorithm SHA256).Hash -ne $release.sha256) {
            throw "HTTP package re-download mismatch: $name"
        }
    }

    $manifestTemp = Join-Path (Split-Path -Parent $manifest) ("$staging-" + [IO.Path]::GetFileName($manifest))
    Copy-Item -LiteralPath $manifest -Destination $manifestTemp
    & scp -- $manifestTemp "${SshTarget}:$pointerTemp"
    $scpExit = $LASTEXITCODE
    Remove-Item -LiteralPath $manifestTemp -Force
    if ($scpExit -ne 0) { throw 'Manifest staging upload failed.' }
    $stagedHash = & ssh $SshTarget "shasum -a 256 '$pointerTemp'"
    if ($LASTEXITCODE -ne 0 -or ($stagedHash -split '\s+')[0] -ne (Get-FileHash $manifest -Algorithm SHA256).Hash) {
        throw 'Staged manifest hash mismatch.'
    }

    # Keep exactly one previous signed pointer for manual rollback, then swap atomically.
    & ssh $SshTarget "test ! -e '$pointerPath' || cp -p '$pointerPath' '$pointerBackup'"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot back up the current pointer.' }
    & ssh $SshTarget "mv '$pointerTemp' '$pointerPath'"
    if ($LASTEXITCODE -ne 0) { throw 'Manifest pointer promotion failed.' }
    $pointerPromoted = $true

    $manifestDownload = Join-Path $verificationDirectory 'current-manifest.json'
    Invoke-WebRequest -Uri $destination -OutFile $manifestDownload -UseBasicParsing
    if ((Get-FileHash $manifestDownload -Algorithm SHA256).Hash -ne (Get-FileHash $manifest -Algorithm SHA256).Hash) {
        throw 'HTTP manifest pointer does not match the signed manifest.'
    }
    # Archive the verified package into the repository-adjacent Release folder.
    # Existing files are never overwritten; a mismatch is reported instead of silently kept.
    New-Item -ItemType Directory -Path $downloads -Force | Out-Null
    foreach ($name in $expected.Keys) {
        $target = Join-Path $downloads $name
        $source = Join-Path $builds $name
        if (Test-Path -LiteralPath $target) {
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected[$name].sha256) {
                Write-Warning "Release/$name already exists with different bytes; left unchanged."
            }
        }
        else {
            Copy-Item -LiteralPath $source -Destination $target
            Copy-Item -LiteralPath "$source.json" -Destination "$target.json" -ErrorAction SilentlyContinue
        }
    }
    Copy-Item -LiteralPath $manifest -Destination (Join-Path $downloads 'feige-windows-update.json') -Force
    Write-Output "Published $tag to $destination"
}
finally {
    if (Test-Path -LiteralPath $verificationDirectory) { Remove-Item -LiteralPath $verificationDirectory -Recurse -Force }
    if ($stageCreated) { & ssh $SshTarget "rm -rf '$stageRemote'" 2>$null }
    & ssh $SshTarget "rm -f '$pointerTemp'" 2>$null
    if ($pointerPromoted) {
        Write-Warning "Pointer was promoted but final verification did not complete. Inspect $destination; the previous pointer is kept at $pointerBackup for manual rollback."
    } elseif ($promoted) {
        Write-Warning "Immutable version retained at $versionPath; the pointer was not advanced. Rerun with -ResumePointer after checking the server."
    }
}
