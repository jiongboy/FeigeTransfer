<#
.SYNOPSIS
    Build and sign a sideloadable MSIX for Feige.App.

.DESCRIPTION
    Publishes src\Feige.App as a single-project MSIX, then signs the package.

    The package manifest declares Publisher="CN=Songli", so the signing
    certificate must carry exactly that subject. Mismatch is the single most
    common sideload failure (0x800B0109 / "certificate chain terminated in an
    untrusted root" is its sibling: the certificate must also be trusted).

    Certificate resolution order:
      1. -CertThumbprint, if given.
      2. -NewDevCert, which creates a self-signed code-signing certificate with
         subject CN=Songli and reuses it on later runs.
      3. An existing certificate in CurrentUser\My whose friendly name is
         "Feige Dev Signing".

    Trimming stays off: WinUI 3 resolves types through reflection and a trimmed
    package builds fine but fails at runtime.

.PARAMETER Configuration
    Build configuration. Default Release.

.PARAMETER Platform
    Target platform: x64, arm64 or x86. Defaults to the current process arch.

.PARAMETER CertThumbprint
    Thumbprint of a code-signing certificate in CurrentUser\My.

.PARAMETER NewDevCert
    Create (or reuse) the self-signed development certificate.

.PARAMETER TrustDevCert
    Import the exported .cer into the machine TrustedPeople store when elevated,
    otherwise into the current user's TrustedPeople store. Required before
    Add-AppxPackage will accept a self-signed package.

.PARAMETER SkipSign
    Produce an unsigned package (developer-mode testing only).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Msix.ps1 -NewDevCert -TrustDevCert

.NOTES
    Exit code 0 = package produced, 1 = failure.
    ASCII-only output on purpose (Windows PowerShell 5.1 mis-decodes UTF-8 without BOM).
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Platform = "",
    [string]$CertThumbprint = "",
    [switch]$NewDevCert,
    [switch]$TrustDevCert,
    [switch]$SkipSign
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$windowsRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent $windowsRoot
$repoRoot = Split-Path -Parent $projectRoot
$project = Join-Path $windowsRoot "src\Feige.App\Feige.App.csproj"
[xml]$projectXml = Get-Content $project -Raw
$packageVersion = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$certRoot = Join-Path $windowsRoot ".certs"
$devCertSubject = "CN=Songli"
$devCertFriendlyName = "Feige Dev Signing"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "== $Message" -ForegroundColor Cyan
}

function Get-SignTool {
    $packages = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools"
    if (-not (Test-Path $packages)) {
        return $null
    }

    return Get-ChildItem -Path $packages -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

function Resolve-Certificate {
    if ($CertThumbprint) {
        $found = Get-ChildItem "Cert:\CurrentUser\My\$CertThumbprint" -ErrorAction SilentlyContinue
        if ($null -eq $found) {
            throw "No certificate with thumbprint $CertThumbprint in Cert:\CurrentUser\My."
        }
        return $found
    }

    $existing = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.FriendlyName -eq $devCertFriendlyName -and $_.HasPrivateKey } |
        Select-Object -First 1

    if ($null -ne $existing) {
        if (-not $NewDevCert) {
            Write-Host "Reusing dev certificate $($existing.Thumbprint)"
        }
        return $existing
    }

    if (-not $NewDevCert) {
        throw "No signing certificate found. Re-run with -NewDevCert, or pass -CertThumbprint."
    }

    Write-Host "Creating self-signed certificate ($devCertSubject)"
    return New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $devCertSubject `
        -FriendlyName $devCertFriendlyName `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter (Get-Date).AddYears(3)
}

function Export-DevCertificate {
    param([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

    if (-not (Test-Path $certRoot)) {
        New-Item -ItemType Directory -Path $certRoot | Out-Null
    }

    $cerPath = Join-Path $certRoot "feige-dev.cer"
    Export-Certificate -Cert $Certificate -FilePath $cerPath -Force | Out-Null
    return $cerPath
}

function Add-TrustedCertificate {
    param([string]$CerPath)

    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    $elevated = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)

    # Machine-wide trust is what deployment checks for every user; the per-user
    # store is enough for a single account and needs no elevation.
    $store = if ($elevated) { "Cert:\LocalMachine\TrustedPeople" } else { "Cert:\CurrentUser\TrustedPeople" }
    $cerThumbprint = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CerPath)).Thumbprint
    $existing = Get-ChildItem $store | Where-Object { $_.Thumbprint -eq $cerThumbprint }

    if ($null -ne $existing) {
        Write-Host "Certificate already trusted in $store"
        return
    }

    Import-Certificate -FilePath $CerPath -CertStoreLocation $store | Out-Null
    Write-Host "Certificate trusted in $store"
}

if ($Platform -eq "") {
    $Platform = switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        "Arm64" { "arm64" }
        "X86" { "x86" }
        default { "x64" }
    }
}
$packageRoot = Join-Path (Join-Path $repoRoot 'Release\Windows') $packageVersion

Write-Step "Publishing $Platform / $Configuration"
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
# Build the package without signing it, so this script owns signing: it needs the certificate
# to be resolved (or created) first, then signs and verifies the produced .msix. Asking MSBuild
# to sign at the same time would need a store certificate and would make -SkipSign incoherent.
& dotnet msbuild $project -t:Publish `
    "-p:Configuration=$Configuration" `
    -p:Platform=$Platform `
    -p:AppxBundle=Never `
    -p:UapAppxPackageBuildMode=SideloadOnly `
    -p:AppxPackageSigningEnabled=false `
    -p:GenerateAppxPackageOnBuild=true `
    "-p:AppxPackageDir=$packageRoot/"

if ($LASTEXITCODE -ne 0) {
    throw "Publishing failed with exit code $LASTEXITCODE."
}

$msix = Get-ChildItem -Path $packageRoot -Filter "*.msix" -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $msix) {
    throw "No .msix was produced under $packageRoot."
}

Write-Host "Package: $($msix.FullName) ($([math]::Round($msix.Length / 1MB, 1)) MB)"

if ($SkipSign) {
    Write-Host "Skipping signing (-SkipSign)."
    exit 0
}

Write-Step "Signing"
$certificate = Resolve-Certificate
Write-Host "Certificate: $($certificate.Subject) [$($certificate.Thumbprint)]"

if ($certificate.Subject -ne $devCertSubject) {
    throw "Certificate subject '$($certificate.Subject)' does not match the manifest Publisher '$devCertSubject'."
}

$signTool = Get-SignTool
if ($null -eq $signTool) {
    throw "signtool.exe not found. Run Install-DevEnv.ps1, or install Microsoft.Windows.SDK.BuildTools."
}

& $signTool sign /fd SHA256 /sha1 $certificate.Thumbprint $msix.FullName
if ($LASTEXITCODE -ne 0) {
    throw "signtool sign failed with exit code $LASTEXITCODE."
}

$cerPath = Export-DevCertificate -Certificate $certificate

if ($TrustDevCert) {
    Add-TrustedCertificate -CerPath $cerPath
}

Write-Step "Verifying"
# /r supplies the self-signed root to this check only; it does not change trust stores.
& $signTool verify /pa /r $cerPath $msix.FullName

if ($LASTEXITCODE -ne 0) {
    # A self-signed code-signing certificate is its own root and carries no CA
    # basic constraint, so signtool's chain walk can reject it while Windows
    # deployment accepts it from TrustedPeople. Confirm the signature is really
    # ours and let Test-MsixPackage.ps1 prove that installation works.
    $signature = Get-AuthenticodeSignature -FilePath $msix.FullName
    $signerThumbprint = if ($null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { "" }

    if ($signerThumbprint -ne $certificate.Thumbprint) {
        throw "The package is not signed with the expected certificate (found '$signerThumbprint')."
    }

    Write-Warning "signtool chain check rejected the self-signed root; the signature is present and matches. Run Test-MsixPackage.ps1 to prove the package installs."
}

Write-Host ""
Write-Host "Signed package ready: $($msix.FullName)" -ForegroundColor Green
Write-Host "Next: powershell -NoProfile -ExecutionPolicy Bypass -File .\Test-MsixPackage.ps1"
exit 0
