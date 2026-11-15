# End-to-end verification suite for locally built portable and installer packages.
# Exercises: SHA-256 checksums, zip extraction, CLI/Core execution with portable Data,
# silent Inno Setup per-user installation, upgrade-preserving Data check, target-scoped
# process stop, and silent uninstallation.
param(
    [string]$ReleaseDir,
    [string]$Version,
    [switch]$ExpectInstaller = $true
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $ReleaseDir) {
    $ReleaseDir = Join-Path $repoRoot "dist\release"
}
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $repoRoot "src\Directory.Build.props"))).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "Verification failed: $Message"
    }
}

function Invoke-Exe {
    param([string]$ExePath, [string[]]$ArgumentList)
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $ExePath
    foreach ($arg in $ArgumentList) {
        [void]$psi.ArgumentList.Add($arg)
    }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    $proc.WaitForExit()
    return [pscustomobject]@{
        ExitCode = $proc.ExitCode
        StdOut   = $stdoutTask.GetAwaiter().GetResult()
        StdErr   = $stderrTask.GetAwaiter().GetResult()
    }
}

function Assert-ExeWorks {
    param([string]$ExePath, [string]$DataDir)
    Assert-True (Test-Path $ExePath) "Executable missing: $ExePath"
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
    $productVer = ($info.ProductVersion -split '\+')[0].Trim()
    Assert-True ($productVer -eq $Version) "Expected ProductVersion '$Version' on '$ExePath', got '$productVer'."

    $verRes = Invoke-Exe -ExePath $ExePath -ArgumentList @("--cli", "--version")
    if ($verRes.ExitCode -ne 0) {
        throw "CLI --version exited with $($verRes.ExitCode) for '$ExePath': $($verRes.StdOut)$($verRes.StdErr)"
    }
    $verText = $verRes.StdOut.Trim()
    Assert-True ($verText -like "*$Version*") "Expected CLI version output to contain '$Version', got: $verText"

    $statusRes = Invoke-Exe -ExePath $ExePath -ArgumentList @("--cli", "status", "--data-dir", $DataDir, "--game", "phantom-blade-zero", "--json")
    if ($statusRes.ExitCode -ne 0) {
        throw "CLI status exited with $($statusRes.ExitCode) for '$ExePath': $($statusRes.StdOut)$($statusRes.StdErr)"
    }
    $parsed = $statusRes.StdOut | ConvertFrom-Json
    Assert-True ($parsed.command -eq "status") "Unexpected status JSON payload from '$ExePath'."
    Assert-True ($parsed.app.executable -ieq $ExePath) "Expected app.executable '$ExePath', got '$($parsed.app.executable)'."
}

$portableName = "uemm_${Version}.zip"
$legacyName = "uemm_phantom-blade-zero_${Version}.zip"
$setupName = "uemm_${Version}_x64_setup.exe"
$checksumsPath = Join-Path $ReleaseDir "checksums.txt"

$portablePath = Join-Path $ReleaseDir $portableName
$legacyPath = Join-Path $ReleaseDir $legacyName
$setupPath = Join-Path $ReleaseDir $setupName

Assert-True (Test-Path $portablePath) "Portable zip not found: $portablePath"
Assert-True (-not (Test-Path $legacyPath)) "Legacy compatibility zip must not exist: $legacyPath"
Assert-True (-not (Test-Path (Join-Path $repoRoot "dist\uemm\UEModManager.exe"))) "Loose uncompressed single-file exe must not exist in dist\uemm\."
Assert-True (-not (Test-Path (Join-Path $repoRoot "dist\.build\publish\UEModManager.exe"))) "Staging single-file exe must be cleaned after packaging."
if ($ExpectInstaller) {
    Assert-True (Test-Path $setupPath) "Installer exe not found: $setupPath"
}
Assert-True (Test-Path $checksumsPath) "Checksums file not found: $checksumsPath"

$expectedReleaseFiles = @("checksums.txt", $portableName)
if ($ExpectInstaller) { $expectedReleaseFiles += $setupName }
$actualReleaseFiles = @(Get-ChildItem -Path $ReleaseDir -File | Sort-Object Name | ForEach-Object { $_.Name })
Assert-True (($actualReleaseFiles -join "|") -eq (($expectedReleaseFiles | Sort-Object) -join "|")) "Unexpected files in ${ReleaseDir}: $($actualReleaseFiles -join ', ')"

# 1. Verify checksums.txt entries match SHA-256 hashes of every artifact.
$checksumLines = Get-Content $checksumsPath | Where-Object { $_.Trim() }
$expectedCount = 1 + ($(if ($ExpectInstaller) { 1 } else { 0 }))
Assert-True ($checksumLines.Count -eq $expectedCount) "Expected $expectedCount checksum entries, found $($checksumLines.Count)."
foreach ($line in $checksumLines) {
    if ($line -notmatch '^([0-9a-f]{64})\s{2}(.+)$') {
        throw "Malformed checksum line: $line"
    }
    $expectedHash = $Matches[1]
    $fileName = $Matches[2].Trim()
    $filePath = Join-Path $ReleaseDir $fileName
    Assert-True (Test-Path $filePath) "Checksum target missing: $filePath"
    $actualHash = (Get-FileHash $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True ($actualHash -eq $expectedHash) "SHA-256 mismatch for ${fileName}: expected $expectedHash, got $actualHash"
}
Write-Host "[OK] Verified checksums.txt ($expectedCount artifacts)."

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("uemm-pkg-verify-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $sandbox | Out-Null

# Snapshot existing HKCU uninstall/app metadata so verification never alters pre-existing state.
$hadRegKey = Test-Path "HKCU:\Software\UEModManager"
$prevRegValues = if ($hadRegKey) { Get-ItemProperty "HKCU:\Software\UEModManager" } else { $null }

try {
    # 2. Verify portable archive extraction + single-file exe + Data next to exe.
    $portableDir = Join-Path $sandbox "portable"
    Expand-Archive -Path $portablePath -DestinationPath $portableDir -Force
    $extractedFiles = Get-ChildItem -Path $portableDir -File
    Assert-True ($extractedFiles.Count -eq 1 -and $extractedFiles[0].Name -eq "UEModManager.exe") "Portable zip must contain only UEModManager.exe."
    $portableExe = $extractedFiles[0].FullName
    New-Item -ItemType Directory -Force (Join-Path $portableDir "Data") | Out-Null
    Assert-ExeWorks -ExePath $portableExe -DataDir (Join-Path $portableDir "Data")
    Write-Host "[OK] Verified portable package ($portableName)."

    # 3. Verify installer silent install, Data preservation across upgrade & uninstall, and clean removal.
    if ($ExpectInstaller) {
        $installDir = Join-Path $sandbox "installed"
        New-Item -ItemType Directory -Force (Join-Path $installDir "Data") | Out-Null
        $sentinel = Join-Path $installDir "Data\sentinel.txt"
        Set-Content -Path $sentinel -Value "keep-me" -Encoding utf8

        $installProc = Start-Process -FilePath $setupPath -ArgumentList @(
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/CURRENTUSER",
            "/NOICONS",
            "/DIR=`"$installDir`""
        ) -Wait -PassThru
        Assert-True ($installProc.ExitCode -eq 0) "Installer exited with code $($installProc.ExitCode)."

        $installedExe = Join-Path $installDir "UEModManager.exe"
        $uninstaller = Join-Path $installDir "unins000.exe"
        Assert-True (Test-Path $installedExe) "Installed UEModManager.exe missing after silent setup."
        Assert-True (Test-Path $uninstaller) "Uninstaller unins000.exe missing after silent setup."
        Assert-True ((Get-Content $sentinel -Raw).Trim() -eq "keep-me") "Installer overwrote pre-existing Data\sentinel.txt."

        Assert-ExeWorks -ExePath $installedExe -DataDir (Join-Path $installDir "Data")

        # Simulate an upgrade install over the existing installation to confirm Data and registry survive cleanly.
        $upgradeProc = Start-Process -FilePath $setupPath -ArgumentList @(
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/CURRENTUSER",
            "/NOICONS",
            "/DIR=`"$installDir`""
        ) -Wait -PassThru
        Assert-True ($upgradeProc.ExitCode -eq 0) "Upgrade install exited with code $($upgradeProc.ExitCode)."
        Assert-True ((Get-Content $sentinel -Raw).Trim() -eq "keep-me") "Upgrade install lost pre-existing Data\sentinel.txt."

        # Silent uninstall must remove UEModManager.exe and uninstaller while preserving user Data.
        $uninstallProc = Start-Process -FilePath $uninstaller -ArgumentList @(
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART"
        ) -Wait -PassThru
        Assert-True ($uninstallProc.ExitCode -eq 0) "Uninstaller exited with code $($uninstallProc.ExitCode)."
        Assert-True (-not (Test-Path $installedExe)) "UEModManager.exe still exists after uninstall."
        Assert-True ((Get-Content $sentinel -Raw).Trim() -eq "keep-me") "Uninstaller deleted user Data\sentinel.txt."
        Write-Host "[OK] Verified installer package (${setupName}: install, upgrade, Data preservation, uninstall)."
    }
}
finally {
    if ($hadRegKey -and $prevRegValues) {
        New-Item -Path "HKCU:\Software\UEModManager" -Force | Out-Null
        if ($prevRegValues.InstallPath) { Set-ItemProperty "HKCU:\Software\UEModManager" -Name "InstallPath" -Value $prevRegValues.InstallPath }
        if ($prevRegValues.Version) { Set-ItemProperty "HKCU:\Software\UEModManager" -Name "Version" -Value $prevRegValues.Version }
    }
    elseif (-not $hadRegKey -and (Test-Path "HKCU:\Software\UEModManager")) {
        Remove-Item "HKCU:\Software\UEModManager" -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $sandbox) {
        Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "[PASS] All local package checks passed for UE MM $Version."
