$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '..\src\Feige.App\Updates\VerifyPinnedAuthenticode.ps1'
. ([IO.Path]::GetFullPath($source))
$fixture = Join-Path $env:TEMP ('feige-pin-test-' + [Guid]::NewGuid().ToString('N') + '.ps1')
$certificate = $null
try {
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject ('CN=FeigePinTest-' + [Guid]::NewGuid().ToString('N')) `
        -CertStoreLocation 'Cert:\CurrentUser\My' -Provider 'Microsoft Software Key Storage Provider' `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddDays(3)
    @'
param([string]$Marker)
Write-Output "PIN_TEST:$Marker"
'@ | Set-Content -LiteralPath $fixture -Encoding UTF8
    Set-AuthenticodeSignature -FilePath $fixture -Certificate $certificate -HashAlgorithm SHA256 | Out-Null
    $sha = New-Object Security.Cryptography.SHA256Managed
    try { $pin = ([BitConverter]::ToString($sha.ComputeHash($certificate.RawData))).Replace('-', '') }
    finally { $sha.Dispose() }
    $result = Test-FeigePinnedAuthenticode -Path $fixture -Pins @($pin)
    if ($result.Sha256 -ne $pin) { throw 'Valid self-signed pin test returned unexpected identity.' }
    try { Test-FeigePinnedAuthenticode -Path $fixture -Pins @('0' * 64); throw 'Wrong pin was accepted.' }
    catch { if ($_.Exception.Message -eq 'Wrong pin was accepted.') { throw } }
    $tampered = Get-Content -LiteralPath $fixture -Raw
    Set-Content -LiteralPath $fixture -Value ($tampered + "`n#tampered") -Encoding UTF8
    try { Test-FeigePinnedAuthenticode -Path $fixture -Pins @($pin); throw 'Tampered file was accepted.' }
    catch { if ($_.Exception.Message -eq 'Tampered file was accepted.') { throw } }
    Write-Output "PIN_TEST_OK status=$($result.Status)"
}
finally {
    if (Test-Path $fixture) { Remove-Item -LiteralPath $fixture -Force }
    if ($certificate) { Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $certificate.Thumbprint) -Force -ErrorAction SilentlyContinue }
}
