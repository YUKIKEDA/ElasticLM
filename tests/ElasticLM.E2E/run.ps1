<#
.SYNOPSIS
    Runs the end-to-end tests against the published elm, elm-server, and elm-service (design §20.7).

.DESCRIPTION
    - Publishes the four for this machine with eng/release/Publish-Rid.ps1 into a temporary folder (-Publish), or takes the
      -OutDir of an earlier Publish-Rid.ps1 (-Bin).
    - Runs tests/ElasticLM.E2E with ELM_E2E_BIN set to that folder. ELM_E2E_POSTGRES, when the caller sets it, adds the
      PostgreSQL runs of the service's flows.
    - Prints the time the tests took and their real-clock waits (at most 2 minutes, which the tests check), and adds both to
      the job summary on GitHub Actions.
    Everything it makes goes in a temporary folder that it removes at the end (.cursor/rules/scratch.mdc); the tests remove
    their own folders, processes, and databases.

.PARAMETER Bin
    The -OutDir of eng/release/Publish-Rid.ps1: the folders elm, elm-server, and elm-service.

.PARAMETER Publish
    Publish for this machine first, instead of taking -Bin. elm-service needs web/admin/dist (design §15.2).

.PARAMETER Configuration
    The configuration the test project is built in.
#>
[CmdletBinding()]
param(
    [string]$Bin,
    [switch]$Publish,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('elm-e2e-run-' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(6)))
New-Item -ItemType Directory $work | Out-Null
$savedBin = $env:ELM_E2E_BIN
$savedWaited = $env:ELM_E2E_WAITED

try {
    if ($Publish) {
        $arm64 = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64'
        $rid = if ($IsWindows) { $arm64 ? 'win-arm64' : 'win-x64' }
        elseif ($IsMacOS) { $arm64 ? 'osx-arm64' : 'osx-x64' }
        else { $arm64 ? 'linux-arm64' : 'linux-x64' }
        if ($IsWindows) {
            # The NativeAOT linker step finds Visual Studio with vswhere.
            $env:PATH = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer') + [IO.Path]::PathSeparator + $env:PATH
        }
        $Bin = Join-Path $work 'publish'
        & (Join-Path $root 'eng/release/Publish-Rid.ps1') -Rid $rid -OutDir $Bin -LogDir (Join-Path $work 'logs') | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
    }
    if (-not $Bin) { throw 'Pass -Bin or -Publish.' }

    $env:ELM_E2E_BIN = (Resolve-Path $Bin).Path
    $env:ELM_E2E_WAITED = Join-Path $work 'waited.txt'
    $started = [DateTimeOffset]::UtcNow
    dotnet test --project (Join-Path $PSScriptRoot 'ElasticLM.E2E.csproj') -c $Configuration | Out-Host
    $code = $LASTEXITCODE
    $took = [DateTimeOffset]::UtcNow - $started
    $waited = (Test-Path $env:ELM_E2E_WAITED) ? (Get-Content $env:ELM_E2E_WAITED -Raw).Trim() : 'unknown'
    $engines = $env:ELM_E2E_POSTGRES ? 'SQLite and PostgreSQL' : 'SQLite'
    $line = "End-to-end tests ($engines): $([Math]::Round($took.TotalSeconds)) s, of which real-clock waits $waited s (budget 120 s)."
    $line
    if ($env:GITHUB_STEP_SUMMARY) { "- $line" | Add-Content $env:GITHUB_STEP_SUMMARY }
    if ($code -ne 0) { throw "The end-to-end tests failed with exit code $code." }
}
finally {
    $env:ELM_E2E_BIN = $savedBin
    $env:ELM_E2E_WAITED = $savedWaited
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
