<#
.SYNOPSIS
    Runs the marked steps of README.md, of every Tutorial page, and of every How-to page with a given elm, elm-server,
    elm-service, and package folder, and builds and runs the C sample of Tutorial 4 with libelasticlm; with -Docker, also
    the pages with docker lines and the elm-service image (design §20.7, V1c1, V1c3a, V1c3b, V1c3e, V1c3d, V1c3c, V1c4a,
    V1c4b, V1c4e, V1c13, and V1c4d2).

.DESCRIPTION
    A step is a fenced code block whose opening line comes right after a marker: <!-- steps:<kind> key=value ... -->. A
    value is a word without spaces and double quotes, or a double-quoted string without double quotes. A key given twice,
    a key the kind does not take, and '-->' inside a value fail. The two markers that stand on a line of their own, with no
    code block, are steps:prebuilt and steps:after (below).

      steps:run                       Each line is one command, run in order, never through a shell. A line starts with
                                      elm, dotnet, elm-server, elm-service, cl, cc, xattr, docker (below), or a program
                                      the page built, and never with cd; the rest is split on spaces, and a double-quoted
                                      part is one argument. Each line must exit with 0. Optional keys:
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
      steps:background ready=<url>    One line that starts with elm-server, elm-service, or docker run --rm --name <name>
                                      (without -d), started without waiting, its output read and dropped. The step is done
                                      when a GET of the ready URL answers 200; the GETs come every 0.5 s with a 5 s limit
                                      each, for at most 60 s. The URL's host is 127.0.0.1, localhost, or [::1]: https for
                                      elm-server, with the server's self-signed certificate accepted; https or http for
                                      elm-service and docker. A page has at most one, and a How-to page any number, each
                                      of which replaces the background process of the page that steps:after names or of
                                      the block before it (below). The process tree is stopped at the end of the page; a
                                      docker background is stopped with docker stop <name>, and its docker process is then
                                      waited for, and killed only after the limit of a command (10 minutes).
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
    line, when it is indented, without name, with a name other than these, when a page names the same name twice, and on a
    How-to page with a name other than image:
      elm           The page's elm lines run -Elm. An elm line needs no marker.
      elm-server    The page's elm-server lines run -ElmServer. An elm-server line needs this marker before it.
      elm-service   The page's elm-service lines run -ElmService. An elm-service line needs this marker before it.
      packages      Creates the folder packages in the work folder and copies every *.nupkg of -Feed into it (no
                    .snupkg). A dotnet add ... package or dotnet package add line needs this marker before it.
      libelasticlm  Creates the folder app in the work folder and puts in it the files of the libelasticlm archive: the
                    library of this system from -Library (elasticlm.dll and elasticlm.lib, libelasticlm.so, or
                    libelasticlm.dylib), and include/elasticlm.h and LICENSE from the repository. On macOS the files get
                    the quarantine attribute of a browser download, as tar leaves them. A cl or cc line needs this
                    marker before it.
      image         With -Docker only. Loads the elm-service image of -Image, an OCI layout tar of one or both
                    platforms: its linux/amd64 manifest gets a docker manifest.json that names it elm-service, as
                    eng/container/Test-Image.ps1 does (eng/container/ImageLayout.ps1), and docker load reads it. The page's
                    docker lines then name the image elm-service, as a reader names the image they pull or build (design
                    §20.7, V1c4d2, reviews R3, R8, and R9). The image is not removed at the end.

    The C lines of Tutorial 4 (design §20.7, V1c3c):
      cl            In an os=windows block only. It runs the cl.exe of the MSVC x64 environment, which the script reads
                    once with eng/Get-MsvcEnvironment.ps1, as the reader's x64 Native Tools Command Prompt sets it up.
                    The line names the file it builds with /Fe:<path>.
      cc            In an os=linux or os=macos block only; the cc on the PATH. The line names the file it builds with
                    -o <path>.
      xattr         In an os=macos block only; the xattr on the PATH.
      <path>        A program that an earlier cl or cc line of the page builds, named by the same relative path with /
                    (and, for cl, without .exe), run by its full path in the work folder; or the one program that an
                    earlier dotnet publish line builds (below). No other file can be run.

    The NativeAOT publish of the How-to guide "Publish with NativeAOT" (design §20.7, V1c4e, review R11): a line
    dotnet publish <project folder> ... -o <folder> (or --output <folder>, and -o: or --output: or -o= or --output=
    followed by the folder) builds <folder>/<the last name of the project folder>, which a later line may run by that path,
    without .exe on Windows. The project folder comes right after publish, and it and the output folder are relative, with
    / and without '..', '.', a leading '-', or a placeholder; the project folder's name has no '.', and a line that names
    more than one output folder fails. A steps:file block that writes into such an output folder, of the page or of the page
    that steps:after names, fails wherever it stands, compared without '.' parts and without case.
    A whole word in single quotes is one argument without its quotes, in an os=linux or os=macos block, or on a page with
    docker lines (below), only; a single quote anywhere else fails. A cl or cc line names one /Fe: or -o. The paths of /Fe:, -o, and a program are relative, with / and
    without '..' or a placeholder, on every system.

    <!-- steps:after page=docs/guide/tutorials/<page>.md --> stands on a line of its own, from the start of the line, on a
    How-to page (docs/guide/how-to/) only, before every other marker of the page, and at most once (design §20.7, V1c4a,
    Q5). The page it names is a Tutorial page, by its path from the repository root, so a page that steps:after names
    never has one itself. The named page runs once in a run of this script, as its own page, before the first How-to page
    that names it, also when -Page does not list it; a page listed again later is not run again (design §20.7, V1c13). Its
    work folder and package folder are kept when it ends, after its background process has stopped. Each How-to page that
    names it runs in a copy of those two folders, with the protection of each folder and file kept (the access rules on
    Windows, the mode on Linux and macOS), and the named page's background line is started again in the copy before the
    How-to page's steps:
      - The named page's steps:prebuilt names, its captured values, and the value it captured with env=ELM_API_KEY carry
        over, so that the How-to page's elm-service, dotnet add ... package, and elm lines and its <name> placeholders work
        as on the named page. A How-to page that captures a name the named page captured fails, as a name captured twice.
      - The named page's passphrase carries over too, since its keys were made with it. Its background process, started
        again in the copy, runs into the How-to page's steps, and stops at the end of the How-to page. A How-to page has no
        steps:prebuilt marker of its own other than name=image. Each of its steps:background blocks replaces the process
        that runs at that point, so that one runs at a time (design §20.7, V1c4b, Q1; V1c4d2, review R5): at the block,
        the running process is stopped and waited for as at the end of a page, and the block's line is started and waited
        for as on any page, with the same passphrase. The block fails when the page has no steps:after, when the named page
        has no steps:background, and when its line starts another program than the named page's (elm-service replaces
        elm-service), unless it is a docker line (design §20.7, V1c4d2, review R4).
      - A failing step of the named page fails the run, before the How-to page. The named page is checked against its
        translation too. The kept folders are removed after the last How-to page of the run that names the page, and at the
        end of the run, also after a failure; a folder that is still there fails the run. A Tutorial page that no listed
        How-to page names keeps nothing, as README.md does not.
    The marker fails when it is indented, when the next line that is not blank opens a code block, and without page.

    elm-server and elm-service run with -ElmServer and -ElmService and get no environment variable whose name starts with
    ELM_ (in any case), so that a variable left in the shell cannot override the page's settings file. The one exception
    is the elm-service of a steps:background block, which gets the run's passphrase as ELM_SIGNING_PASSPHRASE. cl, cc,
    xattr, and a program the page built get no ELM_ variable either; cl and the program get the MSVC environment on Windows,
    which this script's own environment never holds. elm and dotnet get the environment as it is, and elm lines also
    ELM_API_KEY once a block captured it with env. On Windows, dotnet lines also get the Visual Studio Installer folder at
    the front of PATH when it exists, as eng/release/Publish-Rid.ps1 sets it, since a NativeAOT publish runs the Visual
    Studio environment scripts, which call vswhere.exe by name (design §20.7, V1c4e). docker lines run -ContainerCli with no
    ELM_ variable except the run's passphrase as ELM_SIGNING_PASSPHRASE, so that a page's -e ELM_SIGNING_PASSPHRASE without
    a value hands it to the container as the reader's terminal does (design §20.7, V1c4d2, Q9).

    The lines of the container pages (design §20.7, V1c4d2, Q9 and reviews R1, R2, R4, R7, R13, and R16):
      docker        A line that runs -ContainerCli (docker by default) with the rest of the line as its arguments; with
                    -ContainerCli wslc, a line docker cp ... runs wslc container cp ..., which is the one subcommand that
                    wslc 3.0.3 names otherwise. It stands in a block without os or with os=linux, takes capture (env too)
                    as any line, and is never run through a shell, so a page writes no $PWD, $(...), <, |, or line
                    continuation. A page with a docker line, or with steps:prebuilt name=image, or whose steps:after names
                    a page with a docker line, is for a Linux host as a whole: without -Docker it is skipped as a whole
                    (skip: <page>), and it does not count as a page that names a Tutorial (above).
      The containers that docker run and docker create lines name with --name <name>, and the volumes of docker volume
      create <name> lines, are removed at the end of the page, also when it fails: each container with rm -f -v, so that
      the anonymous volumes of its image go with it, and then each volume. A name is literal, never a placeholder. A docker
      create line, and a docker run line without --rm or with -d, names its container; the words after the image are not
      told from docker's options, so a --rm, -d, or --name there counts too. Before the page runs, a container or volume
      of such a name that exists already fails the page, so that a developer's own is never removed. One that is still
      there after the removal fails the page, as a folder does.
      A page that names postgres:18@sha256:..., and its translation, name the digest of .github/workflows/ci.yml, which
      is checked for every listed page, also one that is skipped (design §20.7, V1c4d2, Q10).

    Each page runs in a new work folder outside the clone, with NUGET_PACKAGES set to a new folder of its own and
    ELM_KEY_PASSPHRASE set to a passphrase made for the run. The package folder is new for each page so that a package
    packed again under the same version never comes from an older copy in the global packages folder, and so that the
    job's package cache never holds the packages of one run. A How-to page starts from copies of the folders of the page
    its steps:after names (above). Both folders of a page are removed at the end, also on failure, after the background
    process has stopped, except those that a Tutorial page keeps for the copies, which are removed as said above; a folder
    that is still there fails the page, so that keys and secrets are never left behind unnoticed.

    The page's translation (README.ja.md, or the same file under docs/guide.ja/) must hold the content of each marked
    block of the English page as fenced code blocks of its own, and each steps:prebuilt and steps:after marker as a line
    of its own that is the same apart from the spaces around it, all in the same order.

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
    The pages to run. Default: README.md, docs/guide/tutorials/*.md, and docs/guide/how-to/*.md at the repository root.

.PARAMETER Docker
    Runs the pages with docker lines instead of skipping them (above). release.yml's container job passes it with the two
    container pages in -Page.

.PARAMETER Image
    The elm-service image as an OCI layout tar (eng/container/Build-Image.ps1) for steps:prebuilt name=image. A page with
    that marker fails without it.

.PARAMETER ContainerCli
    The container CLI that docker lines run: docker (the default) or wslc, for a run on Windows with WSL containers
    (.cursor/rules/testing.mdc).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Elm,
    [string]$ElmServer,
    [string]$ElmService,
    [string]$Feed,
    [string]$Library,
    [string[]]$Page,
    [switch]$Docker,
    [string]$Image,
    [ValidateSet('docker', 'wslc')][string]$ContainerCli = 'docker'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$elmPath = (Resolve-Path $Elm).Path
$elmServerPath = $ElmServer ? (Resolve-Path $ElmServer).Path : $null
$elmServicePath = $ElmService ? (Resolve-Path $ElmService).Path : $null
$feedPath = $Feed ? (Resolve-Path $Feed).Path : $null
$libraryPath = $Library ? (Resolve-Path $Library).Path : $null
$imagePath = $Image ? (Resolve-Path $Image).Path : $null
# Get-PlatformManifest and New-DockerArchive, which eng/container/Test-Image.ps1 uses too (design §20.7, V1c4d2, review R8).
. (Join-Path $repoRoot 'eng/container/ImageLayout.ps1')
$prebuiltNames = @('elm', 'elm-server', 'elm-service', 'packages', 'libelasticlm', 'image')
# The program that a page's container lines start with, the name that steps:prebuilt name=image gives the image, and the
# platform of the image it loads (design §20.7, V1c4d2, Q9, reviews R8 and R9).
$containerProgram = 'docker'
$imageName = 'elm-service:latest'
$imageArch = 'amd64'
# The subcommands that -ContainerCli names otherwise than docker. wslc 3.0.3 takes docker's names or aliases for every
# other subcommand that the pages and this script use (volume, exec, load, tag, create, run, start, stop, rm, inspect), and
# has cp under container only (design §20.7, V1c4d2, review R16).
$containerCliMap = @{ docker = @{}; wslc = @{ cp = @('container', 'cp') } }
# A container or volume that a page names, which the end of the page removes by that name.
$containerName = '[A-Za-z0-9][A-Za-z0-9_.-]*'
# The PostgreSQL image that a page names is the one of ci.yml (design §20.7, V1c4d2, Q10).
$postgresReference = 'postgres:18@sha256:'
$ciPostgres = @([regex]::Matches([IO.File]::ReadAllText((Join-Path $repoRoot '.github/workflows/ci.yml')), "$([regex]::Escape($postgresReference))[0-9a-f]{64}") | ForEach-Object Value | Select-Object -Unique)
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
    after = @('page')
}
# The folder of the pages that may carry steps:after, and the folder of the pages it may name (design §20.7, V1c4a, Q5).
$howToFolder = 'docs/guide/how-to/'
$tutorialFolder = 'docs/guide/tutorials/'

if (-not $Page) {
    $Page = @(Join-Path $repoRoot 'README.md') + @(foreach ($folder in $tutorialFolder, $howToFolder) {
            Get-ChildItem (Join-Path $repoRoot $folder) -Filter '*.md' -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object FullName
        })
}
# .NET resolves a relative path against the process's folder, not PowerShell's current location.
$Page = @($Page | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })

function Get-PageName([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    $relative = [IO.Path]::GetRelativePath($repoRoot, $full)
    return ($relative.StartsWith('..') ? $full : $relative).Replace('\', '/')
}

$fenceOpen = '^ {0,3}(?<fence>`{3,}|~{3,})'
# The markers that stand on a line of their own, with no code block.
$lineMarker = '<!--\s*steps:(?<kind>prebuilt|after)(\s|-->)'

# The fenced code blocks of a Markdown file, each with the marker on the line right before its opening line (or none), and
# the steps:prebuilt and steps:after markers, each on a line of its own. Each item has the line number of its marker
# (Order), so that the two lists can be merged in the page's order.
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
            Marker = ($marker -match '^<!--\s*steps:' -and $marker -notmatch $lineMarker) ? $marker : $null
            Line = $start
            Order = $start - 1
            Lines = @(if ($i -gt $start) { $lines[$start..($i - 1)] })
        })
    }
    $lineMarkers = [Collections.Generic.List[object]]::new()
    for ($j = 0; $j -lt $lines.Length; $j++) {
        # A marker's text inside a code block is neither a marker nor shown text a step can rely on: it fails, whatever its
        # kind, so that a page never runs differently from how it reads.
        if ($inBlock.Contains($j)) {
            if ($lines[$j] -match '<!--\s*steps:') { throw "$(Get-PageName $path):$($j + 1): a steps marker cannot stand inside a code block." }
            continue
        }
        $standalone = [regex]::Match($lines[$j], $lineMarker)
        if ($standalone.Success) {
            # The next line that is not blank, as the check of a steps:prebuilt or steps:after marker needs it; only an
            # opening line counts, never the closing line of a block.
            $next = $j + 1
            while ($next -lt $lines.Length -and $lines[$next].Trim() -eq '') { $next++ }
            $lineMarkers.Add([pscustomobject]@{
                Text = $lines[$j]
                Kind = $standalone.Groups['kind'].Value
                Order = $j + 1
                BeforeFence = $openings.Contains($next)
            })
        }
        # Any other steps marker must stand right before a code block.
        elseif ($lines[$j] -match '<!--\s*steps:' -and -not ($blocks | Where-Object { $_.Line -eq $j + 2 -and $_.Marker })) {
            throw "$(Get-PageName $path):$($j + 1): a steps marker must be the line right before a code block's opening line."
        }
    }
    return [pscustomobject]@{ Blocks = $blocks; LineMarkers = $lineMarkers }
}

# The kind and the key=value attributes of a marker; $at names its page and line.
function Read-Marker([string]$at, [string]$text) {
    $marker = [regex]::Match($text, '^<!--\s*steps:(?<kind>[a-z]+)(?<rest>.*?)\s*-->$')
    $kind = $marker.Groups['kind'].Value
    if (-not $marker.Success -or -not $markerKeys.ContainsKey($kind)) {
        throw "$($at): unknown marker; use steps:run, steps:background, steps:file, steps:expect, steps:prebuilt, or steps:after."
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

# The file that a cl line (/Fe:<path>) or a cc line (-o <path>) builds; $null when the line names none or more than one,
# since cc writes the last -o and the set of built files must hold the file that is written (review A2).
function Get-BuiltFile([string]$program, [string[]]$words) {
    if ($program -ceq 'cl') {
        $output = @($words | Where-Object { $_ -clike '/Fe:*' })
        return $output.Count -eq 1 ? $output[0].Substring(4) : $null
    }
    $indexes = @(for ($i = 1; $i -lt $words.Count; $i++) { if ($words[$i] -ceq '-o') { $i } })
    return ($indexes.Count -eq 1 -and $indexes[0] + 1 -lt $words.Count) ? $words[$indexes[0] + 1] : $null
}

# The folder that a dotnet publish line writes to (-o <path>, --output <path>, -o:<path>, or --output:<path>); $null when
# the line names none, and '' when it names more than one (design §20.7, V1c4e, review R11).
function Get-PublishFolder([string[]]$words) {
    $folders = [Collections.Generic.List[string]]::new()
    for ($i = 2; $i -lt $words.Count; $i++) {
        if ($words[$i] -cin '-o', '--output') {
            $folders.Add($i + 1 -lt $words.Count ? $words[$i + 1] : '')
            $i++
        }
        elseif ($words[$i] -cmatch '^(-o|--output)[:=](.*)$') {
            $folders.Add($Matches[2])
        }
    }
    if ($folders.Count -eq 0) { return $null }
    return $folders.Count -eq 1 ? $folders[0] : ''
}

# The container that a docker run or docker create line names with --name <name> or --name=<name> ($null when it names
# none), and the volume of a docker volume create line, added to $made so that the end of the page removes them by name.
# A name is literal, so that it is known before anything runs. A container without a name must remove itself: a docker
# create line names its container, and so does a docker run line without --rm or with -d (design §20.7, V1c4d2, review
# R13). The words after the image are not told from docker's options, so a later --rm, -d, or --name counts too.
function Read-ContainerName([string]$at, [string[]]$words, [object]$made) {
    $subcommand = $words.Count -gt 1 ? $words[1] : ''
    if ($subcommand -ceq 'volume' -and $words.Count -gt 2 -and $words[2] -ceq 'create') {
        if ($words.Count -ne 4 -or $words[3] -cnotmatch "^$containerName$") { throw "$($at): a docker volume create line names one volume, literally, and takes no option." }
        [void]$made.Volumes.Add($words[3])
        return $null
    }
    if ($subcommand -cnotin 'run', 'create') { return $null }
    $names = [Collections.Generic.List[string]]::new()
    for ($i = 2; $i -lt $words.Count; $i++) {
        if ($words[$i] -ceq '--name') {
            $names.Add($i + 1 -lt $words.Count ? $words[$i + 1] : '')
            $i++
        }
        elseif ($words[$i] -cmatch '^--name=(.*)$') {
            $names.Add($Matches[1])
        }
    }
    if ($names.Count -gt 1 -or ($names.Count -eq 1 -and $names[0] -cnotmatch "^$containerName$")) { throw "$($at): a docker $subcommand line names its container once, literally, with --name <name>." }
    $removes = $words -ccontains '--rm'
    $detached = @($words | Where-Object { $_ -cin '-d', '--detach', '--detach=true' }).Count -gt 0
    if ($names.Count -eq 0 -and ($subcommand -ceq 'create' -or -not $removes -or $detached)) {
        throw "$($at): a docker $subcommand line whose container does not remove itself when it ends (docker create, or docker run without --rm or with -d) names it with --name <name>, so that the end of the page removes it."
    }
    if ($names.Count -eq 0) { return $null }
    [void]$made.Containers.Add($names[0])
    return $names[0]
}

# The commands of a steps:run or steps:background block, checked before anything runs. $os is the block's os (or $null),
# $built holds the files that the page's earlier cl, cc, and dotnet publish lines build, and $outputs the folders that its
# dotnet publish lines write to. $containerPage is true on a page with docker lines, which is for Linux as a whole, and $made
# collects the containers and volumes that its docker lines name (Containers, Volumes).
function Read-Command([string]$name, [object]$block, [Collections.Generic.HashSet[string]]$captured, [Collections.Generic.HashSet[string]]$hidden, [Collections.Generic.HashSet[string]]$prebuilt, [string]$os, [Collections.Generic.HashSet[string]]$built, [Collections.Generic.HashSet[string]]$outputs, [bool]$containerPage, [object]$made) {
    $lineNumber = $block.Line
    foreach ($line in $block.Lines) {
        $lineNumber++
        if ($line.Trim() -eq '') { continue }
        $split = @(Split-Word $line)
        [string[]]$words = @($split | ForEach-Object Text)
        # A single quote groups a word as bash and PowerShell do, and is taken in the blocks of those shells only: on
        # Windows the page is typed in PowerShell too, but no Windows line needs it (design §20.7, V1c3c, review R9). A
        # page with docker lines is for a Linux host as a whole (design §20.7, V1c4d2, review R14).
        foreach ($item in $split) {
            if ($item.Single -and $os -cnotin 'linux', 'macos' -and -not $containerPage) { throw "$($name):$($lineNumber): a single-quoted word is taken in an os=linux or os=macos block, or on a page with docker lines, only." }
            if (-not $item.Single -and $item.Text.Contains("'")) { throw "$($name):$($lineNumber): a single quote encloses a whole word." }
        }
        $program = $words[0]
        if ($programOs.ContainsKey($program)) {
            if ($os -cnotin $programOs[$program]) { throw "$($name):$($lineNumber): $($program -ceq 'xattr' ? 'an' : 'a') $program line stands in a block with os=$($programOs[$program] -join ' or os=')." }
            if ($program -cin 'cl', 'cc') {
                if (-not $prebuilt.Contains('libelasticlm')) { throw "$($name):$($lineNumber): a $program line needs a steps:prebuilt name=libelasticlm marker before it." }
                $output = Get-BuiltFile $program $words
                # A placeholder would be filled after this check, with a value that may hold '..' (review A2).
                if (-not $output -or -not (Test-RelativePath $output) -or $output.Contains('<')) {
                    throw "$($name):$($lineNumber): a $program line builds one file in the work folder: one $($program -ceq 'cl' ? '/Fe:<path>' : '-o <path>'), relative, with / and without '..' or a placeholder."
                }
                [void]$built.Add($output)
                # PowerShell runs app/app.exe as app/app too, and the page writes the shorter form on every OS.
                if ($program -ceq 'cl' -and $output.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) { [void]$built.Add($output.Substring(0, $output.Length - 4)) }
            }
            $kind = $program
        }
        elseif ($program.Contains('/') -or $program.Contains('\') -or $program -match '^[A-Za-z]:') {
            # A program that the page built: only a file that an earlier cl, cc, or dotnet publish line of the page builds,
            # so that a page cannot run any other file (design §20.7, V1c3c, reviews R2 and R8; V1c4e, review R11).
            if (-not (Test-RelativePath $program) -or $program.Contains('<')) { throw "$($name):$($lineNumber): a program the page built is named by a relative path, with / and without '..' or a placeholder." }
            if (-not $built.Contains($program)) { throw "$($name):$($lineNumber): $program is not a file that an earlier cl, cc, or dotnet publish line of the page builds." }
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
                # dotnet publish <project folder> ... -o <folder> builds <folder>/<the project folder's name>, the one program
                # of the publish that a later line may run (design §20.7, V1c4e, review R11).
                if ($words.Count -gt 1 -and $words[1] -ceq 'publish') {
                    $folder = Get-PublishFolder $words
                    if ($null -ne $folder) {
                        $project = $words.Count -gt 2 ? $words[2].TrimEnd('/') : ''
                        $folder = $folder.TrimEnd('/')
                        if ($folder -eq '' -or $folder.StartsWith('-') -or -not (Test-RelativePath $folder) -or $folder.Contains('<') -or ($folder -split '/') -contains '.' -or
                            $project -eq '' -or $project.StartsWith('-') -or -not (Test-RelativePath $project) -or $project.Contains('<') -or $project.Contains('.')) {
                            throw "$($name):$($lineNumber): a dotnet publish line names its project folder first and one output folder (-o <path>), both relative, with / and without '.' or a placeholder; the project folder's name has no '.'."
                        }
                        [void]$outputs.Add($folder)
                        [void]$built.Add("$folder/$(($project -split '/')[-1])")
                    }
                }
            }
            'elm-server' {
                if (-not $prebuilt.Contains('elm-server')) { throw "$($name):$($lineNumber): an elm-server line needs a steps:prebuilt name=elm-server marker before it." }
            }
            'elm-service' {
                if (-not $prebuilt.Contains('elm-service')) { throw "$($name):$($lineNumber): an elm-service line needs a steps:prebuilt name=elm-service marker before it." }
            }
            'docker' {
                # design §20.7, V1c4d2, Q9 and reviews R1, R2, and R13: only a run with -Docker runs docker lines, in a block
                # without os or with os=linux, and the end of the page removes what they name.
                if (-not $Docker) { throw "$($name):$($lineNumber): a docker line runs only with -Docker." }
                if ($os -and $os -cne 'linux') { throw "$($name):$($lineNumber): a docker line stands in a block without os or with os=linux." }
                [void](Read-ContainerName "$($name):$($lineNumber)" $words $made)
            }
            'cd' { throw "$($name):$($lineNumber): a step does not change the folder (cd); pass the folder as an argument." }
            default { throw "$($name):$($lineNumber): a step starts with elm, dotnet, elm-server, elm-service, cl, cc, xattr, docker, or a program the page built, not $($words[0])." }
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

# The marked blocks and the steps:prebuilt and steps:after markers of a page, in the page's order.
function Get-Marked([object]$parsed) {
    return @(@($parsed.Blocks | Where-Object Marker) + @($parsed.LineMarkers) | Sort-Object Order)
}

# Whether a parsed page has a docker line in a steps:run or steps:background block, read with the readers of Read-Plan
# (.cursor/rules/engineering.mdc, Repository scripts).
function Test-ContainerParsed([string]$name, [object]$parsed) {
    foreach ($block in $parsed.Blocks | Where-Object Marker) {
        if ((Read-Marker "$($name):$($block.Order)" $block.Marker).Kind -cnotin 'run', 'background') { continue }
        foreach ($line in $block.Lines) {
            if ($line.Trim() -ne '' -and (Split-Step $line)[0] -ceq $containerProgram) { return $true }
        }
    }
    return $false
}

# Whether a page runs the container CLI: it has a docker line or a steps:prebuilt name=image marker, or the page that its
# steps:after names has a docker line. Without -Docker such a page is skipped as a whole (design §20.7, V1c4d2, review R1).
function Test-ContainerPage([string]$path, [switch]$Named) {
    $name = Get-PageName $path
    $parsed = Get-Page $path
    if (Test-ContainerParsed $name $parsed) { return $true }
    foreach ($marker in $parsed.LineMarkers) {
        $read = Read-Marker "$($name):$($marker.Order)" $marker.Text.TrimEnd()
        if ($read.Kind -ceq 'prebuilt' -and $read.Attributes['name'] -ceq 'image') { return $true }
        # A named page that names another is an error that Read-Plan reports; it is not followed here.
        if ($read.Kind -ceq 'after' -and -not $Named -and $read.Attributes['page']) {
            $target = Join-Path $repoRoot $read.Attributes['page']
            if ((Test-Path -LiteralPath $target -PathType Leaf) -and (Test-ContainerPage $target -Named)) { return $true }
        }
    }
    return $false
}

# Checks every marker and line of a page, and of the page its steps:after names, before anything runs. Returns the steps
# (Steps), what a page that runs after this one takes over (Captured, Hidden, Prebuilt, Built), and the page that
# steps:after names, with its own plan (After), or $null.
function Read-Plan([string]$name, [object]$parsed) {
    $captured = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    # The names captured with env: their values go into ELM_API_KEY only, never into a placeholder.
    $hidden = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $prebuilt = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    # The names that the page steps:after names captured, so that the error of a name captured again says where.
    $inherited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $after = $null
    $isHowTo = $name.StartsWith($howToFolder, [StringComparison]::Ordinal)
    # Every name a steps:run marker of the page captures, so that a steps:file block can tell a name captured later from
    # text such as a usage line's <data-folder>.
    $pageCaptures = @($parsed.Blocks | Where-Object Marker | ForEach-Object { [regex]::Match($_.Marker, '\scapture=([^\s"]+)').Groups[1].Value } | Where-Object { $_ })
    # The files that the page's cl, cc, and dotnet publish lines build, which are the only programs a line may run by path,
    # and the folders that its dotnet publish lines write to.
    $built = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $outputs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    # A page with docker lines, and the containers and volumes they name, which the end of the page removes.
    $containerPage = Test-ContainerParsed $name $parsed
    $made = [pscustomobject]@{ Containers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal); Volumes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal) }
    $plan = [Collections.Generic.List[object]]::new()
    $previous = $null
    $previousOs = $null
    foreach ($block in Get-Marked $parsed) {
        $at = "$($name):$($block.Order)"
        if ($block.PSObject.Properties['Text']) {
            # A steps:prebuilt or steps:after marker: a line of its own, from the start of the line, not attached to a code
            # block.
            if ($block.Text -cne $block.Text.TrimStart()) { throw "$($at): a steps:$($block.Kind) marker starts at the beginning of its line." }
            if ($block.BeforeFence) { throw "$($at): a steps:$($block.Kind) marker stands on a line of its own; the next line that is not blank must not open a code block." }
            $marker = Read-Marker $at $block.Text.TrimEnd()
        }
        else {
            $marker = Read-Marker $at $block.Marker
        }
        $attributes = $marker.Attributes
        $step = [pscustomobject]@{ Kind = $marker.Kind; Block = $block; Attributes = $attributes; Commands = @(); Pattern = $null; Exit = 0; Ready = $null; Os = $null; After = $null; Replaces = $false; Container = $null }
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
                    if ($inherited.Contains($capture)) { throw "$($at): $capture is captured twice: $($after.Name) captures it." }
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
                $step.Commands = @(Read-Command $name $block $captured $hidden $prebuilt $step.Os $built $outputs $containerPage $made)
                if ($step.Commands.Count -eq 0) { throw "$($name):$($block.Line): the steps:run block is empty." }
                if ($null -ne $capture) {
                    if ($attributes.ContainsKey('env')) { [void]$hidden.Add($capture) } else { [void]$captured.Add($capture) }
                }
            }
            'background' {
                # A How-to page's steps:background stops the background process of the page that steps:after names, or of
                # the page's steps:background before it, and starts this line instead, such as elm-service with a rewritten
                # service.json; only one runs at a time (design §20.7, V1c4b, Q1; V1c4d2, review R5).
                if ($isHowTo) {
                    if (-not $after) { throw "$($at): a How-to page's steps:background replaces the background process of the page that steps:after names, and the page has no steps:after." }
                    if (-not ($after.Plan.Steps | Where-Object Kind -EQ 'background')) { throw "$($at): a How-to page's steps:background replaces the background process of $($after.Name), which has no steps:background." }
                    $step.Replaces = $true
                }
                if (-not $attributes.ContainsKey('ready')) { throw "$($at): steps:background takes ready=<url>." }
                if (-not $isHowTo -and ($plan | Where-Object Kind -EQ 'background')) { throw "$($at): a page other than a How-to page has at most one steps:background block." }
                $lines = @($block.Lines | Where-Object { $_.Trim() -ne '' })
                if ($lines.Count -ne 1) { throw "$($name):$($block.Line): the steps:background block has one line." }
                $lineAt = "$($name):$($block.Line + 1 + [array]::IndexOf($block.Lines, $lines[0]))"
                $words = Split-Step $lines[0]
                $program = $words[0]
                if ($program -cnotin 'elm-server', 'elm-service', $containerProgram) { throw "$($lineAt): a steps:background line starts with elm-server, elm-service, or docker run." }
                $step.Commands = @(Read-Command $name $block $captured $hidden $prebuilt $null $built $outputs $containerPage $made)
                if ($program -ceq $containerProgram) {
                    # docker run --rm --name <name> in the foreground: docker stop <name> ends it, and the container removes
                    # itself (design §20.7, V1c4d2, Q9 and review R4).
                    $removes = $words -ccontains '--rm'
                    $step.Container = Read-ContainerName $lineAt $words $made
                    if ($words[1] -cne 'run' -or -not $removes -or -not $step.Container -or @($words | Where-Object { $_ -cin '-d', '--detach', '--detach=true' }).Count -gt 0) {
                        throw "$($lineAt): a docker line of steps:background is docker run --rm --name <name>, without -d."
                    }
                }
                elseif ($step.Replaces) {
                    # The page restarts the program that the named page started, such as elm-service with new settings; a
                    # container may replace it instead.
                    $replaced = @($after.Plan.Steps | Where-Object Kind -EQ 'background')[0].Commands[0].Words[0]
                    if ($program -cne $replaced) { throw "$($lineAt): a How-to page's steps:background starts $replaced again, as $($after.Name) does, or docker run, not $program." }
                }
                # elm-server always serves https, with a certificate of its own key; elm-service serves http unless it is
                # given a certificate, and http on a loopback address is what its clients take outside production. A
                # container's port is published on a loopback address of the host.
                $ready = $null
                $schemes = $program -ceq 'elm-server' ? 'https' : 'https?'
                if ($attributes['ready'] -cnotmatch "^${schemes}://(127\.0\.0\.1|localhost|\[::1\])([:/?#]|$)" -or
                    -not [Uri]::TryCreate($attributes['ready'], [UriKind]::Absolute, [ref]$ready) -or $ready.Host -cnotin '127.0.0.1', 'localhost', '[::1]') {
                    throw "$($at): ready of $program is $($program -ceq 'elm-server' ? 'an https' : 'an http or https') URL on 127.0.0.1, localhost, or [::1]."
                }
                $step.Ready = $ready
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
                if (-not $attributes.ContainsKey('name') -or $attributes['name'] -cnotin $prebuiltNames) { throw "$($at): steps:prebuilt takes name=elm, name=elm-server, name=elm-service, name=packages, name=libelasticlm, or name=image." }
                # A How-to page takes the other names from the page that steps:after names; the image is its own (design
                # §20.7, V1c4d2, review R3).
                if ($isHowTo -and $attributes['name'] -cne 'image') { throw "$($at): a How-to page has no steps:prebuilt other than name=image; it takes the other names of the page that steps:after names." }
                if ($attributes['name'] -ceq 'image' -and -not $Docker) { throw "$($at): steps:prebuilt name=image runs only with -Docker." }
                if (-not $prebuilt.Add($attributes['name'])) { throw "$($at): steps:prebuilt name=$($attributes['name']) is given twice on the page." }
            }
            'after' {
                # Only a How-to page carries it, and it names only a Tutorial page, so a named page never has one: the
                # pages do not chain (design §20.7, V1c4a, Q5).
                if (-not $isHowTo) { throw "$($at): steps:after stands on a How-to page ($howToFolder) only." }
                if ($plan.Count -gt 0) { throw "$($at): steps:after stands once, before every other steps marker of the page." }
                $target = $attributes.ContainsKey('page') ? $attributes['page'] : ''
                if ($target -cnotmatch "^$([regex]::Escape($tutorialFolder))[A-Za-z0-9][A-Za-z0-9._-]*\.md$") {
                    throw "$($at): steps:after takes page=$($tutorialFolder)<page>.md, a Tutorial page by its path from the repository root."
                }
                $targetPath = Join-Path $repoRoot $target
                if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) { throw "$($at): $target does not exist." }
                $targetParsed = Get-Page $targetPath
                if (-not (Get-Marked $targetParsed)) { throw "$target has no steps." }
                $targetPlan = Read-Plan $target $targetParsed
                $after = [pscustomobject]@{ Name = $target; Path = $targetPath; Parsed = $targetParsed; Plan = $targetPlan }
                $step.After = $after
                $captured.UnionWith($targetPlan.Captured)
                $hidden.UnionWith($targetPlan.Hidden)
                $inherited.UnionWith($targetPlan.Captured)
                $inherited.UnionWith($targetPlan.Hidden)
                $prebuilt.UnionWith($targetPlan.Prebuilt)
                $built.UnionWith($targetPlan.Built)
                $outputs.UnionWith($targetPlan.Outputs)
            }
        }
        $plan.Add($step)
        # A steps:prebuilt marker runs nothing, so a steps:expect after it still checks the steps:run block before it.
        if ($marker.Kind -cne 'prebuilt') { $previous = $marker.Kind; $previousOs = $step.Os }
    }
    if (-not ($plan | Where-Object Kind -CNE 'after')) { throw "$name has no steps." }
    if (-not $libraryPath -and $prebuilt.Contains('libelasticlm')) { throw "$name needs -Library: it puts libelasticlm in the folder app." }
    if (-not $elmServerPath -and $prebuilt.Contains('elm-server')) { throw "$name needs -ElmServer: it runs elm-server." }
    if (-not $elmServicePath -and $prebuilt.Contains('elm-service')) { throw "$name needs -ElmService: it runs elm-service." }
    if (-not $feedPath -and $prebuilt.Contains('packages')) { throw "$name needs -Feed: it puts the ElasticLM packages in the folder packages." }
    if (-not $imagePath -and $prebuilt.Contains('image')) { throw "$name needs -Image: it loads the elm-service image." }
    # A steps:file block does not write into a folder that a dotnet publish line of the page writes to, before or after
    # it, so that the program run from there is the published one (design §20.7, V1c4e, review R11).
    # The paths are compared without '.' and empty parts and without case, since Windows and macOS do not tell
    # publish/app.exe from ./Publish/app.exe (review C14).
    foreach ($step in $plan | Where-Object Kind -ceq 'file') {
        $value = (($step.Attributes['path'] -split '/') | Where-Object { $_ -ne '' -and $_ -ne '.' }) -join '/'
        foreach ($folder in $outputs) {
            if ($value -ieq $folder -or $value.StartsWith("$folder/", [StringComparison]::OrdinalIgnoreCase)) {
                throw "$($name):$($step.Block.Order): steps:file does not write into $folder, which a dotnet publish line of the page writes to."
            }
        }
    }
    return [pscustomobject]@{ Steps = $plan.ToArray(); Captured = $captured; Hidden = $hidden; Prebuilt = $prebuilt; Built = $built; Outputs = $outputs; After = $after; Made = $made }
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

# The full path of cl.exe in the MSVC environment's PATH; .NET looks a bare name up in this script's own PATH, not in the
# PATH a process is given (design §20.7, V1c3c, review R2).
function Find-Cl([string]$path) {
    foreach ($folder in $path -split [IO.Path]::PathSeparator) {
        if ($folder -eq '') { continue }
        $candidate = Join-Path $folder 'cl.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw 'cl.exe is not on the PATH of the MSVC environment.'
}

# The full path of a program on this script's PATH that can be run, as the shell finds it (review A3).
function Find-Program([string]$program) {
    $found = Get-Command $program -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { throw "$program is not on the PATH." }
    return $found.Source
}

# A process in the work folder. elm-server gets no ELM_ variable: it reads ELM_<KEY> before its settings file.
# elm-service likewise, except that the background elm-service gets the run's passphrase for its signing key. An elm line
# gets the API key captured with env; no other program gets it, and this script's own environment never holds it. cl, cc,
# xattr, and a program the page built get no ELM_ variable either, so that the run's passphrase stays with elm (design
# §20.7, V1c3c, review R10); cl and the built program get the MSVC environment on Windows. A docker line runs -ContainerCli
# with no ELM_ variable except the run's passphrase as ELM_SIGNING_PASSPHRASE (design §20.7, V1c4d2, Q9).
function New-StartInfo([string]$program, [string]$kind, [string[]]$arguments, [string]$folder, [string]$apiKey, [switch]$Background, [switch]$Own) {
    $file = switch -CaseSensitive ($kind) {
        'elm' { $elmPath }
        'dotnet' { $dotnetPath }
        'elm-server' { $elmServerPath }
        'elm-service' { $elmServicePath }
        'cl' { Find-Cl (Get-Msvc)['PATH'] }
        'cc' { Find-Program 'cc' }
        'xattr' { Find-Program 'xattr' }
        # A relative path, so that it never names a file outside the work folder (Read-Command).
        'app' { Join-Path $folder $program }
        'docker' { Find-Program $ContainerCli }
    }
    $info = [Diagnostics.ProcessStartInfo]::new($file)
    # -ContainerCli wslc names a few subcommands otherwise ($containerCliMap).
    if ($kind -ceq 'docker' -and $arguments.Count -gt 0 -and $containerCliMap[$ContainerCli].ContainsKey($arguments[0])) {
        $arguments = @($containerCliMap[$ContainerCli][$arguments[0]]) + @($arguments | Select-Object -Skip 1)
    }
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    $info.WorkingDirectory = $folder
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    if ($kind -cin 'elm-server', 'elm-service', 'cl', 'cc', 'xattr', 'app', 'docker') {
        foreach ($variable in @($info.Environment.Keys)) {
            if ($variable.StartsWith('ELM_', [StringComparison]::OrdinalIgnoreCase)) { [void]$info.Environment.Remove($variable) }
        }
    }
    if ($IsWindows -and ($kind -ceq 'cl' -or ($kind -ceq 'app' -and $script:msvc))) {
        foreach ($entry in (Get-Msvc).GetEnumerator()) { $info.Environment[$entry.Key] = $entry.Value }
    }
    # NativeAOT on Windows runs the Visual Studio environment scripts, which call vswhere.exe by name from the Installer
    # folder; eng/release/Publish-Rid.ps1 puts it on PATH the same way (design §20.7, V1c4e, .cursor/rules/engineering.mdc).
    if ($IsWindows -and $kind -ceq 'dotnet') {
        $programFiles = ${env:ProgramFiles(x86)}
        $installer = $programFiles ? (Join-Path $programFiles 'Microsoft Visual Studio\Installer') : $null
        if ($installer -and (Test-Path -LiteralPath $installer)) { $info.Environment['PATH'] = "$installer;$($info.Environment['PATH'])" }
    }
    if ($kind -ceq 'elm-service' -and $Background) { $info.Environment['ELM_SIGNING_PASSPHRASE'] = $script:passphrase }
    # A docker line hands the passphrase to a container with -e ELM_SIGNING_PASSPHRASE and no value (design §20.7, V1c4d2,
    # Q9). The script's own container commands (-Own), such as the removals at the end of the page, run without it.
    if ($kind -ceq 'docker' -and -not $Own -and $script:passphrase) { $info.Environment['ELM_SIGNING_PASSPHRASE'] = $script:passphrase }
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

# Runs one command of -ContainerCli of this script's own, such as a removal at the end of the page; returns as Invoke-Step.
function Invoke-Container([string[]]$arguments) {
    return Invoke-Step (New-StartInfo $containerProgram 'docker' $arguments ([IO.Path]::GetTempPath()) -Own)
}

# Whether a container or a volume of that name exists.
function Test-Container([string]$type, [string]$objectName) {
    return (Invoke-Container @($type, 'inspect', $objectName)).ExitCode -eq 0
}

# Loads the image of -Image, an OCI layout tar, for steps:prebuilt name=image: its linux/amd64 manifest gets a docker
# manifest.json named elm-service, as eng/container/Test-Image.ps1 does, and docker load reads it (design §20.7, V1c4d2,
# reviews R8 and R9). The converted copy is removed at the end.
function Import-Image([string]$at) {
    $folder = Join-Path ([IO.Path]::GetTempPath()) "elm-steps-image-$([Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(6)))"
    try {
        $layout = Join-Path $folder 'layout'
        New-Item -ItemType Directory $layout | Out-Null
        & (Find-Program 'tar') -xf $imagePath -C $layout
        if ($LASTEXITCODE -ne 0) { throw "$($at): tar could not read -Image." }
        $platform = Get-PlatformManifest $layout $imageArch
        $archive = Join-Path $folder 'docker-archive.tar'
        New-DockerArchive $layout $platform.Manifest $imageName $archive
        $result = Invoke-Container @('load', '--input', $archive)
        if ($result.ExitCode -ne 0) { throw "$($at): $ContainerCli load exited with $($result.ExitCode)." }
        if (-not (Test-Container 'image' $imageName)) { throw "$($at): $ContainerCli load did not name the image $imageName." }
    }
    finally {
        Remove-Item -Recurse -Force -LiteralPath $folder -ErrorAction Continue
    }
}

# Fails when a container or volume that the page names exists before the page runs, so that the end of the page never
# removes one of the developer's own (design §20.7, V1c4d2, review R13).
function Assert-NoContainer([string]$name, [object]$made) {
    foreach ($container in $made.Containers) {
        if (Test-Container 'container' $container) { throw "$($name): a container named $container exists already; the page makes one of that name and removes it at its end." }
    }
    foreach ($volume in $made.Volumes) {
        if (Test-Container 'volume' $volume) { throw "$($name): a volume named $volume exists already; the page makes one of that name and removes it at its end." }
    }
}

# Removes the containers that the page named, with their anonymous volumes, and then its volumes; returns those that are
# still there after a short wait, since a container run with --rm may still be removing itself (design §20.7, V1c4d2,
# review R13).
function Remove-Container([object]$made) {
    $left = [Collections.Generic.List[string]]::new()
    foreach ($container in $made.Containers) { Remove-ContainerObject 'container' $container $left }
    foreach ($volume in $made.Volumes) { Remove-ContainerObject 'volume' $volume $left }
    return , $left.ToArray()
}

function Remove-ContainerObject([string]$type, [string]$objectName, [Collections.Generic.List[string]]$left) {
    $arguments = $type -ceq 'container' ? @('rm', '-f', '-v', $objectName) : @('volume', 'rm', '-f', $objectName)
    for ($try = 0; $try -lt 20 -and (Test-Container $type $objectName); $try++) {
        # The exit code is not read: the check of the loop and the one after it tell whether it is gone.
        [void](Invoke-Container $arguments)
        if (Test-Container $type $objectName) { Start-Sleep -Milliseconds $probeInterval }
    }
    if (Test-Container $type $objectName) { $left.Add("the $type $objectName") }
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
    # Set before anything else can fail, so that the end of the page stops the process, and a container by its name.
    $script:background = $process
    $script:backgroundContainer = $step.Container
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

# Stops the background process tree and waits for it to end. A container is stopped with docker stop <name>, which sends
# its process SIGTERM, and the docker CLI that runs it in the foreground then ends; the tree is killed only when the CLI
# has not ended within the limit of a command (design §20.7, V1c4d2, review R13).
function Stop-Background {
    if (-not $script:background) { return }
    try {
        if ($script:backgroundContainer -and -not $script:background.HasExited) {
            [void](Invoke-Container @('stop', $script:backgroundContainer))
            if ($script:background.WaitForExit([int]$commandTimeout.TotalMilliseconds)) { return }
        }
        try { $script:background.Kill($true) } catch [InvalidOperationException] { }
        $script:background.WaitForExit()
    }
    finally {
        $script:background.Dispose()
        $script:background = $null
        $script:backgroundContainer = $null
    }
}

# A marked block becomes its content, and a steps:prebuilt or steps:after marker its line without the spaces around it.
function Get-TranslationItem([object]$item) {
    return $item.PSObject.Properties['Text'] ? "line`n" + $item.Text.Trim() : "block`n" + ($item.Lines -join "`n")
}

# The translation of a page by its path from the repository root, or $null for a page that has none.
function Get-TranslationName([string]$path) {
    $relative = [IO.Path]::GetRelativePath($repoRoot, [IO.Path]::GetFullPath($path)).Replace('\', '/')
    $translated = $relative -eq 'README.md' ? 'README.ja.md' : ($relative -replace '^docs/guide/', 'docs/guide.ja/')
    return $translated -eq $relative ? $null : $translated
}

# A page and its translation name the PostgreSQL image by the digest of .github/workflows/ci.yml, so that the server a
# reader starts is the one the tests run on (design §20.7, V1c4d2, Q10). Checked for every page, also one skipped without
# -Docker, so that the job without -Docker finds a digest that differs.
function Test-PostgresDigest([string]$path) {
    $translated = Get-TranslationName $path
    foreach ($file in @($path) + @(if ($translated) { Join-Path $repoRoot $translated })) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        $lines = [IO.File]::ReadAllLines($file)
        for ($i = 0; $i -lt $lines.Length; $i++) {
            foreach ($reference in [regex]::Matches($lines[$i], "$([regex]::Escape($postgresReference))[^\s""'``)\]]*")) {
                if ($ciPostgres.Count -ne 1) { throw '.github/workflows/ci.yml does not pin postgres:18 by one digest.' }
                if ($reference.Value -cne $ciPostgres[0]) { throw "$(Get-PageName $file):$($i + 1): postgres:18 is pinned by another digest than in .github/workflows/ci.yml." }
            }
        }
    }
}

function Test-Translation([string]$path, [object]$parsed) {
    $translated = Get-TranslationName $path
    if (-not $translated) { return }
    $translatedPath = Join-Path $repoRoot $translated
    if (-not (Test-Path $translatedPath)) { throw "$(Get-PageName $path) has no translation at $translated." }
    $translation = Get-Page $translatedPath
    $theirs = @(@($translation.Blocks) + @($translation.LineMarkers) | Sort-Object Order | ForEach-Object { Get-TranslationItem $_ })
    $next = 0
    foreach ($item in Get-Marked $parsed) {
        $content = Get-TranslationItem $item
        while ($next -lt $theirs.Count -and $theirs[$next] -cne $content) { $next++ }
        if ($next -ge $theirs.Count) {
            $what = $item.PSObject.Properties['Text'] ? "steps:$($item.Kind) marker" : 'code block'
            throw "$($translated): the $what of $(Get-PageName $path):$($item.Order) is missing, changed, or out of order."
        }
        $next++
    }
}

# Runs the steps of a page in the work folder. $state carries the captured values (Values), the value captured with env for
# the elm lines after it (ApiKey), and the standard output of the last command (LastOutput); a How-to page starts with those
# of the page that its steps:after names (Invoke-Page).
function Invoke-Plan([string]$name, [object[]]$steps, [object]$state, [string]$work) {
    $values = $state.Values
    foreach ($step in $steps) {
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
                    try { $result = Invoke-Step (New-StartInfo $command.Words[0] $command.Kind (Resolve-Argument $command.Words $values) $work $state.ApiKey) }
                    catch {
                        # The reason a compiler or a tool cannot be found holds no argument, output, or value.
                        $reason = $command.Kind -cin 'cl', 'cc', 'xattr', 'docker' ? ": $($_.Exception.Message)" : '.'
                        throw "$($name):$($command.Line): $($command.Shown) could not be run$reason"
                    }
                    if ($result.ExitCode -ne $expected) {
                        throw "$($name):$($command.Line): $($command.Shown) exited with $($result.ExitCode)$($expected -ne 0 ? ", not $expected" : '')."
                    }
                    "ok: $($name):$($command.Line): $($command.Shown)$($expected -ne 0 ? " (exit $expected)" : '')"
                    $state.LastOutput = $result.Output
                }
                if ($step.Attributes.ContainsKey('capture')) {
                    $capture = $step.Attributes['capture']
                    $captured = Get-CapturedValue "$($name):$($last.Line)" $step $result
                    if ($step.Attributes.ContainsKey('env')) {
                        $state.ApiKey = $captured
                        "ok: $($name):$($last.Line): captured $capture for $apiKeyVariable"
                    }
                    else {
                        $values[$capture] = $captured
                        "ok: $($name):$($last.Line): captured $capture"
                    }
                }
            }
            'background' {
                if ($step.Replaces) {
                    # Stopped and waited for as at the end of a page, so that the new process can open the same database file
                    # and listen on the same port.
                    Stop-Background
                    "ok: $($name):$($block.Line - 1): stopped the background process"
                }
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
                $output = @($state.LastOutput -split '\r?\n' | ForEach-Object { $_.TrimEnd() })
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
                    if ($IsMacOS) {
                        # The state of a reader who downloaded the archive with a browser: tar gives the archive's
                        # quarantine attribute to every file it extracts, so the page's xattr line has one to remove
                        # (design §20.7, V1c3c, Q6; the owner's review R2 on PR #237).
                        $quarantine = '0083;{0:x};Safari;{1}' -f [DateTimeOffset]::UtcNow.ToUnixTimeSeconds(), [guid]::NewGuid().ToString().ToUpperInvariant()
                        foreach ($file in Get-ChildItem -LiteralPath $target -File) {
                            & (Find-Program 'xattr') -w com.apple.quarantine $quarantine $file.FullName
                            if ($LASTEXITCODE -ne 0) { throw "$($name):$($block.Order): xattr could not mark $($file.Name) as downloaded." }
                        }
                    }
                }
                elseif ($prebuiltName -ceq 'image') {
                    Import-Image "$($name):$($block.Order)"
                }
                $argumentName = switch -CaseSensitive ($prebuiltName) { 'elm' { '-Elm' } 'elm-server' { '-ElmServer' } 'elm-service' { '-ElmService' } 'packages' { '-Feed' } 'libelasticlm' { '-Library' } 'image' { '-Image' } }
                "ok: $($name):$($block.Order): prebuilt $prebuiltName ($argumentName)"
            }
        }
    }
}

# Copies a folder with its files, keeping their protection: on Windows the access rules of every folder and file that does
# not inherit them, and on Linux and macOS the mode of every folder and file. elm-server checks the protection of its data
# folder at each start (design §20.7, V1c13, review R1). The target must not exist yet. A link fails: the steps make none,
# and following one could copy what is outside the work folder.
function Copy-Protected([string]$source, [string]$target) {
    $folder = [IO.DirectoryInfo]::new($source)
    [void][IO.Directory]::CreateDirectory($target)
    # On Windows the folder's rules come first, so that what is copied into it inherits them as in the source.
    if ($IsWindows) { Copy-Protection $folder.FullName $target -Folder }
    foreach ($item in $folder.EnumerateFileSystemInfos()) {
        if ($item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw "The work folder holds a link, $($item.FullName), which the copy does not follow." }
        $to = Join-Path $target $item.Name
        if ($item -is [IO.DirectoryInfo]) {
            Copy-Protected $item.FullName $to
        }
        else {
            # On Linux and macOS, File.Copy keeps the file's mode.
            [IO.File]::Copy($item.FullName, $to)
            if ($IsWindows) { Copy-Protection $item.FullName $to }
        }
    }
    # On Linux and macOS the folder's mode comes last, so that a folder without the owner's write can still be filled.
    if (-not $IsWindows) { Copy-Protection $folder.FullName $target -Folder }
}

function Copy-Protection([string]$source, [string]$target, [switch]$Folder) {
    if ($IsWindows) {
        $rules = $Folder ? [IO.FileSystemAclExtensions]::GetAccessControl([IO.DirectoryInfo]::new($source)) : [IO.FileSystemAclExtensions]::GetAccessControl([IO.FileInfo]::new($source))
        # A folder or file that inherits its rules gets the same from its new parent, which was copied first. The data
        # folders the steps make (ProtectedFiles) inherit none.
        if (-not $rules.AreAccessRulesProtected) { return }
        $copy = $Folder ? [Security.AccessControl.DirectorySecurity]::new() : [Security.AccessControl.FileSecurity]::new()
        $copy.SetSecurityDescriptorSddlForm($rules.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access), [Security.AccessControl.AccessControlSections]::Access)
        if ($Folder) { [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($target), $copy) }
        else { [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($target), $copy) }
    }
    else {
        [IO.File]::SetUnixFileMode($target, [IO.File]::GetUnixFileMode($source))
    }
}

# Runs a page in new folders, or, for a How-to page whose Tutorial ran earlier in this run, in a copy of that Tutorial's end
# state (design §20.7, V1c13). A Tutorial page that a later How-to page of the run names keeps its folders as the source of
# those copies, until the last such page is done.
function Invoke-Page([string]$path) {
    $name = Get-PageName $path
    if ($script:ran.Contains($name)) {
        "ok: $($name): ran earlier in this run"
        return
    }
    $parsed = Get-Page $path
    if (-not (Get-Marked $parsed)) { throw "$name has no steps." }
    $plan = Read-Plan $name $parsed
    Test-Translation $path $parsed
    $source = $null
    if ($plan.After) {
        Test-Translation $plan.After.Path $plan.After.Parsed
        # The Tutorial runs once in the run, and is reported as its own page, also when the list does not hold it.
        if (-not $script:sources.ContainsKey($plan.After.Name)) { Invoke-Page $plan.After.Path }
        $source = $script:sources[$plan.After.Name]
        # The count of the pages that name it keeps its folders until here; a page that is not counted is a bug of this script.
        if (-not $source) { throw "$($name): $($plan.After.Name) kept no folders to copy." }
    }
    # The containers and volumes that the page's docker lines name are removed at its end, also when it fails, but never one
    # that was there before it (design §20.7, V1c4d2, review R13).
    $made = $plan.Made
    $makes = $made.Containers.Count + $made.Volumes.Count -gt 0
    if ($makes) { Assert-NoContainer $name $made }
    $containersLeft = @()

    $suffix = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(6))
    $work = Join-Path ([IO.Path]::GetTempPath()) "elm-steps-$suffix"
    $packages = Join-Path ([IO.Path]::GetTempPath()) "elm-steps-packages-$suffix"
    $saved = @{}
    foreach ($variable in 'ELM_KEY_PASSPHRASE', 'NUGET_PACKAGES', 'MSBUILDDISABLENODEREUSE', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO') {
        $saved[$variable] = [Environment]::GetEnvironmentVariable($variable)
    }
    $left = @()
    $completed = $false
    $keep = $false
    try {
        if ($source) {
            # The copy's keys were made with the Tutorial's passphrase.
            Copy-Protected $source.Work $work
            Copy-Protected $source.Packages $packages
            $script:passphrase = $source.Passphrase
            $state = [pscustomobject]@{ Values = $source.Values.Clone(); ApiKey = $source.ApiKey; LastOutput = $null }
        }
        else {
            New-Item -ItemType Directory $work, $packages | Out-Null
            $script:passphrase = 'steps ' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(8))
            # A local of the page, so the next page never gets its values or its API key.
            $state = [pscustomobject]@{ Values = @{}; ApiKey = $null; LastOutput = $null }
        }
        $env:ELM_KEY_PASSPHRASE = $script:passphrase
        $env:NUGET_PACKAGES = $packages
        # A build node that outlives the run would hold files in the work folder.
        $env:MSBUILDDISABLENODEREUSE = '1'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DOTNET_NOLOGO = '1'
        if ($source) {
            # The Tutorial's steps ran once already: start its background process again in the copy, then the page's steps.
            "ok: $($name): copy of $($plan.After.Name)"
            if ($source.Background) { Start-Background $plan.After.Name $source.Background $state.Values $work }
        }
        Invoke-Plan $name @($plan.Steps | Where-Object Kind -cne 'after') $state $work
        $completed = $true
        $script:ran.Add($name) | Out-Null
        if ($script:wanted.ContainsKey($name)) {
            $background = @($plan.Steps | Where-Object Kind -ceq 'background') | Select-Object -First 1
            $script:sources[$name] = [pscustomobject]@{ Work = $work; Packages = $packages; Passphrase = $script:passphrase; Values = $state.Values.Clone(); ApiKey = $state.ApiKey; Background = $background }
            $keep = $true
        }
    }
    finally {
        Stop-Background
        if ($makes) {
            $containersLeft = Remove-Container $made
            if (-not $completed -and $containersLeft) { Write-Host "::error::$(Get-ContainerLeftMessage $name $containersLeft)" }
        }
        $script:passphrase = $null
        foreach ($variable in $saved.Keys) { [Environment]::SetEnvironmentVariable($variable, $saved[$variable]) }
        if (-not $keep) {
            # A copy that failed part way may not have made both folders yet.
            $made = @(@($work, $packages) | Where-Object { Test-Path -LiteralPath $_ })
            if ($made) { Remove-Item -Recurse -Force -LiteralPath $made -ErrorAction Continue }
            # A folder that is still there, such as one a process still holds open, would keep keys and secrets. When a step
            # failed, its error is the one that ends the run, so the folders are named here; otherwise the error below names
            # them.
            $left = @(@($work, $packages) | Where-Object { Test-Path -LiteralPath $_ })
            if (-not $completed -and $left) { Write-Host "::error::$(Get-LeftMessage $name $left)" }
        }
    }
    if ($containersLeft) {
        if ($left) { Write-Host "::error::$(Get-ContainerLeftMessage $name $containersLeft)" }
        else { throw (Get-ContainerLeftMessage $name $containersLeft) }
    }
    if ($left) { throw (Get-LeftMessage $name $left) }
    if ($plan.After) {
        # The last page of the run that names the Tutorial removes the source of the copies, which holds its keys.
        $script:wanted[$plan.After.Name]--
        if ($script:wanted[$plan.After.Name] -eq 0) { Remove-Source $plan.After.Name }
    }
}

# Removes the folders that a Tutorial page kept for the copies; fails when one is still there.
function Remove-Source([string]$name) {
    $source = $script:sources[$name]
    if (-not $source) { return }
    $script:sources.Remove($name)
    Remove-Item -Recurse -Force -LiteralPath $source.Work, $source.Packages -ErrorAction Continue
    $left = @(@($source.Work, $source.Packages) | Where-Object { Test-Path -LiteralPath $_ })
    if ($left) { throw (Get-LeftMessage $name $left) }
}

# The error for the containers and volumes of a page that were not removed.
function Get-ContainerLeftMessage([string]$name, [string[]]$objects) {
    return "$($name): $($objects -join ' and ') $($objects.Count -eq 1 ? 'was' : 'were') not removed."
}

# The error for the work folders that were not removed, in the singular or the plural.
function Get-LeftMessage([string]$name, [string[]]$folders) {
    return $folders.Count -eq 1 ? "$($name): the folder $($folders[0]) was not removed." : "$($name): the folders $($folders -join ' and ') were not removed."
}

$script:background = $null
# The container that the background process runs, or $null.
$script:backgroundContainer = $null
$script:msvc = $null
# The run's passphrase of the page that runs: ELM_KEY_PASSPHRASE of its elm lines, and the background elm-service's
# ELM_SIGNING_PASSPHRASE.
$script:passphrase = $null
# The pages that ran, the Tutorial pages that later How-to pages of the run name with the number of such pages, and the
# folders and values that such a Tutorial page kept (design §20.7, V1c13).
$script:ran = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$script:wanted = @{}
$script:sources = @{}
$failed = $false
$errors = [Collections.Generic.List[string]]::new()
try {
    # Without -Docker, a page that runs the container CLI is skipped as a whole, and does not count as a page that names a
    # Tutorial (design §20.7, V1c4d2, review R1).
    $skipped = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if (-not $Docker) {
        foreach ($path in $Page) { if (Test-ContainerPage $path) { [void]$skipped.Add((Get-PageName $path)) } }
    }
    # Counted with the parser that Read-Plan uses, over the pages without repeats, since a page listed again does not run
    # again (review C1, C9).
    $counted = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $Page) {
        if ($skipped.Contains((Get-PageName $path)) -or -not $counted.Add((Get-PageName $path))) { continue }
        foreach ($marker in @((Get-Page $path).LineMarkers | Where-Object Kind -ceq 'after')) {
            $named = (Read-Marker "$(Get-PageName $path):$($marker.Order)" $marker.Text.TrimEnd()).Attributes['page']
            if ($named) { $script:wanted[$named] = 1 + ($script:wanted[$named] ?? 0) }
        }
    }
    foreach ($path in $Page) {
        Test-PostgresDigest $path
        if ($skipped.Contains((Get-PageName $path))) {
            "skip: $(Get-PageName $path)"
            continue
        }
        Invoke-Page $path
    }
}
catch {
    $failed = $true
    $errors.Add($_.Exception.Message)
}
finally {
    # The sources hold keys: remove them also after a failure.
    foreach ($name in @($script:sources.Keys)) {
        try { Remove-Source $name }
        catch { $failed = $true; $errors.Add($_.Exception.Message) }
    }
}
if ($failed) {
    foreach ($message in $errors) { Write-Host "::error::$message" }
    exit 1
}
exit 0
