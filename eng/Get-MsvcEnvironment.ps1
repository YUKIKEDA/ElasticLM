<#
.SYNOPSIS
    Returns the environment variables that Visual Studio's vcvarsall.bat sets for an architecture, without changing this
    process's environment (design §20.7, V1c3c, review R3).

.DESCRIPTION
    Finds the newest Visual Studio with the C++ tools of the architecture through vswhere, runs vcvarsall.bat in a cmd
    process of its own, and returns the variables that it added or changed, as a hashtable whose keys compare without
    case: cl.exe, link.exe, and dumpbin.exe are then on its PATH. Platform is left out: MSBuild would read it as a property
    and use it in its output paths.

    The cmd process gets the Visual Studio Installer folder at the front of its PATH, since the environment scripts of
    Visual Studio call vswhere.exe by name (.cursor/rules/engineering.mdc), and no variable whose name starts with ELM_,
    so that a passphrase of the caller never reaches vcvars or the variables returned.

    tests/native/run.ps1 applies the variables to its own environment; eng/docs/Test-GuideSteps.ps1 gives them to the
    processes of a page's cl and app lines only.

.PARAMETER Architecture
    x64 or arm64: the architecture that cl.exe builds for.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('x64', 'arm64')][string]$Architecture
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The MSVC environment exists on Windows only.' }
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$vswhere = Join-Path $installer 'vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'No Visual Studio Installer (vswhere.exe) was found.' }
$component = $Architecture -eq 'arm64' ? 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' : 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
$vs = & $vswhere -latest -products * -requires $component -property installationPath
if (-not $vs) { throw "No Visual Studio with the C++ tools for $Architecture was found." }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat'

$info = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32\cmd.exe'))
# /s: cmd removes the first and the last quote of the command and keeps the rest as it is, the quotes around the path of
# vcvarsall.bat included. ArgumentList is not used: it escapes quotes as \", which cmd does not read.
$info.Arguments = "/d /s /c `"`"$vcvars`" $Architecture >nul && set`""
$info.UseShellExecute = $false
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
foreach ($name in @($info.Environment.Keys)) {
    if ($name.StartsWith('ELM_', [StringComparison]::OrdinalIgnoreCase)) { [void]$info.Environment.Remove($name) }
}
$info.Environment['PATH'] = $installer + [IO.Path]::PathSeparator + $info.Environment['PATH']
$before = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $info.Environment.GetEnumerator()) { $before[$entry.Key] = $entry.Value }

$process = [Diagnostics.Process]::Start($info)
try {
    $errors = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "vcvarsall.bat $Architecture failed with exit code $($process.ExitCode): $($errors.GetAwaiter().GetResult().Trim())" }
}
finally {
    $process.Dispose()
}

$variables = [hashtable]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in $output -split '\r?\n') {
    if ($line -notmatch '^([^=]+)=(.*)$') { continue }
    $name = $Matches[1]
    $value = $Matches[2]
    if ($name -eq 'Platform') { continue }
    if (-not $before.ContainsKey($name) -or $before[$name] -cne $value) { $variables[$name] = $value }
}
if (-not $variables.ContainsKey('PATH')) { throw "vcvarsall.bat $Architecture did not change PATH." }
return $variables
