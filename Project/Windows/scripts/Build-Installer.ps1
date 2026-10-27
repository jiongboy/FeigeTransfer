<#
.SYNOPSIS
  Publish a self-contained, unpackaged Feige app and build an EXE installer.
.EXAMPLE
  .\Build-Installer.ps1 -Platform x64
.EXAMPLE
  .\Build-Installer.ps1 -Platform arm64 -IsccPath C:\Tools\FeigeInno\ISCC.exe
.NOTES
  Requires Windows, .NET 10 SDK and Inno Setup 6.7 or newer.
  Does not register MSIX packages, change certificate trust, or enable Developer Mode.
  Omit CertThumbprint for an unsigned test installer; App Control may block it (error 4551).
  Signed builds sign unsigned payload binaries plus Setup, its temporary copies and Uninstall.
  Existing vendor signatures are preserved and must validate. Signing never exports a private key.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Platform = 'x64',
    [string]$Version = '',
    [long]$BuildNumber = 0,
    [string]$IsccPath = '',
    [string]$DotnetPath = '',
    [string]$CertThumbprint = '',
    # Signing directly from a PFX avoids importing the private key into a machine the
    # operator may not control (for example a Parallels guest); either source works.
    [string]$SigningPfxPath = '',
    [string]$SigningPfxPassword = '',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$AllowUnsignedTest,
    [switch]$KeepPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'Run this script in Windows PowerShell.' }
$windowsRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent $windowsRoot
$repoRoot = Split-Path -Parent $projectRoot
$project = Join-Path $windowsRoot 'src\Feige.App\Feige.App.csproj'
$installer = Join-Path $windowsRoot 'installer\Feige.iss'
[xml]$projectXml = Get-Content $project -Raw
if (-not $Version) { $Version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be three integers, for example 1.1.0.' }
if ($BuildNumber -lt 0) { throw 'BuildNumber must be zero for development or a positive release build number.' }

if (-not $DotnetPath) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $DotnetPath = $command.Source }
    elseif (Test-Path 'C:\Tools\FeigeDotnet\dotnet.exe') { $DotnetPath = 'C:\Tools\FeigeDotnet\dotnet.exe' }
}
if (-not $DotnetPath -or -not (Test-Path $DotnetPath)) { throw '.NET 10 SDK not found. Pass -DotnetPath or add dotnet to PATH.' }
$env:DOTNET_ROOT = Split-Path -Parent $DotnetPath
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
if (-not $IsccPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $IsccPath = $command.Source }
    else {
        $locations = @(
            'C:\Tools\FeigeInno\ISCC.exe',
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        )
        $IsccPath = $locations | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
}
if (-not $IsccPath -or -not (Test-Path $IsccPath)) { throw 'Inno Setup compiler not found. Install Inno Setup from https://jrsoftware.org/isdl.php or pass -IsccPath.' }

$usingPfx = [bool]$SigningPfxPath
if ($SigningPfxPath -and $CertThumbprint) { throw 'Choose either -CertThumbprint or -SigningPfxPath, not both.' }
if ($usingPfx -and !(Test-Path -LiteralPath $SigningPfxPath -PathType Leaf)) { throw 'SigningPfxPath does not exist.' }
if ($usingPfx -and !$SigningPfxPassword) { throw 'SigningPfxPassword is required with -SigningPfxPath.' }
if (($CertThumbprint -or $usingPfx) -and $AllowUnsignedTest) { throw 'Choose a signing source or -AllowUnsignedTest, not both.' }
if (!$CertThumbprint -and !$usingPfx -and !$AllowUnsignedTest) {
    throw 'A signing certificate is required. Pass -CertThumbprint, or -SigningPfxPath/-SigningPfxPassword, or pass -AllowUnsignedTest for a deliberately unsigned test build that cannot be published.'
}
$releaseSource = Get-Content (Join-Path $windowsRoot 'src\Feige.App\Updates\UpdateSource.json') -Raw | ConvertFrom-Json
$signTool = $null
if ($usingPfx) {
    $pfxFlags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($SigningPfxPath, $SigningPfxPassword, $pfxFlags)
    if (-not $certificate.HasPrivateKey) { throw 'The PFX does not contain a private key.' }
    if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) { throw 'The PFX certificate is not currently valid.' }
    # signtool is given the same PFX so the private key never enters the user store.
    $CertThumbprint = $certificate.Thumbprint
}
if ($CertThumbprint) {
    $CertThumbprint = $CertThumbprint.Replace(' ', '')
    if ($CertThumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'CertThumbprint must be a 40-character SHA-1 certificate thumbprint.' }
    if (-not $usingPfx) {
        $certificate = Get-Item "Cert:\CurrentUser\My\$CertThumbprint"
        if (-not $certificate.HasPrivateKey -or $certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) { throw 'The signing certificate must have an accessible private key and be currently valid.' }
    }
    $publisherHash = New-Object Security.Cryptography.SHA256Managed
    try { $expectedPublisherSha256 = ([BitConverter]::ToString($publisherHash.ComputeHash($certificate.RawData))).Replace('-', '') }
    finally { $publisherHash.Dispose() }
    if ($releaseSource.publisherCertificateSha256.Count -ne 1 -or $releaseSource.publisherCertificateSha256[0] -ne $expectedPublisherSha256) { throw 'UpdateSource.json must pin exactly the SHA-256 of this publisher certificate.' }
    if (-not [Uri]::IsWellFormedUriString($TimestampUrl, [UriKind]::Absolute) -or ([Uri]$TimestampUrl).Scheme -notin @('http', 'https')) { throw 'TimestampUrl must be an absolute HTTP(S) URL.' }
}

$rid = "win-$Platform"
# All architectures and package types for this version share one version folder.
$output = Join-Path (Join-Path $repoRoot 'Release\Windows') $Version
if (-not $CertThumbprint) { Write-Warning 'Building an unsigned test installer. Do not publish it to the update service.' }
$setupName = if ($BuildNumber -gt 0) { "FeigeSetup-$Version-b$BuildNumber-$Platform.exe" } else { "FeigeSetup-$Version-$Platform.exe" }
if ((Test-Path (Join-Path $output $setupName)) -or (Test-Path (Join-Path $output "$setupName.json"))) {
    throw 'That version/build already has output. Use a new version or build number; existing deliverables are never overwritten.'
}
$stagingRoot = Join-Path $windowsRoot 'obj\.staging'
$staging = Join-Path $stagingRoot ([guid]::NewGuid().ToString('N'))
$signingTools = $null
$payload = Join-Path $staging 'app'
New-Item $stagingRoot -ItemType Directory -Force | Out-Null
New-Item $payload -ItemType Directory -Force | Out-Null
New-Item $output -ItemType Directory -Force | Out-Null
try {
    Write-Host "Publishing self-contained $rid / $Version"
    $platformName = if ($Platform -eq 'arm64') { 'ARM64' } else { 'x64' }
    & $DotnetPath publish $project -c Release -r $rid --self-contained true `
        "-p:Platform=$platformName" '-p:FeigeInstallerBuild=true' '-p:PublishReadyToRun=false' `
        '-p:PublishTrimmed=false' "-p:Version=$Version" "-p:FeigeBuildNumber=$BuildNumber" -o $payload
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
    foreach ($required in @('Feige.App.exe', 'coreclr.dll', 'Microsoft.UI.Xaml.dll', 'Feige.App.pri',
        'App.xbf', 'MainWindow.xbf', 'Pages\SendPage.xbf', 'Pages\ReceivePage.xbf', 'Pages\SettingsPage.xbf',
        'Pages\SetupPage.xbf', 'Controls\AttachmentPreviewDialog.xbf', 'Assets\AppIcon.ico')) {
        if (-not (Test-Path (Join-Path $payload $required))) { throw "Incomplete self-contained output: missing $required" }
    }
    $runtime = Get-Content (Join-Path $payload 'Feige.App.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (-not $runtime.runtimeOptions.PSObject.Properties['includedFrameworks']) { throw 'Output unexpectedly requires an installed .NET runtime.' }

    # Preserve upstream notices shipped with the runtime packages.
    $assets = Get-Content (Join-Path (Split-Path $project) 'obj\project.assets.json') -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Name -notmatch '^(BouncyCastle\.Cryptography|Microsoft\.WindowsAppSDK[^/]*|Microsoft\.NETCore\.App\.Runtime[^/]*)/') { continue }
        $relative = $library.Name.ToLowerInvariant()
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $package = Join-Path $folder $relative
            if (-not (Test-Path $package)) { continue }
            foreach ($notice in Get-ChildItem $package -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|THIRD.?PARTY.?NOTICES)' }) {
                $target = Join-Path $payload ('licenses\' + $relative.Replace('/', '-'))
                New-Item $target -ItemType Directory -Force | Out-Null
                Copy-Item $notice.FullName $target
            }
        }
    }

    $signedPayloadFiles = @()
    $appBuildSuffix = if ($BuildNumber -gt 0) { "-b$BuildNumber" } else { '' }
    $isccArgs = @('/Q', "/DAppVersion=$Version", "/DAppBuild=$BuildNumber", "/DAppBuildSuffix=$appBuildSuffix", "/DTargetArch=$Platform", "/DPublishDir=$payload",
        "/DSetupOutputDir=$output", "/DSetupIcon=$(Join-Path $windowsRoot 'src\Feige.App\Assets\AppIcon.ico')")
    if ($CertThumbprint) {
        $signTool = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools') -Recurse -Filter signtool.exe |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        if (-not $signTool) { throw 'signtool.exe not found in the SDK BuildTools package.' }
        # Inno's 32-bit compiler redirects System32 paths (including SYSTEM's NuGet cache),
        # and cannot reliably launch a tool from a UNC share. Stage signtool in a local temp
        # directory so both the compiler and the PowerShell checks use the same path.
        $signingTools = Join-Path $env:TEMP ('feige-signing-tools-' + [guid]::NewGuid().ToString('N'))
        Copy-Item (Split-Path -Parent $signTool) $signingTools -Recurse
        $signTool = Join-Path $signingTools 'signtool.exe'
        $verifyScript = Join-Path $windowsRoot 'src\Feige.App\Updates\VerifyPinnedAuthenticode.ps1'
        . $verifyScript
        # App Control may validate DLLs too; preserve vendor signatures and pin-check self-signed files.
        foreach ($binary in Get-ChildItem $payload -File -Recurse | Where-Object { $_.Extension -in @('.exe', '.dll') }) {
            $signature = Get-AuthenticodeSignature -LiteralPath $binary.FullName
            if ($signature.Status -eq 'NotSigned') {
                if ($usingPfx) {
                    # /f + /p keeps the private key out of the user certificate store.
                    & $signTool sign /fd SHA256 /f $SigningPfxPath /p $SigningPfxPassword /tr $TimestampUrl /td SHA256 $binary.FullName
                }
                else {
                    & $signTool sign /fd SHA256 /sha1 $CertThumbprint /tr $TimestampUrl /td SHA256 $binary.FullName
                }
                if ($LASTEXITCODE -ne 0) { throw "Payload signing failed: $($binary.Name)" }
                $signedPayloadFiles += $binary.FullName.Substring($payload.Length + 1)
                Test-FeigePinnedAuthenticode -Path $binary.FullName -Pins $releaseSource.publisherCertificateSha256 | Out-Null
            }
            elseif ($signature.Status -eq 'Valid') {
                # Preserve Microsoft/vendor signatures; never overwrite them with the Feige publisher key.
            }
            elseif ($signature.Status -in @('NotTrusted','UnknownError')) {
                Test-FeigePinnedAuthenticode -Path $binary.FullName -Pins $releaseSource.publisherCertificateSha256 | Out-Null
            }
            else {
                throw "Existing payload signature is invalid: $($binary.Name) ($($signature.Status)). It will not be overwritten."
            }
        }
        $signedUninstallers = Join-Path $staging 'signed-uninstallers'
        New-Item $signedUninstallers -ItemType Directory -Force | Out-Null
        # Inno must sign its inner Setup/temp copy and Uninstall while compiling, not only the outer EXE afterwards.
        # $q / $f are Inno placeholders. Escape literal dollar signs in supplied paths/URLs.
        $signCommand = if ($usingPfx) {
            '$q{0}$q sign /fd SHA256 /f $q{1}$q /p $q{2}$q /tr $q{3}$q /td SHA256 $f' -f `
                $signTool.Replace('$', '$$'), $SigningPfxPath.Replace('$', '$$'), $SigningPfxPassword.Replace('$', '$$'), $TimestampUrl.Replace('$', '$$')
        }
        else {
            '$q{0}$q sign /fd SHA256 /sha1 {1} /tr $q{2}$q /td SHA256 $f' -f `
                $signTool.Replace('$', '$$'), $CertThumbprint, $TimestampUrl.Replace('$', '$$')
        }
        # Append as separate arguments. A comma list after '+' would be parsed as
        # string + array (space-joined), silently collapsing all four switches.
        $isccArgs += '/DExpectedPublisherSha256=' + $expectedPublisherSha256
        $isccArgs += '/DFeigeSigningEnabled=1'
        $isccArgs += "/DSignedUninstallerOutputDir=$signedUninstallers"
        $isccArgs += "/SFeigeReleaseSign=$signCommand"
    }

    Write-Host 'Compiling EXE installer'
    & $IsccPath @isccArgs $installer
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed: $LASTEXITCODE" }
    $setup = Join-Path $output $setupName
    if ($CertThumbprint) {
        $verifyScript = Join-Path $windowsRoot 'src\Feige.App\Updates\VerifyPinnedAuthenticode.ps1'
        . $verifyScript
        # Signature lookup on a freshly written installer can race the antivirus/file indexer;
        # retry briefly before failing the build.
        $verified = $false
        for ($attempt = 1; $attempt -le 5 -and !$verified; $attempt++) {
            try {
                Test-FeigePinnedAuthenticode -Path $setup -Pins $releaseSource.publisherCertificateSha256 | Out-Null
                $verified = $true
            }
            catch {
                if ($attempt -eq 5) { throw }
                Write-Warning "Installer signature not readable yet (attempt $attempt): $($_.Exception.Message)"
                Start-Sleep -Milliseconds 800
            }
        }
    }
    [ordered]@{
        version = $Version; build = $BuildNumber; architecture = $Platform; installer = [IO.Path]::GetFileName($setup)
        bytes = (Get-Item $setup).Length; sha256 = (Get-FileHash $setup -Algorithm SHA256).Hash
        signed = [bool]$CertThumbprint; signedPayloadFiles = @($signedPayloadFiles); createdUtc = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content "$setup.json" -Encoding UTF8
    Write-Host "Installer ready: $setup" -ForegroundColor Green
    if (-not $CertThumbprint) { Write-Host 'Unsigned test installer: Windows App Control / Smart App Control may block it (error 4551). A trusted signing certificate or an administrator-approved allow policy is required on protected devices.' }
    if ($KeepPublish) { Write-Host "Published files retained: $payload" }
}
finally {
    if ($signingTools -and (Test-Path $signingTools)) { Remove-Item $signingTools -Recurse -Force -ErrorAction SilentlyContinue }
    if (-not $KeepPublish -and (Test-Path $staging)) { Remove-Item $staging -Recurse -Force }
}
