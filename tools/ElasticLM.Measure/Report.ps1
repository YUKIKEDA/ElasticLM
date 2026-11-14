<#
.SYNOPSIS
    Combines the runs of tools/ElasticLM.Measure/run.ps1, writes each figure as the range over the runs, and checks every run
    against the design §4.2 targets (design §20.7, V1b).

.DESCRIPTION
    A run that misses one target, a run without a value that a target is checked on, or a missing run fails the report with
    exit code 1. The activation p99 is checked only on the runs whose disk flush p99 during the activations is at most
    20 ms; the others are left out of it and listed with their figures, and the report fails when no run is left (design
    §4.2 and §20.7, V1d31). The table goes to standard output and, on GitHub Actions, to the job summary.

.PARAMETER Results
    The folder that holds the runs' JSON files (run-*.json).

.PARAMETER Expected
    How many runs there must be.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Results,
    [int]$Expected = 3
)

$ErrorActionPreference = 'Stop'
# design §4.2 (V1d31): a run whose disk flush p99 is above this ran on a slow disk. It is the threshold of the tool's list of
# slow flushes (SlowFlushMilliseconds in Program.cs).
$slowFlushMs = 20
$invariant = [Globalization.CultureInfo]::InvariantCulture
# Each run carries the name of its file (run-1), which the report uses to name it.
$runs = @(Get-ChildItem $Results -Recurse -Filter 'run-*.json' | Sort-Object Name | ForEach-Object {
        Get-Content $_.FullName -Raw | ConvertFrom-Json | Add-Member -NotePropertyName job -NotePropertyValue $_.BaseName -PassThru
    })
$failures = [Collections.Generic.List[string]]::new()
if ($runs.Count -ne $Expected) { $failures.Add("$($runs.Count) runs, not $Expected.") }

function Format-Number([double]$value, [string]$format) { $value.ToString($format, $invariant) }

function Format-Range([double[]]$values, [string]$format = 'N1') {
    if ($values.Count -eq 0) { return '—' }
    $low = ($values | Measure-Object -Minimum).Minimum
    $high = ($values | Measure-Object -Maximum).Maximum
    $low -eq $high ? (Format-Number $low $format) : "$(Format-Number $low $format)–$(Format-Number $high $format)"
}

# Each target: a label, the runs it is checked on, the values of a run, how many values a run has (the self-test runs 4
# times, the server start 5), the limit, and whether a value at most the limit meets it. The values are read run by run, so
# a run with too few values cannot borrow another run's. A run without its values, with another count, or with a $null
# among them misses the target (design §20.7, V1d31): a run that stopped early leaves its result too.
$rows = [Collections.Generic.List[object]]::new()
function Add-Target([string]$label, [string]$unit, [object[]]$jobs, [scriptblock]$value, [double]$limit, [string]$format = 'N1', [int]$count = 1) {
    $values = [Collections.Generic.List[double]]::new()
    $missing = [Collections.Generic.List[string]]::new()
    foreach ($job in $jobs) {
        $items = @($job | ForEach-Object $value)
        if ($items.Count -eq 0 -or @($items | Where-Object { $null -eq $_ }).Count -gt 0) { $missing.Add("no value in $($job.job)"); continue }
        if ($items.Count -ne $count) { $missing.Add("$($items.Count) of $count values in $($job.job)"); continue }
        $items | ForEach-Object { $values.Add([double]$_) }
    }
    $present = [double[]]$values.ToArray()
    $missed = @($present | Where-Object { $_ -gt $limit })
    if ($missing.Count -gt 0) { $failures.Add("$label`: $($missing -join '; ').") }
    if ($missed.Count -gt 0) { $failures.Add("$label`: $($missed -join ', ') $unit, more than $limit $unit.") }
    $met = $missing.Count -eq 0 -and $missed.Count -eq 0
    $rows.Add([pscustomobject]@{ Figure = $label; Range = "$(Format-Range $present $format) $unit"; Target = "≤ $(Format-Number $limit $format) $unit"; Met = $met ? 'yes' : 'NO' })
}
function Add-Record([string]$label, [string]$unit, [scriptblock]$value, [string]$format = 'N1') {
    $present = [double[]]@($runs | ForEach-Object $value | Where-Object { $null -ne $_ })
    $rows.Add([pscustomobject]@{ Figure = $label; Range = "$(Format-Range $present $format) $unit"; Target = '—'; Met = '—' })
}

$leftOut = @()
if ($runs.Count -gt 0) {
    Add-Target 'elm-service size' 'bytes' $runs { $_.sizes.elmService } 40000000 'N0'
    Add-Target 'elm-server size' 'bytes' $runs { $_.sizes.elmServer } 30000000 'N0'
    Add-Target 'libelasticlm size' 'bytes' $runs { $_.sizes.libelasticlm } 8000000 'N0'
    Add-Record 'elm size' 'bytes' { $_.sizes.elm } 'N0'
    Add-Target 'license file verification' 'µs' $runs { $_.selfTest.verifyUs } 1000 'N1' 4
    Add-Record 'proof create (M3b)' 'µs' { $_.selfTest.proofCreateUs }
    Add-Record 'proof verify (M3b)' 'µs' { $_.selfTest.proofVerifyUs }
    Add-Record 'renewal, server side without the proof (M3b)' 'µs' { $_.selfTest.renewalServerUs }
    Add-Target 'elm-server start, server.started' 'ms' $runs { $_.serverStart.recordMs } 1000 'N1' 5
    Add-Record 'elm-server start, from outside' 'ms' { $_.serverStart.outsideMs }
    Add-Target 'renewal p99, 10,000 leases' 'ms' $runs { $_.leases.load.renew.p99Ms } 50 'N2'
    Add-Record 'renewal p50, 10,000 leases' 'ms' { $_.leases.load.renew.p50Ms } 'N2'
    Add-Record 'renewal max, 10,000 leases' 'ms' { $_.leases.load.renew.maxMs } 'N2'
    Add-Target 'elm-server peak memory (VmHWM)' 'bytes' $runs { $_.leases.memory.vmHwmBytes } 200000000 'N0'
    Add-Record 'elm-server cgroup memory.peak' 'bytes' { $_.leases.memory.cgroupPeakBytes } 'N0'
    Add-Target 'lease load: failed acquisitions and renewals' '' $runs { $load = $_.leases.load; ($null -eq $load.acquire.failed -or $null -eq $load.renew.failed) ? $null : $load.acquire.failed + $load.renew.failed } 0 'N0'
    # Issue #393: the requests whose answer broke off are among the failures above; a renewal that the server may have taken
    # leaves the tool with the sequence number before it, and the refusal of the next renewal is counted apart.
    Add-Record 'lease load: requests without a whole answer' '' { $_.leases.load ? @($_.leases.load.broken | Where-Object { $_ }).Count : $null } 'N0'
    Add-Record 'lease load: renewals refused after one without a whole answer' '' { $_.leases.load.renewAfterBroken.failed } 'N0'
    Add-Target 'lease load: refused lease tokens' '' $runs { $_.leases.load.tokensRefused } 0 'N0'
    Add-Target 'lease load: send lag' 'ms' $runs { $_.leases.load.maxSendLagMs } 1000
    $held = @($runs | ForEach-Object { $_.leases.heldAtEnd })
    $missedHeld = @($held | Where-Object { $_ -ne 10000 })
    if ($missedHeld.Count -gt 0) { $failures.Add("Leases held at the end: $($held -join ', '), not 10000.") }
    $rows.Add([pscustomobject]@{ Figure = 'leases held at the end'; Range = (Format-Range @($held | Where-Object { $null -ne $_ }) 'N0'); Target = '= 10,000'; Met = $missedHeld.Count -eq 0 ? 'yes' : 'NO' })

    # design §4.2 (V1d31): the activation p99 is checked on the runs with a normal disk only. A run without a flush p99
    # stays in ($null is not greater than the threshold), so that its missing activation p99 fails the target.
    $leftOut = @($runs | Where-Object { $_.activations.flush.p99Ms -gt $slowFlushMs })
    $normal = @($runs | Where-Object { $leftOut -notcontains $_ })
    $activationLabel = "activation p99, 50/s for 60 s, runs with disk flush p99 ≤ $slowFlushMs ms"
    if ($normal.Count -gt 0) {
        Add-Target $activationLabel 'ms' $normal { $_.activations.activate.p99Ms } 200 'N2'
    }
    else {
        $failures.Add("$activationLabel`: no run had a normal disk (disk flush p99 at most $slowFlushMs ms): a slow runner disk, or a change that makes the disk flush slow on every run.")
        $rows.Add([pscustomobject]@{ Figure = $activationLabel; Range = 'no run'; Target = '≤ 200.00 ms'; Met = 'NO' })
    }
    foreach ($run in $leftOut) {
        $rows.Add([pscustomobject]@{ Figure = "activation p99, $($run.job) left out: disk flush p99 $(Format-Number $run.activations.flush.p99Ms 'N2') ms > $slowFlushMs ms"; Range = "$(Format-Number $run.activations.activate.p99Ms 'N2') ms"; Target = '—'; Met = 'left out' })
    }
    Add-Record 'activation p99, 50/s for 60 s, every run' 'ms' { $_.activations.activate.p99Ms } 'N2'
    Add-Record 'activation p50, 50/s for 60 s' 'ms' { $_.activations.activate.p50Ms } 'N2'
    Add-Record 'activation max, 50/s for 60 s' 'ms' { $_.activations.activate.maxMs } 'N2'
    Add-Target 'activations: failed' '' $runs { $_.activations.activate.failed } 0 'N0'
    Add-Target 'activations: send lag' 'ms' $runs { $_.activations.maxSendLagMs } 1000
    # Issue #182: what the slow activations coincide with.
    Add-Record 'activations slower than 50 ms' '' { $_.activations ? @($_.activations.slow | Where-Object { $_ }).Count : $null } 'N0'
    Add-Record 'activations without a whole answer' '' { $_.activations ? @($_.activations.broken | Where-Object { $_ }).Count : $null } 'N0'
    Add-Record 'disk flush p99 during the activations' 'ms' { $_.activations.flush.p99Ms } 'N2'
    Add-Record 'disk flush max during the activations' 'ms' { $_.activations.flush.maxMs } 'N2'
    Add-Record 'activation p50, JIT (M4b1)' 'ms' { $_.jit.activations.activate.p50Ms } 'N2'
    Add-Record 'activation p99, JIT (M4b1)' 'ms' { $_.jit.activations.activate.p99Ms } 'N2'
    Add-Record 'activation max, JIT (M4b1)' 'ms' { $_.jit.activations.activate.maxMs } 'N2'
    Add-Record 'admin write, JIT, average of 200 (M4a1)' 'ms' { $_.jit.adminWrites.averageMs } 'N3'
    Add-Record 'elm-service start on SQLite, service.started' 'ms' { $_.serviceStart.sqlite.recordMs }
    Add-Record 'elm-service start on SQLite, from outside' 'ms' { $_.serviceStart.sqlite.outsideMs }
    if ($runs[0].serviceStart.postgres) {
        Add-Record 'elm-service start on PostgreSQL, service.started' 'ms' { $_.serviceStart.postgres.recordMs }
        Add-Record 'elm-service start on PostgreSQL, from outside' 'ms' { $_.serviceStart.postgres.outsideMs }
    }
}

$table = @(
    "Measured at $(($runs | ForEach-Object { $_.commit } | Sort-Object -Unique) -join ', ') over $($runs.Count) runs; each range is the lowest and the highest run."
    "CPUs: $(($runs | ForEach-Object { "measure $($_.topology.measure), load $($_.topology.load) (siblings $($_.topology.threadSiblings -join ' / '))" } | Sort-Object -Unique) -join '; ')"
    ''
    '| Figure | Range | Target | Met |'
    '| --- | ---: | ---: | :---: |'
) + @($rows | ForEach-Object { "| $($_.Figure) | $($_.Range) | $($_.Target) | $($_.Met) |" })
if ($leftOut.Count -gt 0) {
    $table += @('', "**Left out of the activation p99 target** (disk flush p99 above $slowFlushMs ms, a slow disk; design §4.2):") + @($leftOut | ForEach-Object {
            "- $($_.job): activation p99 $(Format-Number $_.activations.activate.p99Ms 'N2') ms, disk flush p99 $(Format-Number $_.activations.flush.p99Ms 'N2') ms, more than $slowFlushMs ms."
        })
}
$broken = @(foreach ($run in $runs) {
        foreach ($request in @($run.leases.load.broken) + @($run.activations.broken)) {
            if ($request) { "- $($run.job) at $(Format-Number $request.atS 'N2') s, $($request.path): $($request.message)" }
        }
    })
if ($broken.Count -gt 0) { $table += @('', '**Requests without a whole answer** (Issue #393):') + $broken }
if ($failures.Count -gt 0) { $table += @('', '**Missed:**') + @($failures | ForEach-Object { "- $_" }) }
$table
if ($env:GITHUB_STEP_SUMMARY) { $table | Add-Content $env:GITHUB_STEP_SUMMARY }
exit ($failures.Count -eq 0 ? 0 : 1)
