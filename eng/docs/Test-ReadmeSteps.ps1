<#
.SYNOPSIS
    Runs the commands of README.md's quick steps with a given elm, line by line, in a new folder (design §20.7, V1c1).

.DESCRIPTION
    The steps are the lines of the fenced code block between the markers <!-- readme-steps:start --> and
    <!-- readme-steps:end --> in README.md. Each line starts with "elm"; the rest is split on spaces, and a double-quoted
    part is one argument. The keys get a passphrase made for this run through ELM_KEY_PASSPHRASE, as the README says.
    A failing line stops the run with exit code 1. Only the command's first two words are printed: its arguments are not.

.PARAMETER Elm
    The elm executable to run the steps with.

.PARAMETER Readme
    The README to read. Default: README.md at the repository root.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Elm,
    [string]$Readme = (Join-Path $PSScriptRoot '../../README.md')
)

$ErrorActionPreference = 'Stop'
$elmPath = (Resolve-Path $Elm).Path
$text = Get-Content (Resolve-Path $Readme).Path -Raw
$block = [regex]::Match($text, '(?s)<!-- readme-steps:start -->\s*```[a-z]*\r?\n(?<steps>.*?)```\s*<!-- readme-steps:end -->')
if (-not $block.Success) { throw "$Readme has no code block between <!-- readme-steps:start --> and <!-- readme-steps:end -->." }
$lines = @($block.Groups['steps'].Value -split '\r?\n' | Where-Object { $_.Trim() -ne '' })
if ($lines.Count -eq 0) { throw 'The README steps are empty.' }

$work = Join-Path ([IO.Path]::GetTempPath()) ('elm-readme-' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(6)))
New-Item -ItemType Directory $work | Out-Null
$savedPassphrase = $env:ELM_KEY_PASSPHRASE
$env:ELM_KEY_PASSPHRASE = 'readme ' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(8))
Push-Location $work
try {
    foreach ($line in $lines) {
        $words = @([regex]::Matches($line.Trim(), '"([^"]*)"|(\S+)') | ForEach-Object { $_.Groups[1].Success ? $_.Groups[1].Value : $_.Groups[2].Value })
        if ($words[0] -ne 'elm') { throw "A README step does not start with elm: $($words[0])" }
        [string[]]$arguments = @($words | Select-Object -Skip 1)
        $name = "elm $(($arguments | Select-Object -First 2) -join ' ')"
        & $elmPath @arguments | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "$name exited with $LASTEXITCODE." }
        "ok: $name"
    }
}
finally {
    Pop-Location
    $env:ELM_KEY_PASSPHRASE = $savedPassphrase
    Remove-Item -Recurse -Force $work
}
exit 0
