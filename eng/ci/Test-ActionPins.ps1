#Requires -Version 7
<#
.SYNOPSIS
Checks that every action of the workflows is pinned to the commit of its version tag.

.DESCRIPTION
design §20.5 ("アクションの固定の確かめ", V1d22) and §5.3 T25. For every uses: line of the workflow files, and of the
composite actions they use, the script checks:

1. The line has the form <owner>/<repo>[/<path>]@<40 lowercase hex digits> # vX.Y.Z. A line that holds uses: in
   another form fails too, so that a line the script cannot read is never passed unchecked.
2. The tag vX.Y.Z of https://github.com/<owner>/<repo> points to that commit (git ls-remote; for an annotated tag, the
   commit it peels to). A commit that only a fork holds is served under the parent's name too, so a check that the
   commit exists would pass it; a check against the tag does not.
3. The action's action.yml (or action.yaml) at that commit, fetched with git: a composite action has its own uses:
   lines checked the same way, and a docker action must not run a docker:// image without a digest.
4. Every actions/checkout step sets persist-credentials: false, so the GITHUB_TOKEN is not left in .git/config.

It uses only PowerShell 7 and git, with no token, and reaches github.com over HTTPS. It reports every failure, then
exits with 1 if there was any.

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

$usesPattern = '^\s*(?:-\s+)?uses:\s*(?<quote>[''"]?)(?<target>[^\s#''"]+)\k<quote>\s*(?:#\s*(?<comment>.*?))?\s*$'
$pinnedPattern = '^(?<repo>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)(?<path>/[A-Za-z0-9_./-]+)?@(?<sha>[0-9a-f]{40})$'
$versionPattern = '^v\d+\.\d+\.\d+$'

$failures = [Collections.Generic.List[string]]::new()
$tagCommits = @{}
$definitions = @{}
$checkedDefinitions = [Collections.Generic.HashSet[string]]::new()
$usesCount = 0
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('action-pins-' + [IO.Path]::GetRandomFileName())

function Add-Failure([string]$Where, [int]$Line, [string]$Message) {
    $failures.Add("${Where}:${Line}: $Message")
    if ($env:GITHUB_ACTIONS -eq 'true' -and -not $Where.Contains('@')) {
        Write-Output "::error file=$Where,line=$Line::$Message"
    }
    else {
        Write-Output "error: ${Where}:${Line}: $Message"
    }
}

function Get-Indent([string]$Text) {
    return $Text.Length - $Text.TrimStart(' ').Length
}

function Test-Content([string]$Text) {
    $trimmed = $Text.Trim()
    return $trimmed.Length -gt 0 -and -not $trimmed.StartsWith('#')
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

# Returns the lines of the action definition at the commit, or $null when the commit has neither action.yml nor
# action.yaml in that folder. Throws when git cannot fetch the commit.
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
            $text = & git -C $clone @gitConfig show "${Sha}:$file" 2>$null
            if ($LASTEXITCODE -eq 0) {
                $definitions[$key] = [string[]]@($text)
                break
            }
        }
    }
    return $definitions[$key]
}

# Returns the first-level keys of the top-level runs: block (using, image) of an action definition.
function Get-RunsKeys([string[]]$Lines) {
    $result = @{}
    $inRuns = $false
    $keyIndent = -1
    foreach ($text in $Lines) {
        if ($text -match '^runs:\s*(#.*)?$') {
            $inRuns = $true
            continue
        }
        if (-not $inRuns -or -not (Test-Content $text)) {
            continue
        }
        $indent = Get-Indent $text
        if ($indent -eq 0) {
            break
        }
        if ($keyIndent -lt 0) {
            $keyIndent = $indent
        }
        if ($indent -eq $keyIndent -and $text -match '^\s*(?<key>using|image):\s*[''"]?(?<value>[^''"\s#]+)') {
            $result[$Matches.key] = $Matches.value
        }
    }
    return $result
}

# Returns whether the actions/checkout step whose uses: line is at index $UsesIndex sets persist-credentials: false
# under its with:.
function Test-PersistCredentials([string[]]$Lines, [int]$UsesIndex) {
    $usesText = $Lines[$UsesIndex]
    $start = $UsesIndex
    if ($usesText -match '^(?<lead>\s*)-\s') {
        $dash = $Matches.lead.Length
    }
    else {
        $dash = (Get-Indent $usesText) - 2
        $start = -1
        for ($j = $UsesIndex - 1; $j -ge 0; $j--) {
            if ($Lines[$j] -match '^(?<lead>\s*)-\s' -and $Matches.lead.Length -eq $dash) {
                $start = $j
                break
            }
            if ((Test-Content $Lines[$j]) -and (Get-Indent $Lines[$j]) -le $dash) {
                break
            }
        }
        if ($start -lt 0) {
            return $false
        }
    }
    $inWith = $false
    for ($j = $start; $j -lt $Lines.Count; $j++) {
        $text = $Lines[$j]
        if ($j -gt $start -and (Test-Content $text) -and (Get-Indent $text) -le $dash) {
            break
        }
        if (-not (Test-Content $text)) {
            continue
        }
        $indent = $j -eq $start ? $dash + 2 : (Get-Indent $text)
        if ($indent -eq $dash + 2) {
            $inWith = $text -match '^\s*(-\s+)?with:\s*(#.*)?$'
            continue
        }
        if ($inWith -and $indent -eq $dash + 4 -and $text -match '^\s*persist-credentials:\s*false\s*(#.*)?$') {
            return $true
        }
    }
    return $false
}

# Checks every uses: line of one file: a workflow, or an action definition at a pinned commit.
function Test-UsesLines([string]$Where, [string[]]$Lines, [int]$Depth) {
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $text = $Lines[$i]
        $line = $i + 1
        if ($text.TrimStart().StartsWith('#') -or $text -notmatch '\buses\s*:') {
            continue
        }
        $script:usesCount++
        if ($text -notmatch $usesPattern) {
            Add-Failure $Where $line 'The line holds uses: in a form this check cannot read; write it as "uses: <owner>/<repo>@<commit SHA> # vX.Y.Z".'
            continue
        }
        $target = $Matches.target
        $comment = $Matches['comment']
        if ($target.StartsWith('./') -or $target.StartsWith('docker://')) {
            Add-Failure $Where $line "$target is a local action or a docker:// image, which this check does not cover; change design §20.5 and this script first."
            continue
        }
        if ($target -notmatch $pinnedPattern) {
            Add-Failure $Where $line "$target is not pinned to a full commit SHA (40 lowercase hex digits)."
            continue
        }
        $repo = $Matches.repo
        $folder = $Matches.ContainsKey('path') ? $Matches.path : ''
        $sha = $Matches.sha
        if ($folder -match '\.ya?ml$') {
            Add-Failure $Where $line "$target calls a reusable workflow, which this check does not cover; change design §20.5 and this script first."
            continue
        }
        if ([string]::IsNullOrEmpty($comment) -or $comment -notmatch $versionPattern) {
            Add-Failure $Where $line "$target has no version comment of the form '# vX.Y.Z'."
            continue
        }
        try {
            $tagCommit = Get-TagCommit $repo $comment
        }
        catch {
            Add-Failure $Where $line "the tag $comment of $repo could not be read: $($_.Exception.Message)"
            continue
        }
        if ($null -eq $tagCommit) {
            Add-Failure $Where $line "$repo has no tag $comment."
            continue
        }
        if ($tagCommit -cne $sha) {
            Add-Failure $Where $line "the tag $comment of $repo points to $tagCommit, not to $sha."
            continue
        }
        $pinOk = $true
        if ($repo -eq 'actions/checkout' -and $folder -eq '' -and -not (Test-PersistCredentials $Lines $i)) {
            Add-Failure $Where $line 'actions/checkout does not set persist-credentials: false under with:.'
            $pinOk = $false
        }
        $definitionKey = "$repo$folder@$sha"
        if (-not $checkedDefinitions.Add($definitionKey)) {
            if ($pinOk) {
                Write-Output "ok: ${Where}:${line}: $repo$folder@$sha # $comment"
            }
            continue
        }
        try {
            $definition = Get-ActionDefinition $repo $folder $sha
        }
        catch {
            Add-Failure $Where $line "the action definition of $definitionKey could not be read: $($_.Exception.Message)"
            continue
        }
        if ($null -eq $definition) {
            Add-Failure $Where $line "$definitionKey has no action.yml or action.yaml."
            continue
        }
        $runs = Get-RunsKeys $definition
        $using = $runs.ContainsKey('using') ? $runs.using : ''
        if ($pinOk) {
            Write-Output "ok: ${Where}:${line}: $repo$folder@$sha # $comment (runs.using: $using)"
        }
        if ($using -eq 'composite') {
            if ($Depth -ge 5) {
                Add-Failure $Where $line "$definitionKey nests composite actions more than 5 deep."
                continue
            }
            Test-UsesLines "$repo$folder@$sha/action.yml" $definition ($Depth + 1)
        }
        elseif ($using -eq 'docker') {
            $image = $runs.ContainsKey('image') ? $runs.image : ''
            if ($image.StartsWith('docker://') -and -not $image.Contains('@sha256:')) {
                Add-Failure $Where $line "$definitionKey runs the image $image, which is not pinned by digest."
            }
        }
        elseif ($using -eq '') {
            Add-Failure $Where $line "$definitionKey has no runs.using."
        }
    }
}

$folderPath = [IO.Path]::GetFullPath($WorkflowPath)
$files = @(Get-ChildItem -LiteralPath $folderPath -File | Where-Object Extension -In '.yml', '.yaml' | Sort-Object Name)
if ($files.Count -eq 0) {
    Write-Output "error: $folderPath holds no workflow file."
    exit 1
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
try {
    foreach ($file in $files) {
        $where = [IO.Path]::GetRelativePath($repoRoot, $file.FullName).Replace('\', '/')
        Test-UsesLines $where ([string[]]@(Get-Content -LiteralPath $file.FullName)) 0
    }
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}

Write-Output "$($files.Count) workflow files, $usesCount uses: lines (composite actions included), $($checkedDefinitions.Count) distinct actions, $($failures.Count) failures."
if ($usesCount -eq 0) {
    Write-Output 'error: no uses: line was found.'
    exit 1
}
if ($failures.Count -gt 0) {
    exit 1
}
exit 0
