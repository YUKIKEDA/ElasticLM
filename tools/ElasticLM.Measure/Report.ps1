<#
.SYNOPSIS
    Combines the runs of tools/ElasticLM.Measure/run.ps1, writes each figure as the range over the runs, and checks every run
    against the design §4.2 targets (design §20.7, V1b).

.DESCRIPTION
    A run that misses one target, or a missing run, fails the report with exit code 1. The table goes to standard output and,
    on GitHub Actions, to the job summary.

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
$runs = @(Get-ChildItem $Results -Recurse -Filter 'run-*.json' | Sort-Object Name | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json })
$failures = [Collections.Generic.List[string]]::new()
if ($runs.Count -ne $Expected) { $failures.Add("$($runs.Count) runs, not $Expected.") }

function Format-Range([double[]]$values, [string]$format = 'N1') {
    $low = ($values | Measure-Object -Minimum).Minimum
    $high = ($values | Measure-Object -Maximum).Maximum
    $low -eq $high ? $low.ToString($format, [Globalization.CultureInfo]::InvariantCulture) : "$($low.ToString($format, [Globalization.CultureInfo]::InvariantCulture))–$($high.ToString($format, [Globalization.CultureInfo]::InvariantCulture))"
}

# Each target: a label, the values of every run, the limit, and whether a value at most the limit meets it.
$rows = [Collections.Generic.List[object]]::new()
function Add-Target([string]$label, [string]$unit, [double[]]$values, [double]$limit, [string]$format = 'N1') {
    $missed = @($values | Where-Object { $_ -gt $limit })
    if ($missed.Count -gt 0) { $failures.Add("$label`: $($missed -join ', ') $unit, more than $limit $unit.") }
    $rows.Add([pscustomobject]@{ Figure = $label; Range = "$(Format-Range $values $format) $unit"; Target = "≤ $($limit.ToString($format, [Globalization.CultureInfo]::InvariantCulture)) $unit"; Met = $missed.Count -eq 0 ? 'yes' : 'NO' })
}
function Add-Record([string]$label, [string]$unit, [double[]]$values, [string]$format = 'N1') {
    $rows.Add([pscustomobject]@{ Figure = $label; Range = "$(Format-Range $values $format) $unit"; Target = '—'; Met = '—' })
}

if ($runs.Count -gt 0) {
    Add-Target 'elm-service size' 'bytes' @($runs | ForEach-Object { $_.sizes.elmService }) 40000000 'N0'
    Add-Target 'elm-server size' 'bytes' @($runs | ForEach-Object { $_.sizes.elmServer }) 30000000 'N0'
    Add-Target 'libelasticlm size' 'bytes' @($runs | ForEach-Object { $_.sizes.libelasticlm }) 8000000 'N0'
    Add-Record 'elm size' 'bytes' @($runs | ForEach-Object { $_.sizes.elm }) 'N0'
    Add-Target 'license file verification' 'µs' @($runs | ForEach-Object { $_.selfTest.verifyUs }) 1000
    Add-Record 'proof create (M3b)' 'µs' @($runs | ForEach-Object { $_.selfTest.proofCreateUs })
    Add-Record 'proof verify (M3b)' 'µs' @($runs | ForEach-Object { $_.selfTest.proofVerifyUs })
    Add-Record 'renewal, server side without the proof (M3b)' 'µs' @($runs | ForEach-Object { $_.selfTest.renewalServerUs })
    Add-Target 'elm-server start, server.started' 'ms' @($runs | ForEach-Object { $_.serverStart.recordMs }) 1000
    Add-Record 'elm-server start, from outside' 'ms' @($runs | ForEach-Object { $_.serverStart.outsideMs })
    Add-Target 'renewal p99, 10,000 leases' 'ms' @($runs | ForEach-Object { $_.leases.load.renew.p99Ms }) 50 'N2'
    Add-Record 'renewal p50, 10,000 leases' 'ms' @($runs | ForEach-Object { $_.leases.load.renew.p50Ms }) 'N2'
    Add-Record 'renewal max, 10,000 leases' 'ms' @($runs | ForEach-Object { $_.leases.load.renew.maxMs }) 'N2'
    if (@($runs | Where-Object { -not $_.leases.memory.vmHwmBytes }).Count -gt 0) { $failures.Add('A run has no VmHWM of elm-server.') }
    Add-Target 'elm-server peak memory (VmHWM)' 'bytes' @($runs | ForEach-Object { $_.leases.memory.vmHwmBytes }) 200000000 'N0'
    Add-Record 'elm-server cgroup memory.peak' 'bytes' @($runs | ForEach-Object { $_.leases.memory.cgroupPeakBytes }) 'N0'
    Add-Target 'lease load: failed acquisitions and renewals' '' @($runs | ForEach-Object { $_.leases.load.acquire.failed + $_.leases.load.renew.failed }) 0 'N0'
    Add-Target 'lease load: refused lease tokens' '' @($runs | ForEach-Object { $_.leases.load.tokensRefused }) 0 'N0'
    Add-Target 'lease load: send lag' 'ms' @($runs | ForEach-Object { $_.leases.load.maxSendLagMs }) 1000
    $held = @($runs | ForEach-Object { $_.leases.heldAtEnd })
    $missedHeld = @($held | Where-Object { $_ -ne 10000 })
    if ($missedHeld.Count -gt 0) { $failures.Add("Leases held at the end: $($held -join ', '), not 10000.") }
    $rows.Add([pscustomobject]@{ Figure = 'leases held at the end'; Range = (Format-Range $held 'N0'); Target = '= 10,000'; Met = $missedHeld.Count -eq 0 ? 'yes' : 'NO' })
    Add-Target 'activation p99, 50/s for 60 s' 'ms' @($runs | ForEach-Object { $_.activations.activate.p99Ms }) 200 'N2'
    Add-Record 'activation p50, 50/s for 60 s' 'ms' @($runs | ForEach-Object { $_.activations.activate.p50Ms }) 'N2'
    Add-Record 'activation max, 50/s for 60 s' 'ms' @($runs | ForEach-Object { $_.activations.activate.maxMs }) 'N2'
    Add-Target 'activations: failed' '' @($runs | ForEach-Object { $_.activations.activate.failed }) 0 'N0'
    Add-Target 'activations: send lag' 'ms' @($runs | ForEach-Object { $_.activations.maxSendLagMs }) 1000
    Add-Record 'activation p50, JIT (M4b1)' 'ms' @($runs | ForEach-Object { $_.jit.activations.activate.p50Ms }) 'N2'
    Add-Record 'activation p99, JIT (M4b1)' 'ms' @($runs | ForEach-Object { $_.jit.activations.activate.p99Ms }) 'N2'
    Add-Record 'activation max, JIT (M4b1)' 'ms' @($runs | ForEach-Object { $_.jit.activations.activate.maxMs }) 'N2'
    Add-Record 'admin write, JIT, average of 200 (M4a1)' 'ms' @($runs | ForEach-Object { $_.jit.adminWrites.averageMs }) 'N3'
    Add-Record 'elm-service start on SQLite, service.started' 'ms' @($runs | ForEach-Object { $_.serviceStart.sqlite.recordMs })
    Add-Record 'elm-service start on SQLite, from outside' 'ms' @($runs | ForEach-Object { $_.serviceStart.sqlite.outsideMs })
    if ($runs[0].serviceStart.postgres) {
        Add-Record 'elm-service start on PostgreSQL, service.started' 'ms' @($runs | ForEach-Object { $_.serviceStart.postgres.recordMs })
        Add-Record 'elm-service start on PostgreSQL, from outside' 'ms' @($runs | ForEach-Object { $_.serviceStart.postgres.outsideMs })
    }
}

$table = @(
    "Measured at $(($runs | ForEach-Object { $_.commit } | Sort-Object -Unique) -join ', ') over $($runs.Count) runs; each range is the lowest and the highest run."
    "CPUs: $(($runs | ForEach-Object { "measure $($_.topology.measure), load $($_.topology.load) (siblings $($_.topology.threadSiblings -join ' / '))" } | Sort-Object -Unique) -join '; ')"
    ''
    '| Figure | Range | Target | Met |'
    '| --- | ---: | ---: | :---: |'
) + @($rows | ForEach-Object { "| $($_.Figure) | $($_.Range) | $($_.Target) | $($_.Met) |" })
if ($failures.Count -gt 0) { $table += @('', '**Missed:**') + @($failures | ForEach-Object { "- $_" }) }
$table
if ($env:GITHUB_STEP_SUMMARY) { $table | Add-Content $env:GITHUB_STEP_SUMMARY }
exit ($failures.Count -eq 0 ? 0 : 1)
