<#
.SYNOPSIS
    Checks the elm-service image of one architecture from the OCI layout tar, and runs it (design §20.6).

.DESCRIPTION
    - The layout's index digest is -ExpectedDigest.
    - In this architecture's manifest, the first layers are the base image's layers of the same platform, digest for
      digest, and the layers after them hold only app/, app/elm-service, app/LICENSE, and data/. The elm-service and
      LICENSE in them have the SHA-256 of the same run's release archive.
    - docker load --platform reads this architecture, and the image it loads has the config of the manifest.
    - With keys, certificates, a root key, a nonce secret, and a passphrase made for this run and mounted as a settings file
      and secret files readable by UID 1654, a named volume on /data, and ELM_PUBLICURLS: setup prints a key; the service
      starts, answers /health with 200, logs no override of ASP.NET Core's addresses, and runs as UID 1654; docker stop ends
      it with exit code 0.
    It pushes nothing. Everything it makes is removed at the end (.cursor/rules/scratch.mdc).

.PARAMETER ImageTar
    The OCI layout tar (Build-Image.ps1).

.PARAMETER ExpectedDigest
    The index digest the build job reported.

.PARAMETER ArchiveDir
    The folder with the release archives of this architecture (elm-service and elm).

.PARAMETER Version
    The release version.

.PARAMETER Arch
    amd64 or arm64.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ImageTar,
    [Parameter(Mandatory = $true)][string]$ExpectedDigest,
    [Parameter(Mandatory = $true)][string]$ArchiveDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidateSet('amd64', 'arm64')][string]$Arch
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$rid = if ($Arch -eq 'amd64') { 'linux-x64' } else { 'linux-arm64' }
$work = Join-Path ([IO.Path]::GetTempPath()) ("elm-image-test-" + [Guid]::NewGuid().ToString('N'))
$tag = "elm-service-test:$Arch"
$volume = "elm-image-test-$Arch"
$container = "elm-image-test-$Arch"
$failures = [Collections.Generic.List[string]]::new()

function Invoke-Native([string]$what, [scriptblock]$command) {
    $out = & $command 2>&1
    if ($LASTEXITCODE -ne 0) { $out | Out-Host; throw "$what failed with exit code $LASTEXITCODE." }
    $out
}

function Read-Json([string]$path) { Get-Content -Raw $path | ConvertFrom-Json }

function Get-Blob([string]$layout, [string]$digest) { Join-Path $layout ('blobs/' + $digest.Replace(':', '/')) }

try {
    New-Item -ItemType Directory -Path $work | Out-Null
    $layout = Join-Path $work 'layout'
    New-Item -ItemType Directory -Path $layout | Out-Null
    Invoke-Native 'tar' { tar -xf $ImageTar -C $layout } | Out-Null

    # The index digest.
    $outer = Read-Json (Join-Path $layout 'index.json')
    $digest = $outer.manifests[0].digest
    if ($digest -ne $ExpectedDigest) { $failures.Add("The index digest is $digest, not $ExpectedDigest.") }

    # This architecture's manifest and the base's manifest of the same platform.
    $imageIndex = Read-Json (Get-Blob $layout $digest)
    $descriptor = @($imageIndex.manifests | Where-Object { $_.platform.os -eq 'linux' -and $_.platform.architecture -eq $Arch })
    if ($descriptor.Count -ne 1) { throw "The image index has no single linux/$Arch manifest." }
    $manifest = Read-Json (Get-Blob $layout $descriptor[0].digest)
    $from = (Select-String -Path (Join-Path $root 'eng/container/Dockerfile') -Pattern '^FROM\s+(\S+)$').Matches[0].Groups[1].Value
    $repository = $from.Split('@')[0].Split(':')[0]
    $baseIndex = (Invoke-Native 'imagetools inspect' { docker buildx imagetools inspect --raw $from }) -join "`n" | ConvertFrom-Json
    $baseDescriptor = @($baseIndex.manifests | Where-Object { $_.platform.os -eq 'linux' -and $_.platform.architecture -eq $Arch })
    $baseManifest = (Invoke-Native 'imagetools inspect' { docker buildx imagetools inspect --raw "$repository@$($baseDescriptor[0].digest)" }) -join "`n" | ConvertFrom-Json
    $baseLayers = @($baseManifest.layers | ForEach-Object { $_.digest })
    $layers = @($manifest.layers | ForEach-Object { $_.digest })
    for ($i = 0; $i -lt $baseLayers.Count; $i++) {
        if ($i -ge $layers.Count -or $layers[$i] -ne $baseLayers[$i]) { $failures.Add("Layer $i is not the base's layer $($baseLayers[$i])."); break }
    }

    # The added layers: only these paths, and the files of the release archive.
    $allowed = @('app/', 'app/elm-service', 'app/LICENSE', 'data/')
    $added = Join-Path $work 'added'
    New-Item -ItemType Directory -Path $added | Out-Null
    foreach ($layer in $layers | Select-Object -Skip $baseLayers.Count) {
        $blob = Get-Blob $layout $layer
        $paths = @(Invoke-Native 'tar' { tar -tzf $blob } | ForEach-Object { $_.TrimStart('.', '/') } | Where-Object { $_ })
        $extra = @($paths | Where-Object { $allowed -notcontains $_ -and $allowed -notcontains "$_/" })
        if ($extra.Count -gt 0) { $failures.Add("An added layer holds $($extra -join ', ').") }
        Invoke-Native 'tar' { tar -xzf $blob -C $added } | Out-Null
    }
    $archive = Join-Path $work 'archive'
    New-Item -ItemType Directory -Path $archive | Out-Null
    Invoke-Native 'tar' { tar -xzf (Join-Path $ArchiveDir "elm-service-$Version-$rid.tar.gz") -C $archive } | Out-Null
    foreach ($name in 'elm-service', 'LICENSE') {
        $inImage = Join-Path $added "app/$name"
        if (-not (Test-Path $inImage)) { $failures.Add("The image has no /app/$name."); continue }
        if ((Get-FileHash $inImage -Algorithm SHA256).Hash -ne (Get-FileHash (Join-Path $archive $name) -Algorithm SHA256).Hash) {
            $failures.Add("/app/$name is not the release archive's $name.")
        }
    }

    # Load this platform and check that the loaded image is the manifest's.
    "Docker: $((Invoke-Native 'docker version' { docker version --format '{{.Server.Version}} (API {{.Server.APIVersion}})' }) -join '')"
    $loadOutput = (Invoke-Native 'docker load' { docker load --platform "linux/$Arch" --input $ImageTar }) -join "`n"
    $loadOutput | Out-Host
    # The layout has no image name, so docker load names the image by its ID: the config digest with the classic image
    # store, the manifest's or the index's digest with the containerd image store.
    if ($loadOutput -notmatch 'Loaded image ID: (sha256:[0-9a-f]{64})') { throw 'docker load did not name the image it loaded.' }
    $id = $Matches[1]
    if (@($manifest.config.digest, $descriptor[0].digest, $digest) -notcontains $id) {
        $failures.Add("The loaded image $id is neither the config $($manifest.config.digest) nor the manifest of linux/$Arch.")
    }
    Invoke-Native 'docker tag' { docker tag $id $tag } | Out-Null
    $loadedConfig = (Invoke-Native 'docker image inspect' { docker image inspect --format '{{.Id}}' $tag }).Trim()
    "Loaded linux/$Arch as $id (image ID $loadedConfig)."

    # Run it with secrets as files readable by UID 1654.
    $elmArchive = Join-Path $work 'elm'
    New-Item -ItemType Directory -Path $elmArchive | Out-Null
    Invoke-Native 'tar' { tar -xzf (Join-Path $ArchiveDir "elm-$Version-$rid.tar.gz") -C $elmArchive } | Out-Null
    $elm = Join-Path $elmArchive 'elm'
    $config = Join-Path $work 'config'
    New-Item -ItemType Directory -Path $config | Out-Null
    Push-Location $config
    try {
        $passphrase = 'image test passphrase 0123'
        $env:ELM_KEY_PASSPHRASE = $passphrase
        Invoke-Native 'elm key root create' { & $elm key root create --out root.key.pem --public-out root.pub.pem } | Out-Null
        Invoke-Native 'elm key signing create' { & $elm key signing create --out signing.key.pem --public-out signing.pub.pem } | Out-Null
        Invoke-Native 'elm key certify' { & $elm key certify --root root.key.pem --public signing.pub.pem --use license lease --out signing.cert } | Out-Null
        Remove-Item root.key.pem
        Set-Content passphrase $passphrase -NoNewline
        Set-Content nonce ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))) -NoNewline
        Set-Content connection 'Data Source=/data/elm.db' -NoNewline
        $settings = [ordered]@{
            database = [ordered]@{ provider = 'sqlite'; connectionFile = '/config/connection' }
            signing = [ordered]@{ key = '/config/signing.key.pem'; certificates = @('/config/signing.cert'); passphraseFile = '/config/passphrase' }
            trust = [ordered]@{ rootKeys = @('/config/root.pub.pem') }
            nonce = [ordered]@{ secretFile = '/config/nonce' }
            bootstrapOwner = [ordered]@{ iss = 'https://idp.example'; sub = 'image-test' }
            node = [ordered]@{ name = 'image-test' }
        }
        $settings | ConvertTo-Json -Depth 4 | Set-Content elm-service.json
    }
    finally {
        Remove-Item Env:ELM_KEY_PASSPHRASE -ErrorAction SilentlyContinue
        Pop-Location
    }
    # The runner's user is not UID 1654: the files and the folder are made readable by everyone, in this throwaway folder.
    & chmod 0755 $config
    & chmod 0644 (Get-ChildItem $config | ForEach-Object FullName)

    Invoke-Native 'docker volume create' { docker volume create $volume } | Out-Null
    $common = @('-v', "${volume}:/data", '-v', "${config}:/config:ro", '-e', 'ELM_PUBLICURLS=https://elm.test/')
    # setup prints the key on its own line; its log lines come with it through 2>&1.
    $setupOutput = @(Invoke-Native 'setup' { docker run --rm @common $tag --config /config/elm-service.json setup } | ForEach-Object { $_.ToString().Trim() })
    if (-not ($setupOutput | Where-Object { $_ -match '^elm_' })) { $setupOutput | Out-Host; $failures.Add('setup did not print a key.') }

    Invoke-Native 'docker run' { docker run -d --name $container @common -p '127.0.0.1:18090:8080' $tag --config /config/elm-service.json } | Out-Null
    $ok = $false
    for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
        try { $ok = (Invoke-WebRequest 'http://127.0.0.1:18090/health' -SkipHttpErrorCheck).StatusCode -eq 200 } catch { }
        if (-not $ok) { Start-Sleep -Seconds 1 }
    }
    if (-not $ok) { $failures.Add('The service did not answer /health with 200.') }
    $uid = @(Invoke-Native 'docker top' { docker top $container -eo uid,comm } | Select-Object -Skip 1 | ForEach-Object { ($_.Trim() -split '\s+')[0] })
    if ($uid.Count -eq 0 -or @($uid | Where-Object { $_ -ne '1654' }).Count -gt 0) { $failures.Add("The service runs as UID $($uid -join ', '), not 1654.") }
    Invoke-Native 'docker stop' { docker stop $container } | Out-Null
    $exit = (Invoke-Native 'docker inspect' { docker inspect --format '{{.State.ExitCode}}' $container }).Trim()
    if ($exit -ne '0') { $failures.Add("docker stop ended the service with exit code $exit.") }
    $logs = (& docker logs $container 2>&1 | Out-String)
    if ($logs -match 'Overriding address') { $failures.Add('ASP.NET Core logged that it overrides the addresses.') }
    if ($failures.Count -gt 0) { $logs | Out-Host }

    $size = (Invoke-Native 'docker image inspect' { docker image inspect --format '{{.Size}}' $tag }).Trim()
    $compressed = ($manifest.layers | Measure-Object -Property size -Sum).Sum
    "linux/${Arch}: index $digest, $compressed bytes compressed, $size bytes uncompressed; ran as UID 1654, /health 200, stopped with 0."
}
finally {
    & docker rm -f $container 2>&1 | Out-Null
    & docker volume rm -f $volume 2>&1 | Out-Null
    & docker image rm -f $tag 2>&1 | Out-Null
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

foreach ($failure in $failures) { "FAILED: $failure" }
if ($failures.Count -gt 0) { throw "$($failures.Count) check(s) of the linux/$Arch image failed." }
exit 0
