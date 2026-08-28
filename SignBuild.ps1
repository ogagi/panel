[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BuildDirectory
)

$ErrorActionPreference = 'Stop'
$certificateSubject = 'CN=AI Core Monitor Local Development'
$certificate = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $certificateSubject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($null -eq $certificate) {
    Write-Host 'Creating a per-user AI Core Monitor development signing certificate...'
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $certificateSubject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears(3)
}

# Trust only this public certificate for the current Windows user. The private key
# remains non-exportable in CurrentUser\My.
foreach ($storeName in @('Root', 'TrustedPublisher')) {
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        $storeName,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        if (-not $store.Certificates.Find(
            [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $certificate.Thumbprint,
            $false).Count) {
            $store.Add($certificate)
        }
    }
    finally {
        $store.Dispose()
    }
}

$files = @(
    (Join-Path $BuildDirectory 'AiCoreMonitor.exe'),
    (Join-Path $BuildDirectory 'AiCoreMonitor.dll'),
    (Join-Path $BuildDirectory 'AiCoreMonitor.Core.dll')
)

foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Build output was not found: $file"
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -eq 'Valid' -and
        $signature.SignerCertificate.Thumbprint -eq $certificate.Thumbprint) {
        continue
    }

    Write-Host "Signing $([System.IO.Path]::GetFileName($file))..."
    $result = Set-AuthenticodeSignature -LiteralPath $file -Certificate $certificate -HashAlgorithm SHA256
    if ($result.Status -ne 'Valid') {
        throw "Signing failed for $file`: $($result.StatusMessage)"
    }
}

Write-Host "Build signed with certificate $($certificate.Thumbprint)."
