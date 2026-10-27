<#
.SYNOPSIS
  Create an unsigned release record from final, already built/signed EXE packages under Windows/dist/build.
.NOTES
  Generates release metadata only after packaging is authorized; never builds or uploads a package.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string[]]$InstallerPath, [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$SummaryFile, [string]$MinimumSystemVersion = '10.0.17763.0')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$projectRoot = Join-Path $repoRoot 'Project'
$windowsRoot = Join-Path $projectRoot 'Windows'
# Builds are staged under the platform tree; verified delivery copies go to Release/.
$builds = [IO.Path]::GetFullPath((Join-Path $windowsRoot 'dist\build')) + [IO.Path]::DirectorySeparatorChar
$source = Get-Content (Join-Path $windowsRoot 'src\Feige.App\Updates\UpdateSource.json') -Raw | ConvertFrom-Json
if (!$source.baseUri -or !$source.trustedHosts) { throw 'Configured update base URL and trusted hosts are required.' }
if ($source.channel -notin @('stable', 'preview')) { throw 'Configured update channel is invalid.' }
if ($MinimumSystemVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'MinimumSystemVersion must be four numeric parts.' }
if ($Tag -in @('.', '..') -or $Tag -notmatch '^[A-Za-z0-9._-]+$') { throw 'Invalid release tag.' }
if ($InstallerPath.Count -gt 64) { throw 'At most 64 release records are permitted.' }
$summary = [IO.File]::ReadAllText([IO.Path]::GetFullPath($SummaryFile))
if ($summary.Length -gt 12000) { throw 'Release summary exceeds 12000 characters.' }
$releases = @()
$seenArchitectures = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$seenBuilds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$seenVersions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($inputPath in $InstallerPath) {
    $file = [IO.Path]::GetFullPath($inputPath)
    # The trailing separator keeps sibling prefixes such as dist\build_evil from matching.
    if (!$file.StartsWith($builds, [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $file -PathType Leaf)) {
        throw 'Installer must exist under Windows/dist/build.'
    }
    foreach ($candidate in @($file, "$file.json")) {
        $item = Get-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
        if (!$item) { continue }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Installer and metadata paths through symbolic links/reparse points are not accepted.'
        }
        $dir = if ($item.PSIsContainer) { $item } else { $item.Directory }
        while ($dir) {
            if (($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Installer and metadata paths through symbolic links/reparse points are not accepted.'
            }
            $dir = $dir.Parent
        }
    }
    if (!(Test-Path -LiteralPath "$file.json" -PathType Leaf)) {
        throw "Signed build metadata is missing for $file."
    }
    $name = [IO.Path]::GetFileName($file)
    if ($name -notmatch '^[A-Za-z0-9._-]+\.exe$') { throw 'Invalid installer filename.' }
    . (Join-Path $windowsRoot 'src\Feige.App\Updates\VerifyPinnedAuthenticode.ps1')
    $signature = Test-FeigePinnedAuthenticode -Path $file -Pins $source.publisherCertificateSha256
    $metadataPath = "$file.json"
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if (!$metadata.signed -or $metadata.build -le 0 -or $metadata.architecture -notin @('x64','arm64') -or $metadata.installer -ne $name -or $metadata.version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'A signed package with a valid version, architecture, build, and filename is required.' }
    if (!$seenArchitectures.Add($metadata.architecture)) { throw "Duplicate architecture in release record: $($metadata.architecture)" }
    if ($seenBuilds.Count -gt 0 -and (!$seenBuilds.Contains([string]$metadata.build) -or !$seenVersions.Contains($metadata.version))) { throw 'All architecture packages must have matching version/build metadata.' }
    [void]$seenBuilds.Add([string]$metadata.build)
    [void]$seenVersions.Add($metadata.version)
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if ($metadata.sha256 -ne $hash -or $metadata.bytes -ne (Get-Item -LiteralPath $file).Length) { throw 'Package metadata does not match the final bytes.' }
    $releases += [ordered]@{
        platform='windows'; architecture=$metadata.architecture; channel=$source.channel; installation='exe'
        version=$metadata.version; build=[long]$metadata.build; publishedAt=[DateTimeOffset]::UtcNow.ToString('o')
        minimumSystemVersion=$MinimumSystemVersion; summary=$summary
        notesUrl=([Uri]::new([Uri]::new(([string]$source.baseUri).TrimEnd('/') + '/'), "windows/$($source.channel)/versions/$Tag/notes")).AbsoluteUri
        downloadUrl=([Uri]::new([Uri]::new(([string]$source.baseUri).TrimEnd('/') + '/'), "windows/$($source.channel)/versions/$Tag/$name")).AbsoluteUri
        size=(Get-Item -LiteralPath $file).Length; sha256=$hash; protocolVersion=3
    }
}
$output = Join-Path $builds "$Tag-record.json"
$bytes = [Text.Encoding]::UTF8.GetBytes(([ordered]@{schemaVersion=1;releases=@($releases)} | ConvertTo-Json -Depth 6))
$stream = New-Object IO.FileStream($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
Write-Output "Release record ready: $output"
