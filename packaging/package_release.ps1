#Requires -Version 5.1
<#
.SYNOPSIS
    Reproducible release packaging & SHA-256 verification script for TokLens v2.2.0.

.DESCRIPTION
    Pure Windows PowerShell 5.1+ script with zero external dependencies:
    1. Optionally triggers build.bat (or skips compilation when -SkipBuild is specified).
    2. Verifies required release assets (TokLens.exe, vps\toklens_agent.py,
       vps\install_macos_launchd.sh, README.md, and optional LICENSE).
    3. Builds a deterministic, bit-for-bit reproducible portable archive:
       dist\TokLens-v2.2-win-x64-portable.zip (with normalized POSIX '/' entry paths).
    4. Computes SHA-256 checksums for both TokLens.exe and the portable .zip archive,
       writing standard GNU coreutils-compatible dist\SHA256SUMS.txt.
    5. Compares the real ZIP SHA-256 against Scoop & WinGet manifests and prints
       copy-ready backfill hints without overwriting source files by default
       (pass -UpdateManifests to write hashes back automatically).

.EXAMPLE
    .\packaging\package_release.ps1 -SkipBuild
    .\packaging\package_release.ps1 -SkipBuild -UpdateManifests
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Version = "2.2.0",

    [Parameter(Mandatory = $false)]
    [string]$BundleTag = "v2.2",

    [Parameter(Mandatory = $false)]
    [string]$RepoRoot = "",

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "",

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild,

    [Parameter(Mandatory = $false)]
    [switch]$UpdateManifests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Resolve repository root and target dist directory
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $ResolvedRepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
} else {
    $ResolvedRepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $DistDir = Join-Path $ResolvedRepoRoot "dist"
} elseif ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $DistDir = $OutputDir
} else {
    $DistDir = Join-Path $ResolvedRepoRoot $OutputDir
}

$ZipFileName = "TokLens-$BundleTag-win-x64-portable.zip"
$ExeFileName = "TokLens.exe"
$ZipFullPath = Join-Path $DistDir $ZipFileName
$DistExePath = Join-Path $DistDir $ExeFileName
$SumsFullPath = Join-Path $DistDir "SHA256SUMS.txt"

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host " TokLens Release Packager (v$Version | Bundle: $ZipFileName)" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "Repository Root : $ResolvedRepoRoot"
Write-Host "Output Directory: $DistDir"

# Step 1: Build or SkipBuild
if ($SkipBuild) {
    Write-Host "`n[1/4] Skipping build (-SkipBuild specified). Using existing TokLens.exe." -ForegroundColor Yellow
} else {
    $BuildBat = Join-Path $ResolvedRepoRoot "build.bat"
    if (-not (Test-Path -LiteralPath $BuildBat -PathType Leaf)) {
        throw "Build script not found: $BuildBat (use -SkipBuild to package pre-built binaries)."
    }
    Write-Host "`n[1/4] Building TokLens.exe via build.bat --nopause ..." -ForegroundColor Cyan
    & cmd.exe /d /c "`"$BuildBat`" --nopause"
    if ($LASTEXITCODE -ne 0) {
        throw "build.bat failed with exit code $LASTEXITCODE."
    }
}

# Step 2: Verify required & optional release files
Write-Host "`n[2/4] Verifying release input files ..." -ForegroundColor Cyan

$RequiredAssets = @(
    @{ RelSource = "TokLens.exe";                  ZipEntry = "TokLens.exe" },
    @{ RelSource = "vps\toklens_agent.py";         ZipEntry = "vps/toklens_agent.py" },
    @{ RelSource = "vps\install_macos_launchd.sh"; ZipEntry = "vps/install_macos_launchd.sh" },
    @{ RelSource = "README.md";                    ZipEntry = "README.md" }
)

$EntriesToPack = New-Object System.Collections.Generic.List[object]
$MissingAssets = New-Object System.Collections.Generic.List[string]

foreach ($item in $RequiredAssets) {
    $srcPath = Join-Path $ResolvedRepoRoot $item.RelSource
    if (-not (Test-Path -LiteralPath $srcPath -PathType Leaf)) {
        $MissingAssets.Add("$($item.RelSource) (missing: $srcPath)")
        continue
    }
    $fileInfo = Get-Item -LiteralPath $srcPath
    if ($fileInfo.Length -le 0) {
        $MissingAssets.Add("$($item.RelSource) (empty 0-byte file: $srcPath)")
        continue
    }
    Write-Host ("  [OK] Required : {0,-30} ({1:N0} bytes)" -f $item.RelSource, $fileInfo.Length) -ForegroundColor Green
    $EntriesToPack.Add([PSCustomObject]@{
        SourcePath = $fileInfo.FullName
        ZipEntry   = [string]$item.ZipEntry
    })
}

$LicensePath = Join-Path $ResolvedRepoRoot "LICENSE"
if (Test-Path -LiteralPath $LicensePath -PathType Leaf) {
    $licInfo = Get-Item -LiteralPath $LicensePath
    if ($licInfo.Length -gt 0) {
        Write-Host ("  [OK] Optional : {0,-30} ({1:N0} bytes)" -f "LICENSE", $licInfo.Length) -ForegroundColor Green
        $EntriesToPack.Add([PSCustomObject]@{
            SourcePath = $licInfo.FullName
            ZipEntry   = "LICENSE"
        })
    } else {
        Write-Host "  [--] Optional : LICENSE is 0 bytes, skipping." -ForegroundColor DarkGray
    }
} else {
    Write-Host "  [--] Optional : LICENSE not present, skipping." -ForegroundColor DarkGray
}

if ($MissingAssets.Count -gt 0) {
    $joined = ($MissingAssets | ForEach-Object { "  - $_" }) -join [Environment]::NewLine
    throw ("Pre-flight asset check failed. Missing or empty required file(s):`n" + $joined)
}

# Step 3: Create deterministic portable ZIP in dist\
Write-Host "`n[3/4] Creating deterministic portable bundle ..." -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $DistDir -PathType Container)) {
    New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
}

$SourceExePath = Join-Path $ResolvedRepoRoot "TokLens.exe"
Copy-Item -LiteralPath $SourceExePath -Destination $DistExePath -Force

Add-Type -AssemblyName System.IO.Compression

if (Test-Path -LiteralPath $ZipFullPath -PathType Leaf) {
    Remove-Item -LiteralPath $ZipFullPath -Force
}

# Fixed UTC timestamp guarantees bit-for-bit reproducible archives across runs
$DeterministicTimestamp = New-Object System.DateTimeOffset(2026, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
$OrderedEntries = @($EntriesToPack | Sort-Object -Property ZipEntry)

$zipFileStream = [System.IO.File]::Open(
    $ZipFullPath,
    [System.IO.FileMode]::CreateNew,
    [System.IO.FileAccess]::ReadWrite,
    [System.IO.FileShare]::None
)
try {
    $zipArchive = New-Object System.IO.Compression.ZipArchive(
        $zipFileStream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $true
    )
    try {
        foreach ($entrySpec in $OrderedEntries) {
            $zipEntry = $zipArchive.CreateEntry($entrySpec.ZipEntry, [System.IO.Compression.CompressionLevel]::Optimal)
            $zipEntry.LastWriteTime = $DeterministicTimestamp

            $srcStream = [System.IO.File]::OpenRead($entrySpec.SourcePath)
            try {
                $dstStream = $zipEntry.Open()
                try {
                    $srcStream.CopyTo($dstStream)
                } finally {
                    $dstStream.Dispose()
                }
            } finally {
                $srcStream.Dispose()
            }
        }
    } finally {
        $zipArchive.Dispose()
    }
} finally {
    $zipFileStream.Dispose()
}

# Verify archive readability and non-empty payload
$verifyStream = [System.IO.File]::OpenRead($ZipFullPath)
try {
    $verifyZip = New-Object System.IO.Compression.ZipArchive($verifyStream, [System.IO.Compression.ZipArchiveMode]::Read, $false)
    try {
        if ($verifyZip.Entries.Count -ne $OrderedEntries.Count) {
            throw "Archive entry count mismatch: expected $($OrderedEntries.Count), got $($verifyZip.Entries.Count)."
        }
        foreach ($ve in $verifyZip.Entries) {
            if ($ve.Length -le 0) {
                throw "Archive entry '$($ve.FullName)' extracted size is 0 bytes."
            }
        }
    } finally {
        $verifyZip.Dispose()
    }
} finally {
    $verifyStream.Dispose()
}

$zipSize = (Get-Item -LiteralPath $ZipFullPath).Length
Write-Host ("  [OK] Packaged : {0} ({1:N0} bytes, {2} files)" -f $ZipFullPath, $zipSize, $OrderedEntries.Count) -ForegroundColor Green

# Step 4: Compute SHA-256 & write dist\SHA256SUMS.txt + Manifest Backfill Verification
Write-Host "`n[4/4] Computing SHA-256 checksums & verifying manifests ..." -ForegroundColor Cyan

$ZipHashLower = (Get-FileHash -LiteralPath $ZipFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
$ZipHashUpper = $ZipHashLower.ToUpperInvariant()
$ExeHashLower = (Get-FileHash -LiteralPath $DistExePath -Algorithm SHA256).Hash.ToLowerInvariant()

$SumsLines = @(
    "$ZipHashLower  $ZipFileName",
    "$ExeHashLower  $ExeFileName"
)
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($SumsFullPath, (($SumsLines -join "`n") + "`n"), $Utf8NoBom)

Write-Host "  [OK] Generated: $SumsFullPath" -ForegroundColor Green
foreach ($line in $SumsLines) {
    Write-Host "       $line"
}

$ScoopManifestPath  = Join-Path $ResolvedRepoRoot "packaging\scoop\toklens.json"
$WingetManifestPath = Join-Path $ResolvedRepoRoot "packaging\winget\YePiXpert.TokLens.yaml"

Write-Host "`n----------------------------------------------------------------" -ForegroundColor Cyan
Write-Host " Package Manifest SHA-256 Verification & Backfill Hints" -ForegroundColor Cyan
Write-Host "----------------------------------------------------------------" -ForegroundColor Cyan

$NeedsBackfill = $false

if (Test-Path -LiteralPath $ScoopManifestPath -PathType Leaf) {
    $scoopRaw = [System.IO.File]::ReadAllText($ScoopManifestPath, [System.Text.Encoding]::UTF8)
    if ($UpdateManifests) {
        $scoopUpdated = [regex]::Replace($scoopRaw, '("hash"\s*:\s*")[0-9a-fA-F]{64}(")', "`${1}$ZipHashLower`${2}")
        [System.IO.File]::WriteAllText($ScoopManifestPath, $scoopUpdated, $Utf8NoBom)
        Write-Host "  [UPDATED] Scoop  : $ScoopManifestPath -> $ZipHashLower" -ForegroundColor Green
    } else {
        $scoopObj = $scoopRaw | ConvertFrom-Json
        $currentScoopHash = [string]$scoopObj.architecture.'64bit'.hash
        if ($currentScoopHash.ToLowerInvariant() -eq $ZipHashLower) {
            Write-Host "  [MATCH]   Scoop  : hash matches ($ZipHashLower)" -ForegroundColor Green
        } else {
            $NeedsBackfill = $true
            Write-Host "  [PENDING] Scoop  : $ScoopManifestPath" -ForegroundColor Yellow
            Write-Host "            Current: $currentScoopHash"
            Write-Host "            Actual : `"hash`": `"$ZipHashLower`"" -ForegroundColor White
        }
    }
} else {
    Write-Host "  [WARN] Scoop manifest not found at: $ScoopManifestPath" -ForegroundColor Yellow
}

if (Test-Path -LiteralPath $WingetManifestPath -PathType Leaf) {
    $wingetRaw = [System.IO.File]::ReadAllText($WingetManifestPath, [System.Text.Encoding]::UTF8)
    if ($UpdateManifests) {
        $wingetUpdated = [regex]::Replace($wingetRaw, '(InstallerSha256:\s*)[0-9a-fA-F]{64}', "`${1}$ZipHashUpper")
        [System.IO.File]::WriteAllText($WingetManifestPath, $wingetUpdated, $Utf8NoBom)
        Write-Host "  [UPDATED] WinGet : $WingetManifestPath -> $ZipHashUpper" -ForegroundColor Green
    } else {
        $m = [regex]::Match($wingetRaw, 'InstallerSha256:\s*([0-9a-fA-F]{64})')
        $currentWingetHash = if ($m.Success) { $m.Groups[1].Value } else { "<not found>" }
        if ($currentWingetHash.ToUpperInvariant() -eq $ZipHashUpper) {
            Write-Host "  [MATCH]   WinGet : InstallerSha256 matches ($ZipHashUpper)" -ForegroundColor Green
        } else {
            $NeedsBackfill = $true
            Write-Host "  [PENDING] WinGet : $WingetManifestPath" -ForegroundColor Yellow
            Write-Host "            Current: $currentWingetHash"
            Write-Host "            Actual : InstallerSha256: $ZipHashUpper" -ForegroundColor White
        }
    }
} else {
    Write-Host "  [WARN] WinGet manifest not found at: $WingetManifestPath" -ForegroundColor Yellow
}

if (-not $UpdateManifests -and $NeedsBackfill) {
    Write-Host "`n  [HINT] Source manifests were NOT overwritten (safe default)." -ForegroundColor Cyan
    Write-Host "         To auto-backfill SHA-256 into Scoop & WinGet manifests after final build, run:" -ForegroundColor Cyan
    Write-Host "         powershell -ExecutionPolicy Bypass -File .\packaging\package_release.ps1 -SkipBuild -UpdateManifests" -ForegroundColor White
}

Write-Host "`n[DONE] Release packaging completed successfully." -ForegroundColor Green
