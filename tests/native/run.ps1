<#
.SYNOPSIS
    Builds and runs the C test of libelasticlm against the published library (design §17.4).

.DESCRIPTION
    - Compares the names the library exports with the functions include/elasticlm.h declares (design §17.3).
    - Compiles tests/native/test_elasticlm.c with warnings as errors (MSVC /W4 /WX, gcc and clang -Wall -Wextra -Werror).
    - Makes keys and two licenses with elm in a temporary folder: one unbound, one bound to the key the test creates.
    - Runs the verification test in its two steps.
    - Starts elm-service on a free loopback port (with 10 requests a second per installation key, not 2), makes a product, a feature, a policy, a license, and the product's trial
      policy with the setup key, and runs the activation test against it: activation, a refresh, deactivation, and a trial
      (design §17.5, §20.7).
    - Runs the offline activation in phases of the C test that share one activation folder: the test writes the request
      file, elm offline process gets the answer from elm-service, and the test imports it. Then it revokes that license
      through the administration API, and the test's refresh must be refused with ELM_LICENSE_REVOKED (design §20.7).
    - Starts elm-server on a free https loopback port with a server license of 15-second leases bound to its key, makes a
      cloud-floating license on elm-service, and runs the lease test against both (design §17.6).
    - Prints the library's runtime dependencies.
    Everything it makes goes in a temporary folder that it removes at the end (.cursor/rules/scratch.mdc).

.PARAMETER Library
    The published libelasticlm (elasticlm.dll, libelasticlm.so, or libelasticlm.dylib).

.PARAMETER Elm
    The elm executable used to make the keys and licenses and to pass the offline request to elm-service.

.PARAMETER Service
    The elm-service executable the activation test runs against.

.PARAMETER Server
    The elm-server executable the lease test runs against.

.PARAMETER Rid
    The runtime identifier the library was published for (design §20.5). Defaults to this machine's. The C test is compiled
    for it: osx-x64 on an arm64 Mac runs under Rosetta.

.PARAMETER Publish
    Publish the four with NativeAOT for this machine first, into the temporary folder, instead of taking -Library, -Elm,
    -Service, and -Server. elm-service needs web/admin/dist (design §15.2).
#>
[CmdletBinding()]
param(
    [string]$Library,
    [string]$Elm,
    [string]$Service,
    [string]$Server,
    [string]$Rid,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'

# The script changes the environment (the passphrase of this run's keys, the MSVC variables); it puts it back at the end so
# the calling shell keeps its own (.cursor/rules/scratch.mdc).
$savedEnvironment = [Environment]::GetEnvironmentVariables()
$root = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$work = Join-Path ([IO.Path]::GetTempPath()) ('elm-native-' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(6)))
New-Item -ItemType Directory $work | Out-Null

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE." }
}

# On Windows, the MSVC environment of the newest Visual Studio, found with vswhere (cl.exe, link.exe, dumpbin.exe).
# design §20.5: the tools of the machine's architecture, so win-arm64 is built and run on an arm64 runner.
function Import-MsvcEnvironment {
    $arm64 = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64'
    $component = if ($arm64) { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -products * -requires $component -property installationPath
    if (-not $vs) { throw 'No Visual Studio with the C++ tools was found.' }
    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat'
    $arch = if ($arm64) { 'arm64' } else { 'x64' }
    foreach ($line in (cmd /c "`"$vcvars`" $arch >nul && set")) {
        if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2]) }
    }

    # vcvars sets Platform, which MSBuild would read as a property and use in its output paths.
    [Environment]::SetEnvironmentVariable('Platform', $null)
}

try {
    $arm64 = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64'
    $rid = if ($Rid) { $Rid }
    elseif ($IsWindows) { $arm64 ? 'win-arm64' : 'win-x64' }
    elseif ($IsMacOS) { $arm64 ? 'osx-arm64' : 'osx-x64' }
    elseif (Test-Path '/etc/alpine-release') { 'linux-musl-x64' }
    else { $arm64 ? 'linux-arm64' : 'linux-x64' }
    if ($IsWindows) {
        # The NativeAOT linker step finds Visual Studio with vswhere too.
        $env:PATH = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer') + [IO.Path]::PathSeparator + $env:PATH
    }

    if ($Publish) {
        $out = Join-Path $work 'publish'
        Invoke-Checked 'Publishing ElasticLM.Native' { dotnet publish (Join-Path $root 'src/ElasticLM.Native') -c Release -r $rid -o $out -nologo | Out-Host }
        Invoke-Checked 'Publishing elm' { dotnet publish (Join-Path $root 'src/ElasticLM.Cli') -c Release -r $rid -o $out -nologo | Out-Host }
        Invoke-Checked 'Publishing elm-service' { dotnet publish (Join-Path $root 'src/ElasticLM.Service') -c Release -r $rid -o $out -nologo | Out-Host }
        $Library = Join-Path $out ($IsWindows ? 'elasticlm.dll' : ($IsMacOS ? 'libelasticlm.dylib' : 'libelasticlm.so'))
        $Elm = Join-Path $out ($IsWindows ? 'elm.exe' : 'elm')
        Invoke-Checked 'Publishing elm-server' { dotnet publish (Join-Path $root 'src/ElasticLM.Server') -c Release -r $rid -o $out -nologo | Out-Host }
        $Service = Join-Path $out ($IsWindows ? 'elm-service.exe' : 'elm-service')
        $Server = Join-Path $out ($IsWindows ? 'elm-server.exe' : 'elm-server')
    }

    if (-not $Library -or -not $Elm -or -not $Service -or -not $Server) { throw 'Pass -Library, -Elm, -Service, and -Server, or -Publish.' }
    if ($IsWindows) { Import-MsvcEnvironment }
    $Library = (Resolve-Path $Library).Path
    $Elm = (Resolve-Path $Elm).Path
    $Service = (Resolve-Path $Service).Path
    $Server = (Resolve-Path $Server).Path
    $header = Join-Path $root 'include/elasticlm.h'

    # design §17.3: the library's own exports must be the header's functions. The NativeAOT runtime also exports
    # DotNetRuntimeDebugHeader (seen in the M6a check), which is allowed.
    $declared = @(Select-String -Path $header -Pattern '^ELM_FUNCTION\([^,]+,\s*(elm_\w+),' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object)
    $exported = if ($IsWindows) {
        @((& dumpbin /exports $Library) -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+\S' -replace '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+).*', '$1')
    } elseif ($IsMacOS) {
        @((& nm -gU $Library) | ForEach-Object { ($_ -split '\s+')[-1] -replace '^_', '' })
    } else {
        # NativeAOT gives its Linux exports the symbol version V1.0, which nm prints as name@@V1.0.
        @((& nm -D --defined-only $Library) | ForEach-Object { ($_ -split '\s+')[-1] -replace '@.*$', '' })
    }
    $exported = @($exported | Where-Object { $_ -match '^(elm_|DotNet)' -and $_ -ne 'DotNetRuntimeDebugHeader' } | Sort-Object -Unique)
    $difference = Compare-Object $declared $exported
    if ($difference) {
        $difference | Format-Table | Out-Host
        throw 'The exported names differ from the header.'
    }
    "Exports match the header: $($declared.Count) functions."

    $test = Join-Path $work ($IsWindows ? 'test_elasticlm.exe' : 'test_elasticlm')
    $source = Join-Path $PSScriptRoot 'test_elasticlm.c'
    $include = Join-Path $root 'include'
    if ($IsWindows) {
        Invoke-Checked 'Compiling the C test' { cl /nologo /W4 /WX /std:c11 /I $include $source /Fo"$(Join-Path $work 'test_elasticlm.obj')" /Fe"$test" | Out-Host }
    } elseif ($IsMacOS) {
        # The test is built for the library's architecture: an x86_64 test runs under Rosetta and loads the osx-x64 library.
        $arch = $rid -eq 'osx-x64' ? 'x86_64' : 'arm64'
        Invoke-Checked 'Compiling the C test' { clang -arch $arch -std=c11 -Wall -Wextra -Werror -I $include $source -o $test -lpthread }
    } else {
        Invoke-Checked 'Compiling the C test' { gcc -std=c11 -Wall -Wextra -Werror -pthread -I $include $source -o $test -ldl }
    }

    # Keys and licenses made for this run only.
    Push-Location $work
    try {
        $env:ELM_KEY_PASSPHRASE = 'native test passphrase 0123'
        Invoke-Checked 'elm key root create' { & $Elm key root create --out root.key.pem --public-out root.pub.pem | Out-Null }
        Invoke-Checked 'elm key signing create' { & $Elm key signing create --out signing.key.pem --public-out signing.pub.pem | Out-Null }
        Invoke-Checked 'elm key certify' { & $Elm key certify --root root.key.pem --public signing.pub.pem --use license lease --out signing.cert | Out-Null }
        $signingKid = (& $Elm key thumbprint signing.pub.pem).Trim()
        $keyFolder = Join-Path $work 'installation'
        $thumbprint = (& $test thumbprint $Library $keyFolder).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'The test could not create the installation key.' }

        $expires = [DateTimeOffset]::UtcNow.AddDays(30).ToString('yyyy-MM-ddTHH:mm:ssZ')
        $common = @('license', 'issue', '--signing-key', 'signing.key.pem', '--certificate', 'signing.cert', '--product', 'acme-cad', '--feature', 'pro;ver=>=2.0 <3.0', '--expires', $expires, '--meta', 'customer=Contoso', '--trust', 'root.pub.pem')
        Invoke-Checked 'elm license issue (unbound)' { & $Elm @common --license-id c-unbound --out unbound.elmlic | Out-Null }
        Invoke-Checked 'elm license issue (bound)' { & $Elm @common --license-id c-bound --bind-key $thumbprint --out bound.elmlic | Out-Null }

        & $test verify $Library $keyFolder (Join-Path $work 'root.pub.pem') $signingKid (Join-Path $work 'unbound.elmlic') (Join-Path $work 'bound.elmlic')
        if ($LASTEXITCODE -ne 0) { throw 'The C test failed.' }

        # design §17.5: the activation test against elm-service with this run's keys, nonce secret, and SQLite database.
        # The C test gets the listening URL itself: without publicUrls, a proof's htu is compared with it, so 127.0.0.1
        # is not mixed with localhost.
        function Get-FreePort {
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $free = $listener.LocalEndpoint.Port
            $listener.Stop()
            $free
        }
        $url = "http://127.0.0.1:$(Get-FreePort)"
        $env:ELM_SIGNING_PASSPHRASE = $env:ELM_KEY_PASSPHRASE
        $env:ELM_NONCE_SECRET = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $env:ELM_SIGNING_KEY = Join-Path $work 'signing.key.pem'
        $env:ELM_SIGNING_CERTIFICATES = Join-Path $work 'signing.cert'
        $env:ELM_TRUST_ROOTKEYS = Join-Path $work 'root.pub.pem'
        $env:ELM_DATABASE_CONNECTION = "Data Source=$(Join-Path $work 'service.db')"
        $env:ELM_BOOTSTRAPOWNER_ISS = 'https://idp.example'
        $env:ELM_BOOTSTRAPOWNER_SUB = 'native-test'
        $env:ELM_LISTEN = $url

        # design §14.7: the default of 2 requests a second per installation key fits a real device. The activation test
        # sends three requests with one key at once (activate, refresh, deactivate), so this run allows more.
        $env:ELM_RATELIMIT_CLIENTPERKEY = '10'
        $apiKey = (& $Service setup).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $apiKey.StartsWith('elm_')) { throw 'elm-service setup did not print a key.' }
        $process = Start-Process $Service -PassThru -NoNewWindow
        try {
            $ready = $false
            for ($i = 0; $i -lt 30 -and -not $ready; $i++) {
                try { $ready = (Invoke-WebRequest "$url/health" -SkipHttpErrorCheck).StatusCode -eq 200 } catch { }
                if (-not $ready) { Start-Sleep -Seconds 1 }
            }
            if (-not $ready) { throw 'elm-service did not answer /health.' }

            $headers = @{ Authorization = "Bearer $apiKey" }
            function Add-AdminRecord([string]$path, [string]$body) {
                Invoke-RestMethod "$url/v1/admin/$path" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
            }
            $product = (Add-AdminRecord 'products' '{"code":"acme-cad","name":"Acme CAD"}').id
            Add-AdminRecord "products/$product/features" '{"code":"pro","name":"Pro"}' | Out-Null
            $policy = (Add-AdminRecord 'policies' "{`"productId`":`"$product`",`"name`":`"native`",`"maxActivations`":3}").id
            $licenseKey = (Add-AdminRecord 'licenses' "{`"policyId`":`"$policy`",`"name`":`"native`",`"features`":[{`"code`":`"pro`"}]}").key

            # design §9.3, §12.9: the product offers a trial through its trial policy, set by PATCH. A trial policy is
            # node-locked and has no grace period.
            $trial = (Add-AdminRecord 'policies' "{`"productId`":`"$product`",`"name`":`"trial`",`"licenseKind`":`"trial`",`"gracePeriod`":0}").id
            Invoke-RestMethod "$url/v1/admin/products/$product" -Method Patch -Headers $headers -ContentType 'application/json' -Body "{`"trialPolicyId`":`"$trial`"}" | Out-Null

            $rootKey = Join-Path $work 'root.pub.pem'
            & $test activation $Library (Join-Path $work 'activation') $rootKey $url $licenseKey
            if ($LASTEXITCODE -ne 0) { throw 'The C activation test failed.' }

            # design §20.7: the offline activation and the refused refresh, in phases that share one activation folder.
            # The C program has no HTTP client, so elm offline process carries the request between the first two, and the
            # administration API revokes the license before the third.
            $offline = Add-AdminRecord 'licenses' "{`"policyId`":`"$policy`",`"name`":`"offline`",`"features`":[{`"code`":`"pro`"}]}"
            $offlineFolder = Join-Path $work 'offline'
            $request = Join-Path $work 'offline.elmreq'
            $answer = Join-Path $work 'offline.elmresp'
            & $test offline-request $Library $offlineFolder $rootKey $url $offline.key $request
            if ($LASTEXITCODE -ne 0) { throw 'The C offline request test failed.' }
            Invoke-Checked 'elm offline process' { & $Elm offline process --url $url --in $request --out $answer | Out-Null }
            & $test offline-import $Library $offlineFolder $rootKey $url $answer
            if ($LASTEXITCODE -ne 0) { throw 'The C offline import test failed.' }
            Add-AdminRecord "licenses/$($offline.id)/revoke" '' | Out-Null
            & $test revoked $Library $offlineFolder $rootKey $url
            if ($LASTEXITCODE -ne 0) { throw 'The C revoked refresh test failed.' }

            # design §17.6: a cloud-floating license on the service, and elm-server on https with its own self-signed
            # certificate. The server's data folder is not made here: the server makes it 0700, and ProtectedFiles refuses
            # a folder that group or other can write.
            $cloud = (Add-AdminRecord 'policies' "{`"productId`":`"$product`",`"name`":`"cloud`",`"floating`":`"cloud`"}").id
            $leaseKey = (Add-AdminRecord 'licenses' "{`"policyId`":`"$cloud`",`"name`":`"lease`",`"features`":[{`"code`":`"pro`",`"seats`":2}]}").key
            $serverUrl = "https://127.0.0.1:$(Get-FreePort)"
            $env:ELM_LISTEN = $serverUrl
            $env:ELM_ADMIN_LISTEN = "http://127.0.0.1:$(Get-FreePort)"
            $env:ELM_DATADIRECTORY = Join-Path $work 'server-data'
            $env:ELM_PRODUCT = 'acme-cad'
            $env:ELM_LICENSE = Join-Path $work 'server.elmlic'

            # Without its license the server stops at once and names its installation key, which it has just made.
            $refusal = (& $Server 2>&1 | Out-String)
            if ($refusal -notmatch 'installation key ([A-Za-z0-9_-]{43})') { $refusal; throw 'elm-server did not name its installation key.' }
            $serverKey = $Matches[1]
            Invoke-Checked 'elm license issue (server)' {
                & $Elm license issue --signing-key signing.key.pem --certificate signing.cert --product acme-cad --license-id c-server --kind server `
                    --feature 'pro;seats=3' --expires $expires --bind-key $serverKey --lease-minutes 0.25 --borrow-days 1 --trust root.pub.pem `
                    --out server.elmlic | Out-Null
            }

            $serverProcess = Start-Process $Server -PassThru -NoNewWindow
            try {
                # elm-server has no /health; its license endpoint answers once it listens.
                $ready = $false
                for ($i = 0; $i -lt 30 -and -not $ready; $i++) {
                    try { $ready = (Invoke-WebRequest "$serverUrl/v1/server/license" -SkipCertificateCheck -SkipHttpErrorCheck).StatusCode -eq 200 } catch { }
                    if (-not $ready) { Start-Sleep -Seconds 1 }
                }
                if (-not $ready) { throw 'elm-server did not answer /v1/server/license.' }

                & $test lease $Library (Join-Path $work 'lease') (Join-Path $work 'root.pub.pem') $serverUrl $url $leaseKey
                if ($LASTEXITCODE -ne 0) { throw 'The C lease test failed.' }
            }
            finally {
                Stop-Process -Id $serverProcess.Id -ErrorAction SilentlyContinue
                $serverProcess.WaitForExit()
            }
        }
        finally {
            # Windows keeps the SQLite file open until the process ends, so the folder is removed only after that.
            Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
            $process.WaitForExit()
        }
    }
    finally {
        Pop-Location
    }

    # design §17.4: the library's runtime dependencies, for the PR.
    'Runtime dependencies:'
    if ($IsWindows) { (& dumpbin /dependents $Library) -match '\.dll' }
    elseif ($IsMacOS) { & otool -L $Library }
    else { & ldd $Library }
    "Library: $Library ($((Get-Item $Library).Length) bytes)"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    foreach ($name in @([Environment]::GetEnvironmentVariables().Keys)) {
        if (-not $savedEnvironment.Contains($name)) { [Environment]::SetEnvironmentVariable($name, $null) }
    }
    foreach ($entry in $savedEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value) }
}
