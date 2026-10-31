<#
.SYNOPSIS
    Exports the repository's SBOM from GitHub's dependency graph as SPDX JSON (design §20.5).

.DESCRIPTION
    Uses the asynchronous export: GET .../dependency-graph/sbom/generate-report answers 201 with sbom_url, and fetching
    that URL answers 202 while the report is made and 302 to a temporary download URL once it is ready
    (https://docs.github.com/en/rest/dependency-graph/sboms). The synchronous export closes on 2026-11-13.
    The SBOM is the dependency graph when it is exported (the default branch), not necessarily the tag's commit.
    Records how many NuGet packages it lists with a version, since central package management's Directory.Packages.props
    is not among the dependency graph's manifest files; a warning is written when there are none.

.PARAMETER Repository
    owner/name (github.repository).

.PARAMETER Token
    A token with read access to the repository (GITHUB_TOKEN).

.PARAMETER OutFile
    Where to write the SPDX JSON.

.PARAMETER TimeoutSeconds
    How long to wait for the report.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$Token,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
$headers = @{ Authorization = "Bearer $Token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }

$started = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/dependency-graph/sbom/generate-report" -Headers $headers
if (-not $started.sbom_url) { throw 'generate-report gave no sbom_url.' }

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$location = $null
while (-not $location) {
    $answer = Invoke-WebRequest -Uri $started.sbom_url -Headers $headers -MaximumRedirection 0 -SkipHttpErrorCheck
    switch ([int]$answer.StatusCode) {
        302 { $location = [string]$answer.Headers['Location'] }
        202 {
            if ([DateTimeOffset]::UtcNow -gt $deadline) { throw "The SBOM was not ready after $TimeoutSeconds seconds." }
            Start-Sleep -Seconds 10
        }
        default { throw "fetch-report answered $($answer.StatusCode)." }
    }
}

# The download URL is temporary and signed; it gets no authorization header.
Invoke-WebRequest -Uri $location -OutFile $OutFile
$sbom = Get-Content -Raw $OutFile | ConvertFrom-Json
$document = if ($sbom.sbom) { $sbom.sbom } else { $sbom }
$nuget = @($document.packages | Where-Object {
        $_.versionInfo -and @($_.externalRefs | Where-Object { $_.referenceLocator -like 'pkg:nuget/*' }).Count -gt 0
    })
"SBOM: $(@($document.packages).Count) packages, $($nuget.Count) NuGet packages with a version."
if ($nuget.Count -eq 0) { '::warning::The SBOM lists no NuGet package with a version (design §20.5).' }
