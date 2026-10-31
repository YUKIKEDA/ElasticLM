<#
.SYNOPSIS
    Packs the checked publish folders of one runtime identifier into the release archives (design §20.5).

.DESCRIPTION
    For elm, elm-server, elm-service, and libelasticlm, writes <name>-<version>-<rid>.zip (win-*) or .tar.gz (the others)
    with the binary and LICENSE (libelasticlm also gets include/elasticlm.h), and <name>-<version>-<rid>-symbols with the
    debug symbols. Run Test-Publish.ps1 first: this script packs what it finds and does not check the folders again.

.PARAMETER Rid
    The runtime identifier the folders were published for.

.PARAMETER Version
    The release version (Get-ReleaseVersion.ps1).

.PARAMETER PublishDir
    The folder with the elm, elm-server, elm-service, and libelasticlm subfolders.

.PARAMETER OutDir
    Where the archives go.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'linux-musl-x64', 'osx-arm64', 'osx-x64')]
    [string]$Rid,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$windows = $Rid.StartsWith('win-')
$extension = if ($windows) { 'zip' } else { 'tar.gz' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$staging = Join-Path ([IO.Path]::GetTempPath()) ("elm-pack-" + [Guid]::NewGuid().ToString('N'))

function New-Archive([string]$folder, [string]$archive) {
    if ($windows) {
        Compress-Archive -Path (Join-Path $folder '*') -DestinationPath $archive -CompressionLevel Optimal
    }
    else {
        # tar keeps the executable bit; -C makes the paths relative to the folder.
        & tar -czf $archive -C $folder .
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $archive." }
    }
}

try {
    foreach ($name in @('elm', 'elm-server', 'elm-service', 'libelasticlm')) {
        $folder = Join-Path $PublishDir $name
        $binary = if ($name -eq 'libelasticlm') {
            if ($windows) { 'elasticlm.dll' } elseif ($Rid.StartsWith('osx-')) { 'libelasticlm.dylib' } else { 'libelasticlm.so' }
        }
        elseif ($windows) { "$name.exe" }
        else { $name }

        $main = Join-Path $staging $name
        $symbols = Join-Path $staging "$name-symbols"
        New-Item -ItemType Directory -Force -Path $main, $symbols | Out-Null
        Copy-Item (Join-Path $folder $binary) $main
        Copy-Item (Join-Path $root 'LICENSE') $main
        if ($name -eq 'libelasticlm') { Copy-Item (Join-Path $root 'include/elasticlm.h') $main }
        Get-ChildItem -Path $folder -Force | Where-Object { $_.Name -ne $binary } | Copy-Item -Destination $symbols -Recurse

        New-Archive $main (Join-Path $OutDir "$name-$Version-$Rid.$extension")
        New-Archive $symbols (Join-Path $OutDir "$name-$Version-$Rid-symbols.$extension")
    }
}
finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}

Get-ChildItem $OutDir | ForEach-Object { "$($_.Name) $($_.Length)" }

# A native command's exit code (codesign, tar, a stopped process) would otherwise become the step's result.
exit 0
