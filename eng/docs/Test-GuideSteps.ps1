<#
.SYNOPSIS
    Runs the marked steps of README.md and of every Tutorial page with a given elm (design §20.7, V1c1 and V1c3a).

.DESCRIPTION
    A step is a fenced code block whose opening line comes right after one of these markers:

      <!-- steps:run -->               Each line is one command, run in order. A line starts with elm or dotnet and never
                                       with cd; the rest is split on spaces, and a double-quoted part is one argument.
                                       <repo> in an argument becomes the repository root, with / as the separator. elm is
                                       the executable given by -Elm.
      <!-- steps:file path=<path> -->  The block's content is written to that path, relative to the work folder.
      <!-- steps:expect -->            Each line must appear, in order and as a whole line, in the standard output of the
                                       last command of the steps:run block right before this block.
      <!-- steps:prebuilt name=elm --> The one-line block that publishes elm is not run: -Elm takes its place.

    Other comment lines, such as the sample comment of the code test (GuideSamplesTests), may stand above the marker. <repo>
    is replaced in steps:run lines only. A block without a marker is not run. Each page runs in a new work folder outside the clone, with NUGET_PACKAGES set to
    a new folder of its own (so that a package cache never stands in for the package the page packs) and
    ELM_KEY_PASSPHRASE set to a passphrase made for the run. Both folders are removed at the end, also on failure.

    The page's translation (README.ja.md, or the same file under docs/guide.ja/) must hold the content of each marked
    block of the English page, in the same order, as fenced code blocks of its own.

    A failing step stops the run with exit code 1. Only the page, the line, and the command's first two words are
    printed: arguments and output are not.

.PARAMETER Elm
    The elm executable to run the steps with.

.PARAMETER Page
    The pages to run. Default: README.md and docs/guide/tutorials/*.md at the repository root.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Elm,
    [string[]]$Page
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repoForSteps = $repoRoot.TrimEnd('\', '/').Replace('\', '/')
$elmPath = (Resolve-Path $Elm).Path
$dotnetPath = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$commandTimeout = [TimeSpan]::FromMinutes(10)

if (-not $Page) {
    $Page = @(Join-Path $repoRoot 'README.md') + @(Get-ChildItem (Join-Path $repoRoot 'docs/guide/tutorials') -Filter '*.md' -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object FullName)
}
# .NET resolves a relative path against the process's folder, not PowerShell's current location.
$Page = @($Page | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })

function Get-PageName([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    $relative = [IO.Path]::GetRelativePath($repoRoot, $full)
    return ($relative.StartsWith('..') ? $full : $relative).Replace('\', '/')
}

# The fenced code blocks of a Markdown file, each with the marker on the line right before its opening line (or none).
function Get-Block([string]$path) {
    $lines = [IO.File]::ReadAllLines($path)
    $blocks = [Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $open = [regex]::Match($lines[$i], '^ {0,3}(?<fence>`{3,}|~{3,})')
        if (-not $open.Success) { continue }
        $fence = $open.Groups['fence'].Value
        $close = '^ {0,3}' + [regex]::Escape($fence[0]) + '{' + $fence.Length + ',}\s*$'
        $marker = $i -gt 0 ? $lines[$i - 1].Trim() : ''
        $start = $i + 1
        for ($i = $start; $i -lt $lines.Length -and $lines[$i] -notmatch $close; $i++) { }
        if ($i -ge $lines.Length) { throw "$(Get-PageName $path):$($start): the code block is not closed." }
        $blocks.Add([pscustomobject]@{
            Marker = $marker -match '^<!--\s*steps:' ? $marker : $null
            Line = $start
            Lines = @(if ($i -gt $start) { $lines[$start..($i - 1)] })
        })
    }
    # A steps marker must stand right before a code block.
    for ($j = 0; $j -lt $lines.Length; $j++) {
        if ($lines[$j] -match '<!--\s*steps:' -and -not ($blocks | Where-Object { $_.Line -eq $j + 2 -and $_.Marker })) {
            throw "$(Get-PageName $path):$($j + 1): a steps marker must be the line right before a code block's opening line."
        }
    }
    return $blocks
}

function Split-Step([string]$line) {
    return @([regex]::Matches($line.Trim(), '"([^"]*)"|(\S+)') | ForEach-Object { $_.Groups[1].Success ? $_.Groups[1].Value : $_.Groups[2].Value })
}

# Runs one command in the work folder; returns its exit code and its standard output.
function Invoke-Step([string]$file, [string[]]$arguments, [string]$folder) {
    $info = [Diagnostics.ProcessStartInfo]::new($file)
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    $info.WorkingDirectory = $folder
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit([int]$commandTimeout.TotalMilliseconds)) {
            $process.Kill($true)
            return [pscustomobject]@{ ExitCode = 'a timeout'; Output = @() }
        }
        $process.WaitForExit()
        [void]$stderr.GetAwaiter().GetResult()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = @($stdout.GetAwaiter().GetResult() -split '\r?\n') }
    }
    finally {
        $process.Dispose()
    }
}

function Test-Translation([string]$path, [object[]]$blocks) {
    $relative = [IO.Path]::GetRelativePath($repoRoot, [IO.Path]::GetFullPath($path)).Replace('\', '/')
    $translated = $relative -eq 'README.md' ? 'README.ja.md' : ($relative -replace '^docs/guide/', 'docs/guide.ja/')
    if ($translated -eq $relative) { return }
    $translatedPath = Join-Path $repoRoot $translated
    if (-not (Test-Path $translatedPath)) { throw "$(Get-PageName $path) has no translation at $translated." }
    $theirs = @(Get-Block $translatedPath | ForEach-Object { $_.Lines -join "`n" })
    $next = 0
    foreach ($block in $blocks | Where-Object Marker) {
        $content = $block.Lines -join "`n"
        while ($next -lt $theirs.Count -and $theirs[$next] -cne $content) { $next++ }
        if ($next -ge $theirs.Count) { throw "$($translated): the code block of $(Get-PageName $path):$($block.Line) is missing, changed, or out of order." }
        $next++
    }
}

function Invoke-Page([string]$path) {
    $name = Get-PageName $path
    $blocks = @(Get-Block $path)
    if (-not ($blocks | Where-Object Marker)) { throw "$name has no steps." }
    Test-Translation $path $blocks

    $suffix = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(6))
    $work = Join-Path ([IO.Path]::GetTempPath()) "elm-steps-$suffix"
    $packages = Join-Path ([IO.Path]::GetTempPath()) "elm-steps-packages-$suffix"
    New-Item -ItemType Directory $work, $packages | Out-Null
    $saved = @{}
    foreach ($variable in 'ELM_KEY_PASSPHRASE', 'NUGET_PACKAGES', 'MSBUILDDISABLENODEREUSE', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO') {
        $saved[$variable] = [Environment]::GetEnvironmentVariable($variable)
    }
    $env:ELM_KEY_PASSPHRASE = 'steps ' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(8))
    $env:NUGET_PACKAGES = $packages
    # A build node that outlives the run would hold files in the work folder.
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    try {
        $lastOutput = $null
        $previous = $null
        foreach ($block in $blocks | Where-Object Marker) {
            $marker = [regex]::Match($block.Marker, '^<!--\s*steps:(?<kind>[a-z]+)(?:\s+(?<key>[a-z]+)=(?<value>\S+))?\s*-->$')
            $kind = $marker.Success ? $marker.Groups['kind'].Value : ''
            $key = $marker.Groups['key'].Value
            $value = $marker.Groups['value'].Value
            switch ($kind) {
                'run' {
                    if ($key) { throw "$($name):$($block.Line - 1): steps:run takes no attribute." }
                    $lineNumber = $block.Line
                    $ran = 0
                    foreach ($line in $block.Lines) {
                        $lineNumber++
                        if ($line.Trim() -eq '') { continue }
                        [string[]]$words = Split-Step $line
                        $shown = ($words | Select-Object -First 2) -join ' '
                        $file = switch -CaseSensitive ($words[0]) {
                            'elm' { $elmPath }
                            'dotnet' { $dotnetPath }
                            'cd' { throw "$($name):$($lineNumber): a step does not change the folder (cd); pass the folder as an argument." }
                            default { throw "$($name):$($lineNumber): a step starts with elm or dotnet, not $($words[0])." }
                        }
                        [string[]]$arguments = @($words | Select-Object -Skip 1 | ForEach-Object { $_.Replace('<repo>', $repoForSteps) })
                        $result = Invoke-Step $file $arguments $work
                        if ($result.ExitCode -ne 0) { throw "$($name):$($lineNumber): $shown exited with $($result.ExitCode)." }
                        "ok: $($name):$($lineNumber): $shown"
                        $lastOutput = $result.Output
                        $ran++
                    }
                    if ($ran -eq 0) { throw "$($name):$($block.Line): the steps:run block is empty." }
                }
                'file' {
                    if ($key -cne 'path' -or $value -eq '') { throw "$($name):$($block.Line - 1): steps:file takes path=<relative path>." }
                    if ([IO.Path]::IsPathRooted($value) -or $value.Contains('\') -or ($value -split '/') -contains '..') {
                        throw "$($name):$($block.Line - 1): the path of steps:file is relative to the work folder, with / and without '..'."
                    }
                    $target = Join-Path $work $value
                    New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
                    [IO.File]::WriteAllText($target, ($block.Lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
                    "ok: $($name):$($block.Line - 1): file $value"
                }
                'expect' {
                    if ($key) { throw "$($name):$($block.Line - 1): steps:expect takes no attribute." }
                    if ($previous -ne 'run') { throw "$($name):$($block.Line - 1): steps:expect must follow a steps:run block." }
                    $output = @($lastOutput | ForEach-Object { $_.TrimEnd() })
                    $at = 0
                    $lineNumber = $block.Line
                    foreach ($expected in $block.Lines) {
                        $lineNumber++
                        if ($expected.Trim() -eq '') { continue }
                        while ($at -lt $output.Count -and $output[$at] -cne $expected.TrimEnd()) { $at++ }
                        if ($at -ge $output.Count) { throw "$($name):$($lineNumber): the expected line is not in the output, or not in this order." }
                        $at++
                    }
                    "ok: $($name):$($block.Line - 1): expect"
                }
                'prebuilt' {
                    if ($key -cne 'name' -or $value -cne 'elm') { throw "$($name):$($block.Line - 1): steps:prebuilt takes name=elm." }
                    if (@($block.Lines | Where-Object { $_.Trim() -ne '' }).Count -ne 1) { throw "$($name):$($block.Line): the steps:prebuilt block has one line." }
                    "ok: $($name):$($block.Line - 1): prebuilt elm (-Elm)"
                }
                default { throw "$($name):$($block.Line - 1): unknown marker; use steps:run, steps:file, steps:expect, or steps:prebuilt." }
            }
            $previous = $kind
        }
    }
    finally {
        foreach ($variable in $saved.Keys) { [Environment]::SetEnvironmentVariable($variable, $saved[$variable]) }
        Remove-Item -Recurse -Force -LiteralPath $work, $packages -ErrorAction Continue
    }
}

try {
    foreach ($path in $Page) { Invoke-Page $path }
}
catch {
    Write-Host "::error::$($_.Exception.Message)"
    exit 1
}
exit 0
