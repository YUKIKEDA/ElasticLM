<#
.SYNOPSIS
    Runs the marked steps of README.md and of every Tutorial page with a given elm and elm-server (design §20.7, V1c1,
    V1c3a, and V1c3b).

.DESCRIPTION
    A step is a fenced code block whose opening line comes right after a marker: <!-- steps:<kind> key=value ... -->. A
    value is a word without spaces and double quotes, or a double-quoted string without double quotes. A key given twice,
    a key the kind does not take, and '-->' inside a value fail.

      steps:run                       Each line is one command, run in order. A line starts with elm, dotnet, or
                                      elm-server and never with cd; the rest is split on spaces, and a double-quoted part
                                      is one argument. Each line must exit with 0. Optional keys, about the block's last
                                      command:
                                        exit=<0-255>        the exit code it must end with instead of 0.
                                        capture=<name>      takes a value from its output. The name is lowercase words
                                                            joined by at least one hyphen (server-key), and a page
                                                            captures a name once.
                                        stream=stderr       takes the value from the standard error (default: stdout).
                                        pattern="<regex>"   the value is group 1 of the first match, so the pattern needs a
                                                            group; without a pattern, it is the whole stream, trimmed.
                                      stream and pattern need capture. A value must be one line of at most 256 characters
                                      of A-Za-z0-9._~- and must not be empty.
      steps:background ready=<url>    One line that starts with elm-server, started without waiting, its output read and
                                      dropped. The step is done when a GET of the ready URL (https on 127.0.0.1,
                                      localhost, or [::1], with the server's self-signed certificate accepted) answers
                                      200; the GETs come every 0.5 s with a 5 s limit each, for at most 60 s. A page has
                                      at most one. The process tree is stopped at the end of the page.
      steps:file path=<path>          The block's content is written to that path, relative to the work folder.
      steps:expect                    Each line must appear, in order and as a whole line, in the standard output of the
                                      last command of the steps:run block right before this block.
      steps:prebuilt name=<program>   The one-line block that publishes elm or elm-server is not run: -Elm or -ElmServer
                                      takes its place. An elm-server line needs this block for elm-server before it.

    Other comment lines, such as the sample comment of the code test (GuideSamplesTests), may stand above the marker. A
    block without a marker is not run. In each argument of a steps:run or steps:background line (after the split), <repo>
    becomes the repository root with / as the separator, and <name> becomes the value captured under that name earlier on
    the page; any other <name> left in a line fails. steps:file and steps:expect blocks are used as written.

    elm-server runs with -ElmServer and gets no environment variable whose name starts with ELM_ (in any case), so that a
    variable left in the shell cannot override the page's settings file. elm and dotnet get the environment as it is.

    Each page runs in a new work folder outside the clone, with NUGET_PACKAGES set to a new folder of its own (so that a
    package cache never stands in for the package the page packs) and ELM_KEY_PASSPHRASE set to a passphrase made for the
    run. Both folders are removed at the end, also on failure, after the background process has stopped.

    The page's translation (README.ja.md, or the same file under docs/guide.ja/) must hold the content of each marked
    block of the English page, in the same order, as fenced code blocks of its own.

    A failing step stops the run with exit code 1. Only the page, the line, and the command's first two words are
    printed: arguments, output, and captured values are not.

.PARAMETER Elm
    The elm executable to run the steps with.

.PARAMETER ElmServer
    The elm-server executable. A page with an elm-server line or a steps:prebuilt name=elm-server block fails without it.

.PARAMETER Page
    The pages to run. Default: README.md and docs/guide/tutorials/*.md at the repository root.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Elm,
    [string]$ElmServer,
    [string[]]$Page
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repoForSteps = $repoRoot.TrimEnd('\', '/').Replace('\', '/')
$elmPath = (Resolve-Path $Elm).Path
$elmServerPath = $ElmServer ? (Resolve-Path $ElmServer).Path : $null
$dotnetPath = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$commandTimeout = [TimeSpan]::FromMinutes(10)
$readyTimeout = [TimeSpan]::FromSeconds(60)
$probeTimeout = [TimeSpan]::FromSeconds(5)
$probeInterval = 500
$captureName = '[a-z][a-z0-9]*(?:-[a-z0-9]+)+'
$valueLimit = 256
$markerKeys = @{
    run = @('capture', 'stream', 'pattern', 'exit')
    background = @('ready')
    file = @('path')
    expect = @()
    prebuilt = @('name')
}

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

# The kind and the key=value attributes of a block's marker.
function Read-Marker([string]$name, [object]$block) {
    $at = "$($name):$($block.Line - 1)"
    $marker = [regex]::Match($block.Marker, '^<!--\s*steps:(?<kind>[a-z]+)(?<rest>.*?)\s*-->$')
    $kind = $marker.Groups['kind'].Value
    if (-not $marker.Success -or -not $markerKeys.ContainsKey($kind)) {
        throw "$($at): unknown marker; use steps:run, steps:background, steps:file, steps:expect, or steps:prebuilt."
    }
    $rest = $marker.Groups['rest'].Value
    if ($rest.Contains('-->')) { throw "$($at): a marker is one line, and a value cannot hold '-->'." }
    $attributes = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $token = [regex]'\G\s+(?<key>[a-z]+)=(?:"(?<quoted>[^"]*)"|(?<word>[^\s"]+))(?=\s|$)'
    $position = 0
    while ($position -lt $rest.Length) {
        $match = $token.Match($rest, $position)
        if (-not $match.Success) {
            if ($rest.Substring($position) -match '^\s+[a-z]+="[^"]*$') { throw "$($at): a quoted value is not closed." }
            throw "$($at): the marker's attributes are key=value, separated by spaces; a value is a word without spaces and quotes, or a quoted string without quotes."
        }
        $key = $match.Groups['key'].Value
        if ($attributes.ContainsKey($key)) { throw "$($at): $key is given twice." }
        if ($markerKeys[$kind] -cnotcontains $key) { throw "$($at): steps:$kind takes no $key." }
        $attributes[$key] = $match.Groups['quoted'].Success ? $match.Groups['quoted'].Value : $match.Groups['word'].Value
        $position += $match.Length
    }
    return [pscustomobject]@{ Kind = $kind; Attributes = $attributes }
}

function Split-Step([string]$line) {
    return @([regex]::Matches($line.Trim(), '"([^"]*)"|(\S+)') | ForEach-Object { $_.Groups[1].Success ? $_.Groups[1].Value : $_.Groups[2].Value })
}

# The commands of a steps:run or steps:background block, checked before anything runs.
function Read-Command([string]$name, [object]$block, [Collections.Generic.HashSet[string]]$captured, [Collections.Generic.HashSet[string]]$prebuilt) {
    $lineNumber = $block.Line
    foreach ($line in $block.Lines) {
        $lineNumber++
        if ($line.Trim() -eq '') { continue }
        [string[]]$words = Split-Step $line
        switch -CaseSensitive ($words[0]) {
            { $_ -in 'elm', 'dotnet' } { }
            'elm-server' {
                if (-not $prebuilt.Contains('elm-server')) { throw "$($name):$($lineNumber): an elm-server line needs a steps:prebuilt name=elm-server block before it." }
            }
            'cd' { throw "$($name):$($lineNumber): a step does not change the folder (cd); pass the folder as an argument." }
            default { throw "$($name):$($lineNumber): a step starts with elm, dotnet, or elm-server, not $($words[0])." }
        }
        foreach ($word in $words | Select-Object -Skip 1) {
            foreach ($placeholder in [regex]::Matches($word, "<($captureName)>")) {
                if (-not $captured.Contains($placeholder.Groups[1].Value)) {
                    throw "$($name):$($lineNumber): $($placeholder.Value) is not a value captured before this line."
                }
            }
        }
        [pscustomobject]@{ Line = $lineNumber; Words = $words; Shown = ($words | Select-Object -First 2) -join ' ' }
    }
}

# Checks every marker and line of a page before anything runs; returns the steps.
function Read-Plan([string]$name, [object[]]$blocks) {
    $captured = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $prebuilt = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $plan = [Collections.Generic.List[object]]::new()
    $previous = $null
    foreach ($block in $blocks | Where-Object Marker) {
        $marker = Read-Marker $name $block
        $attributes = $marker.Attributes
        $at = "$($name):$($block.Line - 1)"
        $step = [pscustomobject]@{ Kind = $marker.Kind; Block = $block; Attributes = $attributes; Commands = @(); Pattern = $null; Exit = 0; Ready = $null }
        switch ($marker.Kind) {
            'run' {
                $capture = $attributes.ContainsKey('capture') ? $attributes['capture'] : $null
                if (-not $capture -and ($attributes.ContainsKey('stream') -or $attributes.ContainsKey('pattern'))) {
                    throw "$($at): stream and pattern need capture."
                }
                if ($null -ne $capture) {
                    if ($capture -cnotmatch "^$captureName$" -or $capture -ceq 'repo') {
                        throw "$($at): a captured name is lowercase words joined by hyphens, such as server-key."
                    }
                    if ($captured.Contains($capture)) { throw "$($at): $capture is captured twice on the page." }
                }
                if ($attributes.ContainsKey('stream') -and $attributes['stream'] -cnotin 'stdout', 'stderr') { throw "$($at): stream is stdout or stderr." }
                if ($attributes.ContainsKey('pattern')) {
                    try {
                        $step.Pattern = [regex]::new($attributes['pattern'], [Text.RegularExpressions.RegexOptions]::CultureInvariant, [TimeSpan]::FromSeconds(1))
                    }
                    catch {
                        throw "$($at): the pattern is not a .NET regular expression."
                    }
                    if ($step.Pattern.GetGroupNumbers().Count -lt 2) { throw "$($at): the pattern has no group to take the value from." }
                }
                if ($attributes.ContainsKey('exit')) {
                    if ($attributes['exit'] -cnotmatch '^(0|[1-9][0-9]{0,2})$' -or [int]$attributes['exit'] -gt 255) { throw "$($at): exit is an integer from 0 to 255." }
                    $step.Exit = [int]$attributes['exit']
                }
                $step.Commands = @(Read-Command $name $block $captured $prebuilt)
                if ($step.Commands.Count -eq 0) { throw "$($name):$($block.Line): the steps:run block is empty." }
                if ($null -ne $capture) { [void]$captured.Add($capture) }
            }
            'background' {
                if (-not $attributes.ContainsKey('ready')) { throw "$($at): steps:background takes ready=<url>." }
                if ($plan | Where-Object Kind -EQ 'background') { throw "$($at): a page has at most one steps:background block." }
                $ready = $null
                if ($attributes['ready'] -cnotmatch '^https://(127\.0\.0\.1|localhost|\[::1\])([:/?#]|$)' -or
                    -not [Uri]::TryCreate($attributes['ready'], [UriKind]::Absolute, [ref]$ready) -or $ready.Host -cnotin '127.0.0.1', 'localhost', '[::1]') {
                    throw "$($at): ready is an https URL on 127.0.0.1, localhost, or [::1]."
                }
                $step.Ready = $ready
                $lines = @($block.Lines | Where-Object { $_.Trim() -ne '' })
                if ($lines.Count -ne 1) { throw "$($name):$($block.Line): the steps:background block has one line." }
                if ((Split-Step $lines[0])[0] -cne 'elm-server') { throw "$($name):$($block.Line + 1 + [array]::IndexOf($block.Lines, $lines[0])): a steps:background line starts with elm-server." }
                $step.Commands = @(Read-Command $name $block $captured $prebuilt)
            }
            'file' {
                $value = $attributes.ContainsKey('path') ? $attributes['path'] : ''
                if ($value -eq '') { throw "$($at): steps:file takes path=<relative path>." }
                if ([IO.Path]::IsPathRooted($value) -or $value.Contains('\') -or ($value -split '/') -contains '..') {
                    throw "$($at): the path of steps:file is relative to the work folder, with / and without '..'."
                }
            }
            'expect' {
                if ($previous -ne 'run') { throw "$($at): steps:expect must follow a steps:run block." }
            }
            'prebuilt' {
                if (-not $attributes.ContainsKey('name') -or $attributes['name'] -cnotin 'elm', 'elm-server') { throw "$($at): steps:prebuilt takes name=elm or name=elm-server." }
                if (@($block.Lines | Where-Object { $_.Trim() -ne '' }).Count -ne 1) { throw "$($name):$($block.Line): the steps:prebuilt block has one line." }
                [void]$prebuilt.Add($attributes['name'])
            }
        }
        $plan.Add($step)
        $previous = $marker.Kind
    }
    if ($plan.Count -eq 0) { throw "$name has no steps." }
    if (-not $elmServerPath -and $prebuilt.Contains('elm-server')) { throw "$name needs -ElmServer: it runs elm-server." }
    return $plan
}

# A process in the work folder. elm-server gets no ELM_ variable: it reads ELM_<KEY> before its settings file.
function New-StartInfo([string]$program, [string[]]$arguments, [string]$folder) {
    $file = switch -CaseSensitive ($program) { 'elm' { $elmPath } 'dotnet' { $dotnetPath } 'elm-server' { $elmServerPath } }
    $info = [Diagnostics.ProcessStartInfo]::new($file)
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    $info.WorkingDirectory = $folder
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    if ($program -ceq 'elm-server') {
        foreach ($variable in @($info.Environment.Keys)) {
            if ($variable.StartsWith('ELM_', [StringComparison]::OrdinalIgnoreCase)) { [void]$info.Environment.Remove($variable) }
        }
    }
    return $info
}

# Runs one command in the work folder; returns its exit code, its standard output, and its standard error.
function Invoke-Step([Diagnostics.ProcessStartInfo]$info) {
    $process = [Diagnostics.Process]::Start($info)
    try {
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit([int]$commandTimeout.TotalMilliseconds)) {
            $process.Kill($true)
            return [pscustomobject]@{ ExitCode = 'a timeout'; Output = ''; Error = '' }
        }
        $process.WaitForExit()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
    }
    finally {
        $process.Dispose()
    }
}

# Substitutes <repo> and the captured values in each argument (after the split, so a value never adds an argument).
function Resolve-Argument([string[]]$words, [hashtable]$values) {
    [string[]]$arguments = @($words | Select-Object -Skip 1 | ForEach-Object {
            $argument = $_.Replace('<repo>', $repoForSteps)
            foreach ($valueName in $values.Keys) { $argument = $argument.Replace("<$valueName>", $values[$valueName]) }
            $argument
        })
    return , $arguments
}

# The value a steps:run block captures from its last command. Error messages never hold the value.
function Get-CapturedValue([string]$at, [object]$step, [object]$result) {
    $stream = $step.Attributes.ContainsKey('stream') ? $step.Attributes['stream'] : 'stdout'
    $text = $stream -ceq 'stderr' ? $result.Error : $result.Output
    if ($step.Pattern) {
        try { $match = $step.Pattern.Match($text) }
        catch [Text.RegularExpressions.RegexMatchTimeoutException] { throw "$($at): the pattern took more than 1 s on the $stream." }
        catch { if ($_.Exception.InnerException -is [Text.RegularExpressions.RegexMatchTimeoutException]) { throw "$($at): the pattern took more than 1 s on the $stream." } else { throw } }
        if (-not $match.Success) { throw "$($at): the pattern does not match the $stream." }
        $value = $match.Groups[1].Value
    }
    else {
        $value = $text.Trim()
    }
    if ($value -eq '') { throw "$($at): the captured value is empty." }
    if ($value.Length -gt $valueLimit) { throw "$($at): the captured value is longer than $valueLimit characters." }
    if ($value.Contains("`n") -or $value.Contains("`r")) { throw "$($at): the captured value is more than one line." }
    if ($value -cnotmatch '^[A-Za-z0-9._~-]+$') { throw "$($at): the captured value holds a character other than A-Za-z0-9._~-." }
    return $value
}

# Starts the background command and waits until its ready URL answers 200.
function Start-Background([string]$name, [object]$step, [hashtable]$values, [string]$folder) {
    $command = $step.Commands[0]
    $at = "$($name):$($command.Line)"
    $info = New-StartInfo $command.Words[0] (Resolve-Argument $command.Words $values) $folder
    try { $process = [Diagnostics.Process]::Start($info) }
    catch { throw "$($at): $($command.Shown) could not be started." }
    # Set before anything else can fail, so that the end of the page stops the process.
    $script:background = $process
    $process.StandardInput.Close()
    # Read and drop the output, so that a full pipe never stops the server.
    [void]$process.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null)
    [void]$process.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
    $handler = [Net.Http.HttpClientHandler]::new()
    # Only this probe accepts the server's self-signed certificate; the clients check the server's key against its license
    # (design §13.5).
    $handler.ServerCertificateCustomValidationCallback = [Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = $probeTimeout
    try {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            if ($process.HasExited) { throw "$($at): $($command.Shown) exited with $($process.ExitCode) before it was ready." }
            if ($clock.Elapsed -ge $readyTimeout) { throw "$($at): $($command.Shown) was not ready within $($readyTimeout.TotalSeconds) s." }
            $status = 0
            try {
                $response = $client.GetAsync($step.Ready).GetAwaiter().GetResult()
                $status = [int]$response.StatusCode
                $response.Dispose()
            }
            catch {
                # Not listening yet, or no answer within the probe's limit.
            }
            if ($status -eq 200) { break }
            Start-Sleep -Milliseconds $probeInterval
        }
    }
    finally {
        $client.Dispose()
    }
    "ok: $($at): $($command.Shown) (background, process $($process.Id))"
}

# Stops the background process tree and waits for it to end.
function Stop-Background {
    if (-not $script:background) { return }
    try {
        try { $script:background.Kill($true) } catch [InvalidOperationException] { }
        $script:background.WaitForExit()
    }
    finally {
        $script:background.Dispose()
        $script:background = $null
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
    $plan = Read-Plan $name $blocks
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
        $values = @{}
        foreach ($step in $plan) {
            $block = $step.Block
            switch ($step.Kind) {
                'run' {
                    $last = $step.Commands[-1]
                    foreach ($command in $step.Commands) {
                        $expected = $command.Line -eq $last.Line ? $step.Exit : 0
                        try { $result = Invoke-Step (New-StartInfo $command.Words[0] (Resolve-Argument $command.Words $values) $work) }
                        catch { throw "$($name):$($command.Line): $($command.Shown) could not be run." }
                        if ($result.ExitCode -ne $expected) {
                            throw "$($name):$($command.Line): $($command.Shown) exited with $($result.ExitCode)$($expected -ne 0 ? ", not $expected" : '')."
                        }
                        "ok: $($name):$($command.Line): $($command.Shown)$($expected -ne 0 ? " (exit $expected)" : '')"
                        $lastOutput = $result.Output
                    }
                    if ($step.Attributes.ContainsKey('capture')) {
                        $capture = $step.Attributes['capture']
                        $values[$capture] = Get-CapturedValue "$($name):$($last.Line)" $step $result
                        "ok: $($name):$($last.Line): captured $capture"
                    }
                }
                'background' {
                    Start-Background $name $step $values $work
                }
                'file' {
                    $value = $step.Attributes['path']
                    $target = Join-Path $work $value
                    New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
                    [IO.File]::WriteAllText($target, ($block.Lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
                    "ok: $($name):$($block.Line - 1): file $value"
                }
                'expect' {
                    $output = @($lastOutput -split '\r?\n' | ForEach-Object { $_.TrimEnd() })
                    $at = 0
                    $lineNumber = $block.Line
                    foreach ($expectedLine in $block.Lines) {
                        $lineNumber++
                        if ($expectedLine.Trim() -eq '') { continue }
                        while ($at -lt $output.Count -and $output[$at] -cne $expectedLine.TrimEnd()) { $at++ }
                        if ($at -ge $output.Count) { throw "$($name):$($lineNumber): the expected line is not in the output, or not in this order." }
                        $at++
                    }
                    "ok: $($name):$($block.Line - 1): expect"
                }
                'prebuilt' {
                    $program = $step.Attributes['name']
                    "ok: $($name):$($block.Line - 1): prebuilt $program ($($program -ceq 'elm' ? '-Elm' : '-ElmServer'))"
                }
            }
        }
    }
    finally {
        Stop-Background
        foreach ($variable in $saved.Keys) { [Environment]::SetEnvironmentVariable($variable, $saved[$variable]) }
        Remove-Item -Recurse -Force -LiteralPath $work, $packages -ErrorAction Continue
    }
}

$script:background = $null
try {
    foreach ($path in $Page) { Invoke-Page $path }
}
catch {
    Write-Host "::error::$($_.Exception.Message)"
    exit 1
}
exit 0
