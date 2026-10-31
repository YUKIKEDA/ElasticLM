<#
.SYNOPSIS
    Runs the published executables of one runtime identifier once (design §20.5).

.DESCRIPTION
    The same smoke test as CI's NativeAOT job: elm --help and elm-server --help answer; elm-server refuses to start without
    trust; elm-service, with keys made by elm, prints a key from setup, starts with nothing beside it, and answers /health.
    The keys, passphrases, and the database are made in a temporary folder for this run and removed at the end
    (.cursor/rules/scratch.mdc), and the environment is put back.

.PARAMETER PublishDir
    The folder with the elm, elm-server, and elm-service subfolders (Publish-Rid.ps1).

.PARAMETER Port
    The loopback port elm-service listens on.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [int]$Port = 18080
)

$ErrorActionPreference = 'Stop'
$PublishDir = (Resolve-Path $PublishDir).Path
$exe = if ($IsWindows) { '.exe' } else { '' }
$elm = Join-Path $PublishDir "elm/elm$exe"
$server = Join-Path $PublishDir "elm-server/elm-server$exe"
$service = Join-Path $PublishDir "elm-service/elm-service$exe"
$work = Join-Path ([IO.Path]::GetTempPath()) ("elm-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$saved = [Environment]::GetEnvironmentVariables()

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE." }
}

Push-Location $work
try {
    Invoke-Checked 'elm --help' { & $elm --help | Out-Null }
    Invoke-Checked 'elm-server --help' { & $server --help | Out-Null }
    $env:ELM_PRODUCT = 'acme-cad'
    $out = & $server 2>&1 | Out-String
    if ($LASTEXITCODE -ne 1 -or $out -notmatch 'trust.rootKeys') { $out; throw 'elm-server did not refuse to start without trust.' }

    $env:ELM_KEY_PASSPHRASE = 'smoke passphrase 0123'
    Invoke-Checked 'elm key root create' { & $elm key root create --out root.key.pem --public-out root.pub.pem | Out-Null }
    Invoke-Checked 'elm key signing create' { & $elm key signing create --out signing.key.pem --public-out signing.pub.pem | Out-Null }
    Invoke-Checked 'elm key certify' { & $elm key certify --root root.key.pem --public signing.pub.pem --use license lease --out signing.cert | Out-Null }
    $env:ELM_SIGNING_PASSPHRASE = $env:ELM_KEY_PASSPHRASE
    $env:ELM_NONCE_SECRET = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $env:ELM_SIGNING_KEY = 'signing.key.pem'
    $env:ELM_SIGNING_CERTIFICATES = 'signing.cert'
    $env:ELM_TRUST_ROOTKEYS = 'root.pub.pem'
    $env:ELM_DATABASE_CONNECTION = 'Data Source=smoke.db'
    $env:ELM_BOOTSTRAPOWNER_ISS = 'https://idp.example'
    $env:ELM_BOOTSTRAPOWNER_SUB = 'smoke'
    $env:ELM_LISTEN = "http://127.0.0.1:$Port"
    $key = (& $service setup | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $key.StartsWith('elm_')) { throw 'elm-service setup did not print a key.' }
    $process = Start-Process $service -PassThru -NoNewWindow
    try {
        $ok = $false
        for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
            try { $ok = (Invoke-WebRequest "http://127.0.0.1:$Port/health" -SkipHttpErrorCheck).StatusCode -eq 200 } catch { }
            if (-not $ok) { Start-Sleep -Seconds 1 }
        }
        if (-not $ok) { throw 'elm-service did not answer /health.' }
    }
    finally {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
        $process.WaitForExit()
    }
    'The executables ran: elm, elm-server, elm-service.'
}
finally {
    Pop-Location
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    foreach ($name in @([Environment]::GetEnvironmentVariables().Keys)) {
        if (-not $saved.Contains($name)) { [Environment]::SetEnvironmentVariable($name, $null) }
    }
}

# A native command's exit code (codesign, tar, a stopped process) would otherwise become the step's result.
exit 0
