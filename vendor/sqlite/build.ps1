# Builds SQLite from the pinned amalgamation in this folder on Windows (design §14.2, decision 0.72); build.sh does the same
# on Linux and macOS.
#
# Output, for the runtime identifier given:
#   <OutDir>/static/e_sqlite3.lib  a static library that NativeAOT links into elm-service
#   <OutDir>/shared/e_sqlite3.dll  a shared library that JIT runs (tests, dotnet run) load
#
# The C compiler is the one NativeAOT already needs: MSVC.
# Usage: ./build.ps1 -Rid win-x64 -OutDir <folder>

param(
    [Parameter(Mandatory = $true)][string] $Rid,
    [Parameter(Mandatory = $true)][string] $OutDir
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $IsWindows -or $Rid -notlike "win-*") {
    Write-Error "build.ps1 builds the Windows targets on Windows; use build.sh for $Rid."
    exit 1
}

# The amalgamation is checked against the hashes recorded when it was added, so an edited copy is not built.
$listed = @()
foreach ($line in Get-Content -Path "SHA256SUMS") {
    $expected, $file = $line -split "\s+", 2
    $listed += $file
    $actual = (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        Write-Error "vendor/sqlite/$file does not match SHA256SUMS ($actual)."
        exit 1
    }
}

# A file left out of SHA256SUMS would be built without a check.
foreach ($required in @("sqlite3.c", "sqlite3.h")) {
    if ($listed -notcontains $required) {
        Write-Error "vendor/sqlite/SHA256SUMS does not list $required."
        exit 1
    }
}

# The options are shared with build.sh.
$defines = @(Get-Content -Path "defines.txt" | Where-Object { $_ -and -not $_.StartsWith("#") })

$static = Join-Path $OutDir "static"
$shared = Join-Path $OutDir "shared"
New-Item -ItemType Directory -Force -Path $static, $shared | Out-Null

function Invoke-Native([string] $command) {
    Write-Host $command
    cmd /c $command
    if ($LASTEXITCODE -ne 0) {
        Write-Error "The SQLite build failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
# design §20.5: win-arm64 needs MSVC's ARM64 tools, the other Windows targets the x64 ones.
$component = if ($Rid -eq "win-arm64") { "Microsoft.VisualStudio.Component.VC.Tools.ARM64" } else { "Microsoft.VisualStudio.Component.VC.Tools.x86.x64" }
$vs = & $vswhere -latest -products * -requires $component -property installationPath
if (-not $vs) {
    Write-Error "MSVC was not found. Install Visual Studio's 'Desktop development with C++' workload (.cursor/rules/engineering.mdc)."
    exit 1
}

# vcvarsall.bat calls vswhere.exe by name; the installer folder is not on PATH by default.
$env:PATH = (Split-Path $vswhere) + ";" + $env:PATH
$arch = if ($Rid -eq "win-arm64") { "arm64" } else { "x64" }
$vcvars = "`"$vs\VC\Auxiliary\Build\vcvarsall.bat`" $arch"
$flags = ($defines | ForEach-Object { "/D$_" }) -join " "
$obj = Join-Path $OutDir "obj"
New-Item -ItemType Directory -Force -Path $obj | Out-Null

# Two compiles: the shared library exports the API; the static one does not, so elm-service exports nothing.
Invoke-Native "call $vcvars >nul && cl /nologo /c /O2 /MT $flags /Fo`"$obj\static.obj`" sqlite3.c && lib /nologo /OUT:`"$static\e_sqlite3.lib`" `"$obj\static.obj`""
Invoke-Native "call $vcvars >nul && cl /nologo /c /O2 /MD $flags /DSQLITE_API=__declspec(dllexport) /Fo`"$obj\shared.obj`" sqlite3.c && link /nologo /DLL /OUT:`"$shared\e_sqlite3.dll`" `"$obj\shared.obj`""

Write-Host "SQLite built for $Rid in $OutDir."
