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
    if ($certificate.Subject -ne $certificate.Issuer) { throw 'Pinned publisher must be a self-signed root certificate.' }
    $constraintsExtension = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' } | Select-Object -First 1
    if ($constraintsExtension) {
        $constraints = New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($constraintsExtension, $constraintsExtension.Critical)
        if (!$constraints.CertificateAuthority) { throw 'Publisher certificate has invalid CA constraints.' }
    }
    if ($certificate.PublicKey.Key.KeySize -lt 3072 -or $certificate.PublicKey.Oid.Value -ne '1.2.840.113549.1.1.1') {
        throw 'Publisher certificate must use RSA 3072 or stronger.'
    }

    # Accept exactly the expected self-signed-root condition; every other chain error remains fatal.
    $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
    try {
        $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $chain.ChainPolicy.VerificationFlags = [Security.Cryptography.X509Certificates.X509VerificationFlags]::AllowUnknownCertificateAuthority
        $chainOk = $chain.Build($certificate)
        $errors = @($chain.ChainStatus | ForEach-Object { $_.Status } | Where-Object { [int]$_ -ne 0 })
        $untrustedRootOnly = $errors.Count -eq 1 -and $errors[0] -eq [Security.Cryptography.X509Certificates.X509ChainStatusFlags]::UntrustedRoot
        if ($signature.Status -eq 'Valid') {
            if (!$chainOk -or $errors.Count -gt 0) { throw 'Unexpected certificate chain status.' }
        } elseif ($signature.Status -in @('NotTrusted', 'UnknownError') -and !$untrustedRootOnly) {
            throw "Only an untrusted root is permitted; chain status: $($errors -join ',')."
        } elseif ($signature.Status -notin @('Valid', 'NotTrusted', 'UnknownError')) {
            throw "Unsupported Authenticode status: $($signature.Status)."
        }
    } finally { $chain.Dispose() }

    return [pscustomobject]@{ Thumbprint=$certificate.Thumbprint; Sha256=$pin; Subject=$certificate.Subject; Status=$signature.Status }
}
