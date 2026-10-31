<#
.SYNOPSIS
    Measures the design §4.2 targets against published binaries (design §20.7, V1b) and writes one run's results as JSON.

.DESCRIPTION
    - Sizes of the published elm-server, elm-service, elm, and libelasticlm.
    - samples/VerifyLicense --self-test, 4 times: one license file's verification, a proof, and a renewal's server-side work.
    - elm-server from start to listening, 5 times: the server's record and the time to the first HTTPS answer from outside.
    - 10,000 concurrent leases of elm-server: tools/ElasticLM.Measure leases; renewal times, failures, the send lag, the
      leases held at the end, and the server's peak memory.
    - elm-service from start to listening on SQLite and, with -Postgres, on PostgreSQL, 3 times each on a migrated database.
    - Online activations at 50 a second for 60 seconds against the published elm-service, and the same load and the admin
      API's audited write (M4a1) against a JIT Release elm-service.
    On Linux the measured process runs on two CPUs of one half of the cores and the load on the other half (taskset), and the
    lease server runs in a systemd scope (its cgroup also records peak memory). Settings and secrets go by file. Keys,
    licenses, databases, and settings are made in a temporary folder and removed at the end (.cursor/rules/scratch.mdc); no
    key, token, or secret goes into the output.

.PARAMETER Bin
    The -OutDir of eng/release/Publish-Rid.ps1 or eng/release/publish-rid.sh: elm, elm-server, elm-service, libelasticlm.

.PARAMETER VerifyLicense
    The NativeAOT-published samples/VerifyLicense executable.

.PARAMETER Out
    The JSON file this run writes.

.PARAMETER Postgres
    A PostgreSQL connection string for the start measurement on PostgreSQL, or nothing to skip it.

.PARAMETER Small
    A short trial of the steps on a developer's machine: leases of 15 seconds, 5 keys of 4 leases, 100 activations at 10 a
    second, 20 admin writes. Its figures are not the §4.2 measurement.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Bin,
    [Parameter(Mandatory = $true)][string]$VerifyLicense,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Postgres,
    [switch]$Small
)

$ErrorActionPreference = 'Stop'
# The script works in its own folder: the paths it was given are made absolute first.
$Bin = (Resolve-Path $Bin).Path
$Out = [IO.Path]::GetFullPath($Out)
$root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('elm-measure-' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(6)))
New-Item -ItemType Directory $work | Out-Null
$exe = $IsWindows ? '.exe' : ''
$library = $IsWindows ? 'elasticlm.dll' : 'libelasticlm.so'
$elm = (Resolve-Path (Join-Path $Bin "elm/elm$exe")).Path
$server = (Resolve-Path (Join-Path $Bin "elm-server/elm-server$exe")).Path
$service = (Resolve-Path (Join-Path $Bin "elm-service/elm-service$exe")).Path
$VerifyLicense = (Resolve-Path $VerifyLicense).Path
$tool = Join-Path $root 'tools/ElasticLM.Measure/bin/Release/net10.0/ElasticLM.Measure.dll'
# The commit measured: on a pull request the workflow names its head, not the merge commit it checks out.
$commit = $env:MEASURE_COMMIT ? $env:MEASURE_COMMIT : (& git -C $root rev-parse HEAD).Trim()
$results = [ordered]@{ commit = $commit; small = [bool]$Small }
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
$savedPassphrase = $env:ELM_KEY_PASSPHRASE

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE." }
}

function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $port
}

function Write-Settings([string]$name, $settings) {
    $path = Join-Path $work $name
    $settings | ConvertTo-Json -Depth 5 | Set-Content $path
    $path
}

# design §20.7: two halves of the cores, by the CPUs that share one physical core.
$measureCpus = '0,1'
$loadCpus = '2,3'
if ($IsLinux) {
    $siblings = @(Get-ChildItem /sys/devices/system/cpu -Directory -Filter 'cpu[0-9]*' | ForEach-Object { (Get-Content (Join-Path $_.FullName 'topology/thread_siblings_list')).Trim() } | Sort-Object -Unique)
    $results.topology = [ordered]@{ cpus = [Environment]::ProcessorCount; threadSiblings = $siblings }
    $expand = { param($list) @($list -split ',' | ForEach-Object { if ($_ -match '^(\d+)-(\d+)$') { [int]$Matches[1]..[int]$Matches[2] } else { [int]$_ } }) }
    if ($siblings.Count -ge 2 -and (& $expand $siblings[0]).Count -eq 2) {
        $measureCpus = (& $expand $siblings[0]) -join ','
        $loadCpus = (& $expand $siblings[1]) -join ','
    }
    $results.topology.measure = $measureCpus
    $results.topology.load = $loadCpus
}

# A process measured on the measuring CPUs, started directly (no sudo, no scope), with its output in a log.
function Start-Direct([string]$path, [string[]]$arguments, [string]$log) {
    $file = $IsLinux ? 'taskset' : $path
    $list = $IsLinux ? (@('-c', $measureCpus, $path) + $arguments) : $arguments
    $process = Start-Process $file -ArgumentList $list -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru
    $processes.Add($process)
    $process
}

function Stop-Direct($process) {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    $process.WaitForExit()
}

function Wait-Answer([string]$url) {
    for ($i = 0; $i -lt 3000; $i++) {
        try {
            if ((Invoke-WebRequest $url -SkipHttpErrorCheck -SkipCertificateCheck -TimeoutSec 5).StatusCode -eq 200) { return }
        }
        catch { }
        Start-Sleep -Milliseconds 20
    }
    throw "$url did not answer."
}

# The time from the process start to the first answer, and the process's own record of its start.
function Measure-Start([string]$path, [string[]]$arguments, [string]$url, [string]$log, [string]$pattern, [switch]$Keep) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Direct $path $arguments $log
    Wait-Answer $url
    $outside = $clock.Elapsed.TotalMilliseconds
    if (-not $Keep) { Stop-Direct $process }
    $record = (Select-String -Path $log -Pattern $pattern | Select-Object -First 1).Matches[0].Groups[1].Value
    [pscustomobject]@{ Process = $process; Figure = [ordered]@{ recordMs = [double]$record; outsideMs = [Math]::Round($outside, 1) } }
}

function Invoke-Load([string[]]$arguments) {
    if ($IsLinux) { & taskset -c $loadCpus dotnet $tool @arguments } else { & dotnet $tool @arguments }
    if ($LASTEXITCODE -ne 0) { throw "ElasticLM.Measure $($arguments[0]) failed with exit code $LASTEXITCODE." }
}

try {
    Invoke-Checked 'Building ElasticLM.Measure' { dotnet build (Join-Path $root 'tools/ElasticLM.Measure') -c Release -v q -nologo | Out-Host }
    Push-Location $work
    $env:ELM_KEY_PASSPHRASE = 'measure passphrase ' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(8))
    Invoke-Checked 'elm key root create' { & $elm key root create --out root.key.pem --public-out root.pub.pem | Out-Null }
    Invoke-Checked 'elm key signing create' { & $elm key signing create --out signing.key.pem --public-out signing.pub.pem | Out-Null }
    Invoke-Checked 'elm key certify' { & $elm key certify --root root.key.pem --public signing.pub.pem --use license lease --out signing.cert | Out-Null }
    $rootKey = Join-Path $work 'root.pub.pem'
    $expires = [DateTimeOffset]::UtcNow.AddDays(30).ToString('yyyy-MM-ddTHH:mm:ssZ')

    $results.sizes = [ordered]@{
        elmServer = (Get-Item $server).Length
        elmService = (Get-Item $service).Length
        elm = (Get-Item $elm).Length
        libelasticlm = (Get-Item (Join-Path $Bin "libelasticlm/$library")).Length
    }

    # design §4.2: one license file's verification; M3b: a proof and a renewal's server-side work. 4 runs on the measuring CPUs.
    $results.selfTest = @()
    foreach ($n in 1..4) {
        $selfTest = $IsLinux ? (& taskset -c $measureCpus $VerifyLicense --self-test) : (& $VerifyLicense --self-test)
        if ($LASTEXITCODE -ne 0) { throw 'VerifyLicense --self-test failed.' }
        $value = { param($pattern) [double](($selfTest | Select-String $pattern).Matches[0].Groups[1].Value) }
        $results.selfTest += [ordered]@{
            verifyUs = & $value '^verify: ([0-9.]+) us'
            proofCreateUs = & $value '^proof: create ([0-9.]+) us'
            proofVerifyUs = & $value ', verify ([0-9.]+) us \('
            renewalServerUs = & $value '^renewal \(server, without the proof\): ([0-9.]+) us'
        }
    }

    # elm-server: settings by file; a first run without the license makes the key the server license is bound to.
    $leaseMinutes = $Small ? '0.25' : '10'
    $seats = $Small ? 20 : 10000
    $serverUrl = "https://127.0.0.1:$(Get-FreePort)"
    $adminUrl = "http://127.0.0.1:$(Get-FreePort)"
    $serverData = Join-Path $work 'server-data'
    $serverSettings = Write-Settings 'server.json' ([ordered]@{
            product = 'acme-cad'
            listen = $serverUrl
            license = (Join-Path $work 'server.elmlic')
            dataDirectory = $serverData
            trust = @{ rootKeys = @($rootKey) }
            admin = @{ listen = $adminUrl }
            # design §20.7: the loopback load is one source; the limit is raised to its upper bound for the measurement only.
            rateLimit = @{ perIp = 10000 }
        })
    $refusal = (& $server --config $serverSettings 2>&1 | Out-String)
    if ($refusal -notmatch 'installation key ([A-Za-z0-9_-]{43})') { throw 'elm-server did not name its installation key.' }
    $serverKey = $Matches[1]
    Invoke-Checked 'elm license issue (server)' {
        & $elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id measure-server --kind server `
            --feature "pro;seats=$seats" --expires $expires --bind-key $serverKey --lease-minutes $leaseMinutes --trust root.pub.pem --out server.elmlic | Out-Null
    }

    # M3b: start to listening, 5 times, started directly on the measuring CPUs.
    $results.serverStart = @(foreach ($n in 1..5) {
            (Measure-Start $server @('--config', $serverSettings) "$serverUrl/v1/server/license" (Join-Path $work "server-start-$n.log") 'server\.started: .* in ([0-9.]+) ms').Figure
        })

    # design §4.2: 10,000 concurrent leases, the server in a systemd scope on the measuring CPUs.
    $log = Join-Path $work 'server-load.log'
    $memory = [ordered]@{}
    if ($IsLinux) {
        $uid = (& id -u).Trim()
        $gid = (& id -g).Trim()
        $process = Start-Process sudo -ArgumentList @('systemd-run', '--scope', '--quiet', '--unit=elm-measure-server', "--uid=$uid", "--gid=$gid", '-p', "AllowedCPUs=$measureCpus", '--', $server, '--config', $serverSettings) -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru
    }
    else {
        $process = Start-Process $server -ArgumentList @('--config', $serverSettings) -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru
    }
    $processes.Add($process)
    try {
        Wait-Answer "$serverUrl/v1/server/license"
        $leaseJson = Join-Path $work 'leases.json'
        $load = $Small ? @('--keys', '5', '--per-key', '4', '--acquire-rate', '2.5', '--renew-seconds', '8', '--cycles', '2') : @('--keys', '100', '--per-key', '100', '--acquire-rate', '50', '--renew-seconds', '200', '--cycles', '2')
        Invoke-Load (@('leases', '--url', $serverUrl, '--trust', $rootKey, '--work', (Join-Path $work 'lease-keys'), '--out', $leaseJson) + $load)
        $status = (& $elm server status --json --url $adminUrl --data $serverData) | ConvertFrom-Json
        if ($IsLinux) {
            $group = (& systemctl show -p ControlGroup --value elm-measure-server.scope).Trim()
            $serverPid = @(Get-Content "/sys/fs/cgroup$group/cgroup.procs")[0].Trim()
            $memory.vmHwmBytes = 1024 * [long]((Select-String -Path "/proc/$serverPid/status" -Pattern '^VmHWM:\s+([0-9]+) kB').Matches[0].Groups[1].Value)
            $memory.cgroupPeakBytes = [long](Get-Content "/sys/fs/cgroup$group/memory.peak")
        }
        else {
            $memory.peakWorkingSetBytes = (Get-Process -Name elm-server | Select-Object -First 1).PeakWorkingSet64
        }
        $results.leases = [ordered]@{ load = (Get-Content $leaseJson -Raw | ConvertFrom-Json); heldAtEnd = $status.leases; memory = $memory }
    }
    finally {
        if ($IsLinux) { & sudo systemctl stop elm-measure-server.scope | Out-Null } else { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
        $process.WaitForExit()
    }

    # elm-service: settings and secrets by file (design §14.8).
    $nonceFile = Join-Path $work 'nonce.secret'
    [IO.File]::WriteAllText($nonceFile, [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
    $passphraseFile = Join-Path $work 'signing.passphrase'
    [IO.File]::WriteAllText($passphraseFile, $env:ELM_KEY_PASSPHRASE)
    function Write-ServiceSettings([string]$name, [string]$provider, [string]$connection, [string]$url) {
        $connectionFile = Join-Path $work "$name.connection"
        [IO.File]::WriteAllText($connectionFile, $connection)
        Write-Settings "$name.json" ([ordered]@{
                listen = $url
                database = @{ provider = $provider; connectionFile = $connectionFile }
                signing = @{ key = (Join-Path $work 'signing.key.pem'); certificates = @((Join-Path $work 'signing.cert')); passphraseFile = $passphraseFile }
                trust = @{ rootKeys = @($rootKey) }
                bootstrapOwner = @{ iss = 'https://idp.example'; sub = 'measure' }
                nonce = @{ secretFile = $nonceFile }
                # design §20.7: one source sends the whole load; the limits are raised for the measurement only.
                rateLimit = @{ clientPerIp = 10000; perIp = 10000; perKey = 10000 }
            })
    }

    # Sets up a database, then starts the service and returns its admin key, which stays in a file.
    function Initialize-Service([string]$settings, [string[]]$command) {
        $key = (& $command[0] @($command | Select-Object -Skip 1) setup --config $settings).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $key.StartsWith('elm_')) { throw "elm-service setup failed ($settings)." }
        $file = "$settings.key"
        [IO.File]::WriteAllText($file, $key)
        $file
    }

    function Measure-Activations([string]$url, [string]$keyFile, [string]$label) {
        $headers = @{ Authorization = "Bearer $((Get-Content $keyFile -Raw).Trim())" }
        $add = { param($path, $body) Invoke-RestMethod "$url/v1/admin/$path" -Method Post -Headers $headers -ContentType 'application/json' -Body $body }
        $count = $Small ? 100 : 3000
        $product = (& $add 'products' '{"code":"acme-cad","name":"Acme CAD"}').id
        & $add "products/$product/features" '{"code":"pro","name":"Pro"}' | Out-Null
        $policy = (& $add 'policies' "{`"productId`":`"$product`",`"name`":`"measure`",`"maxActivations`":$count}").id
        $licenseFile = Join-Path $work "$label.license-key"
        [IO.File]::WriteAllText($licenseFile, (& $add 'licenses' "{`"policyId`":`"$policy`",`"name`":`"measure`",`"features`":[{`"code`":`"pro`"}]}").key)
        $json = Join-Path $work "$label-activations.json"
        $rate = $Small ? '10' : '50'
        Invoke-Load @('activations', '--url', $url, '--license-key-file', $licenseFile, '--count', "$count", '--rate', $rate, '--work', (Join-Path $work "$label-keys"), '--out', $json)
        Get-Content $json -Raw | ConvertFrom-Json
    }

    # M4a1: start to listening on a migrated database, 3 times on each engine; the third SQLite start stays for the load.
    $results.serviceStart = [ordered]@{}
    $databases = [ordered]@{ sqlite = "Data Source=$(Join-Path $work 'service.db')" }
    if ($Postgres) { $databases.postgres = $Postgres }
    foreach ($database in $databases.Keys) {
        $url = "http://127.0.0.1:$(Get-FreePort)"
        $settings = Write-ServiceSettings "service-$database" $database $databases[$database] $url
        $keyFile = Initialize-Service $settings @($service)
        $figures = @()
        foreach ($n in 1..3) {
            $keep = $database -eq 'sqlite' -and $n -eq 3
            $started = Measure-Start $service @('--config', $settings) "$url/health" (Join-Path $work "service-$database-$n.log") 'service\.started: .* in ([0-9.]+) ms' -Keep:$keep
            $figures += $started.Figure
        }
        $results.serviceStart[$database] = $figures
        if ($database -eq 'sqlite') {
            try { $results.activations = Measure-Activations $url $keyFile 'aot' } finally { Stop-Direct $started.Process }
        }
    }

    # M4b1 and M4a1 with their own kind of build: a JIT Release elm-service on SQLite, on the measuring CPUs.
    Invoke-Checked 'Building elm-service (JIT, Release)' { dotnet build (Join-Path $root 'src/ElasticLM.Service') -c Release -v q -nologo | Out-Host }
    $jit = Join-Path $root 'src/ElasticLM.Service/bin/Release/net10.0/elm-service.dll'
    $url = "http://127.0.0.1:$(Get-FreePort)"
    $settings = Write-ServiceSettings 'service-jit' 'sqlite' "Data Source=$(Join-Path $work 'service-jit.db')" $url
    $keyFile = Initialize-Service $settings @('dotnet', $jit)
    $process = Start-Direct 'dotnet' @($jit, '--config', $settings) (Join-Path $work 'service-jit.log')
    try {
        Wait-Answer "$url/health"
        $adminJson = Join-Path $work 'admin-writes.json'
        Invoke-Load @('admin-writes', '--url', $url, '--api-key-file', $keyFile, '--count', ($Small ? '20' : '200'), '--runs', '3', '--out', $adminJson)
        $results.jit = [ordered]@{
            activations = Measure-Activations $url $keyFile 'jit'
            adminWrites = (Get-Content $adminJson -Raw | ConvertFrom-Json)
        }
    }
    finally {
        Stop-Direct $process
    }
}
catch {
    # The products' own logs hold no secret (design §18); their ends tell what stopped.
    foreach ($log in Get-ChildItem $work -Filter '*.log*' -ErrorAction SilentlyContinue) {
        "--- $($log.Name)"
        Get-Content $log.FullName -Tail 15
    }
    throw
}
finally {
    Pop-Location -ErrorAction SilentlyContinue
    foreach ($process in $processes) { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } }
    $env:ELM_KEY_PASSPHRASE = $savedPassphrase
    $results | ConvertTo-Json -Depth 8 | Set-Content $Out
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
"Wrote $Out"
