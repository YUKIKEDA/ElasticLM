<#
.SYNOPSIS
    Checks how Report.ps1 judges the activation p99 of runs on a slow disk (design §4.2 and §20.7, V1d31).

.DESCRIPTION
    Each case writes three run-N.json files and runs Report.ps1 over them in a child process, with the job summary in a file
    of the case. A run holds only the figures that the report checks against a target. Every value is the one of
    run 43425339215's run 1 (commit 5feb83b), which met every target, except the activation p99 and the disk flush p99,
    which each case takes from a recorded run (run N/M is the run-M.json of Measure run N):
    - a run on a slow disk is left out: the runs of 43425339215 (5.04 / 1.02, 7.93 / 2.33, 269.96 / 104.63 ms): exit 0;
      the target's range holds only runs 1 and 2, and the table and the list below it name run 3 with both figures and
      the threshold;
    - two runs on a slow disk are left out: the runs of 43433230281 (4.90 / 1.16, 182.12 / 114.54, 93.85 / 24.13 ms):
      exit 0, both named;
    - a slow activation on a normal disk fails: 630.14 ms of 42635311788/2 with the highest normal flush p99, 7.97 ms of
      43215084184/3, beside runs 1 and 2 of 43425339215: exit 1, the miss named, nothing left out;
    - three runs on a slow disk fail: 42635311788/2 (630.14 / 180.27), 43425339215/3 (269.96 / 104.63), and
      43434745442/3 (207.41 / 36.96 ms): exit 1 with "no run had a normal disk", all three named;
    - the threshold itself: a flush p99 of exactly 20 ms is a normal disk, so 630.14 ms on it fails; 20.01 ms is a slow
      disk, so the same run is left out and the report passes;
    - the target itself: an activation p99 of exactly 200 ms on a normal disk passes;
    - a run without its activations (it stopped before them) is not left out: exit 1, the activation p99 named as having
      no value in that run;
    - a left-out run (43425339215/3) is still judged on the other activation targets: with one failed activation, and
      with a send lag of 1,500 ms, exit 1 with that target named;
    - a run's values are read run by run: a run without its self-test, with one self-test of four, with a self-test
      without its figure, without its server starts, or without its count of failed renewals misses that target with
      exit 1, while the other runs have theirs.
    The files are made in a temporary folder that is removed at the end.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$report = Join-Path $PSScriptRoot 'Report.ps1'
$work = Join-Path ([IO.Path]::GetTempPath()) ('elm-report-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$failures = 0

# A run with the figures of 43425339215's run 1 that the report checks against a target, and the given activation and
# flush p99. $null for the activation p99 leaves out the activations as a run that stopped before them does.
function New-Run([object]$activationP99, [double]$flushP99, [scriptblock]$change) {
    $run = [ordered]@{
        commit = '5feb83be3ad9ea5ee126c77d215c530f115cd3c3'
        sizes = [ordered]@{ elmServer = 13276720; elmService = 26804760; libelasticlm = 7171624 }
        selfTest = @(134.6, 134.4, 138.7, 139.0 | ForEach-Object { [ordered]@{ verifyUs = $_ } })
        serverStart = @(128.0, 107.0, 108.0, 109.0, 104.0 | ForEach-Object { [ordered]@{ recordMs = $_ } })
        leases = [ordered]@{
            load = [ordered]@{ maxSendLagMs = 2.8; acquire = [ordered]@{ failed = 0 }; renew = [ordered]@{ failed = 0; p99Ms = 1.58 }; tokensRefused = 0 }
            heldAtEnd = 10000
            memory = [ordered]@{ vmHwmBytes = 105541632 }
        }
    }
    if ($null -ne $activationP99) {
        $run.activations = [ordered]@{ maxSendLagMs = 3.9; activate = [ordered]@{ failed = 0; p99Ms = $activationP99 }; flush = [ordered]@{ p99Ms = $flushP99 } }
    }
    if ($change) { & $change $run }
    $run
}

function Invoke-Case([string]$name, [object[]]$runs, [int]$expected, [string[]]$present, [string[]]$absent = @()) {
    $folder = Join-Path $work ($name -replace '[^A-Za-z0-9]+', '-')
    New-Item -ItemType Directory -Path $folder | Out-Null
    for ($i = 0; $i -lt $runs.Count; $i++) {
        $runs[$i] | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $folder "run-$($i + 1).json") -Encoding utf8NoBOM
    }
    $summary = Join-Path $folder 'summary.md'
    $savedSummary = $env:GITHUB_STEP_SUMMARY
    $env:GITHUB_STEP_SUMMARY = $summary
    try {
        & pwsh -NoProfile -NonInteractive -File $report -Results $folder -Expected 3 | Out-Null
        $code = $LASTEXITCODE
    }
    finally {
        $env:GITHUB_STEP_SUMMARY = $savedSummary
    }
    $text = (Test-Path $summary) ? (Get-Content $summary -Raw -Encoding utf8) : ''
    $problems = @()
    if ($code -ne $expected) { $problems += "exit code $code, not $expected" }
    foreach ($line in $present) { if (-not $text.Contains($line)) { $problems += "no line with: $line" } }
    foreach ($line in $absent) { if ($text.Contains($line)) { $problems += "a line with: $line" } }
    if ($problems.Count -eq 0) {
        "ok: $name"
    }
    else {
        "FAILED: $name ($($problems -join '; '))"
        "--- summary:"
        $text
        $script:failures++
    }
}

$target = '| activation p99, 50/s for 60 s, runs with disk flush p99 ≤ 20 ms |'
$listed = '**Left out of the activation p99 target** (disk flush p99 above 20 ms, a slow disk; design §4.2):'
try {
    Invoke-Case 'a run on a slow disk is left out' @((New-Run 5.04 1.02), (New-Run 7.93 2.33), (New-Run 269.96 104.63)) 0 @(
        "$target 5.04–7.93 ms | ≤ 200.00 ms | yes |"
        '| activation p99, run-3 left out: disk flush p99 104.63 ms > 20 ms | 269.96 ms | — | left out |'
        $listed
        '- run-3: activation p99 269.96 ms, disk flush p99 104.63 ms, more than 20 ms.'
        '| activation p99, 50/s for 60 s, every run | 5.04–269.96 ms | — | — |'
    ) @('**Missed:**', 'run-1 left out', 'run-2 left out')

    Invoke-Case 'two runs on a slow disk are left out' @((New-Run 4.90 1.16), (New-Run 182.12 114.54), (New-Run 93.85 24.13)) 0 @(
        "$target 4.90 ms | ≤ 200.00 ms | yes |"
        '- run-2: activation p99 182.12 ms, disk flush p99 114.54 ms, more than 20 ms.'
        '- run-3: activation p99 93.85 ms, disk flush p99 24.13 ms, more than 20 ms.'
    ) @('**Missed:**')

    Invoke-Case 'a slow activation on a normal disk fails' @((New-Run 5.04 1.02), (New-Run 7.93 2.33), (New-Run 630.14 7.97)) 1 @(
        "$target 5.04–630.14 ms | ≤ 200.00 ms | NO |"
        '- activation p99, 50/s for 60 s, runs with disk flush p99 ≤ 20 ms: 630.14 ms, more than 200 ms.'
    ) @($listed, 'left out')

    Invoke-Case 'three runs on a slow disk fail' @((New-Run 630.14 180.27), (New-Run 269.96 104.63), (New-Run 207.41 36.96)) 1 @(
        "$target no run | ≤ 200.00 ms | NO |"
        '- activation p99, 50/s for 60 s, runs with disk flush p99 ≤ 20 ms: no run had a normal disk (disk flush p99 at most 20 ms): a slow runner disk, or a change that makes the disk flush slow on every run.'
        '- run-1: activation p99 630.14 ms, disk flush p99 180.27 ms, more than 20 ms.'
        '- run-2: activation p99 269.96 ms, disk flush p99 104.63 ms, more than 20 ms.'
        '- run-3: activation p99 207.41 ms, disk flush p99 36.96 ms, more than 20 ms.'
    )

    Invoke-Case 'a flush p99 of exactly 20 ms is a normal disk' @((New-Run 5.04 1.02), (New-Run 7.93 2.33), (New-Run 630.14 20)) 1 @(
        '- activation p99, 50/s for 60 s, runs with disk flush p99 ≤ 20 ms: 630.14 ms, more than 200 ms.'
    ) @('left out')

    Invoke-Case 'a flush p99 of 20.01 ms is a slow disk' @((New-Run 5.04 1.02), (New-Run 7.93 2.33), (New-Run 630.14 20.01)) 0 @(
        '- run-3: activation p99 630.14 ms, disk flush p99 20.01 ms, more than 20 ms.'
    ) @('**Missed:**')

    Invoke-Case 'an activation p99 of exactly 200 ms meets the target' @((New-Run 5.04 1.02), (New-Run 7.93 2.33), (New-Run 200 7.97)) 0 @(
        "$target 5.04–200.00 ms | ≤ 200.00 ms | yes |"
    ) @('**Missed:**')

    Invoke-Case 'a run without its activations is not left out' @((New-Run 5.04 1.02), (New-Run 7.93 2.33), (New-Run $null 0)) 1 @(
        '- activation p99, 50/s for 60 s, runs with disk flush p99 ≤ 20 ms: no value in run-3.'
        '- activations: failed: no value in run-3.'
    ) @($listed)

    $slow = { param($change) New-Run 269.96 104.63 $change }
    Invoke-Case 'a left-out run is still judged on failed activations' @((New-Run 5.04 1.02), (New-Run 7.93 2.33), (& $slow { param($r) $r.activations.activate.failed = 1 })) 1 @(
        '- activations: failed: 1 , more than 0 .'
        '- run-3: activation p99 269.96 ms, disk flush p99 104.63 ms, more than 20 ms.'
    )

    Invoke-Case 'a left-out run is still judged on the send lag' @((New-Run 5.04 1.02), (New-Run 7.93 2.33), (& $slow { param($r) $r.activations.maxSendLagMs = 1500 })) 1 @(
        '- activations: send lag: 1500 ms, more than 1000 ms.'
    )

    Invoke-Case 'a run without its self-test misses the verification target' @((New-Run 5.04 1.02), (New-Run 7.93 2.33), (New-Run 7.93 2.33 { param($r) $r.Remove('selfTest') })) 1 @(
        '- license file verification: no value in run-3.'
    )

    Invoke-Case 'a run with one self-test of four misses the verification target' @((New-Run 5.04 1.02), (New-Run 7.93 2.33), (New-Run 7.93 2.33 { param($r) $r.selfTest = @($r.selfTest[0]) })) 1 @(
        '- license file verification: 1 of 4 values in run-3.'
    )

    Invoke-Case 'a run with a self-test without its figure misses the verification target' @((New-Run 5.04 1.02), (New-Run 7.93 2.33), (New-Run 7.93 2.33 { param($r) $r.selfTest[1].Remove('verifyUs') })) 1 @(
        '- license file verification: no value in run-3.'
    )

    Invoke-Case 'a run without its server starts misses the start target' @((New-Run 5.04 1.02), (New-Run 7.93 2.33), (New-Run 7.93 2.33 { param($r) $r.Remove('serverStart') })) 1 @(
        '- elm-server start, server.started: no value in run-3.'
    )

    Invoke-Case 'a run without its failed renewals misses the failure target' @((New-Run 5.04 1.02), (New-Run 7.93 2.33), (New-Run 7.93 2.33 { param($r) $r.leases.load.renew.Remove('failed') })) 1 @(
        '- lease load: failed acquisitions and renewals: no value in run-3.'
    )
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures -gt 0) {
    "$failures case(s) failed."
    exit 1
}
'Every case passed.'
exit 0
