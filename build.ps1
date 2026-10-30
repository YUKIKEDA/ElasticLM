# Local verification gate. Hosted CI (.github/workflows/ci.yml) runs the same steps.
# Usage: ./build.ps1 [-SkipWeb]   (PowerShell 7)
#   -SkipWeb: do not build web/admin; use the web/admin/dist that is there (CI's .NET jobs receive it from the SPA job).
# Exits non-zero on the first failure.
param([switch] $SkipWeb)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Invoke-Step([string] $name, [scriptblock] $command) {
    Write-Host "==> $name"
    & $command
    if ($LASTEXITCODE -ne 0) {
        Write-Error "$name failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
}

function Stop-Build([string] $message) {
    Write-Error $message
    exit 1
}

$solution = "ElasticLM.slnx"
$web = Join-Path $PSScriptRoot "web/admin"

# design §15.2 (decision 0.100): the admin UI is built before .NET, which embeds web/admin/dist.
if ((Test-Path $web) -and -not $SkipWeb) {
    $wanted = (Get-Content (Join-Path $web ".node-version") -Raw).Trim()
    $node = Get-Command node -ErrorAction SilentlyContinue
    $found = if ($node) { (& node --version).TrimStart("v") } else { "none" }
    if ($found -ne $wanted) {
        Stop-Build "web/admin needs Node.js $wanted (web/admin/.node-version); this shell has $found."
    }

    if (-not (Get-Command corepack -ErrorAction SilentlyContinue)) {
        Stop-Build "web/admin needs Corepack, which Node.js $wanted includes; it is not on the PATH."
    }

    Push-Location $web
    try {
        # Corepack checks pnpm's version and hash against packageManager in package.json; the first run downloads pnpm.
        Invoke-Step "Install web/admin (pnpm, frozen lockfile)" { corepack pnpm install --frozen-lockfile }

        # design §15.2: pnpm's paths under node_modules are long; on Windows the relative path stays within 160 characters,
        # leaving 100 of the 260 for the checkout's own folder.
        if ($IsWindows) {
            $longest = Get-ChildItem -Recurse -Force -File -Path node_modules -ErrorAction SilentlyContinue |
                ForEach-Object { $_.FullName.Substring($web.Length + 1) } |
                Sort-Object Length -Descending |
                Select-Object -First 1
            Write-Host "Longest path under web/admin: $($longest.Length) characters ($longest)"
            if ($longest.Length -gt 160) {
                Stop-Build "A path under web/admin is $($longest.Length) characters, more than 160: $longest"
            }
        }

        Invoke-Step "Type-check web/admin (tsc)" { corepack pnpm run typecheck }
        Invoke-Step "Test web/admin (Vitest)" { corepack pnpm test }
        Invoke-Step "Build web/admin (Vite)" { corepack pnpm run build }
    }
    finally {
        Pop-Location
    }
}
elseif ((Test-Path $web) -and -not (Test-Path (Join-Path $web "dist/index.html"))) {
    Stop-Build "-SkipWeb needs web/admin/dist, which the SPA job or 'corepack pnpm build' in web/admin makes."
}

Invoke-Step "Restore local tools" { dotnet tool restore }
Invoke-Step "Check format (CSharpier)" { dotnet csharpier check . }
Invoke-Step "Restore" { dotnet restore $solution }

# Test projects follow the tests/*.Tests naming (docs/conventions.md).
$testProjects = @(Get-ChildItem -Path tests -Recurse -Filter *.Tests.csproj -ErrorAction SilentlyContinue)

# Release is what ships. Debug runs the checks that exist only there.
foreach ($configuration in "Release", "Debug") {
    Invoke-Step "Build $configuration (warnings are errors)" { dotnet build $solution --no-restore -c $configuration }

    foreach ($project in $testProjects) {
        Invoke-Step "Test $($project.BaseName) ($configuration)" { dotnet test --project $project.FullName --no-build -c $configuration }
    }
}

Write-Host "build.ps1 completed successfully."
