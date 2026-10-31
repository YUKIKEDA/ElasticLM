<#
.SYNOPSIS
    Checks that Get-ReleaseVersion.ps1 refuses what it must refuse (design §20.5).

.DESCRIPTION
    Runs Get-ReleaseVersion.ps1 in a child process with:
    - a tag of another version than VersionPrefix: exit 1;
    - a tag of VersionPrefix and a CHANGELOG.md with no section for it: exit 1;
    - a tag of VersionPrefix-rc.1 and a CHANGELOG.md that has only the section of VersionPrefix: exit 1;
    - a tag of VersionPrefix and a CHANGELOG.md with its section (dated, as Keep a Changelog writes it): exit 0, and the
      notes are the section's text and the SQLite line, without the next section;
    - a pull request: exit 0, with the suffix ci.<run>.
    The changelogs are made in a temporary folder that is removed at the end.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1'
$root = Join-Path $PSScriptRoot '../..'
$props = Get-Content -Raw (Join-Path $root 'Directory.Build.props')
if ($props -notmatch '<VersionPrefix>([^<]+)</VersionPrefix>') { throw 'No VersionPrefix.' }
$prefix = $Matches[1]

$work = Join-Path ([IO.Path]::GetTempPath()) ("elm-release-test-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$failures = 0

function Invoke-Case([string]$name, [string]$ref, [string]$event, [string]$changelog, [int]$expected, [scriptblock]$check) {
    $file = Join-Path $work 'CHANGELOG.md'
    Set-Content -Path $file -Value $changelog -Encoding utf8NoBOM
    $notes = Join-Path $work 'notes.md'
    Remove-Item $notes -ErrorAction SilentlyContinue
    $savedOutput = $env:GITHUB_OUTPUT
    $env:GITHUB_OUTPUT = Join-Path $work 'output.txt'
    Remove-Item $env:GITHUB_OUTPUT -ErrorAction SilentlyContinue
    try {
        $out = & pwsh -NoProfile -NonInteractive -File $script -Ref $ref -EventName $event -RunNumber 42 -Root $root -Changelog $file -NotesPath $notes 2>&1
        $code = $LASTEXITCODE
        $ok = $code -eq $expected
        if ($ok -and $check) { $ok = & $check $notes $env:GITHUB_OUTPUT }
    }
    finally {
        $env:GITHUB_OUTPUT = $savedOutput
    }
    if ($ok) { "ok   $name" }
    else {
        "FAIL $name (exit $code, expected $expected)"
        $out | ForEach-Object { "     $_" }
        $script:failures++
    }
}

try {
    $sections = "# Changelog`n`n## [Unreleased]`n`n- Next.`n`n## [$prefix] - 2026-01-01`n`n### Added`n`n- First.`n`n## [0.0.1] - 2025-01-01`n`n- Old.`n"
    Invoke-Case 'a tag of another version is refused' 'refs/tags/v99.0.0' 'push' $sections 1 $null
    Invoke-Case 'a tag with no section is refused' "refs/tags/v$prefix" 'push' "# Changelog`n`n## [Unreleased]`n" 1 $null
    Invoke-Case 'a prerelease tag needs its own section' "refs/tags/v$prefix-rc.1" 'push' $sections 1 $null
    Invoke-Case 'a tag with its section passes' "refs/tags/v$prefix" 'push' $sections 0 {
        param($notes, $output)
        $text = Get-Content -Raw $notes
        $values = Get-Content -Raw $output
        $text.Contains('- First.') -and -not $text.Contains('- Old.') -and -not $text.Contains('- Next.') -and $text.Contains('SQLite ') -and
            $values -match "(?m)^version=$([regex]::Escape($prefix))\r?$" -and $values -match '(?m)^prerelease=false\r?$'
    }
    Invoke-Case 'a pull request gets the ci suffix' 'refs/pull/7/merge' 'pull_request' $sections 0 {
        param($notes, $output)
        (Get-Content -Raw $output).Contains("version=$prefix-ci.42") -and (Get-Content -Raw $notes).Contains('- Next.')
    }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

if ($failures -gt 0) { throw "$failures case(s) of Get-ReleaseVersion.ps1 failed." }
'Get-ReleaseVersion.ps1 refuses what it must refuse.'
