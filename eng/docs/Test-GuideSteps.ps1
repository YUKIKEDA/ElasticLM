<#
.SYNOPSIS
    Runs the marked steps of README.md and of every Tutorial page with a given elm, elm-server, elm-service, and package
    folder, and builds and runs the C sample of Tutorial 4 with libelasticlm (design §20.7, V1c1, V1c3a, V1c3b, V1c3e, V1c3d,
    and V1c3c).

.DESCRIPTION
    A step is a fenced code block whose opening line comes right after a marker: <!-- steps:<kind> key=value ... -->. A
    value is a word without spaces and double quotes, or a double-quoted string without double quotes. A key given twice,
    a key the kind does not take, and '-->' inside a value fail. The one marker that stands on a line of its own, with no
    code block, is steps:prebuilt (below).

      steps:run                       Each line is one command, run in order. A line starts with elm, dotnet, elm-server,
                                      elm-service, cl, cc, xattr, or a program the page built, and never with cd; the
                                      rest is split on spaces, and a double-quoted part is one argument. Each line must
                                      exit with 0. Optional keys:
                                        os=<os>             windows, linux, or macos: the block runs on that system only
                                                            and is skipped on the others. It takes no capture, stream,
                                                            pattern, or env, and a steps:expect cannot follow it.
                                      and, about the block's last command:
                                        exit=<0-255>        the exit code it must end with instead of 0.
                                        capture=<name>      takes a value from its output. The name is lowercase words
                                                            joined by at least one hyphen (server-key), and a page
                                                            captures a name once.
                                        stream=stderr       takes the value from the standard error (default: stdout).
                                        pattern="<regex>"   the value is group 1 of the first match, so the pattern needs a
                                                            group; without a pattern, it is the whole stream, trimmed.
                                        env=ELM_API_KEY     the value goes into ELM_API_KEY of the page's later elm lines
                                                            only, never into a placeholder, a file, another program, or
                                                            this script's own environment. No other name is taken.
                                      stream, pattern, and env need capture. A value must be one line of at most 256
                                      characters of A-Za-z0-9._~- and must not be empty.
      steps:background ready=<url>    One line that starts with elm-server or elm-service, started without waiting, its
                                      output read and dropped. The step is done when a GET of the ready URL answers 200;
                                      the GETs come every 0.5 s with a 5 s limit each, for at most 60 s. The URL's host is
                                      127.0.0.1, localhost, or [::1]: https for elm-server, with the server's self-signed
                                      certificate accepted; https or http for elm-service. A page has at most one. The
                                      process tree is stopped at the end of the page.
      steps:file path=<path>          The block's content is written to that path, relative to the work folder, with each
                                      <name> captured earlier on the page replaced by its value. A <name> that the page
                                      captures later, or captures with env, fails before anything runs; any other <...>
                                      stays as written.
      steps:expect                    Each line must appear, in order and as a whole line, in the standard output of the
                                      last command of the steps:run block right before this block. A steps:prebuilt
                                      marker between them does not count, since it runs nothing.

    Other comment lines, such as the sample comment of the code test (GuideSamplesTests), may stand above the marker. A
    block without a marker is not run, and a steps marker inside a code block fails. In each argument of a steps:run or
    steps:background line (after the split), <name> becomes the value captured under that name earlier on the page; any
    other <name> left in a line, such as <repo>, fails before anything runs. steps:expect blocks are used as written. A
    name captured with env fills no placeholder: a line or a steps:file block that holds it fails.

    <!-- steps:prebuilt name=<name> --> stands on a line of its own, from the start of the line, after the paragraph that
    tells the reader to download what it names. It fails when the next line that is not blank is a code block's opening
    line, when it is indented, without name, with a name other than these, and when a page names the same name twice:
      elm           The page's elm lines run -Elm. An elm line needs no marker.
      elm-server    The page's elm-server lines run -ElmServer. An elm-server line needs this marker before it.
      elm-service   The page's elm-service lines run -ElmService. An elm-service line needs this marker before it.
      packages      Creates the folder packages in the work folder and copies every *.nupkg of -Feed into it (no
                    .snupkg). A dotnet add ... package or dotnet package add line needs this marker before it.
      libelasticlm  Creates the folder app in the work folder and puts in it the files of the libelasticlm archive: the
                    library of this system from -Library (elasticlm.dll and elasticlm.lib, libelasticlm.so, or
                    libelasticlm.dylib), and include/elasticlm.h and LICENSE from the repository. A cl or cc line needs
                    this marker before it.

    The C lines of Tutorial 4 (design §20.7, V1c3c):
      cl            In an os=windows block only. It runs the cl.exe of the MSVC x64 environment, which the script reads
                    once with eng/Get-MsvcEnvironment.ps1, as the reader's x64 Native Tools Command Prompt sets it up.
                    The line names the file it builds with /Fe:<path>.
      cc            In an os=linux or os=macos block only; the cc on the PATH. The line names the file it builds with
                    -o <path>.
      xattr         In an os=macos block only; the xattr on the PATH.
      <path>        A program that an earlier cl or cc line of the page builds, named by the same relative path with /
                    (and, for cl, without .exe), run by its full path in the work folder. No other file can be run.
    A whole word in single quotes is one argument without its quotes, in an os=linux or os=macos block only; a single quote
    anywhere else fails. The paths of /Fe:, -o, and a program are relative, with / and without '..', on every system.

    elm-server and elm-service run with -ElmServer and -ElmService and get no environment variable whose name starts with
    ELM_ (in any case), so that a variable left in the shell cannot override the page's settings file. The one exception
    is the elm-service of a steps:background block, which gets the run's passphrase as ELM_SIGNING_PASSPHRASE. cl, cc,
    xattr, and a program the page built get no ELM_ variable either; cl and the program get the MSVC environment on Windows,
    which this script's own environment never holds. elm and dotnet get the environment as it is, and elm lines also
    ELM_API_KEY once a block captured it with env.

    Each page runs in a new work folder outside the clone, with NUGET_PACKAGES set to a new folder of its own and
    ELM_KEY_PASSPHRASE set to a passphrase made for the run. The package folder is new for each page so that a package
    packed again under the same version never comes from an older copy in the global packages folder, and so that the
    job's package cache never holds the packages of one run. Both folders are removed at the end, also on failure, after
    the background process has stopped; a folder that is still there fails the page, so that keys and secrets are never
    left behind unnoticed.

    The page's translation (README.ja.md, or the same file under docs/guide.ja/) must hold the content of each marked
    block of the English page as fenced code blocks of its own, and each steps:prebuilt marker as a line of its own that
    is the same apart from the spaces around it, all in the same order.

    A failing step stops the run with exit code 1. Only the page, the line, and the command's first two words are
    printed: arguments, output, and captured values are not.

.PARAMETER Elm
    The elm executable to run the steps with.

.PARAMETER ElmServer
    The elm-server executable. A page with an elm-server line or a steps:prebuilt name=elm-server marker fails without it.

.PARAMETER ElmService
    The elm-service executable. A page with an elm-service line or a steps:prebuilt name=elm-service marker fails without
    it.

.PARAMETER Feed
    The folder of the ElasticLM packages (.nupkg) for steps:prebuilt name=packages. A page with that marker or a
    dotnet add ... package or dotnet package add line fails without it.

.PARAMETER Library
    The folder of the published libelasticlm for steps:prebuilt name=libelasticlm. A page with that marker fails without it.

.PARAMETER Page
    The pages to run. Default: README.md and docs/guide/tutorials/*.md at the repository root.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Elm,
    [string]$ElmServer,
    [string]$ElmService,
    [string]$Feed,
    [string]$Library,
    [string[]]$Page
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$elmPath = (Resolve-Path $Elm).Path
$elmServerPath = $ElmServer ? (Resolve-Path $ElmServer).Path : $null
$elmServicePath = $ElmService ? (Resolve-Path $ElmService).Path : $null
$feedPath = $Feed ? (Resolve-Path $Feed).Path : $null
$libraryPath = $Library ? (Resolve-Path $Library).Path : $null
$prebuiltNames = @('elm', 'elm-server', 'elm-service', 'packages', 'libelasticlm')
$osNames = @('windows', 'linux', 'macos')
$thisOs = $IsWindows ? 'windows' : ($IsMacOS ? 'macos' : 'linux')
# The files of the libelasticlm archive (eng/release/Pack-Rid.ps1, release.yml's "Check the libelasticlm zips"): the
# library of the OS from -Library, and the header and the license from the repository (design §20.7, V1c3c, review R1).
$libraryFiles = switch ($thisOs) { 'windows' { @('elasticlm.dll', 'elasticlm.lib') } 'linux' { @('libelasticlm.so') } 'macos' { @('libelasticlm.dylib') } }
# The OS that runs each new program: a cl line runs on Windows only, cc on Linux and macOS, xattr on macOS.
$programOs = @{ cl = @('windows'); cc = @('linux', 'macos'); xattr = @('macos') }
$apiKeyVariable = 'ELM_API_KEY'
$dotnetPath = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
$commandTimeout = [TimeSpan]::FromMinutes(10)
$readyTimeout = [TimeSpan]::FromSeconds(60)
$probeTimeout = [TimeSpan]::FromSeconds(5)
$probeInterval = 500
$captureName = '[a-z][a-z0-9]*(?:-[a-z0-9]+)+'
$valueLimit = 256
$markerKeys = @{
    run = @('capture', 'stream', 'pattern', 'exit', 'env', 'os')
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

$fenceOpen = '^ {0,3}(?<fence>`{3,}|~{3,})'
$prebuiltMarker = '<!--\s*steps:prebuilt(\s|-->)'

# The fenced code blocks of a Markdown file, each with the marker on the line right before its opening line (or none), and
# the steps:prebuilt markers, each on a line of its own. Each item has the line number of its marker (Order), so that the
# two lists can be merged in the page's order.
function Get-Page([string]$path) {
    $lines = [IO.File]::ReadAllLines($path)
    $blocks = [Collections.Generic.List[object]]::new()
    # The 0-based indexes of the opening lines, and of every line from an opening line to its closing line.
    $openings = [Collections.Generic.HashSet[int]]::new()
    $inBlock = [Collections.Generic.HashSet[int]]::new()
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $open = [regex]::Match($lines[$i], $fenceOpen)
        if (-not $open.Success) { continue }
        $fence = $open.Groups['fence'].Value
        $close = '^ {0,3}' + [regex]::Escape($fence[0]) + '{' + $fence.Length + ',}\s*$'
        $marker = $i -gt 0 ? $lines[$i - 1].Trim() : ''
        [void]$openings.Add($i)
        $start = $i + 1
        for ($i = $start; $i -lt $lines.Length -and $lines[$i] -notmatch $close; $i++) { }
        if ($i -ge $lines.Length) { throw "$(Get-PageName $path):$($start): the code block is not closed." }
        for ($k = $start - 1; $k -le $i; $k++) { [void]$inBlock.Add($k) }
        $blocks.Add([pscustomobject]@{
            Marker = ($marker -match '^<!--\s*steps:' -and $marker -notmatch $prebuiltMarker) ? $marker : $null
            Line = $start
            Order = $start - 1
            Lines = @(if ($i -gt $start) { $lines[$start..($i - 1)] })
        })
    }
    $prebuilt = [Collections.Generic.List[object]]::new()
    for ($j = 0; $j -lt $lines.Length; $j++) {
        # A marker's text inside a code block is neither a marker nor shown text a step can rely on: it fails, whatever its
        # kind, so that a page never runs differently from how it reads.
        if ($inBlock.Contains($j)) {
            if ($lines[$j] -match '<!--\s*steps:') { throw "$(Get-PageName $path):$($j + 1): a steps marker cannot stand inside a code block." }
            continue
        }
        if ($lines[$j] -match $prebuiltMarker) {
            # The next line that is not blank, as the check of a steps:prebuilt marker needs it; only an opening line
            # counts, never the closing line of a block.
            $next = $j + 1
            while ($next -lt $lines.Length -and $lines[$next].Trim() -eq '') { $next++ }
            $prebuilt.Add([pscustomobject]@{
                Text = $lines[$j]
                Order = $j + 1
                BeforeFence = $openings.Contains($next)
            })
        }
        # Any other steps marker must stand right before a code block.
        elseif ($lines[$j] -match '<!--\s*steps:' -and -not ($blocks | Where-Object { $_.Line -eq $j + 2 -and $_.Marker })) {
            throw "$(Get-PageName $path):$($j + 1): a steps marker must be the line right before a code block's opening line."
        }
    }
    return [pscustomobject]@{ Blocks = $blocks; Prebuilt = $prebuilt }
}

# The kind and the key=value attributes of a marker; $at names its page and line.
function Read-Marker([string]$at, [string]$text) {
    $marker = [regex]::Match($text, '^<!--\s*steps:(?<kind>[a-z]+)(?<rest>.*?)\s*-->$')
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

# The words of a line. A double-quoted part is one argument without its quotes, and so is a whole word in single quotes
# (Single is then true); any other word is taken as it is.
function Split-Word([string]$line) {
    return @([regex]::Matches($line.Trim(), '"(?<double>[^"]*)"|(?<=^|\s)''(?<single>[^'']*)''(?=\s|$)|(?<word>\S+)') | ForEach-Object {
            if ($_.Groups['double'].Success) { [pscustomobject]@{ Text = $_.Groups['double'].Value; Single = $false } }
            elseif ($_.Groups['single'].Success) { [pscustomobject]@{ Text = $_.Groups['single'].Value; Single = $true } }
            else { [pscustomobject]@{ Text = $_.Groups['word'].Value; Single = $false } }
        })
}

function Split-Step([string]$line) {
    return @(Split-Word $line | ForEach-Object Text)
}

# A path of a page that names a file in the work folder: relative, with / only, and without '..'. The check is the same on
# every OS, since [IO.Path]::IsPathRooted takes C:x and \x as relative on Linux and macOS (design §20.7, V1c3c, review R8).
function Test-RelativePath([string]$path) {
    return $path -ne '' -and $path -notmatch '^[A-Za-z]:' -and -not $path.StartsWith('/') -and -not $path.Contains('\') -and ($path -split '/') -notcontains '..'
}

# The file that a cl line (/Fe:<path>) or a cc line (-o <path>) builds; $null when the line names none.
function Get-BuiltFile([string]$program, [string[]]$words) {
    if ($program -ceq 'cl') {
        $output = @($words | Where-Object { $_ -clike '/Fe:*' })
        return $output.Count -eq 1 ? $output[0].Substring(4) : $null
    }
    $index = [array]::IndexOf($words, '-o')
    return ($index -gt 0 -and $index + 1 -lt $words.Count) ? $words[$index + 1] : $null
}

# The commands of a steps:run or steps:background block, checked before anything runs. $os is the block's os (or $null),
# and $built holds the files that the page's earlier cl and cc lines build.
function Read-Command([string]$name, [object]$block, [Collections.Generic.HashSet[string]]$captured, [Collections.Generic.HashSet[string]]$hidden, [Collections.Generic.HashSet[string]]$prebuilt, [string]$os, [Collections.Generic.HashSet[string]]$built) {
    $lineNumber = $block.Line
    foreach ($line in $block.Lines) {
        $lineNumber++
        if ($line.Trim() -eq '') { continue }
        $split = @(Split-Word $line)
        [string[]]$words = @($split | ForEach-Object Text)
        # A single quote groups a word as bash and PowerShell do, and is taken in the blocks of those shells only: on
        # Windows the page is typed in PowerShell too, but no Windows line needs it (design §20.7, V1c3c, review R9).
        foreach ($item in $split) {
            if ($item.Single -and $os -cnotin 'linux', 'macos') { throw "$($name):$($lineNumber): a single-quoted word is taken in an os=linux or os=macos block only." }
            if (-not $item.Single -and $item.Text.Contains("'")) { throw "$($name):$($lineNumber): a single quote encloses a whole word." }
        }
        $program = $words[0]
        if ($programOs.ContainsKey($program)) {
            if ($os -cnotin $programOs[$program]) { throw "$($name):$($lineNumber): $($program -ceq 'xattr' ? 'an' : 'a') $program line stands in a block with os=$($programOs[$program] -join ' or os=')." }
            if ($program -cin 'cl', 'cc') {
                if (-not $prebuilt.Contains('libelasticlm')) { throw "$($name):$($lineNumber): a $program line needs a steps:prebuilt name=libelasticlm marker before it." }
                $output = Get-BuiltFile $program $words
                if (-not $output -or -not (Test-RelativePath $output)) {
                    throw "$($name):$($lineNumber): a $program line builds a file in the work folder: $($program -ceq 'cl' ? '/Fe:<path>' : '-o <path>'), relative, with / and without '..'."
                }
                [void]$built.Add($output)
                # PowerShell runs app/app.exe as app/app too, and the page writes the shorter form on every OS.
                if ($program -ceq 'cl' -and $output.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) { [void]$built.Add($output.Substring(0, $output.Length - 4)) }
            }
            $kind = $program
        }
        elseif ($program.Contains('/') -or $program.Contains('\') -or $program -match '^[A-Za-z]:') {
            # A program that the page built: only a file that an earlier cl or cc line of the page builds, so that a page
            # cannot run any other file (design §20.7, V1c3c, reviews R2 and R8).
            if (-not (Test-RelativePath $program)) { throw "$($name):$($lineNumber): a program the page built is named by a relative path, with / and without '..'." }
            if (-not $built.Contains($program)) { throw "$($name):$($lineNumber): $program is not a file that an earlier cl or cc line of the page builds." }
            $kind = 'app'
        }
        else {
            $kind = $program
        }
        switch -CaseSensitive ($kind) {
            'cl' { }
            'cc' { }
            'xattr' { }
            'app' { }
            'elm' { }
            'dotnet' {
                # dotnet add <project> package <id>, dotnet add package <id> in the project's folder, and the newer
                # dotnet package add <id>.
                $addsPackage = $words.Count -gt 2 -and (
                    ($words[1] -ceq 'add' -and ($words[2] -ceq 'package' -or ($words.Count -gt 3 -and $words[3] -ceq 'package'))) -or
                    ($words[1] -ceq 'package' -and $words[2] -ceq 'add'))
                if ($addsPackage -and -not $prebuilt.Contains('packages')) {
                    throw "$($name):$($lineNumber): a dotnet add package or dotnet package add line needs a steps:prebuilt name=packages marker before it."
                }
            }
            'elm-server' {
                if (-not $prebuilt.Contains('elm-server')) { throw "$($name):$($lineNumber): an elm-server line needs a steps:prebuilt name=elm-server marker before it." }
            }
            'elm-service' {
                if (-not $prebuilt.Contains('elm-service')) { throw "$($name):$($lineNumber): an elm-service line needs a steps:prebuilt name=elm-service marker before it." }
            }
            'cd' { throw "$($name):$($lineNumber): a step does not change the folder (cd); pass the folder as an argument." }
            default { throw "$($name):$($lineNumber): a step starts with elm, dotnet, elm-server, elm-service, cl, cc, xattr, or a program the page built, not $($words[0])." }
        }
        # Any <name> in an argument, a single word such as <repo> or <RID> included, is a placeholder, and only a name
        # captured earlier on the page fills one; a name that cannot be captured is never filled.
        foreach ($word in $words | Select-Object -Skip 1) {
            foreach ($placeholder in [regex]::Matches($word, '<([A-Za-z][A-Za-z0-9._-]*)>')) {
                if ($hidden.Contains($placeholder.Groups[1].Value)) {
                    throw "$($name):$($lineNumber): $($placeholder.Value) was captured for $apiKeyVariable only and fills no placeholder."
                }
                if (-not $captured.Contains($placeholder.Groups[1].Value)) {
                    throw "$($name):$($lineNumber): $($placeholder.Value) is not a value captured before this line."
                }
            }
        }
        [pscustomobject]@{ Line = $lineNumber; Words = $words; Kind = $kind; Shown = ($words | Select-Object -First 2) -join ' ' }
    }
}

# The marked blocks and the steps:prebuilt markers of a page, in the page's order.
function Get-Marked([object]$parsed) {
    return @(@($parsed.Blocks | Where-Object Marker) + @($parsed.Prebuilt) | Sort-Object Order)
}

# Checks every marker and line of a page before anything runs; returns the steps.
function Read-Plan([string]$name, [object]$parsed) {
    $captured = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    # The names captured with env: their values go into ELM_API_KEY only, never into a placeholder.
    $hidden = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $prebuilt = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    # Every name a steps:run marker of the page captures, so that a steps:file block can tell a name captured later from
    # text such as a usage line's <data-folder>.
    $pageCaptures = @($parsed.Blocks | Where-Object Marker | ForEach-Object { [regex]::Match($_.Marker, '\scapture=([^\s"]+)').Groups[1].Value } | Where-Object { $_ })
    # The files that the page's cl and cc lines build, which are the only programs a line may run by path.
    $built = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $plan = [Collections.Generic.List[object]]::new()
    $previous = $null
    $previousOs = $null
    foreach ($block in Get-Marked $parsed) {
        $at = "$($name):$($block.Order)"
        if ($block.PSObject.Properties['Text']) {
            # A steps:prebuilt marker: a line of its own, from the start of the line, not attached to a code block.
            if ($block.Text -cne $block.Text.TrimStart()) { throw "$($at): a steps:prebuilt marker starts at the beginning of its line." }
            if ($block.BeforeFence) { throw "$($at): a steps:prebuilt marker stands on a line of its own; the next line that is not blank must not open a code block." }
            $marker = Read-Marker $at $block.Text.TrimEnd()
        }
        else {
            $marker = Read-Marker $at $block.Marker
        }
        $attributes = $marker.Attributes
        $step = [pscustomobject]@{ Kind = $marker.Kind; Block = $block; Attributes = $attributes; Commands = @(); Pattern = $null; Exit = 0; Ready = $null; Os = $null }
        switch ($marker.Kind) {
            'run' {
                if ($attributes.ContainsKey('os')) {
                    if ($attributes['os'] -cnotin $osNames) { throw "$($at): os is windows, linux, or macos." }
                    # A block that another OS skips would leave its captured name without a value (design §20.7, V1c3c,
                    # review R5).
                    if ($attributes.ContainsKey('capture') -or $attributes.ContainsKey('stream') -or $attributes.ContainsKey('pattern') -or $attributes.ContainsKey('env')) {
                        throw "$($at): a block with os takes no capture, stream, pattern, or env."
                    }
                    $step.Os = $attributes['os']
                }
                $capture = $attributes.ContainsKey('capture') ? $attributes['capture'] : $null
                if (-not $capture -and ($attributes.ContainsKey('stream') -or $attributes.ContainsKey('pattern') -or $attributes.ContainsKey('env'))) {
                    throw "$($at): stream, pattern, and env need capture."
                }
                if ($null -ne $capture) {
                    if ($capture -cnotmatch "^$captureName$") {
                        throw "$($at): a captured name is lowercase words joined by hyphens, such as server-key."
                    }
                    if ($captured.Contains($capture) -or $hidden.Contains($capture)) { throw "$($at): $capture is captured twice on the page." }
                }
                if ($attributes.ContainsKey('env') -and $attributes['env'] -cne $apiKeyVariable) { throw "$($at): env takes $apiKeyVariable only." }
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
                $step.Commands = @(Read-Command $name $block $captured $hidden $prebuilt $step.Os $built)
                if ($step.Commands.Count -eq 0) { throw "$($name):$($block.Line): the steps:run block is empty." }
                if ($null -ne $capture) {
                    if ($attributes.ContainsKey('env')) { [void]$hidden.Add($capture) } else { [void]$captured.Add($capture) }
                }
            }
            'background' {
                if (-not $attributes.ContainsKey('ready')) { throw "$($at): steps:background takes ready=<url>." }
                if ($plan | Where-Object Kind -EQ 'background') { throw "$($at): a page has at most one steps:background block." }
                $lines = @($block.Lines | Where-Object { $_.Trim() -ne '' })
                if ($lines.Count -ne 1) { throw "$($name):$($block.Line): the steps:background block has one line." }
                $program = (Split-Step $lines[0])[0]
                if ($program -cnotin 'elm-server', 'elm-service') { throw "$($name):$($block.Line + 1 + [array]::IndexOf($block.Lines, $lines[0])): a steps:background line starts with elm-server or elm-service." }
                # elm-server always serves https, with a certificate of its own key; elm-service serves http unless it is
                # given a certificate, and http on a loopback address is what its clients take outside production.
                $ready = $null
                $schemes = $program -ceq 'elm-service' ? 'https?' : 'https'
                if ($attributes['ready'] -cnotmatch "^${schemes}://(127\.0\.0\.1|localhost|\[::1\])([:/?#]|$)" -or
                    -not [Uri]::TryCreate($attributes['ready'], [UriKind]::Absolute, [ref]$ready) -or $ready.Host -cnotin '127.0.0.1', 'localhost', '[::1]') {
                    throw "$($at): ready of $program is $($program -ceq 'elm-service' ? 'an http or https' : 'an https') URL on 127.0.0.1, localhost, or [::1]."
                }
                $step.Ready = $ready
                $step.Commands = @(Read-Command $name $block $captured $hidden $prebuilt $null $built)
            }
            'file' {
                $value = $attributes.ContainsKey('path') ? $attributes['path'] : ''
                if ($value -eq '') { throw "$($at): steps:file takes path=<relative path>." }
                if ([IO.Path]::IsPathRooted($value) -or $value.Contains('\') -or ($value -split '/') -contains '..') {
                    throw "$($at): the path of steps:file is relative to the work folder, with / and without '..'."
                }
                # Only the names that the page captures are placeholders, so that a sample's usage line (<data-folder>),
                # <configuration>, or List<string> stay as written. A mistyped name stays too; the request that sends the
                # file then fails (design §20.7, V1c3d, the owner's decision D1).
                $lineNumber = $block.Line
                foreach ($line in $block.Lines) {
                    $lineNumber++
                    foreach ($placeholder in [regex]::Matches($line, "<($captureName)>")) {
                        if ($hidden.Contains($placeholder.Groups[1].Value)) {
                            throw "$($name):$($lineNumber): $($placeholder.Value) was captured for $apiKeyVariable only and fills no placeholder."
                        }
                        if (-not $captured.Contains($placeholder.Groups[1].Value) -and $pageCaptures -ccontains $placeholder.Groups[1].Value) {
                            throw "$($name):$($lineNumber): $($placeholder.Value) is captured only after this block."
                        }
                    }
                }
            }
            'expect' {
                if ($previous -ne 'run') { throw "$($at): steps:expect must follow a steps:run block." }
                # The block before it would be skipped on the other OSes, which would then check another block's output.
                if ($previousOs) { throw "$($at): steps:expect follows a steps:run block without os." }
            }
            'prebuilt' {
                if (-not $attributes.ContainsKey('name') -or $attributes['name'] -cnotin $prebuiltNames) { throw "$($at): steps:prebuilt takes name=elm, name=elm-server, name=elm-service, name=packages, or name=libelasticlm." }
                if (-not $prebuilt.Add($attributes['name'])) { throw "$($at): steps:prebuilt name=$($attributes['name']) is given twice on the page." }
            }
        }
        $plan.Add($step)
        # A steps:prebuilt marker runs nothing, so a steps:expect after it still checks the steps:run block before it.
        if ($marker.Kind -cne 'prebuilt') { $previous = $marker.Kind; $previousOs = $step.Os }
    }
    if ($plan.Count -eq 0) { throw "$name has no steps." }
    if (-not $libraryPath -and $prebuilt.Contains('libelasticlm')) { throw "$name needs -Library: it puts libelasticlm in the folder app." }
    if (-not $elmServerPath -and $prebuilt.Contains('elm-server')) { throw "$name needs -ElmServer: it runs elm-server." }
    if (-not $elmServicePath -and $prebuilt.Contains('elm-service')) { throw "$name needs -ElmService: it runs elm-service." }
    if (-not $feedPath -and $prebuilt.Contains('packages')) { throw "$name needs -Feed: it puts the ElasticLM packages in the folder packages." }
    return $plan
}

# The MSVC x64 environment of the reader's x64 Native Tools Command Prompt, read once in the run, the first time a cl line
# runs (eng/Get-MsvcEnvironment.ps1). It goes to the processes of cl and app lines only, never to this script's own
# environment (design §20.7, V1c3c, review R3).
function Get-Msvc {
    if (-not $script:msvc) {
        try { $script:msvc = & (Join-Path $repoRoot 'eng/Get-MsvcEnvironment.ps1') -Architecture x64 }
        catch { throw "a cl line needs Visual Studio with the C++ tools for x64: $($_.Exception.Message)" }
    }
    return $script:msvc
}

# The full path of a program on a PATH; .NET looks a bare name up in this script's own PATH, not in the PATH a process is
# given, so cl is looked up in the MSVC environment's PATH here (design §20.7, V1c3c, review R2).
function Find-Program([string]$program, [string]$path) {
    $file = $IsWindows ? "$program.exe" : $program
    foreach ($folder in $path -split [IO.Path]::PathSeparator) {
        if ($folder -eq '') { continue }
        $candidate = Join-Path $folder $file
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw "$program is not on the PATH."
}

# A process in the work folder. elm-server gets no ELM_ variable: it reads ELM_<KEY> before its settings file.
# elm-service likewise, except that the background elm-service gets the run's passphrase for its signing key. An elm line
# gets the API key captured with env; no other program gets it, and this script's own environment never holds it. cl, cc,
# xattr, and a program the page built get no ELM_ variable either, so that the run's passphrase stays with elm (design
# §20.7, V1c3c, review R10); cl and the built program get the MSVC environment on Windows.
function New-StartInfo([string]$program, [string]$kind, [string[]]$arguments, [string]$folder, [string]$apiKey, [switch]$Background) {
    $file = switch -CaseSensitive ($kind) {
        'elm' { $elmPath }
        'dotnet' { $dotnetPath }
        'elm-server' { $elmServerPath }
        'elm-service' { $elmServicePath }
        'cl' { Find-Program 'cl' (Get-Msvc)['PATH'] }
        'cc' { Find-Program 'cc' $env:PATH }
        'xattr' { Find-Program 'xattr' $env:PATH }
        # A relative path, so that it never names a file outside the work folder (Read-Command).
        'app' { Join-Path $folder $program }
    }
    $info = [Diagnostics.ProcessStartInfo]::new($file)
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    $info.WorkingDirectory = $folder
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    if ($kind -cin 'elm-server', 'elm-service', 'cl', 'cc', 'xattr', 'app') {
        foreach ($variable in @($info.Environment.Keys)) {
            if ($variable.StartsWith('ELM_', [StringComparison]::OrdinalIgnoreCase)) { [void]$info.Environment.Remove($variable) }
        }
    }
    if ($IsWindows -and ($kind -ceq 'cl' -or ($kind -ceq 'app' -and $script:msvc))) {
        foreach ($entry in (Get-Msvc).GetEnumerator()) { $info.Environment[$entry.Key] = $entry.Value }
    }
    if ($kind -ceq 'elm-service' -and $Background) { $info.Environment['ELM_SIGNING_PASSPHRASE'] = $script:passphrase }
    if ($kind -ceq 'elm' -and $apiKey) { $info.Environment[$apiKeyVariable] = $apiKey }
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

# Substitutes the captured values in each argument (after the split, so a value never adds an argument).
function Resolve-Argument([string[]]$words, [hashtable]$values) {
    [string[]]$arguments = @($words | Select-Object -Skip 1 | ForEach-Object {
            $argument = $_
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
    $info = New-StartInfo $command.Words[0] $command.Kind (Resolve-Argument $command.Words $values) $folder -Background
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

# A marked block becomes its content, and a steps:prebuilt marker its line without the spaces around it.
function Get-TranslationItem([object]$item) {
    return $item.PSObject.Properties['Text'] ? "prebuilt`n" + $item.Text.Trim() : "block`n" + ($item.Lines -join "`n")
}

function Test-Translation([string]$path, [object]$parsed) {
    $relative = [IO.Path]::GetRelativePath($repoRoot, [IO.Path]::GetFullPath($path)).Replace('\', '/')
    $translated = $relative -eq 'README.md' ? 'README.ja.md' : ($relative -replace '^docs/guide/', 'docs/guide.ja/')
    if ($translated -eq $relative) { return }
    $translatedPath = Join-Path $repoRoot $translated
    if (-not (Test-Path $translatedPath)) { throw "$(Get-PageName $path) has no translation at $translated." }
    $translation = Get-Page $translatedPath
    $theirs = @(@($translation.Blocks) + @($translation.Prebuilt) | Sort-Object Order | ForEach-Object { Get-TranslationItem $_ })
    $next = 0
    foreach ($item in Get-Marked $parsed) {
        $content = Get-TranslationItem $item
        while ($next -lt $theirs.Count -and $theirs[$next] -cne $content) { $next++ }
        if ($next -ge $theirs.Count) {
            $what = $item.PSObject.Properties['Text'] ? 'steps:prebuilt marker' : 'code block'
            throw "$($translated): the $what of $(Get-PageName $path):$($item.Order) is missing, changed, or out of order."
        }
        $next++
    }
}

function Invoke-Page([string]$path) {
    $name = Get-PageName $path
    $parsed = Get-Page $path
    if (-not (Get-Marked $parsed)) { throw "$name has no steps." }
    $plan = Read-Plan $name $parsed
    Test-Translation $path $parsed

    $suffix = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(6))
    $work = Join-Path ([IO.Path]::GetTempPath()) "elm-steps-$suffix"
    $packages = Join-Path ([IO.Path]::GetTempPath()) "elm-steps-packages-$suffix"
    New-Item -ItemType Directory $work, $packages | Out-Null
    $saved = @{}
    foreach ($variable in 'ELM_KEY_PASSPHRASE', 'NUGET_PACKAGES', 'MSBUILDDISABLENODEREUSE', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO') {
        $saved[$variable] = [Environment]::GetEnvironmentVariable($variable)
    }
    $script:passphrase = 'steps ' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(8))
    $env:ELM_KEY_PASSPHRASE = $script:passphrase
    $env:NUGET_PACKAGES = $packages
    # A build node that outlives the run would hold files in the work folder.
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $left = @()
    $completed = $false
    try {
        $lastOutput = $null
        $values = @{}
        # The value captured with env, for the elm lines after it; a local of the page, so the next page never gets it.
        $apiKey = $null
        foreach ($step in $plan) {
            $block = $step.Block
            # A block for another OS is skipped; its marker and lines were checked with the rest of the page.
            if ($step.Os -and $step.Os -cne $thisOs) {
                "skip: $($name):$($block.Line - 1): os=$($step.Os)"
                continue
            }
            switch ($step.Kind) {
                'run' {
                    $last = $step.Commands[-1]
                    foreach ($command in $step.Commands) {
                        $expected = $command.Line -eq $last.Line ? $step.Exit : 0
                        try { $result = Invoke-Step (New-StartInfo $command.Words[0] $command.Kind (Resolve-Argument $command.Words $values) $work $apiKey) }
                        catch {
                            # The reason a compiler or a tool cannot be found holds no argument, output, or value.
                            $reason = $command.Kind -cin 'cl', 'cc', 'xattr' ? ": $($_.Exception.Message)" : '.'
                            throw "$($name):$($command.Line): $($command.Shown) could not be run$reason"
                        }
                        if ($result.ExitCode -ne $expected) {
                            throw "$($name):$($command.Line): $($command.Shown) exited with $($result.ExitCode)$($expected -ne 0 ? ", not $expected" : '')."
                        }
                        "ok: $($name):$($command.Line): $($command.Shown)$($expected -ne 0 ? " (exit $expected)" : '')"
                        $lastOutput = $result.Output
                    }
                    if ($step.Attributes.ContainsKey('capture')) {
                        $capture = $step.Attributes['capture']
                        $captured = Get-CapturedValue "$($name):$($last.Line)" $step $result
                        if ($step.Attributes.ContainsKey('env')) {
                            $apiKey = $captured
                            "ok: $($name):$($last.Line): captured $capture for $apiKeyVariable"
                        }
                        else {
                            $values[$capture] = $captured
                            "ok: $($name):$($last.Line): captured $capture"
                        }
                    }
                }
                'background' {
                    Start-Background $name $step $values $work
                }
                'file' {
                    $value = $step.Attributes['path']
                    $target = Join-Path $work $value
                    New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
                    # A value is only A-Za-z0-9._~-, so it needs no escaping inside a JSON string or XML text.
                    $content = ($block.Lines -join "`n") + "`n"
                    foreach ($valueName in $values.Keys) { $content = $content.Replace("<$valueName>", $values[$valueName]) }
                    [IO.File]::WriteAllText($target, $content, [Text.UTF8Encoding]::new($false))
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
                    $prebuiltName = $step.Attributes['name']
                    if ($prebuiltName -ceq 'packages') {
                        $target = Join-Path $work 'packages'
                        New-Item -ItemType Directory -Force $target | Out-Null
                        # The page does not have the reader download the symbols packages (.snupkg).
                        $nupkgs = @(Get-ChildItem -LiteralPath $feedPath -File | Where-Object Extension -EQ '.nupkg')
                        if ($nupkgs.Count -eq 0) { throw "$($name):$($block.Order): -Feed holds no .nupkg file." }
                        foreach ($nupkg in $nupkgs) { Copy-Item -LiteralPath $nupkg.FullName -Destination $target }
                    }
                    elseif ($prebuiltName -ceq 'libelasticlm') {
                        # The files of the archive the reader extracts into app: the library from -Library, without its
                        # debug symbols, and the header and the license from the repository, which Pack-Rid.ps1 adds.
                        $target = Join-Path $work 'app'
                        New-Item -ItemType Directory -Force $target | Out-Null
                        foreach ($file in $libraryFiles) {
                            $source = Join-Path $libraryPath $file
                            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "$($name):$($block.Order): -Library holds no $file." }
                            Copy-Item -LiteralPath $source -Destination $target
                        }
                        Copy-Item -LiteralPath (Join-Path $repoRoot 'include/elasticlm.h'), (Join-Path $repoRoot 'LICENSE') -Destination $target
                    }
                    $argumentName = switch -CaseSensitive ($prebuiltName) { 'elm' { '-Elm' } 'elm-server' { '-ElmServer' } 'elm-service' { '-ElmService' } 'packages' { '-Feed' } 'libelasticlm' { '-Library' } }
                    "ok: $($name):$($block.Order): prebuilt $prebuiltName ($argumentName)"
                }
            }
        }
        $completed = $true
    }
    finally {
        Stop-Background
        $script:passphrase = $null
        foreach ($variable in $saved.Keys) { [Environment]::SetEnvironmentVariable($variable, $saved[$variable]) }
        Remove-Item -Recurse -Force -LiteralPath $work, $packages -ErrorAction Continue
        # A folder that is still there, such as one a process still holds open, would keep keys and secrets. When a step
        # failed, its error is the one that ends the run, so the folders are named here; otherwise the error below names them.
        $left = @(@($work, $packages) | Where-Object { Test-Path -LiteralPath $_ })
        if (-not $completed -and $left) { Write-Host "::error::$(Get-LeftMessage $name $left)" }
    }
    if ($left) { throw (Get-LeftMessage $name $left) }
}

# The error for the work folders that were not removed, in the singular or the plural.
function Get-LeftMessage([string]$name, [string[]]$folders) {
    return $folders.Count -eq 1 ? "$($name): the folder $($folders[0]) was not removed." : "$($name): the folders $($folders -join ' and ') were not removed."
}

$script:background = $null
$script:msvc = $null
# The run's passphrase of the page that runs: ELM_KEY_PASSPHRASE of its elm lines, and the background elm-service's
# ELM_SIGNING_PASSPHRASE.
$script:passphrase = $null
try {
    foreach ($path in $Page) { Invoke-Page $path }
}
catch {
    Write-Host "::error::$($_.Exception.Message)"
    exit 1
}
exit 0
