<#
.SYNOPSIS
    Builds the elm-service container image as an OCI layout tar from the release archives (design §20.6).

.DESCRIPTION
    - Takes elm-service and LICENSE out of elm-service-<version>-linux-x64.tar.gz and -linux-arm64.tar.gz in -ArchiveDir,
      one folder for each architecture (amd64, arm64), and adds an empty data/: that is the whole build context.
    - Builds linux/amd64 and linux/arm64 with eng/container/Dockerfile and docker buildx, with no provenance and no SBOM
      (the release workflow attests the image itself), the times fixed to the commit's (SOURCE_DATE_EPOCH and
      rewrite-timestamp), and the labels and index annotations of design §20.6.
    - Prints the index digest, the digest of the single descriptor in the layout's index.json, and writes it as digest to
      $env:GITHUB_OUTPUT when it is set.
    It needs a buildx builder with the docker-container driver: the docker driver has no OCI exporter.

.PARAMETER Version
    The release version.

.PARAMETER Revision
    The commit the image is built from.

.PARAMETER ArchiveDir
    The folder with the release archives of linux-x64 and linux-arm64.

.PARAMETER OutFile
    The OCI layout tar to write.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Revision,
    [Parameter(Mandatory = $true)][string]$ArchiveDir,
    [Parameter(Mandatory = $true)][string]$OutFile
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$context = Join-Path ([IO.Path]::GetTempPath()) ("elm-image-" + [Guid]::NewGuid().ToString('N'))

try {
    foreach ($pair in @(@('linux-x64', 'amd64'), @('linux-arm64', 'arm64'))) {
        $archive = Join-Path $ArchiveDir "elm-service-$Version-$($pair[0]).tar.gz"
        if (-not (Test-Path $archive)) { throw "$archive is missing." }
        $folder = Join-Path $context $pair[1]
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
        & tar -xzf $archive -C $folder
        if ($LASTEXITCODE -ne 0) { throw "tar could not read $archive." }
        foreach ($name in 'elm-service', 'LICENSE') {
            if (-not (Test-Path (Join-Path $folder $name))) { throw "$archive has no $name." }
        }
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $context 'data') | Out-Null

    $env:SOURCE_DATE_EPOCH = (& git -C $root log -1 --format=%ct $Revision).Trim()
    if ($LASTEXITCODE -ne 0) { throw "git could not read the time of $Revision." }

    # design §20.6: the same values as labels of each platform's config and as annotations of the index, which GHCR reads
    # for a multi-platform image. None of them is trusted for a check (T26).
    $values = [ordered]@{
        'org.opencontainers.image.source' = 'https://github.com/YUKIKEDA/ElasticLM'
        'org.opencontainers.image.version' = $Version
        'org.opencontainers.image.revision' = $Revision
        'org.opencontainers.image.licenses' = 'MIT'
        'org.opencontainers.image.title' = 'elm-service'
        'org.opencontainers.image.description' = 'The ElasticLM hosted activation service'
    }
    $arguments = @('buildx', 'build', '--platform', 'linux/amd64,linux/arm64', '--provenance=false', '--sbom=false', '--file', (Join-Path $root 'eng/container/Dockerfile'))
    $output = "type=oci,dest=$OutFile,rewrite-timestamp=true"
    foreach ($key in $values.Keys) {
        $arguments += @('--label', "$key=$($values[$key])")
        $output += ",annotation-index.$key=$($values[$key])"
    }
    $arguments += @('--output', $output, $context)
    & docker @arguments
    if ($LASTEXITCODE -ne 0) { throw 'docker buildx build failed.' }
}
finally {
    Remove-Item -Recurse -Force $context -ErrorAction SilentlyContinue
}

# The layout's index.json has one descriptor: the image index of both platforms.
$index = (& tar -xOf $OutFile index.json) -join "`n" | ConvertFrom-Json
if (@($index.manifests).Count -ne 1 -or $index.manifests[0].mediaType -ne 'application/vnd.oci.image.index.v1+json') {
    throw 'The layout does not hold exactly one image index.'
}
$digest = $index.manifests[0].digest
if ($env:GITHUB_OUTPUT) { "digest=$digest" >> $env:GITHUB_OUTPUT }
"Built elm-service $Version ($Revision) for linux/amd64 and linux/arm64: $digest"
exit 0
