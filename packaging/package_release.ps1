#Requires -Version 5.1
<# Builds local release assets and manifests. Does not publish or submit packages. #>
[CmdletBinding()]
param([string]$RepoRoot='', [string]$OutputDir='', [switch]$SkipBuild, [switch]$UpdateManifests, [switch]$VerifyOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if (-not $RepoRoot) { $RepoRoot=Split-Path -Parent $PSScriptRoot }
$root=(Resolve-Path -LiteralPath $RepoRoot).Path
$version=[IO.File]::ReadAllText((Join-Path $root 'version.txt')).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.txt.' }
if (-not $OutputDir) { $OutputDir=Join-Path $root 'dist' }
if (-not [IO.Path]::IsPathRooted($OutputDir)) { $OutputDir=Join-Path $root $OutputDir }
$dist=[IO.Path]::GetFullPath($OutputDir)
$zipName="TokLens-v$version-win-x64-portable.zip"
$zipPath=Join-Path $dist $zipName
$releaseBase="https://github.com/YePiXpert/TokLens/releases/download/v$version"
$utf8=New-Object Text.UTF8Encoding($false)
$manifestDir=Join-Path $dist 'manifests'
$scoopPath=Join-Path $manifestDir 'toklens.json'
$wingetPath=Join-Path $manifestDir 'YePiXpert.TokLens.yaml'
$assetNames=@($zipName,'TokLens.exe','toklens_agent.py','install_macos_launchd.sh','service_tier_prices.json')
function Assert-Release {
    $lines=Get-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt')
    foreach ($name in $assetNames) {
        $path=Join-Path $dist $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Missing or empty asset: $name" }
        $hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if (@($lines | Where-Object { $_ -ceq "$hash  $name" }).Count -ne 1) { throw "Checksum mismatch or duplicate: $name" }
    }
    $scoop=Get-Content -LiteralPath $scoopPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $zipHash=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    if ($scoop.version -ne $version -or $scoop.architecture.'64bit'.url -cne "$releaseBase/$zipName" -or $scoop.architecture.'64bit'.hash -ine $zipHash) { throw 'Scoop manifest version, URL or SHA-256 mismatch.' }
    $winget=Get-Content -LiteralPath $wingetPath -Raw -Encoding UTF8
    foreach ($text in @("PackageVersion: $version","InstallerUrl: $releaseBase/$zipName","InstallerSha256: $zipHash")) {
        if (-not $winget.Contains($text)) { throw "WinGet manifest mismatch: $text" }
    }
    $binaryVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $dist 'TokLens.exe')).FileVersion
    if ($binaryVersion -ne "$version.0") { throw "Executable version mismatch: $binaryVersion" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if ($zip.Entries.Count -ne 1) { throw 'The Windows portable archive must contain only TokLens.exe.' }
        foreach ($entry in @('TokLens.exe')) {
            $found=$zip.GetEntry($entry)
            if ($null -eq $found -or $found.Length -eq 0) { throw "Missing or empty archive entry: $entry" }
        }
    } finally { $zip.Dispose() }
    Write-Host "Verified v${version}: assets, archive, checksums, executable and package manifests."
}
if ($VerifyOnly) { Assert-Release; exit 0 }
& (Join-Path $root 'packaging\sync_version.ps1') -Check
if (-not $SkipBuild) {
    Push-Location -LiteralPath $root
    try { & cmd.exe /d /c 'build.bat --nopause'; if ($LASTEXITCODE -ne 0) { throw 'Build failed.' } } finally { Pop-Location }
}
New-Item -ItemType Directory -Path $dist,$manifestDir -Force | Out-Null
$sources=[ordered]@{'TokLens.exe'='TokLens.exe'}
foreach ($relative in $sources.Values) {
    $path=Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Missing or empty input: $relative" }
}
foreach ($relative in @('TokLens.exe','vps/toklens_agent.py','vps/install_macos_launchd.sh','pricing/service_tier_prices.json')) {
    Copy-Item -LiteralPath (Join-Path $root $relative) -Destination (Join-Path $dist ([IO.Path]::GetFileName($relative))) -Force
}
Add-Type -AssemblyName System.IO.Compression
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
$stream=[IO.File]::Open($zipPath,[IO.FileMode]::CreateNew)
try {
    $zip=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        foreach ($entryName in ($sources.Keys | Sort-Object)) {
            $entry=$zip.CreateEntry($entryName,[IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime=New-Object DateTimeOffset(2026,1,1,0,0,0,[TimeSpan]::Zero)
            $inputStream=[IO.File]::OpenRead((Join-Path $root $sources[$entryName]))
            $outputStream=$entry.Open()
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
$zipHash=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$scoop=Get-Content -LiteralPath (Join-Path $root 'packaging/scoop/toklens.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$scoop.version=$version
$scoop.architecture.'64bit'.url="$releaseBase/$zipName"
$scoop.architecture.'64bit'.hash=$zipHash.ToLowerInvariant()
$scoop.autoupdate.architecture.'64bit'.url='https://github.com/YePiXpert/TokLens/releases/download/v$version/TokLens-v$version-win-x64-portable.zip'
[IO.File]::WriteAllText($scoopPath,(ConvertTo-Json -InputObject $scoop -Depth 10)+"`n",$utf8)
$winget=Get-Content -LiteralPath (Join-Path $root 'packaging/winget/YePiXpert.TokLens.yaml') -Raw -Encoding UTF8
$winget=[regex]::Replace($winget,'(?m)^PackageVersion:.*$',"PackageVersion: $version")
$winget=[regex]::Replace($winget,'(?m)^ReleaseNotesUrl:.*$',"ReleaseNotesUrl: https://github.com/YePiXpert/TokLens/releases/tag/v$version")
$winget=[regex]::Replace($winget,'(?m)^(\s*)InstallerUrl:.*$',"`${1}InstallerUrl: $releaseBase/$zipName")
$winget=[regex]::Replace($winget,'(?m)^(\s*)InstallerSha256:.*$',"`${1}InstallerSha256: $zipHash")
[IO.File]::WriteAllText($wingetPath,$winget,$utf8)
$sums=foreach ($name in $assetNames) { (Get-FileHash -LiteralPath (Join-Path $dist $name) -Algorithm SHA256).Hash.ToLowerInvariant()+"  $name" }
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'),($sums -join "`n")+"`n",$utf8)
Assert-Release
if ($UpdateManifests) {
    Copy-Item -LiteralPath $scoopPath -Destination (Join-Path $root 'packaging/scoop/toklens.json') -Force
    Copy-Item -LiteralPath $wingetPath -Destination (Join-Path $root 'packaging/winget/YePiXpert.TokLens.yaml') -Force
    Write-Host 'Updated local manifests. No external package-manager submission was made.'
}
