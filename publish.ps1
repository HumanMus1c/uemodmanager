# Builds local release packages only (no loose exe or legacy-named zip left behind):
#   dist\release\uemm_<version>.zip             portable zip package
#   dist\release\uemm_<version>_x64_setup.exe   Inno Setup per-user Windows x64 installer
#   dist\release\checksums.txt                  SHA-256 checksums for all packages
# The exe is a single file, framework dependent (needs the .NET 8 Desktop Runtime). The publish
# checks it (CheckPublishedExe in UEManager.UI.csproj) before packaging.
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [string]$IsccPath,
    [switch]$Verify
)

$ErrorActionPreference = "Stop"

function Resolve-IsccPath {
    param([string]$ExplicitPath)
    if ($ExplicitPath) {
        if (-not (Test-Path $ExplicitPath)) {
            throw "Inno Setup compiler not found at '$ExplicitPath'."
        }
        return (Resolve-Path $ExplicitPath).Path
    }
    $cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ -and (Test-Path $_) }
    $found = $candidates | Select-Object -First 1
    if ($found) {
        return $found
    }
    return $null
}

$dist = Join-Path $PSScriptRoot "dist"
$build = Join-Path $dist ".build"
$slnx = Join-Path $PSScriptRoot "src\UEManager.slnx"
$project = Join-Path $PSScriptRoot "src\UEManager.UI\UEManager.UI.csproj"
$version = ([xml](Get-Content (Join-Path $PSScriptRoot "src\Directory.Build.props"))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1

if (-not $SkipTests) {
    dotnet test $slnx -c $Configuration --artifacts-path $build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$release = Join-Path $dist "release"
if (Test-Path $release) { Remove-Item $release -Recurse -Force }
New-Item -ItemType Directory -Force $release | Out-Null

# Remove any stale loose exe from earlier runs unless user data lives alongside it.
$legacyOut = Join-Path $dist "uemm"
if (Test-Path $legacyOut) {
    $legacyExe = Join-Path $legacyOut "UEModManager.exe"
    if (Test-Path $legacyExe) { Remove-Item $legacyExe -Force }
    if ((Get-ChildItem $legacyOut -Force | Measure-Object).Count -eq 0) {
        Remove-Item $legacyOut -Force
    }
}

$stage = Join-Path $build "publish"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

try {
    dotnet publish $project -c $Configuration --artifacts-path $build -o $stage "-p:DebugType=none"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    # The zip name is part of the update check: the app takes the zip of a release by this name (AppInfo.ReleaseFileName).
    $zip = Join-Path $release "uemm_$version.zip"
    Compress-Archive -Path (Join-Path $stage "UEModManager.exe") -DestinationPath $zip

    if (-not $SkipInstaller) {
        $iscc = Resolve-IsccPath -ExplicitPath $IsccPath
        if (-not $iscc) {
            throw "Inno Setup 6 compiler (ISCC.exe) was not found. Install JRSoftware.InnoSetup, pass -IsccPath, or use -SkipInstaller."
        }
        $iss = Join-Path $PSScriptRoot "packaging\windows\installer.iss"
        $setupBase = "uemm_${version}_x64_setup"
        & $iscc "/Q" "/DAppVersion=$version" "/DSourceDir=$stage" "/DOutputDir=$release" "/DOutputBaseFilename=$setupBase" $iss
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
}

$artifacts = Get-ChildItem -Path $release -File | Where-Object { $_.Extension -in @(".zip", ".exe") } | Sort-Object Name
$checksumFile = Join-Path $release "checksums.txt"
$artifacts | ForEach-Object {
    $hash = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
} | Set-Content -Path $checksumFile -Encoding ascii

Write-Host "Published packages to $release (single-file check passed):"
Get-ChildItem $release -File | Sort-Object Name | ForEach-Object { Write-Host "  $($_.Name)" }

if ($Verify) {
    $verifyScript = Join-Path $PSScriptRoot "packaging\windows\verify-packages.ps1"
    & $verifyScript -ReleaseDir $release -Version $version -ExpectInstaller:(-not $SkipInstaller)
}
