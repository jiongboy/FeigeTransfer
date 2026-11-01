function Test-FeigePinnedAuthenticodeChainStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Status,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$StatusMessage,
        [Parameter(Mandatory)][bool]$ChainOk,
        [Security.Cryptography.X509Certificates.X509ChainStatusFlags[]]$ChainErrors = @()
    )

    $errors = @($ChainErrors | Where-Object { [int]$_ -ne 0 })
    $untrustedRootOnly = $errors.Count -eq 1 -and $errors[0] -eq [Security.Cryptography.X509Certificates.X509ChainStatusFlags]::UntrustedRoot
    if ($Status -eq 'Valid') {
        if (!$ChainOk -or $errors.Count -gt 0) { throw 'Unexpected certificate chain status.' }
        return $true
    }
    if ($Status -in @('NotTrusted', 'UnknownError') -and $untrustedRootOnly) { return $true }

    # A pinned self-signed publisher can be present in Root yet fail WinTrust's
    # BasicConstraints policy. Accept only that exact diagnostic with a clean built chain.
    $pinnedBasicConstraintsError = $Status -eq 'UnknownError' -and
        $StatusMessage -ceq "A certificate's basic constraint extension has not been observed" -and
        $ChainOk -and $errors.Count -eq 0
    if ($pinnedBasicConstraintsError) { return $true }

    if ($Status -in @('NotTrusted', 'UnknownError')) {
        throw "Only an untrusted root or the pinned BasicConstraints diagnostic is permitted; chain status: $($errors -join ',')."
    }
    throw "Unsupported Authenticode status: $Status."
}

function Test-FeigePinnedAuthenticode {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string[]]$Pins)

    $invalidPins = @($Pins | Where-Object { $_ -notmatch '^[A-Fa-f0-9]{64}$' })
    if ($Pins.Count -eq 0 -or $Pins.Count -gt 8 -or $invalidPins.Count -gt 0) {
        throw 'Invalid publisher pin configuration.'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if (!$signature.SignerCertificate -or $signature.Status -notin @('Valid', 'NotTrusted', 'UnknownError')) {
        throw "Authenticode signature is absent, malformed, or invalid ($($signature.Status))."
    }

    $certificate = $signature.SignerCertificate
    $sha = New-Object Security.Cryptography.SHA256Managed
    try { $pin = ([BitConverter]::ToString($sha.ComputeHash($certificate.RawData))).Replace('-', '') }
    finally { $sha.Dispose() }
    if ($Pins -notcontains $pin) { throw 'Publisher certificate pin mismatch.' }
    if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) { throw 'Publisher certificate is outside its validity period.' }

    $eku = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
    if (!$eku -or !($eku.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' })) {
        throw 'Publisher certificate is not authorized for code signing.'
    }
    $keyUsageExtension = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.15' } | Select-Object -First 1
    if (!$keyUsageExtension) { throw 'Publisher certificate does not declare key usage.' }
    $keyUsage = New-Object Security.Cryptography.X509Certificates.X509KeyUsageExtension($keyUsageExtension, $keyUsageExtension.Critical)
    if (($keyUsage.KeyUsages -band [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -eq 0) {
        throw 'Publisher certificate does not allow digital signatures.'
    }
    if ($certificate.Subject -ne $certificate.Issuer) { throw 'Pinned publisher must be a self-signed root certificate.' }
    $constraintsExtension = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' } | Select-Object -First 1
    if ($constraintsExtension) {
        $constraints = New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($constraintsExtension, $constraintsExtension.Critical)
        if (!$constraints.CertificateAuthority) { throw 'Publisher certificate has invalid CA constraints.' }
    }
    if ($certificate.PublicKey.Key.KeySize -lt 3072 -or $certificate.PublicKey.Oid.Value -ne '1.2.840.113549.1.1.1') {
        throw 'Publisher certificate must use RSA 3072 or stronger.'
    }

    $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
    try {
        $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $chain.ChainPolicy.VerificationFlags = [Security.Cryptography.X509Certificates.X509VerificationFlags]::AllowUnknownCertificateAuthority
        $chainOk = $chain.Build($certificate)
        $errors = @($chain.ChainStatus | ForEach-Object { $_.Status } | Where-Object { [int]$_ -ne 0 })
        Test-FeigePinnedAuthenticodeChainStatus -Status $signature.Status -StatusMessage $signature.StatusMessage `
            -ChainOk $chainOk -ChainErrors $errors | Out-Null
    } finally { $chain.Dispose() }

    return [pscustomobject]@{ Thumbprint=$certificate.Thumbprint; Sha256=$pin; Subject=$certificate.Subject; Status=$signature.Status }
}
