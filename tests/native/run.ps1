<#
.SYNOPSIS
    Builds and runs the C test of libelasticlm against the published library (design §17.4).

.DESCRIPTION
    - Compares the names the library exports with the functions include/elasticlm.h declares (design §17.3).
    - Compiles tests/native/test_elasticlm.c with warnings as errors (MSVC /W4 /WX, gcc and clang -Wall -Wextra -Werror).
    - Makes keys and two licenses with elm in a temporary folder: one unbound, one bound to the key the test creates.
    - Runs the verification test in its two steps.
    - Starts elm-service on a free loopback port, makes a product, a feature, a policy, and a license with the setup key,
      and runs the activation test against it (design §17.5).
    - Prints the library's runtime dependencies.
    Everything it makes goes in a temporary folder that it removes at the end (.cursor/rules/scratch.mdc).

.PARAMETER Library
    The published libelasticlm (elasticlm.dll, libelasticlm.so, or libelasticlm.dylib).

.PARAMETER Elm
    The elm executable used to make the keys and licenses.

.PARAMETER Service
    The elm-service executable the activation test runs against.

.PARAMETER Publish
    Publish the three with NativeAOT for this machine first, into the temporary folder, instead of taking -Library, -Elm,
    and -Service. elm-service needs web/admin/dist (design §15.2).
#>
[CmdletBinding()]
param(
    [string]$Library,
    [string]$Elm,
    [string]$Service,
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
function Import-MsvcEnvironment {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'No Visual Studio with the C++ tools was found.' }
    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    foreach ($line in (cmd /c "`"$vcvars`" >nul && set")) {
        if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2]) }
    }

    # vcvars sets Platform, which MSBuild would read as a property and use in its output paths.
    [Environment]::SetEnvironmentVariable('Platform', $null)
}

try {
    $rid = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { 'osx-arm64' } else { 'linux-x64' }
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
        $Service = Join-Path $out ($IsWindows ? 'elm-service.exe' : 'elm-service')
    }

    if (-not $Library -or -not $Elm -or -not $Service) { throw 'Pass -Library, -Elm, and -Service, or -Publish.' }
    if ($IsWindows) { Import-MsvcEnvironment }
    $Library = (Resolve-Path $Library).Path
    $Elm = (Resolve-Path $Elm).Path
    $Service = (Resolve-Path $Service).Path
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
        Invoke-Checked 'Compiling the C test' { clang -std=c11 -Wall -Wextra -Werror -I $include $source -o $test -lpthread }
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
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        $url = "http://127.0.0.1:$port"
        $env:ELM_SIGNING_PASSPHRASE = $env:ELM_KEY_PASSPHRASE
        $env:ELM_NONCE_SECRET = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $env:ELM_SIGNING_KEY = Join-Path $work 'signing.key.pem'
        $env:ELM_SIGNING_CERTIFICATES = Join-Path $work 'signing.cert'
        $env:ELM_TRUST_ROOTKEYS = Join-Path $work 'root.pub.pem'
        $env:ELM_DATABASE_CONNECTION = "Data Source=$(Join-Path $work 'service.db')"
        $env:ELM_BOOTSTRAPOWNER_ISS = 'https://idp.example'
        $env:ELM_BOOTSTRAPOWNER_SUB = 'native-test'
        $env:ELM_LISTEN = $url
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

            & $test activation $Library (Join-Path $work 'activation') (Join-Path $work 'root.pub.pem') $url $licenseKey
            if ($LASTEXITCODE -ne 0) { throw 'The C activation test failed.' }
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
