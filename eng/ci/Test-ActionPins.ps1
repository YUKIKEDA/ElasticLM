#Requires -Version 7
<#
.SYNOPSIS
Checks that every action of the workflows is pinned to the commit of its version tag.

.DESCRIPTION
design §20.5 ("アクションの固定の確かめ", V1d22) and §5.3 T25. The workflow files, and the definitions of the composite
actions they use, are read as YAML by tools/ElasticLM.ActionPins (YamlDotNet). The tool checks everything that needs no
network and is tested in tests/ElasticLM.ActionPins.Tests: what it does not read (anchors, aliases, tags, merge keys, a
stray uses key, a watched key in another case, U+0085, U+2028, U+2029, or a lone CR), the form of each uses value
(<owner>/<repo>[/<path>]@<40 lowercase hex digits> and a comment # vX.Y.Z), persist-credentials: false on every
actions/checkout, and an action's runs mapping. This script adds what needs the actions' repositories:

1. The tag vX.Y.Z of https://github.com/<owner>/<repo> points to the pinned commit (git ls-remote; for an annotated tag,
   the commit it peels to). A commit that only a fork holds is served under the parent's name too, so a check that the
   commit exists would pass it; a check against the tag does not.
2. The action's action.yml (or action.yaml) at that commit, fetched with git, is read by the tool as an action; a
   composite action has its steps checked the same way, at most 5 deep.

It uses PowerShell 7, the .NET SDK (to build the tool), and git, with no token, and reaches github.com over HTTPS. It
reports every failure, then exits with 1 if there was any.

.PARAMETER WorkflowPath
The folder whose *.yml and *.yaml files are checked. The default is the repository's .github/workflows.
#>
[CmdletBinding()]
param(
    [string]$WorkflowPath = (Join-Path $PSScriptRoot '../../.github/workflows')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# git must never stop to ask for credentials: an unknown repository answers like a private one, and Git Credential
# Manager on Windows would open a sign-in window.
$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'never'
[string[]]$gitConfig = @('-c', 'credential.helper=')

$failures = [Collections.Generic.List[string]]::new()
$tagCommits = @{}
$definitions = @{}
$checkedDefinitions = [Collections.Generic.HashSet[string]]::new()
$usesCount = 0
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('action-pins-' + [IO.Path]::GetRandomFileName())
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$toolProject = Join-Path $repoRoot 'tools/ElasticLM.ActionPins/ElasticLM.ActionPins.csproj'
$toolPath = Join-Path $workRoot 'tool/ElasticLM.ActionPins.dll'

function Add-Failure([string]$Where, [int]$Line, [string]$Message) {
    $failures.Add("${Where}:${Line}: $Message")
    if ($env:GITHUB_ACTIONS -eq 'true' -and -not $Where.Contains('@')) {
        Write-Output "::error file=$Where,line=$Line::$Message"
    }
    else {
        Write-Output "error: ${Where}:${Line}: $Message"
    }
}

# Runs the YAML reader on the files and returns its lines, each split at its tabs.
function Read-Yaml([string]$Mode, [string[]]$Paths) {
    $lines = & dotnet $toolPath $Mode @Paths
    if ($LASTEXITCODE -ne 0) {
        throw "the YAML reader failed with exit code $LASTEXITCODE"
    }
    return , @($lines | Where-Object { $_ } | ForEach-Object { , ([string]$_).Split("`t") })
}

# Returns the commit that refs/tags/<Tag> of the repository points to, or $null when there is no such tag. Throws when
# git cannot reach the repository.
function Get-TagCommit([string]$Repo, [string]$Tag) {
    $key = "$Repo@$Tag"
    if (-not $tagCommits.ContainsKey($key)) {
        $lines = & git @gitConfig ls-remote "https://github.com/$Repo" "refs/tags/$Tag" "refs/tags/$Tag^{}" 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "git ls-remote https://github.com/$Repo failed with exit code ${LASTEXITCODE}: $($lines -join ' ')"
        }
        # ls-remote matches its patterns against the tail of each ref name, so only a line whose name is the same is used.
        $direct = $null
        $peeled = $null
        foreach ($entry in $lines) {
            $parts = ([string]$entry).Split("`t")
            if ($parts.Count -ne 2) {
                continue
            }
            if ($parts[1] -ceq "refs/tags/$Tag^{}") {
                $peeled = $parts[0]
            }
            elseif ($parts[1] -ceq "refs/tags/$Tag") {
                $direct = $parts[0]
            }
        }
        $tagCommits[$key] = $null -ne $peeled ? $peeled : $direct
    }
    return $tagCommits[$key]
}

# Writes the action definition at the commit to a file of its own and returns that file's path, or $null when the
# commit has neither action.yml nor action.yaml in that folder. Throws when git cannot fetch the commit or the file.
function Get-ActionDefinition([string]$Repo, [string]$Folder, [string]$Sha) {
    $key = "$Repo$Folder@$Sha"
    if (-not $definitions.ContainsKey($key)) {
        $clone = Join-Path $workRoot ($Repo.Replace('/', '_') + '_' + $Sha)
        if (-not (Test-Path -LiteralPath $clone)) {
            New-Item -ItemType Directory -Path $clone | Out-Null
            & git init -q $clone
            if ($LASTEXITCODE -ne 0) {
                throw "git init failed with exit code $LASTEXITCODE"
            }
            & git -C $clone remote add origin "https://github.com/$Repo"
            # Only the commit, and only the blobs that are read: the blob of action.yml comes on demand.
            $out = & git -C $clone @gitConfig fetch -q --depth 1 --filter=blob:none origin $Sha 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "git fetch of $Repo@$Sha failed with exit code ${LASTEXITCODE}: $($out -join ' ')"
            }
        }
        $definitions[$key] = $null
        $prefix = $Folder.TrimStart('/')
        foreach ($name in 'action.yml', 'action.yaml') {
            $file = $prefix.Length -gt 0 ? "$prefix/$name" : $name
            # The tree came with the fetch, so ls-tree tells a missing file apart from a blob that could not be fetched.
            $entry = & git -C $clone ls-tree $Sha -- $file 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "git ls-tree of $Repo@$Sha failed with exit code ${LASTEXITCODE}: $($entry -join ' ')"
            }
            if ([string]::IsNullOrWhiteSpace(($entry -join ''))) {
                continue
            }
            # The bytes go to the file as git gives them: PowerShell's own redirection would decode and encode them.
            $target = Join-Path $workRoot ('definition-' + $definitions.Count + '-' + $name)
            $start = [Diagnostics.ProcessStartInfo]::new('git')
            foreach ($argument in @('-C', $clone) + $gitConfig + @('cat-file', 'blob', "${Sha}:$file")) {
                $start.ArgumentList.Add($argument)
            }
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $process = [Diagnostics.Process]::Start($start)
            $stream = [IO.File]::Create($target)
            try {
                $process.StandardOutput.BaseStream.CopyTo($stream)
            }
            finally {
                $stream.Dispose()
            }
            $errorText = $process.StandardError.ReadToEnd()
            $process.WaitForExit()
            if ($process.ExitCode -ne 0) {
                throw "the file $file is in the tree of $Repo@$Sha, but git could not read it (exit code $($process.ExitCode)): $errorText"
            }
            $definitions[$key] = $target
            break
        }
    }
    return $definitions[$key]
}

# Checks the lines of the YAML reader for one or more files. $Names maps the path the reader was given to the name in
# the messages.
function Test-Records([object[]]$Records, [hashtable]$Names, [int]$Depth) {
    foreach ($record in $Records) {
        $where = $Names.ContainsKey($record[1]) ? $Names[$record[1]] : $record[1]
        switch ($record[0]) {
            'error' {
                Add-Failure $where ([int]$record[2]) $record[3]
            }
            'pin' {
                $script:usesCount++
                Test-Pin $where ([int]$record[2]) $record[3] $record[4] $record[5] $record[6] $Depth
            }
            'runs' {
            }
            default {
                Add-Failure $where 0 "the YAML reader wrote a line this script does not know: $($record[0])"
            }
        }
    }
}

# The tool has checked the form; this compares the pin with its tag and reads the action at the pinned commit.
function Test-Pin([string]$Where, [int]$Line, [string]$Repo, [string]$Folder, [string]$Sha, [string]$Version, [int]$Depth) {
    try {
        $tagCommit = Get-TagCommit $Repo $Version
    }
    catch {
        Add-Failure $Where $Line "the tag $Version of $Repo could not be read: $($_.Exception.Message)"
        return
    }
    if ($null -eq $tagCommit) {
        Add-Failure $Where $Line "$Repo has no tag $Version."
        return
    }
    if ($tagCommit -cne $Sha) {
        Add-Failure $Where $Line "the tag $Version of $Repo points to $tagCommit, not to $Sha."
        return
    }
    $definitionKey = "$Repo$Folder@$Sha"
    if (-not $checkedDefinitions.Add($definitionKey)) {
        Write-Output "ok: ${Where}:${Line}: $definitionKey # $Version"
        return
    }
    try {
        $definition = Get-ActionDefinition $Repo $Folder $Sha
    }
    catch {
        Add-Failure $Where $Line "the action definition of $definitionKey could not be read: $($_.Exception.Message)"
        return
    }
    if ($null -eq $definition) {
        Add-Failure $Where $Line "$definitionKey has no action.yml or action.yaml."
        return
    }
    $records = Read-Yaml 'action' @($definition)
    $runs = @($records | Where-Object { $_[0] -eq 'runs' })
    $using = $runs.Count -eq 1 ? $runs[0][2] : ''
    Write-Output "ok: ${Where}:${Line}: $definitionKey # $Version (runs.using: $using)"
    # A composite action may use another; the limit ends a chain that loops through actions that point at each other.
    if ($Depth -ge 5 -and @($records | Where-Object { $_[0] -eq 'pin' }).Count -gt 0) {
        Add-Failure $Where $Line "$definitionKey nests composite actions more than 5 deep."
        return
    }
    $definitionName = "$definitionKey/" + (Split-Path -Leaf $definition).Split('-', 3)[2]
    Test-Records $records @{ $definition = $definitionName } ($Depth + 1)
}

$folderPath = [IO.Path]::GetFullPath($WorkflowPath)
# -Force: on Linux a file whose name starts with a dot is hidden, and such a workflow file is checked too.
$files = @(Get-ChildItem -LiteralPath $folderPath -File -Force | Where-Object Extension -In '.yml', '.yaml' | Sort-Object Name)
if ($files.Count -eq 0) {
    Write-Output "error: $folderPath holds no workflow file."
    exit 1
}
# A control character in a path would split the tool's tab-separated lines or end the ::error command early.
$badNames = @($files | Where-Object { $_.FullName -match '\p{Cc}' })
if ($badNames.Count -gt 0) {
    Write-Output "error: $($badNames.Count) workflow file names hold a control character."
    exit 1
}
try {
    New-Item -ItemType Directory -Path $workRoot | Out-Null
    $build = & dotnet build $toolProject -c Release --nologo -v q -o (Split-Path $toolPath) 2>&1
    if ($LASTEXITCODE -ne 0) {
        $build | Write-Output
        Write-Output "error: the YAML reader (tools/ElasticLM.ActionPins) did not build."
        exit 1
    }
    $names = @{}
    foreach ($file in $files) {
        $names[$file.FullName] = [IO.Path]::GetRelativePath($repoRoot, $file.FullName).Replace('\', '/')
    }
    Test-Records (Read-Yaml 'workflow' @($files.FullName)) $names 0
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}

Write-Output "$($files.Count) workflow files, $usesCount uses: values (composite actions included), $($checkedDefinitions.Count) distinct actions, $($failures.Count) failures."
if ($usesCount -eq 0 -and $failures.Count -eq 0) {
    Write-Output 'error: no uses: value was found.'
    exit 1
}
if ($failures.Count -gt 0) {
    exit 1
}
exit 0
