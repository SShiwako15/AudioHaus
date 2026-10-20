[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64')]
    [string]$Platform = 'x64',
    [string]$Version = '1.0.0',
    [switch]$SkipBuild,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildOutput = Join-Path $repoRoot "artifacts\bin\$Platform\$Configuration\AudioHaus.App"
$releaseRoot = Join-Path $repoRoot 'artifacts\release'
$stage = Join-Path $releaseRoot 'AudioHaus'

function Find-CommandPath([string]$name) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $installation = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($installation) {
            $candidate = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
            if (Test-Path $candidate) {
                return $candidate
            }
        }
    }

    return $null
}

if ($Configuration -ne 'Release') {
    Write-Warning 'Packaging a non-Release configuration is intended for local verification only.'
}

if (-not $SkipBuild) {
    $msbuild = Find-CommandPath 'msbuild.exe'
    if (-not $msbuild) {
        throw 'MSBuild was not found. Install the Visual Studio C++ workload and run this script from Developer PowerShell, or use -SkipBuild only with a previously verified Release output.'
    }

    Write-Host "Building AudioHaus.sln ($Configuration|$Platform)..."
    & $msbuild (Join-Path $repoRoot 'AudioHaus.sln') /restore "/p:Configuration=$Configuration" "/p:Platform=$Platform"
    if ($LASTEXITCODE -ne 0) {
        throw "AudioHaus.sln build failed with exit code $LASTEXITCODE."
    }
} else {
    Write-Warning "Skipping build; using existing output at $buildOutput."
}

if (-not (Test-Path $buildOutput)) {
    throw "Application output was not found at $buildOutput."
}

if (Test-Path $stage) {
    Remove-Item $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item (Join-Path $buildOutput '*') $stage -Recurse -Force

$requiredFiles = @(
    'AudioHaus.App.exe',
    'AudioHaus.App.dll',
    'AudioHaus.App.runtimeconfig.json',
    'AudioHaus.App.deps.json',
    'AudioHaus.Interop.dll',
    'AudioHaus.Native.dll'
)
foreach ($file in $requiredFiles) {
    $path = Join-Path $stage $file
    if (-not (Test-Path $path)) {
        throw "Required release file is missing: $file"
    }
}

$unexpected = Get-ChildItem $stage -Recurse -File | Where-Object {
    $_.Extension -in @('.pdb', '.ilk', '.iobj', '.ipdb')
}
if ($unexpected) {
    $unexpected | Remove-Item -Force
}

if ($SkipInstaller) {
    Write-Host "Release files staged at $stage"
    exit 0
}

$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    $isccCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($candidate in $isccCandidates) {
        if (Test-Path $candidate) {
            $iscc = Get-Item $candidate
            break
        }
    }
}
if (-not $iscc) {
    throw 'Inno Setup compiler was not found. Install Inno Setup 6, then rerun this script or use -SkipInstaller to validate the staged release.'
}

$iss = Join-Path $PSScriptRoot 'AudioHaus.iss'
Write-Host "Building installer with $($iscc.Source)..."
& $iscc.Source "/DAppVersion=$Version" "/DSourceDir=$stage" "/DOutputDir=$releaseRoot" $iss
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

Write-Host "Installer created under $releaseRoot"
