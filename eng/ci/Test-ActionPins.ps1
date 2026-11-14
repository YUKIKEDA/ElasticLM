#Requires -Version 7
<#
.SYNOPSIS
Checks that every action of the workflows is pinned to the commit of its version tag.

.DESCRIPTION
design §20.5 ("アクションの固定の確かめ", V1d22) and §5.3 T25. The workflow files, and the definitions of the composite
actions they use, are read as YAML by tools/ElasticLM.ActionPins (YamlDotNet), which also refuses what this check does
not read (anchors, aliases, tags, merge keys, a uses key outside a step or a job, a watched key in another case, and
U+0085, U+2028, U+2029, or a lone CR in the file). For every jobs.<id>.steps[*].uses and runs.steps[*].uses, the script
checks:

1. The value is <owner>/<repo>[/<path>]@<40 lowercase hex digits>, and the rest of its line is a comment # vX.Y.Z.
   A path segment is never empty, "." or "..". A local action, a docker:// image, and a reusable workflow (also
   jobs.<id>.uses) are refused.
2. The tag vX.Y.Z of https://github.com/<owner>/<repo> points to that commit (git ls-remote; for an annotated tag, the
   commit it peels to). A commit that only a fork holds is served under the parent's name too, so a check that the
   commit exists would pass it; a check against the tag does not.
3. The action's action.yml (or action.yaml) at that commit, fetched with git: a composite action has its steps checked
   the same way, and a docker action must not run a docker:// image without a digest.
4. Every actions/checkout step has with.persist-credentials equal to the scalar false, so the GITHUB_TOKEN is not left
   in .git/config.

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

# A path segment is never empty, "." or "..": actions/checkout/.@<sha> names the same action as actions/checkout@<sha>.
$pinnedPattern = '^(?<repo>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)(?<path>(?:/(?!\.\.?(?:/|@))[A-Za-z0-9_.-]+)+)?@(?<sha>[0-9a-f]{40})$'
$commentPattern = '^\s+#\s*(?<version>\S+)\s*$'
$versionPattern = '^v\d+\.\d+\.\d+$'

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
            'job-uses' {
                $script:usesCount++
                Add-Failure $where ([int]$record[2]) "$($record[3]) calls a reusable workflow, which this check does not cover; change design §20.5 and this script first."
            }
            'uses' {
                $script:usesCount++
                Test-Use $where ([int]$record[2]) $record[3] $record[4] $record[5] $Depth
            }
            'runs' {
            }
            default {
                Add-Failure $where 0 "the YAML reader wrote a line this script does not know: $($record[0])"
            }
        }
    }
}

function Test-Use([string]$Where, [int]$Line, [string]$Target, [string]$Rest, [string]$Persist, [int]$Depth) {
    if ($Target.StartsWith('./') -or $Target.StartsWith('docker://')) {
        Add-Failure $Where $Line "$Target is a local action or a docker:// image, which this check does not cover; change design §20.5 and this script first."
        return
    }
    if ($Target -notmatch $pinnedPattern) {
        Add-Failure $Where $Line "$Target is not pinned to a full commit SHA (40 lowercase hex digits)."
        return
    }
    $repo = $Matches.repo
    $folder = $Matches.ContainsKey('path') ? $Matches.path : ''
    $sha = $Matches.sha
    if ($folder -match '\.ya?ml$') {
        Add-Failure $Where $Line "$Target calls a reusable workflow, which this check does not cover; change design §20.5 and this script first."
        return
    }
    if ($Rest -notmatch $commentPattern -or $Matches.version -notmatch $versionPattern) {
        Add-Failure $Where $Line "$Target has no version comment of the form '# vX.Y.Z' after it on its line."
        return
    }
    $version = ($Rest -replace $commentPattern, '${version}')
    try {
        $tagCommit = Get-TagCommit $repo $version
    }
    catch {
        Add-Failure $Where $Line "the tag $version of $repo could not be read: $($_.Exception.Message)"
        return
    }
    if ($null -eq $tagCommit) {
        Add-Failure $Where $Line "$repo has no tag $version."
        return
    }
    if ($tagCommit -cne $sha) {
        Add-Failure $Where $Line "the tag $version of $repo points to $tagCommit, not to $sha."
        return
    }
    $pinOk = $true
    # Any path under actions/checkout counts, and so does a name in another case or with .git: the check is not the
    # place to learn which of these GitHub resolves to the same action.
    if ($repo -match '^actions/checkout(?:\.git)?$' -and $Persist -cne 'false') {
        Add-Failure $Where $Line 'actions/checkout does not set with.persist-credentials to false.'
        $pinOk = $false
    }
    $definitionKey = "$repo$folder@$sha"
    if (-not $checkedDefinitions.Add($definitionKey)) {
        if ($pinOk) {
            Write-Output "ok: ${Where}:${Line}: $definitionKey # $version"
        }
        return
    }
    try {
        $definition = Get-ActionDefinition $repo $folder $sha
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
    $image = $runs.Count -eq 1 ? $runs[0][3] : ''
    if ($pinOk) {
        Write-Output "ok: ${Where}:${Line}: $definitionKey # $version (runs.using: $using)"
    }
    $definitionName = "$definitionKey/" + (Split-Path -Leaf $definition).Split('-', 3)[2]
    if ($using -eq 'composite' -and $Depth -ge 5) {
        Add-Failure $Where $Line "$definitionKey nests composite actions more than 5 deep."
        return
    }
    if ($using -ne 'composite' -and @($records | Where-Object { $_[0] -eq 'uses' }).Count -gt 0) {
        Add-Failure $Where $Line "$definitionKey has steps with uses, but runs.using is $using, not composite."
        return
    }
    Test-Records $records @{ $definition = $definitionName } ($Depth + 1)
    if ($using -eq 'docker' -and $image.StartsWith('docker://') -and -not $image.Contains('@sha256:')) {
        Add-Failure $Where $Line "$definitionKey runs the image $image, which is not pinned by digest."
    }
    elseif ($using -eq '') {
        Add-Failure $Where $Line "$definitionKey has no runs.using."
    }
}

$folderPath = [IO.Path]::GetFullPath($WorkflowPath)
# -Force: on Linux a file whose name starts with a dot is hidden, and such a workflow file is checked too.
$files = @(Get-ChildItem -LiteralPath $folderPath -File -Force | Where-Object Extension -In '.yml', '.yaml' | Sort-Object Name)
if ($files.Count -eq 0) {
    Write-Output "error: $folderPath holds no workflow file."
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
