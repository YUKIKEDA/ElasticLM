<#
.SYNOPSIS
    Checks the NativeAOT publish folders of one runtime identifier (design §4.1, §4.2, §20.5).

.DESCRIPTION
    For elm, elm-server, elm-service, and libelasticlm under -PublishDir:
    - Single binary: the folder holds the binary and its debug symbols only (.pdb on Windows, .dbg on Linux, a .dSYM folder
      on macOS). Any other file fails the check and is named.
    - Runtime dependencies: dumpbin /dependents (Windows, recorded only), the NEEDED entries of readelf -d (Linux), or
      otool -L (macOS). On Linux with glibc, only glibc's own libraries are allowed; on linux-musl-x64, only musl's libc;
      on macOS, only /usr/lib/ and /System/Library/.
    - glibc: the newest GLIBC_ symbol version objdump -T shows must be 2.28 or older (design §4.1). -RecordGlibcOnly records
      it without the limit, for CI, which builds on the runner's newer glibc.
    - macOS signature: codesign -dv is recorded; osx-arm64 binaries must carry the linker's ad-hoc signature (design §5.4).
    - Size: elm-service up to 40,000,000 bytes, elm-server up to 30,000,000, libelasticlm up to 8,000,000 (design §4.2,
      decimal megabytes). elm has no target and is recorded only. Debug symbols are not counted.
    Writes what it found to -Summary (the job summary by default) and fails at the end when any check failed.
    It runs on the OS family of the runtime identifier: Windows, Linux (glibc or musl binaries), or macOS.

.PARAMETER Rid
    The runtime identifier the folders were published for.

.PARAMETER PublishDir
    The folder with the elm, elm-server, elm-service, and libelasticlm subfolders (Publish-Rid.ps1).

.PARAMETER RecordGlibcOnly
    Record the newest glibc symbol version without applying the 2.28 limit.

.PARAMETER Summary
    The Markdown file to append to. Defaults to $env:GITHUB_STEP_SUMMARY; nothing is appended when both are empty.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'linux-musl-x64', 'osx-arm64', 'osx-x64')]
    [string]$Rid,
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [string]$Summary = $env:GITHUB_STEP_SUMMARY,
    [switch]$RecordGlibcOnly
)

$ErrorActionPreference = 'Stop'
$failures = [Collections.Generic.List[string]]::new()
$lines = [Collections.Generic.List[string]]::new()
$lines.Add("### $Rid")

$windows = $Rid.StartsWith('win-')
$mac = $Rid.StartsWith('osx-')
$musl = $Rid -eq 'linux-musl-x64'
$glibc = $Rid.StartsWith('linux-') -and -not $musl

# glibc 2.28 and older keep libpthread, libdl, and librt apart from libc; all of them are part of glibc.
$glibcLibraries = @('libc.so.6', 'libm.so.6', 'libpthread.so.0', 'libdl.so.2', 'librt.so.1', 'ld-linux-x86-64.so.2', 'ld-linux-aarch64.so.1')
$limits = @{ 'elm-service' = 40000000; 'elm-server' = 30000000; 'libelasticlm' = 8000000 }

function Get-BinaryName([string]$name) {
    if ($name -eq 'libelasticlm') {
        if ($windows) { 'elasticlm.dll' } elseif ($mac) { 'libelasticlm.dylib' } else { 'libelasticlm.so' }
    }
    elseif ($windows) { "$name.exe" }
    else { $name }
}

function Get-SymbolName([string]$binary) {
    if ($windows) { [IO.Path]::GetFileNameWithoutExtension($binary) + '.pdb' }
    elseif ($mac) { "$binary.dSYM" }
    else { "$binary.dbg" }
}

function Get-Dumpbin {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -products * -property installationPath
    $found = @(Get-ChildItem -Path (Join-Path $vs 'VC\Tools\MSVC') -Recurse -Filter dumpbin.exe -ErrorAction SilentlyContinue)
    $hostDir = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'Hostarm64' } else { 'Hostx64' }
    $preferred = @($found | Where-Object { $_.FullName -match "\\$hostDir\\" })
    if ($preferred.Count -gt 0) { $preferred[0].FullName } elseif ($found.Count -gt 0) { $found[0].FullName } else { throw 'dumpbin.exe was not found.' }
}

foreach ($name in @('elm', 'elm-server', 'elm-service', 'libelasticlm')) {
    $folder = Join-Path $PublishDir $name
    $binary = Get-BinaryName $name
    $path = Join-Path $folder $binary
    if (-not (Test-Path $path -PathType Leaf)) {
        $failures.Add("$name has no $binary in $folder.")
        continue
    }

    # Single binary: nothing but the binary and its symbols.
    $allowed = @($binary, (Get-SymbolName $binary))
    $extra = @(Get-ChildItem -Path $folder -Force | Where-Object { $allowed -notcontains $_.Name } | ForEach-Object { $_.Name })
    if ($extra.Count -gt 0) { $failures.Add("$name has files beside it: $($extra -join ', ').") }

    $size = (Get-Item $path).Length
    $limit = $limits[$name]
    $sizeText = if ($limit) { "$size bytes (limit $limit)" } else { "$size bytes (no limit)" }
    if ($limit -and $size -gt $limit) { $failures.Add("$name is $size bytes, over the limit of $limit (design §4.2).") }

    # Runtime dependencies.
    $dependencies = @()
    if ($windows) {
        $dependencies = @((& (Get-Dumpbin) /dependents $path) -match '^\s+\S+\.dll\s*$' | ForEach-Object { $_.Trim() })
    }
    elseif ($mac) {
        # The first line is the file itself; a library's second line is its own install name.
        $dependencies = @((& otool -L $path) | Select-Object -Skip 1 | ForEach-Object { ($_.Trim() -split ' \(')[0] } | Where-Object { $_ -ne '@rpath/libelasticlm.dylib' })
        $outside = @($dependencies | Where-Object { -not ($_.StartsWith('/usr/lib/') -or $_.StartsWith('/System/Library/')) })
        if ($outside.Count -gt 0) { $failures.Add("$name depends on libraries outside the system: $($outside -join ', ').") }
    }
    else {
        $dependencies = @((& readelf -d $path) | Select-String -Pattern '\(NEEDED\).*\[(.+)\]' | ForEach-Object { $_.Matches[0].Groups[1].Value })
        $other = if ($musl) {
            @($dependencies | Where-Object { $_ -notmatch '^(libc\.musl-x86_64\.so\.1|ld-musl-x86_64\.so\.1)$' })
        }
        else {
            @($dependencies | Where-Object { $glibcLibraries -notcontains $_ })
        }
        if ($other.Count -gt 0) { $failures.Add("$name needs libraries outside the C library: $($other -join ', ').") }
    }

    $extraText = ''
    if ($glibc) {
        $versions = @((& objdump -T $path) | Select-String -Pattern 'GLIBC_([0-9]+(\.[0-9]+)+)' -AllMatches | ForEach-Object { $_.Matches } | ForEach-Object { [version]$_.Groups[1].Value } | Sort-Object -Unique)
        $newest = if ($versions.Count -gt 0) { $versions[-1] } else { [version]'0.0' }
        $extraText = "; newest GLIBC_ $newest"
        if (-not $RecordGlibcOnly -and $newest -gt [version]'2.28') { $failures.Add("$name needs GLIBC_$newest, newer than 2.28 (design §4.1).") }
    }
    if ($mac) {
        $signature = (& codesign -dv $path 2>&1 | Out-String)
        $adhoc = $signature -match 'Signature=adhoc'
        $extraText = "; signature $(if ($adhoc) { 'ad-hoc' } else { 'none or other' })"
        if ($Rid -eq 'osx-arm64' -and -not $adhoc) { $failures.Add("$name has no ad-hoc signature (design §5.4).") }
    }

    $lines.Add("- ${name}: $binary, $sizeText$extraText; depends on $($dependencies -join ', ')")
}

foreach ($failure in $failures) { $lines.Add("- FAILED: $failure") }
$lines | Out-Host
if ($Summary) { $lines | Add-Content -Path $Summary -Encoding utf8NoBOM }
if ($failures.Count -gt 0) { throw "$($failures.Count) check(s) of the $Rid publish failed." }

# A native command's exit code (codesign, tar, a stopped process) would otherwise become the step's result.
exit 0
