<#
.SYNOPSIS
    Writes the SBOM of the commit being built as SPDX 2.3 JSON (design §20.5).

.DESCRIPTION
    Lists what the release is made from, at the commit of this run:
    - every NuGet package the shipped projects restore, with the version restore resolved, direct and transitive
      (dotnet list package --include-transitive --format json for elm, elm-server, elm-service, libelasticlm, and the two
      packages). Build-only packages such as the analyzers are listed too: they run in the build.
    - every npm package of web/admin/pnpm-lock.yaml (the admin UI that elm-service embeds, and its build tools);
    - SQLite, built in from the amalgamation of vendor/sqlite, with the version of its sqlite3.h.
    Each package has a purl. Licenses are not asserted (NOASSERTION). GitHub's dependency graph was tried first and listed
    the NuGet packages without versions, because central package management keeps them in Directory.Packages.props.
    Run it after a restore can reach nuget.org; it restores the projects itself.

.PARAMETER Version
    The release version (Get-ReleaseVersion.ps1).

.PARAMETER Commit
    The commit being built (github.sha).

.PARAMETER OutFile
    Where to write the SPDX JSON.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Commit,
    [Parameter(Mandatory = $true)][string]$OutFile
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function ConvertTo-SpdxId([string]$text) { 'SPDXRef-' + ($text -replace '[^A-Za-z0-9.-]', '-') }

$packages = [Collections.Generic.Dictionary[string, object]]::new()
function Add-Package([string]$kind, [string]$name, [string]$packageVersion, [string]$purl, [string]$download) {
    $id = ConvertTo-SpdxId "$kind-$name-$packageVersion"
    if ($packages.ContainsKey($id)) { return }
    $packages[$id] = [ordered]@{
        SPDXID = $id
        name = $name
        versionInfo = $packageVersion
        downloadLocation = $download
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'NOASSERTION'
        copyrightText = 'NOASSERTION'
        externalRefs = @([ordered]@{ referenceCategory = 'PACKAGE-MANAGER'; referenceType = 'purl'; referenceLocator = $purl })
    }
}

# NuGet: what restore resolved for the shipped projects.
foreach ($project in @('src/ElasticLM.Cli', 'src/ElasticLM.Server', 'src/ElasticLM.Service', 'src/ElasticLM.Native', 'src/ElasticLM.Licensing', 'src/ElasticLM.Client')) {
    $path = Join-Path $root $project
    & dotnet restore $path --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore $project failed." }
    $json = (& dotnet list $path package --include-transitive --format json) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "dotnet list package $project failed." }
    $listed = $json | ConvertFrom-Json
    foreach ($framework in @($listed.projects | ForEach-Object { $_.frameworks })) {
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            if (-not $package) { continue }
            $id = [string]$package.id
            $resolved = [string]$package.resolvedVersion
            Add-Package 'nuget' $id $resolved "pkg:nuget/$id@$resolved" "https://www.nuget.org/packages/$id/$resolved"
        }
    }
}

# npm: every package of the admin UI's lockfile. Keys under "packages:" are '<name>@<version>' (scoped names start with @).
$inPackages = $false
foreach ($line in Get-Content (Join-Path $root 'web/admin/pnpm-lock.yaml')) {
    if ($line -match '^[A-Za-z]') { $inPackages = $line -eq 'packages:'; continue }
    if ($inPackages -and $line -match "^  '?(@?[^@'\s]+)@([^'(\s:]+)'?:\s*$") {
        # PowerShell names ignore case: $npmVersion, not $version, which would overwrite -Version.
        $name = $Matches[1]
        $npmVersion = $Matches[2]
        $encoded = if ($name.StartsWith('@')) { '%40' + $name.Substring(1) } else { $name }
        Add-Package 'npm' $name $npmVersion "pkg:npm/$encoded@$npmVersion" "https://registry.npmjs.org/$name/-/$($name.Split('/')[-1])-$npmVersion.tgz"
    }
}

# SQLite, built in from vendor/sqlite.
$header = Get-Content -Raw (Join-Path $root 'vendor/sqlite/sqlite3.h')
if ($header -notmatch '#define SQLITE_VERSION\s+"([^"]+)"') { throw 'vendor/sqlite/sqlite3.h has no SQLITE_VERSION.' }
Add-Package 'generic' 'sqlite' $Matches[1] "pkg:generic/sqlite@$($Matches[1])" 'https://sqlite.org/download.html'

$rootId = 'SPDXRef-ElasticLM'
$document = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "elasticlm-$Version"
    documentNamespace = "https://github.com/YUKIKEDA/ElasticLM/spdx/$Version/$Commit"
    creationInfo = [ordered]@{
        created = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        creators = @('Tool: ElasticLM eng/release/New-Sbom.ps1')
    }
    packages = @(
        [ordered]@{
            SPDXID = $rootId
            name = 'ElasticLM'
            versionInfo = $Version
            downloadLocation = "git+https://github.com/YUKIKEDA/ElasticLM@$Commit"
            filesAnalyzed = $false
            licenseConcluded = 'MIT'
            licenseDeclared = 'MIT'
            copyrightText = 'NOASSERTION'
        }
    ) + @($packages.Values)
    relationships = @([ordered]@{ spdxElementId = 'SPDXRef-DOCUMENT'; relationshipType = 'DESCRIBES'; relatedSpdxElement = $rootId }) +
        @($packages.Keys | ForEach-Object { [ordered]@{ spdxElementId = $rootId; relationshipType = 'DEPENDS_ON'; relatedSpdxElement = $_ } })
}

$document | ConvertTo-Json -Depth 8 | Set-Content -Path $OutFile -Encoding utf8NoBOM
$nuget = @($packages.Keys | Where-Object { $_.StartsWith('SPDXRef-nuget-') }).Count
$npm = @($packages.Keys | Where-Object { $_.StartsWith('SPDXRef-npm-') }).Count
"SBOM: $nuget NuGet packages, $npm npm packages, and SQLite, for ElasticLM $Version at $Commit."
exit 0
