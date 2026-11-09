<#
.SYNOPSIS
    Builds the elm-service container image as an OCI layout tar from the release archives (design §20.6), or, for a
    contributor, the image of one architecture as an OCI layout tar or a tar for docker load (design §20.7, V1c4d2, R15).

.DESCRIPTION
    - Takes elm-service and LICENSE out of elm-service-<version>-linux-x64.tar.gz and -linux-arm64.tar.gz in -ArchiveDir
      (only the one of -Platform when it is given), one folder for each architecture (amd64, arm64), and adds an empty data/:
      that is the whole build context.
    - Builds linux/amd64 and linux/arm64, or the one -Platform, with eng/container/Dockerfile and docker buildx, with no
      provenance and no SBOM (the release workflow attests the image itself), the times fixed to the commit's
      (SOURCE_DATE_EPOCH and rewrite-timestamp), and the labels and index annotations of design §20.6. A single platform
      has no image index, so the same values go to its manifest's annotations instead.
    - With -Format oci (the default), prints the digest of the single descriptor in the layout's index.json (the image
      index, or the manifest of the one -Platform), and writes it as digest to $env:GITHUB_OUTPUT when it is set. With
      -Format docker, writes a tar that docker load reads, with the image named elm-service, the name the pages of the user
      documentation use.
    It needs a buildx builder with the docker-container driver: the docker driver has no OCI exporter. The release workflow
    passes neither -Platform nor -Format.

.PARAMETER Version
    The release version.

.PARAMETER Revision
    The commit the image is built from.

.PARAMETER ArchiveDir
    The folder with the release archives of linux-x64 and linux-arm64, or of the one -Platform.

.PARAMETER OutFile
    The tar to write.

.PARAMETER Platform
    linux/amd64 or linux/arm64: builds that platform only. Default: both.

.PARAMETER Format
    oci (an OCI layout tar, the default) or docker (a tar for docker load, with one -Platform only).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Revision,
    [Parameter(Mandatory = $true)][string]$ArchiveDir,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [ValidateSet('linux/amd64', 'linux/arm64')][string]$Platform,
    [ValidateSet('oci', 'docker')][string]$Format = 'oci'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$context = Join-Path ([IO.Path]::GetTempPath()) ("elm-image-" + [Guid]::NewGuid().ToString('N'))
# docker load of a multi-platform tar takes no platform of the docker exporter's choice, so docker needs one platform.
if ($Format -eq 'docker' -and -not $Platform) { throw '-Format docker builds one platform: give -Platform.' }
# @() keeps one pair a list of one pair, not the pair's two strings.
$pairs = @(@(@('linux-x64', 'amd64'), @('linux-arm64', 'arm64')) | Where-Object { -not $Platform -or "linux/$($_[1])" -eq $Platform })
$platforms = ($pairs | ForEach-Object { "linux/$($_[1])" }) -join ','

try {
    foreach ($pair in $pairs) {
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
    $arguments = @('buildx', 'build', '--platform', $platforms, '--provenance=false', '--sbom=false', '--file', (Join-Path $root 'eng/container/Dockerfile'))
    $output = $Format -eq 'docker' ? "type=docker,name=elm-service,dest=$OutFile,rewrite-timestamp=true" : "type=oci,dest=$OutFile,rewrite-timestamp=true"
    $annotation = $Platform ? 'annotation-manifest' : 'annotation-index'
    foreach ($key in $values.Keys) {
        $arguments += @('--label', "$key=$($values[$key])")
        $output += ",$annotation.$key=$($values[$key])"
    }
    $arguments += @('--output', $output, $context)
    & docker @arguments
    if ($LASTEXITCODE -ne 0) { throw 'docker buildx build failed.' }
}
finally {
    Remove-Item -Recurse -Force $context -ErrorAction SilentlyContinue
}

if ($Format -eq 'docker') {
    "Built elm-service $Version ($Revision) for $Platform as a tar for docker load, named elm-service: $OutFile"
    exit 0
}

# The layout's index.json has one descriptor: the image index of both platforms, or the manifest of the one -Platform.
$expected = $Platform ? 'application/vnd.oci.image.manifest.v1+json' : 'application/vnd.oci.image.index.v1+json'
$index = (& tar -xOf $OutFile index.json) -join "`n" | ConvertFrom-Json
if (@($index.manifests).Count -ne 1 -or $index.manifests[0].mediaType -ne $expected) {
    throw "The layout does not hold exactly one $($Platform ? 'image manifest' : 'image index')."
}
$digest = $index.manifests[0].digest
if ($env:GITHUB_OUTPUT) { "digest=$digest" >> $env:GITHUB_OUTPUT }
"Built elm-service $Version ($Revision) for $($platforms.Replace(',', ' and ')): $digest"
exit 0
