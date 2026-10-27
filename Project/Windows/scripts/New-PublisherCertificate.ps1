<#
.SYNOPSIS
  Create a self-signed Authenticode publisher certificate and export only its public certificate.
.DESCRIPTION
  The private key remains non-exportable in the current user's Windows certificate store.
  Back up/recover it through an approved protected Windows mechanism before relying on it for releases.
#>
[CmdletBinding()]
param([string]$Name = 'FeigeTransfer-Release-2026', [string]$OutputDirectory = '', [string]$CertificateFileName = 'FeigePublisher-2026.cer')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'Run this script in Windows PowerShell on the release machine.' }
if ($Name -notmatch '^[A-Za-z0-9._-]{1,80}$' -or $CertificateFileName -notmatch '^FeigePublisher-[0-9]{4}\.cer$') { throw 'Invalid certificate name or versioned public certificate filename.' }
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot '.signing' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$repoSigning = [IO.Path]::GetFullPath((Join-Path $repoRoot '.signing'))
if ($OutputDirectory -ne $repoSigning) { throw 'Public certificate export directory must be the repository root .signing/ folder.' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $candidate = $_
    $isCa = $false
    foreach ($extension in $candidate.Extensions) {
        if ($extension.Oid.Value -eq '2.5.29.19') {
            $constraints = New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($extension, $extension.Critical)
            $isCa = $constraints.CertificateAuthority
        }
    }
    $candidate.Subject -eq "CN=$Name" -and $candidate.HasPrivateKey -and $candidate.NotAfter -gt (Get-Date) -and $isCa
} | Sort-Object NotAfter -Descending | Select-Object -First 1
if ($null -eq $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject "CN=$Name" -FriendlyName $Name `
        -CertStoreLocation 'Cert:\CurrentUser\My' -Provider 'Microsoft Software Key Storage Provider' `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable `
        -KeyUsage CertSign, CRLSign, DigitalSignature -TextExtension @(
            '2.5.29.19={critical}{text}ca=TRUE&pathlength=0',
            '2.5.29.37={text}1.3.6.1.5.5.7.3.3'
        ) -NotAfter (Get-Date).AddYears(10)
}
$cer = Join-Path $OutputDirectory $CertificateFileName
if (Test-Path -LiteralPath $cer) {
    throw 'Versioned public certificate file already exists; refusing to overwrite.'
} else {
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
}
$sha = New-Object Security.Cryptography.SHA256Managed
try { $fingerprint = ([BitConverter]::ToString($sha.ComputeHash($cert.RawData))).Replace('-', '') }
finally { $sha.Dispose() }
[pscustomobject]@{ certificatePath=$cer; thumbprint=$cert.Thumbprint; sha256=$fingerprint; subject=$cert.Subject; expires=$cert.NotAfter; isCertificateAuthority=($cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' } | Select-Object -First 1).CertificateAuthority; privateKeyExportable=$false } | Format-List
Write-Warning 'The private key is non-exportable and remains in Cert:\CurrentUser\My on this Windows account. Preserve access to this signing identity using a separate protected recovery plan.'
