<#
.SYNOPSIS
    One-click update + build + launch for the Rx Verify overlay.

.DESCRIPTION
    Designed to be started from the "Rx Verify" Desktop shortcut created
    by install-shortcut.ps1, or run directly from wherever this repo is
    checked out (see README.md). Every run:

      1. Self-locates the repo from the folder this script lives in
         ($PSScriptRoot) - no hardcoded path. Works identically whether
         the repo is at \claude\rx-verify, \rx-verify, or anywhere else,
         since if this script is running, the repo already exists around
         it.
      2. git fetch origin + git checkout -f -B main origin/main - forces
         the local `main` branch to exactly match GitHub's `main`,
         regardless of local drift (detached HEAD, wrong branch, a
         missing local `main`, a dirty tree, or diverged local commits).
         GitHub is the source of truth on these deploy-and-test
         machines, so this intentionally discards local modifications.
         If the fetch or checkout fails, the script stops with a
         plain-English message naming which step failed.
      3. npm install - ONLY if package-lock.json changed since the last
         successful install (hash cached locally), or node_modules is
         missing (first run). This is the one step that's safe to skip
         when unchanged.
      4. npm run build (the TypeScript matching engine, emits
         dist\cli.js) - ALWAYS runs, every invocation. No staleness
         guesswork.
      5. dotnet build (the WPF overlay) - ALWAYS runs, every invocation.
         Both builds are incremental under the hood and fast even when
         nothing changed.
      6. Launches the freshly built overlay .exe.

    Any failed step (git fetch/checkout, npm install, npm run build, dotnet build,
    or not finding the built .exe) prints exactly which step failed and
    the exact path/command involved, then holds the window open with
    "Press Enter to close" so the error is readable even when this was
    launched via double-click. On success, it just launches and exits.

    PowerShell 5.1 compatible on purpose (Windows' default) - no PS7-only
    syntax (ternary, ??, &&/||, Join-Path -AdditionalChildPath, etc.).

.NOTES
    SYNTHETIC DATA ONLY applies to this repo as a whole (see README.md)
    - this script itself never touches patient/prescriber data, only
    source code and build artifacts.
#>

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------
# Config - the repo root is wherever THIS SCRIPT lives, never a
# hardcoded path. That's what makes it work identically no matter which
# folder Will (or any machine) has the repo cloned into.
# ---------------------------------------------------------------------
$RepoPath = $PSScriptRoot

function Write-Step {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Cyan
}

function Write-Detail {
    param([string]$Message)
    Write-Host "  $Message" -ForegroundColor DarkGray
}

function Write-ErrorBlock {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Red
}

function Stop-WithMessage {
    param([string]$Message)
    Write-ErrorBlock $Message
    Write-ErrorBlock 'Copy the text above (including any error output) and send it to Will/dev. Nothing has been changed or discarded.'
    Read-Host 'Press Enter to close this window'
    exit 1
}

function Get-FileHashSafe {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    return (Get-FileHash -Path $Path -Algorithm SHA256).Hash
}

function Get-CachedValue {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    $value = Get-Content -Path $Path -Raw -ErrorAction SilentlyContinue
    if ($null -eq $value) { return $null }
    return $value.Trim()
}

function Set-CachedValue {
    param([string]$Path, [string]$Value)
    Set-Content -Path $Path -Value $Value -NoNewline
}

# ---------------------------------------------------------------------
# Sanity check: make sure this really looks like the rx-verify repo
# before doing anything (protects against the script being copied
# somewhere odd on its own).
# ---------------------------------------------------------------------
$gitDir = Join-Path $RepoPath '.git'
if (-not (Test-Path $gitDir)) {
    Stop-WithMessage "This script expects to live inside the rx-verify git repo, but no .git folder was found at $RepoPath. Re-clone the repo (see README.md) and run this script from inside it."
}

Set-Location -Path $RepoPath

$CacheDir = Join-Path $RepoPath '.launcher-cache'
if (-not (Test-Path $CacheDir)) {
    New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null
}
$LockfileHashPath = Join-Path $CacheDir 'lockfile.hash'

# ---------------------------------------------------------------------
# Step 1: sync to origin/main. Rather than `git pull --ff-only` (which
# can silently no-op and leave the working tree on stale code if the
# local checkout has drifted off `main` - detached HEAD, a different
# local branch, or diverged local commits), force the local `main`
# branch to exactly match GitHub's `main` every run. GitHub is the
# source of truth for these deploy-and-test machines; the app's own
# settings live in %AppData% and build output is gitignored, so
# discarding local modifications here is safe and intended.
# ---------------------------------------------------------------------
Write-Host "Syncing to latest from GitHub (origin/main)..." -ForegroundColor Cyan
$fetchOutput = git fetch origin 2>&1
$fetchExitCode = $LASTEXITCODE
$fetchOutput | ForEach-Object { Write-Detail $_ }
if ($fetchExitCode -ne 0) {
    Stop-WithMessage "git fetch origin failed in $RepoPath. Check the network connection and try again."
}

$checkoutOutput = git checkout -f -B main origin/main 2>&1
$checkoutExitCode = $LASTEXITCODE
$checkoutOutput | ForEach-Object { Write-Detail $_ }
if ($checkoutExitCode -ne 0) {
    Stop-WithMessage "git checkout -f -B main origin/main failed in $RepoPath. The local checkout could not be synced to match GitHub's main branch."
}

# ---------------------------------------------------------------------
# Step 2: npm install - only if package-lock.json changed, or
# node_modules isn't there at all yet (first run). This is the one slow
# step that's safe to skip when unchanged; everything after it always
# runs fresh.
# ---------------------------------------------------------------------
$lockfilePath = Join-Path $RepoPath 'package-lock.json'
$currentLockHash = Get-FileHashSafe -Path $lockfilePath
$cachedLockHash = Get-CachedValue -Path $LockfileHashPath
$nodeModulesPath = Join-Path $RepoPath 'node_modules'

$needInstall = $true
if (($cachedLockHash -ne $null) -and ($cachedLockHash -eq $currentLockHash) -and (Test-Path $nodeModulesPath)) {
    $needInstall = $false
}

if ($needInstall) {
    Write-Step 'Installing dependencies...'
    Write-Detail 'package-lock.json changed (or first run) - running npm install...'
    npm install
    if ($LASTEXITCODE -ne 0) {
        Stop-WithMessage "npm install failed in $RepoPath (see the error above)."
    }
    Set-CachedValue -Path $LockfileHashPath -Value $currentLockHash
} else {
    Write-Detail 'Dependencies unchanged - skipping npm install.'
}

# ---------------------------------------------------------------------
# Step 3: npm run build - ALWAYS runs. It's incremental/fast; running it
# unconditionally means there's never a stale dist\cli.js to debug.
# ---------------------------------------------------------------------
Write-Step 'Building engine (npm run build)...'
npm run build
if ($LASTEXITCODE -ne 0) {
    Stop-WithMessage "npm run build failed in $RepoPath (see the error above)."
}

$distEntryPath = Join-Path $RepoPath 'dist\cli.js'
if (-not (Test-Path $distEntryPath)) {
    Stop-WithMessage "npm run build reported success but $distEntryPath still doesn't exist. Something is wrong with the engine build output."
}

# ---------------------------------------------------------------------
# Step 4: dotnet build (overlay) - ALWAYS runs, same reasoning as above.
# Will's own test showed dotnet build takes well under a second once
# warm, so there's no real cost to always rebuilding.
# ---------------------------------------------------------------------
$overlayProjectDir = Join-Path $RepoPath 'overlay\RxVerifyOverlay'
$overlayBinDebugDir = Join-Path $overlayProjectDir 'bin\Debug'

function Find-OverlayExe {
    if (-not (Test-Path $overlayBinDebugDir)) { return $null }
    $found = Get-ChildItem -Path $overlayBinDebugDir -Filter 'RxVerifyOverlay.exe' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $found) { return $null }
    return $found.FullName
}

if (-not (Test-Path $overlayProjectDir)) {
    Stop-WithMessage "Overlay project folder not found at $overlayProjectDir. The repo checkout looks incomplete or corrupted."
}

Write-Step 'Building overlay (dotnet build)...'
Push-Location $overlayProjectDir
try {
    dotnet build
    $overlayBuildExitCode = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($overlayBuildExitCode -ne 0) {
    Stop-WithMessage "dotnet build failed in $overlayProjectDir (see the error above)."
}

$overlayExePath = Find-OverlayExe
if (($overlayExePath -eq $null) -or (-not (Test-Path $overlayExePath))) {
    Stop-WithMessage "dotnet build succeeded but RxVerifyOverlay.exe was not found anywhere under $overlayBinDebugDir (searched recursively for bin\Debug\net8.0-windows*\RxVerifyOverlay.exe). Something is wrong with the overlay build output path."
}

# ---------------------------------------------------------------------
# Step 5: launch.
# ---------------------------------------------------------------------
Write-Step "Launching Rx Verify ($overlayExePath)..."
try {
    Start-Process -FilePath $overlayExePath -WorkingDirectory (Split-Path -Path $overlayExePath -Parent)
} catch {
    Stop-WithMessage "Failed to launch $overlayExePath. Error: $($_.Exception.Message)"
}
