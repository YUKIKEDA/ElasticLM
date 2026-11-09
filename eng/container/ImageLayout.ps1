<#
.SYNOPSIS
    Functions that read the elm-service image's OCI layout and make a tar that docker load reads (design §20.6; §20.7,
    V1c4d2, review R8). Dot-sourced by Test-Image.ps1 and eng/docs/Test-GuideSteps.ps1, so that the conversion is in one
    place.

.DESCRIPTION
    Docker 28's classic image store does not read an OCI layout (it looks for docker's manifest.json), so the layout gets a
    manifest.json that names one platform's config and layers: the same blobs, nothing rebuilt (PR #170).
#>

# The JSON of a file in the layout.
function Read-LayoutJson([string]$path) { Get-Content -Raw -LiteralPath $path | ConvertFrom-Json }

# The path of a blob in the extracted layout. The digest comes from the layout's own index and manifests, so it is checked
# before it becomes part of a path.
function Get-LayoutBlob([string]$layout, [string]$digest) {
    Join-Path $layout (Get-LayoutPath $digest)
}

# The path of a blob relative to the layout, as docker's manifest.json names it, after the same check.
function Get-LayoutPath([string]$digest) {
    if ($digest -cnotmatch '^sha256:[0-9a-f]{64}$') { throw "The layout names a blob by $digest, which is not a SHA-256 digest." }
    'blobs/' + $digest.Replace(':', '/')
}

# The manifest of linux/<Arch> in an extracted layout, with its descriptor (Descriptor, Manifest). The layout's index.json
# names one image index of several platforms (Build-Image.ps1 without -Platform), or one manifest of a single platform
# (Build-Image.ps1 -Platform), whose config then names the platform.
function Get-PlatformManifest([string]$layout, [string]$Arch) {
    $outer = Read-LayoutJson (Join-Path $layout 'index.json')
    if (@($outer.manifests).Count -ne 1) { throw 'The layout does not name exactly one image.' }
    $top = $outer.manifests[0]
    if ($top.mediaType -eq 'application/vnd.oci.image.index.v1+json') {
        $imageIndex = Read-LayoutJson (Get-LayoutBlob $layout $top.digest)
        $descriptor = @($imageIndex.manifests | Where-Object { $_.platform.os -eq 'linux' -and $_.platform.architecture -eq $Arch })
        if ($descriptor.Count -ne 1) { throw "The image index has no single linux/$Arch manifest." }
        return [pscustomobject]@{ Descriptor = $descriptor[0]; Manifest = Read-LayoutJson (Get-LayoutBlob $layout $descriptor[0].digest) }
    }
    if ($top.mediaType -ne 'application/vnd.oci.image.manifest.v1+json') { throw "The layout names a $($top.mediaType), not an image index or manifest." }
    $manifest = Read-LayoutJson (Get-LayoutBlob $layout $top.digest)
    $config = Read-LayoutJson (Get-LayoutBlob $layout $manifest.config.digest)
    if ($config.os -ne 'linux' -or $config.architecture -ne $Arch) { throw "The layout's image is $($config.os)/$($config.architecture), not linux/$Arch." }
    return [pscustomobject]@{ Descriptor = $top; Manifest = $manifest }
}

# Writes the docker manifest.json of one platform's manifest into the extracted layout, named -Tag (name:tag), and packs
# the layout into -OutFile for docker load.
function New-DockerArchive([string]$layout, [object]$manifest, [string]$Tag, [string]$OutFile) {
    $dockerManifest = @(
        [ordered]@{
            Config = Get-LayoutPath $manifest.config.digest
            RepoTags = @($Tag)
            Layers = @($manifest.layers | ForEach-Object { Get-LayoutPath $_.digest })
        }
    )
    ConvertTo-Json -InputObject $dockerManifest -Depth 4 | Set-Content -LiteralPath (Join-Path $layout 'manifest.json') -Encoding utf8NoBOM
    & tar -cf $OutFile -C $layout .
    if ($LASTEXITCODE -ne 0) { throw "tar could not pack $OutFile." }
}
