#Requires -Version 7
<#
.SYNOPSIS
Turns on TLS in a running PostgreSQL container with a CA and a server certificate made for this run.

.DESCRIPTION
design §14.2 (V1d26b): the gate's PostgreSQL and the end-to-end tests' PostgreSQL take TLS, so that the service's
connections are tried with SSL Mode=VerifyFull. The script makes a P-256 CA and a server certificate signed by it, for
localhost and 127.0.0.1, valid for one day, with .NET's CertificateRequest; no key is kept in the repository. It copies the
server's certificate and key into the container's /var/lib/postgresql/tls/, gives them to the postgres user (the key with
mode 0600, which PostgreSQL asks of a key it owns), turns on ssl with ALTER SYSTEM, reloads the configuration, and checks
that ssl is on. It writes the CA's certificate to the RootPath file and removes its own copy of the server's key.

The container is the official postgres image (Debian), which has chown, chmod, and psql. Docker and wslc are both taken:
the files are copied as a folder, as wslc copies only into a folder.

.PARAMETER Container
The name or ID of the running PostgreSQL container.

.PARAMETER RootPath
The file the CA's certificate (PEM) is written to: the Root Certificate of the connection strings.

.PARAMETER Tool
The container command: docker (the default, as on the runners) or wslc.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Container,
    [Parameter(Mandatory)][string]$RootPath,
    [ValidateSet('docker', 'wslc')][string]$Tool = 'docker'
)

$ErrorActionPreference = 'Stop'
$RootPath = [IO.Path]::GetFullPath($RootPath)
$work = Join-Path ([IO.Path]::GetTempPath()) ('elm-postgres-tls-' + [Guid]::NewGuid().ToString('N'))
$tls = Join-Path $work 'tls'
New-Item -ItemType Directory -Path $tls | Out-Null

function Invoke-Tool {
    param([string[]]$Arguments)
    $output = & $Tool @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Tool $($Arguments -join ' ') failed: $output"
    }
    return $output
}

try {
    # The image first prepares its data with a server that listens on its Unix socket only, then restarts it; the server
    # that answers over TCP is the one that stays.
    for ($i = 0; $i -lt 60; $i++) {
        & $Tool exec $Container pg_isready -q -h 127.0.0.1 -U postgres 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Seconds 1
    }

    if ($LASTEXITCODE -ne 0) {
        throw "PostgreSQL in $Container does not take connections over TCP after 60 seconds."
    }

    $now = [DateTimeOffset]::UtcNow
    $caKey = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    $caRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=ElasticLM CI PostgreSQL CA', $caKey, [Security.Cryptography.HashAlgorithmName]::SHA256)
    $caRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
    $caRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]'KeyCertSign, CrlSign', $true))
    $ca = $caRequest.CreateSelfSigned($now.AddMinutes(-5), $now.AddDays(1))

    $serverKey = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    $serverRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=localhost', $serverKey, [Security.Cryptography.HashAlgorithmName]::SHA256)
    $names = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $names.AddDnsName('localhost')
    $names.AddIpAddress([Net.IPAddress]::Loopback)
    $serverRequest.CertificateExtensions.Add($names.Build())
    $serverRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $usages = [Security.Cryptography.OidCollection]::new()
    $usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1')) | Out-Null
    $serverRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $false))
    $serial = [byte[]]::new(16)
    [Security.Cryptography.RandomNumberGenerator]::Fill($serial)
    $serial[0] = $serial[0] -band 0x7f
    $server = $serverRequest.Create($ca, $now.AddMinutes(-5), $now.AddDays(1), $serial)

    [IO.File]::WriteAllText((Join-Path $tls 'server.crt'), $server.ExportCertificatePem() + "`n")
    [IO.File]::WriteAllText((Join-Path $tls 'server.key'), $serverKey.ExportPkcs8PrivateKeyPem() + "`n")
    [IO.File]::WriteAllText($RootPath, $ca.ExportCertificatePem() + "`n")

    Invoke-Tool @('cp', $tls, "${Container}:/var/lib/postgresql/") | Out-Null
    Invoke-Tool @('exec', $Container, 'sh', '-c',
        'chown -R postgres:postgres /var/lib/postgresql/tls && chmod 600 /var/lib/postgresql/tls/server.key') | Out-Null
    foreach ($sql in @(
            "ALTER SYSTEM SET ssl_cert_file = '/var/lib/postgresql/tls/server.crt'",
            "ALTER SYSTEM SET ssl_key_file = '/var/lib/postgresql/tls/server.key'",
            'ALTER SYSTEM SET ssl = on',
            'SELECT pg_reload_conf()')) {
        Invoke-Tool @('exec', $Container, 'psql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1', '-c', $sql) | Out-Null
    }

    # The reload is asynchronous: a new session sees the new value once the postmaster has read the file.
    $state = ''
    for ($i = 0; $i -lt 20 -and $state -ne 'on'; $i++) {
        Start-Sleep -Milliseconds 250
        $state = ((Invoke-Tool @('exec', $Container, 'psql', '-U', 'postgres', '-tA', '-c', 'SHOW ssl')) | Out-String).Trim()
    }

    if ($state -ne 'on') {
        throw "PostgreSQL in $Container did not turn ssl on (SHOW ssl gave '$state'); read its log."
    }

    "PostgreSQL in $Container takes TLS; the root certificate is $RootPath."
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
