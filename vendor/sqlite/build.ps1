# Builds SQLite from the pinned amalgamation in this folder (design §14.2, decision 0.72).
#
# Output, for the runtime identifier given:
#   <OutDir>/static/  a static library that NativeAOT links into elm-service (e_sqlite3.lib, or libe_sqlite3.a)
#   <OutDir>/shared/  a shared library that JIT runs (tests, dotnet run) load (e_sqlite3.dll, libe_sqlite3.so, libe_sqlite3.dylib)
#
# The C compiler is the one NativeAOT already needs: MSVC on Windows, clang on Linux and macOS.
# Usage: ./build.ps1 -Rid win-x64 -OutDir <folder>

param(
    [Parameter(Mandatory = $true)][string] $Rid,
    [Parameter(Mandatory = $true)][string] $OutDir
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

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

# Options read from https://sqlite.org/compile.html: double-quoted strings are identifiers only, memory statistics are off,
# and column metadata is on because Microsoft.Data.Sqlite asks for it.
$defines = @(
    "SQLITE_THREADSAFE=1",
    "SQLITE_DQS=0",
    "SQLITE_DEFAULT_MEMSTATUS=0",
    "SQLITE_ENABLE_COLUMN_METADATA=1"
)

$static = Join-Path $OutDir "static"
$shared = Join-Path $OutDir "shared"
New-Item -ItemType Directory -Force -Path $static, $shared | Out-Null

function Invoke-Native([string] $command) {
    Write-Host $command
    if ($IsWindows) {
        cmd /c $command
    }
    else {
        # sh, not bash: the .NET SDK's Alpine image has no bash (design §20.5).
        sh -c $command
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "The SQLite build failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
}

if ($Rid -like "win-*") {
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
}
else {
    $flags = ($defines | ForEach-Object { "-D$_" }) -join " "
    $obj = Join-Path $OutDir "sqlite3.o"
    $extension = if ($Rid -like "osx-*") { "dylib" } else { "so" }
    $libraries = if ($Rid -like "osx-*") { "" } else { "-lpthread -ldl -lm" }
    # design §20.5: on macOS the target comes from the RID, with the minimum version NativeAOT links for (12.0), so osx-x64
    # builds on an arm64 runner and the object agrees with the executable.
    $target = switch ($Rid) {
        "osx-x64" { "--target=x86_64-apple-macos12.0" }
        "osx-arm64" { "--target=arm64-apple-macos12.0" }
        default { "" }
    }
    Invoke-Native "clang -c -O2 -fPIC $target $flags -o '$obj' sqlite3.c"
    Invoke-Native "ar rcs '$static/libe_sqlite3.a' '$obj'"
    Invoke-Native "clang -shared $target -o '$shared/libe_sqlite3.$extension' '$obj' $libraries"
}

Write-Host "SQLite built for $Rid in $OutDir."
