<#
.SYNOPSIS
    Publishes the three executables and libelasticlm for one runtime identifier with NativeAOT (design §20.5).

.DESCRIPTION
    Publishes elm, elm-server, elm-service, and libelasticlm, each into its own folder under -OutDir, and fails when a
    publish fails or reports a warning. dsymutil's missing clang module caches on macOS are not counted: the Swift parts of
    .NET's Apple crypto carry debug references to the module cache of the machine that built them (.cursor/rules/aot.mdc).
    When DOTNET_SDK_VERSION is set, the SDK in use must be that version, so every runtime identifier of a release is built
    with the same ILCompiler and runtime. elm-service needs web/admin/dist (design §15.2).

.PARAMETER Rid
    The runtime identifier, one of the seven of design §4.1.

.PARAMETER VersionSuffix
    The version suffix (rc.1, ci.42), or empty for a release of VersionPrefix itself.

.PARAMETER OutDir
    The folder that gets elm, elm-server, elm-service, and libelasticlm subfolders.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'linux-musl-x64', 'osx-arm64', 'osx-x64')]
    [string]$Rid,
    [string]$VersionSuffix = '',
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

$sdk = (& dotnet --version).Trim()
if ($env:DOTNET_SDK_VERSION -and $sdk -ne $env:DOTNET_SDK_VERSION) {
    throw "The .NET SDK is $sdk, not DOTNET_SDK_VERSION $($env:DOTNET_SDK_VERSION)."
}

if ($IsWindows) {
    # The NativeAOT linker step and the SQLite build find Visual Studio with vswhere, which is not on PATH by default.
    $env:PATH = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer') + [IO.Path]::PathSeparator + $env:PATH
}

$targets = @(
    @{ Project = 'src/ElasticLM.Cli'; Name = 'elm' },
    @{ Project = 'src/ElasticLM.Server'; Name = 'elm-server' },
    @{ Project = 'src/ElasticLM.Service'; Name = 'elm-service' },
    @{ Project = 'src/ElasticLM.Native'; Name = 'libelasticlm' }
)

foreach ($target in $targets) {
    $folder = Join-Path $OutDir $target.Name
    $arguments = @('publish', (Join-Path $root $target.Project), '-c', 'Release', '-r', $Rid, '-o', $folder, '-nologo')
    if ($VersionSuffix) { $arguments += "-p:VersionSuffix=$VersionSuffix" }
    $log = & dotnet @arguments 2>&1
    $code = $LASTEXITCODE
    $log | Out-Host
    if ($code -ne 0) { throw "The NativeAOT publish of $($target.Name) for $Rid failed." }
    $warnings = @($log | Select-String -Pattern ': warning ' | Where-Object { $_.Line -notmatch 'ModuleCache/.+\.pcm: No such file or directory' })
    if ($warnings.Count -gt 0) { throw "The NativeAOT publish of $($target.Name) for $Rid reported $($warnings.Count) warnings." }
}

"Published elm, elm-server, elm-service, and libelasticlm for $Rid with SDK $sdk into $OutDir."

# A native command's exit code (codesign, tar, a stopped process) would otherwise become the step's result.
exit 0
