<#
.SYNOPSIS
    Checks the contents of the ElasticLM.Licensing and ElasticLM.Client packages (design §20.5).

.DESCRIPTION
    For each package in -PackageDir:
    - the .nuspec's version is -Version, its license is the expression MIT, and it names a readme that is in the package
      and not empty;
    - its repository element has a commit (Source Link was on);
    - lib/net10.0/ holds the .dll and its .xml documentation;
    - the .snupkg holds the .pdb, and the .pdb's source paths start with /_/ (ContinuousIntegrationBuild was on, so the
      build is deterministic and has no path of the machine that built it).
    Fails at the end when any check failed, naming each.

.PARAMETER PackageDir
    The folder that holds the .nupkg and .snupkg files.

.PARAMETER Version
    The version the packages must have.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDir,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$failures = [Collections.Generic.List[string]]::new()

function Read-Entry([IO.Compression.ZipArchive]$zip, [string]$name) {
    $entry = $zip.GetEntry($name)
    if (-not $entry) { return $null }
    $stream = $entry.Open()
    try {
        $memory = [IO.MemoryStream]::new()
        $stream.CopyTo($memory)
        , $memory.ToArray()
    }
    finally { $stream.Dispose() }
}

foreach ($id in @('ElasticLM.Licensing', 'ElasticLM.Client')) {
    $nupkg = Join-Path $PackageDir "$id.$Version.nupkg"
    $snupkg = Join-Path $PackageDir "$id.$Version.snupkg"
    if (-not (Test-Path $nupkg)) { $failures.Add("$id.$Version.nupkg is missing."); continue }
    if (-not (Test-Path $snupkg)) { $failures.Add("$id.$Version.snupkg is missing."); continue }

    $zip = [IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
        $nuspecBytes = Read-Entry $zip "$id.nuspec"
        if (-not $nuspecBytes) { $failures.Add("$id has no $id.nuspec."); continue }
        # The .nuspec starts with a byte order mark, which [xml] does not accept in a string.
        [xml]$nuspec = [Text.Encoding]::UTF8.GetString($nuspecBytes).TrimStart([char]0xFEFF)
        $metadata = $nuspec.package.metadata
        if ($metadata.version -ne $Version) { $failures.Add("$id has version $($metadata.version), not $Version.") }
        if ($metadata.license.type -ne 'expression' -or $metadata.license.'#text' -ne 'MIT') { $failures.Add("$id's license is not the expression MIT.") }
        $readme = [string]$metadata.readme
        $readmeBytes = if ($readme) { Read-Entry $zip $readme } else { $null }
        if (-not $readmeBytes -or $readmeBytes.Length -eq 0) { $failures.Add("$id has no readme in the package.") }
        if (-not $metadata.repository.commit) { $failures.Add("$id's repository element has no commit (Source Link).") }
        foreach ($file in @("lib/net10.0/$id.dll", "lib/net10.0/$id.xml")) {
            if (-not $zip.GetEntry($file)) { $failures.Add("$id has no $file.") }
        }
    }
    finally { $zip.Dispose() }

    $symbols = [IO.Compression.ZipFile]::OpenRead($snupkg)
    try {
        $pdbBytes = Read-Entry $symbols "lib/net10.0/$id.pdb"
        if (-not $pdbBytes) { $failures.Add("$id.snupkg has no lib/net10.0/$id.pdb.") }
        else {
            $provider = [Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbImage([Collections.Immutable.ImmutableArray]::Create([byte[]]$pdbBytes))
            try {
                $reader = $provider.GetMetadataReader()
                $paths = @(foreach ($handle in $reader.Documents) { $reader.GetString($reader.GetDocument($handle).Name) })
                $local = @($paths | Where-Object { -not $_.StartsWith('/_/') })
                if ($paths.Count -eq 0) { $failures.Add("$id.pdb lists no source documents.") }
                elseif ($local.Count -gt 0) { $failures.Add("$id.pdb has source paths outside /_/, such as $($local[0]) (ContinuousIntegrationBuild).") }
            }
            finally { $provider.Dispose() }
        }
    }
    finally { $symbols.Dispose() }

    "Checked $id $Version."
}

foreach ($failure in $failures) { "FAILED: $failure" }
if ($failures.Count -gt 0) { throw "$($failures.Count) check(s) of the packages failed." }
