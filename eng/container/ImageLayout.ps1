<#
.SYNOPSIS
    Functions that read the elm-service image's OCI layout and make a tar that docker load reads (design §20.6; §20.7,
    V1c4d2, review R8). Dot-sourced by Test-Image.ps1 and eng/docs/Test-GuideSteps.ps1, so that the conversion is in one
    place.

.DESCRIPTION
    Docker's two image stores read the tar in two ways, so it carries the name for both: the same blobs, nothing rebuilt.
    - The classic image store (Docker 28.0.4 on the ubuntu-24.04 runners) does not read an OCI layout: it looks for docker's
      manifest.json, so the layout gets a manifest.json that names one platform's config and layers (PR #170).
    - The containerd image store (Docker 29.4.2 on the ubuntu-26.04 runners) reads a tar that holds oci-layout as an OCI
      layout and does not read manifest.json (containerd 2.2.3, core/images/archive/importer.go, ImportIndex). It names an
      image only from the annotation io.containerd.image.name of a descriptor in index.json (client/import.go), and
      docker load then prints "Loaded image ID:" for an image without one (moby docker-v29.4.2,
      daemon/containerd/image_exporter.go, LoadImage). So the descriptor in index.json gets that annotation (Issue #462).
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

# The manifest of linux/<Arch> in an extracted layout. The layout's index.json names one image index of several platforms
# (Build-Image.ps1 without -Platform), or one manifest of a single platform (Build-Image.ps1 -Platform), whose config then
# names the platform.
function Get-PlatformManifest([string]$layout, [string]$Arch) {
    $outer = Read-LayoutJson (Join-Path $layout 'index.json')
    if (@($outer.manifests).Count -ne 1) { throw 'The layout does not name exactly one image.' }
    $top = $outer.manifests[0]
    if ($top.mediaType -eq 'application/vnd.oci.image.index.v1+json') {
        $imageIndex = Read-LayoutJson (Get-LayoutBlob $layout $top.digest)
        $descriptor = @($imageIndex.manifests | Where-Object { $_.platform.os -eq 'linux' -and $_.platform.architecture -eq $Arch })
        if ($descriptor.Count -ne 1) { throw "The image index has no single linux/$Arch manifest." }
        return Read-LayoutJson (Get-LayoutBlob $layout $descriptor[0].digest)
    }
    if ($top.mediaType -ne 'application/vnd.oci.image.manifest.v1+json') { throw "The layout names a $($top.mediaType), not an image index or manifest." }
    $manifest = Read-LayoutJson (Get-LayoutBlob $layout $top.digest)
    $config = Read-LayoutJson (Get-LayoutBlob $layout $manifest.config.digest)
    if ($config.os -ne 'linux' -or $config.architecture -ne $Arch) { throw "The layout's image is $($config.os)/$($config.architecture), not linux/$Arch." }
    return $manifest
}

# Names the image of the extracted layout -Tag (name:tag, a name of one part without a registry) for both image stores, and
# packs the layout into -OutFile for docker load: a docker manifest.json of one platform's manifest for the classic image
# store, and the annotation io.containerd.image.name on the descriptor in index.json for the containerd image store, which
# takes the full name (docker.io/library/<name>:<tag>).
function New-DockerArchive([string]$layout, [object]$manifest, [string]$Tag, [string]$OutFile) {
    if ($Tag -cnotmatch '^[a-z0-9]+(?:[._-][a-z0-9]+)*:[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$') { throw "$Tag is not a name of one part with a tag." }
    $indexPath = Join-Path $layout 'index.json'
    $index = Read-LayoutJson $indexPath
    if (@($index.manifests).Count -ne 1) { throw 'The layout does not name exactly one image.' }
    $annotations = [ordered]@{}
    if ($index.manifests[0].PSObject.Properties['annotations']) {
        foreach ($annotation in $index.manifests[0].annotations.PSObject.Properties) { $annotations[$annotation.Name] = $annotation.Value }
    }
    $annotations['io.containerd.image.name'] = "docker.io/library/$Tag"
    $index.manifests[0] | Add-Member -NotePropertyName annotations -NotePropertyValue ([pscustomobject]$annotations) -Force
    ConvertTo-Json -InputObject $index -Depth 8 | Set-Content -LiteralPath $indexPath -Encoding utf8NoBOM
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
