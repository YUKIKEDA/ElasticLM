<#
.SYNOPSIS
    Decides the release version and writes the release notes (design §20.5).

.DESCRIPTION
    - The version comes from VersionPrefix in Directory.Build.props.
    - On a tag push (-Ref refs/tags/v...), the tag without its "v" must be the prefix, or the prefix, a hyphen, and a suffix
      (rc.1). The suffix becomes VersionSuffix. Any other run (a pull request, workflow_dispatch) uses the suffix ci.<run>.
    - On a tag push, CHANGELOG.md must have a section for the version itself: from the line that starts with "## [<version>]"
      to the line before the next "## ". Its text, with a line naming the SQLite version of vendor/sqlite, is the release
      notes. Any other run takes the "[Unreleased]" section, which may be empty.
    - Writes version, suffix, and prerelease to $env:GITHUB_OUTPUT when it is set, and the notes to -NotesPath.
    Exits with 1 and a message on stderr when a check fails.

.PARAMETER Ref
    The Git ref of the run (github.ref).

.PARAMETER EventName
    The event of the run (github.event_name). Only "push" with a tag ref is a release.

.PARAMETER RunNumber
    The run number (github.run_number), for the suffix of runs that are not a release.

.PARAMETER Root
    The repository root. Defaults to two folders above this script.

.PARAMETER Changelog
    The changelog to read. Defaults to CHANGELOG.md in the root.

.PARAMETER NotesPath
    Where to write the release notes. Nothing is written when it is empty.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Ref,
    [Parameter(Mandatory = $true)][string]$EventName,
    [Parameter(Mandatory = $true)][string]$RunNumber,
    [string]$Root = (Join-Path $PSScriptRoot '../..'),
    [string]$Changelog = '',
    [string]$NotesPath = ''
)

$ErrorActionPreference = 'Stop'

function Stop-Release([string]$message) {
    [Console]::Error.WriteLine($message)
    exit 1
}

$props = Get-Content -Raw (Join-Path $Root 'Directory.Build.props')
if ($props -notmatch '<VersionPrefix>([0-9]+\.[0-9]+\.[0-9]+)</VersionPrefix>') {
    Stop-Release 'Directory.Build.props has no VersionPrefix of the form major.minor.patch.'
}
$prefix = $Matches[1]

$release = $EventName -eq 'push' -and $Ref.StartsWith('refs/tags/v')
if ($release) {
    $tagVersion = $Ref.Substring('refs/tags/v'.Length)
    if ($tagVersion -eq $prefix) {
        $suffix = ''
    }
    elseif ($tagVersion.StartsWith("$prefix-") -and $tagVersion.Substring($prefix.Length + 1) -match '^[0-9A-Za-z]+(\.[0-9A-Za-z]+)*$') {
        $suffix = $tagVersion.Substring($prefix.Length + 1)
    }
    else {
        Stop-Release "The tag v$tagVersion is not VersionPrefix $prefix or $prefix-<suffix>."
    }
}
else {
    if ($RunNumber -notmatch '^[0-9]+$') { Stop-Release "The run number '$RunNumber' is not a number." }
    $suffix = "ci.$RunNumber"
}

$version = if ($suffix) { "$prefix-$suffix" } else { $prefix }

# The section of the version (a release) or of [Unreleased] (any other run), up to the next "## " line.
if (-not $Changelog) { $Changelog = Join-Path $Root 'CHANGELOG.md' }
$heading = if ($release) { "## [$version]" } else { '## [Unreleased]' }
$lines = @(Get-Content $Changelog)
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i].StartsWith($heading) -and ($lines[$i].Length -eq $heading.Length -or $lines[$i][$heading.Length] -eq ' ')) {
        $start = $i
        break
    }
}
if ($start -lt 0 -and $release) { Stop-Release "CHANGELOG.md has no section that starts with '$heading'." }

$body = @()
if ($start -ge 0) {
    for ($i = $start + 1; $i -lt $lines.Count -and -not $lines[$i].StartsWith('## '); $i++) { $body += $lines[$i] }
}

$header = Get-Content -Raw (Join-Path $Root 'vendor/sqlite/sqlite3.h')
if ($header -notmatch '#define SQLITE_VERSION\s+"([^"]+)"') { Stop-Release 'vendor/sqlite/sqlite3.h has no SQLITE_VERSION.' }
$notes = (($body -join "`n").Trim() + "`n`nSQLite $($Matches[1]) is built in from vendor/sqlite (not listed in the SBOM).`n").TrimStart()

if ($NotesPath) { Set-Content -Path $NotesPath -Value $notes -NoNewline -Encoding utf8NoBOM }
if ($env:GITHUB_OUTPUT) {
    "version=$version" >> $env:GITHUB_OUTPUT
    "suffix=$suffix" >> $env:GITHUB_OUTPUT
    "prerelease=$(if ($suffix -and $release) { 'true' } else { 'false' })" >> $env:GITHUB_OUTPUT
}
"Version $version (suffix '$suffix', release $release)."
