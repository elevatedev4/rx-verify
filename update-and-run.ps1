<#
.SYNOPSIS
    One-click update + build + launch for the Rx Verify overlay.

.DESCRIPTION
    Designed to be started from the "Rx Verify" Desktop shortcut created
    by install-shortcut.ps1 (see README.md "Rapid update/deploy
    workflow"). Every run:

      1. Makes sure the repo exists at $env:USERPROFILE\rx-verify
         (clones it fresh if it's missing entirely - e.g. a wiped
         machine, a new workstation, an antivirus quarantine incident).
      2. git pull --ff-only - never merges, never rebases, never
         discards anything. If this fails (diverged history, a
         conflicting local edit, no network), the script stops with a
         plain-English message and does NOT touch the working tree.
      3. npm install - ONLY if package-lock.json changed since the last
         successful install (hash cached locally).
      4. npm run build (the TypeScript matching engine, emits
         dist\cli.js) - ONLY if src\ or the package/tsconfig files
         changed since the last successful build (a tree-hash cached
         locally).
      5. dotnet build (the WPF overlay) - ONLY if overlay\ changed since
         the last successful build. Otherwise the already-built .exe is
         launched directly, skipping MSBuild's startup cost entirely.
      6. Launches the overlay .exe.

    Every step is idempotent: running this twice in a row with nothing
    changed does no network/npm/dotnet work at all and just launches the
    app - that's the common case once you're actively using the app
    day to day. When there IS an update, everything above still happens
    automatically; you just double-click the same shortcut either way.

    PowerShell 5.1 compatible on purpose (Windows' default) - no PS7-only
    syntax (ternary, ??, &&/||, Join-Path -AdditionalChildPath, etc.).

.NOTES
    SYNTHETIC DATA ONLY applies to this repo as a whole (see README.md)
    - this script itself never touches patient/prescriber data, only
    source code and build artifacts.
#>

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------
# Config - no hardcoded usernames anywhere below; $env:USERPROFILE is
# the one thing this script trusts to find "the current user's folder"
# on any Windows machine.
# ---------------------------------------------------------------------
$RepoUrl = 'https://github.com/elevatedev4/rx-verify.git'
$RepoPath = Join-Path $env:USERPROFILE 'rx-verify'

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
# Legacy-copy notice: the canonical checkout this workflow manages is
# $env:USERPROFILE\rx-verify (per Will's explicit direction - the old
# ...\claude\rx-verify checkout is his to delete whenever he's ready,
# this script never deletes anything on its own). If that old copy is
# still sitting around, say so, every run, so it doesn't linger
# forgotten. Purely informational - never touches it.
# ---------------------------------------------------------------------
function Write-LegacyCopyNoticeIfPresent {
    $legacyRepoPath = Join-Path $env:USERPROFILE 'claude\rx-verify'
    $legacyGitDir = Join-Path $legacyRepoPath '.git'
    if (Test-Path $legacyGitDir) {
        Write-Step 'Note: legacy copy at ~\claude\rx-verify is no longer used - safe to delete.'
    }
}

# ---------------------------------------------------------------------
# Stale engine-path migration: the overlay persists an ABSOLUTE engine
# CLI path (EngineCliPath) in %AppData%\RxVerifyOverlay\settings.json
# (see overlay/RxVerifyOverlay/Models/OverlaySettings.cs). On a machine
# that used to have the repo checked out under the old
# ...\claude\rx-verify location, that saved path still points there -
# so this launcher would happily update/build the NEW
# $env:USERPROFILE\rx-verify checkout while the overlay keeps silently
# running the STALE dist\cli.js from the old one. Rewrite just that one
# key, only when: the settings file exists, EngineCliPath contains the
# old '\claude\rx-verify\' segment, and the equivalent new path actually
# exists (never point the overlay at a path that isn't there). Every
# other key in the file is round-tripped untouched.
# ---------------------------------------------------------------------
function Update-StaleEngineSettingsPath {
    param([string]$RepoPath)

    if (-not $env:APPDATA) { return }
    $settingsPath = Join-Path $env:APPDATA 'RxVerifyOverlay\settings.json'
    if (-not (Test-Path $settingsPath)) { return }

    $legacyMarker = '\claude\rx-verify\'

    try {
        $json = Get-Content -Path $settingsPath -Raw -ErrorAction Stop
        $settingsObj = $json | ConvertFrom-Json -ErrorAction Stop
    } catch {
        Write-Detail 'Could not read overlay settings.json to check for a stale engine path - leaving it alone.'
        return
    }

    if ($null -eq $settingsObj) { return }
    if (-not ($settingsObj.PSObject.Properties.Name -contains 'EngineCliPath')) { return }

    $currentEnginePath = $settingsObj.EngineCliPath
    if ([string]::IsNullOrWhiteSpace($currentEnginePath)) { return }

    $markerIndex = $currentEnginePath.ToLowerInvariant().IndexOf($legacyMarker.ToLowerInvariant())
    if ($markerIndex -lt 0) { return } # absent or already pointing somewhere else - leave it alone.

    $remainder = $currentEnginePath.Substring($markerIndex + $legacyMarker.Length)
    $newEnginePath = Join-Path $RepoPath $remainder

    if ($newEnginePath -eq $currentEnginePath) { return }
    if (-not (Test-Path $newEnginePath)) {
        # The equivalent new-location file doesn't exist (yet) - don't
        # point the overlay at something that isn't there.
        return
    }

    $settingsObj.EngineCliPath = $newEnginePath
    try {
        ($settingsObj | ConvertTo-Json -Depth 5) | Set-Content -Path $settingsPath -Encoding UTF8
        Write-Step "Updated overlay settings: engine path was pointing at the old \claude\rx-verify checkout - repointed to $newEnginePath"
    } catch {
        Write-Detail 'Could not update overlay settings.json automatically - update the engine CLI path by hand in the overlay Engine settings panel.'
    }
}

# ---------------------------------------------------------------------
# Step 0: make sure the repo exists at the canonical path.
# ---------------------------------------------------------------------
$gitDir = Join-Path $RepoPath '.git'
if (-not (Test-Path $gitDir)) {
    if ((Test-Path $RepoPath) -and ((Get-ChildItem -Path $RepoPath -Force | Measure-Object).Count -gt 0)) {
        Stop-WithMessage "$RepoPath exists but doesn't look like the rx-verify git repo (no .git folder), and it isn't empty. Rename or remove that folder, or tell Will/dev what's in it, then try again."
    }

    Write-Step "Rx Verify not found at $RepoPath - cloning a fresh copy..."
    $parent = Split-Path -Path $RepoPath -Parent
    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    git clone $RepoUrl $RepoPath
    if ($LASTEXITCODE -ne 0) {
        Stop-WithMessage 'git clone failed (see the error above - usually no network, or the repo URL/permissions changed).'
    }
}

Set-Location -Path $RepoPath

Write-LegacyCopyNoticeIfPresent

$CacheDir = Join-Path $RepoPath '.launcher-cache'
if (-not (Test-Path $CacheDir)) {
    New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null
}
$LockfileHashPath = Join-Path $CacheDir 'lockfile.hash'
$EngineBuildKeyPath = Join-Path $CacheDir 'engine-build.key'
$OverlayBuildKeyPath = Join-Path $CacheDir 'overlay-build.key'

# ---------------------------------------------------------------------
# Step 1: pull. --ff-only guarantees this either fast-forwards cleanly
# or fails loudly and changes NOTHING - no merge, no rebase, no stash,
# nothing that could ever clobber a local edit.
# ---------------------------------------------------------------------
Write-Step 'Checking for updates...'
$beforeHead = (git rev-parse HEAD).Trim()
$pullOutput = git pull --ff-only 2>&1
$pullExitCode = $LASTEXITCODE
$pullOutput | ForEach-Object { Write-Detail $_ }
if ($pullExitCode -ne 0) {
    Stop-WithMessage 'git pull --ff-only failed. This usually means there are local changes on this machine that conflict with the update, or the history has diverged.'
}
$afterHead = (git rev-parse HEAD).Trim()
$repoChanged = $beforeHead -ne $afterHead

# ---------------------------------------------------------------------
# Step 2: npm install - only if package-lock.json changed, or
# node_modules isn't there at all yet (first run).
# ---------------------------------------------------------------------
$lockfilePath = Join-Path $RepoPath 'package-lock.json'
$currentLockHash = Get-FileHashSafe -Path $lockfilePath
$cachedLockHash = Get-CachedValue -Path $LockfileHashPath
$nodeModulesPath = Join-Path $RepoPath 'node_modules'

$needInstall = $true
if (($cachedLockHash -ne $null) -and ($cachedLockHash -eq $currentLockHash) -and (Test-Path $nodeModulesPath)) {
    $needInstall = $false
}

# ---------------------------------------------------------------------
# Step 3: npm run build - only if src\ or the package/tsconfig files
# changed since the last successful build. git rev-parse HEAD:src is
# the tree hash of src\ AS COMMITTED - this deliberately does not look
# at uncommitted local edits, since this machine is expected to only
# ever run what "git pull" brought in, never local source changes.
# ---------------------------------------------------------------------
$packageJsonPath = Join-Path $RepoPath 'package.json'
$tsconfigPath = Join-Path $RepoPath 'tsconfig.json'
$distEntryPath = Join-Path $RepoPath 'dist\cli.js'

$srcTreeHash = (git rev-parse 'HEAD:src').Trim()
$currentEngineBuildKey = $srcTreeHash + '|' + (Get-FileHashSafe -Path $packageJsonPath) + '|' + $currentLockHash + '|' + (Get-FileHashSafe -Path $tsconfigPath)
$cachedEngineBuildKey = Get-CachedValue -Path $EngineBuildKeyPath

$needEngineBuild = $true
if (($cachedEngineBuildKey -ne $null) -and ($cachedEngineBuildKey -eq $currentEngineBuildKey) -and (Test-Path $distEntryPath)) {
    $needEngineBuild = $false
}

# ---------------------------------------------------------------------
# Step 4: dotnet build (overlay) - only if overlay\ changed since the
# last successful build AND the built .exe is still where we left it.
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

$overlayTreeHash = (git rev-parse 'HEAD:overlay').Trim()
$currentOverlayBuildKey = $overlayTreeHash
$cachedOverlayBuildKey = Get-CachedValue -Path $OverlayBuildKeyPath
$overlayExePath = Find-OverlayExe

$needOverlayBuild = $true
if (($cachedOverlayBuildKey -ne $null) -and ($cachedOverlayBuildKey -eq $currentOverlayBuildKey) -and ($overlayExePath -ne $null)) {
    $needOverlayBuild = $false
}

# ---------------------------------------------------------------------
# One clear line up front, then the detail as each step actually runs.
# ---------------------------------------------------------------------
if (-not $repoChanged -and -not $needInstall -and -not $needEngineBuild -and -not $needOverlayBuild) {
    Write-Step 'Already up to date - launching...'
} else {
    Write-Step 'Updating... building... launching...'
}

if ($needInstall) {
    Write-Detail 'package-lock.json changed (or first run) - running npm install...'
    npm install
    if ($LASTEXITCODE -ne 0) {
        Stop-WithMessage 'npm install failed (see the error above).'
    }
    Set-CachedValue -Path $LockfileHashPath -Value $currentLockHash
} else {
    Write-Detail 'Dependencies unchanged - skipping npm install.'
}

if ($needEngineBuild) {
    Write-Detail 'Engine source changed - running npm run build...'
    npm run build
    if ($LASTEXITCODE -ne 0) {
        Stop-WithMessage 'npm run build failed (see the error above).'
    }
    Set-CachedValue -Path $EngineBuildKeyPath -Value $currentEngineBuildKey
} else {
    Write-Detail 'Engine build unchanged - skipping npm run build.'
}

if ($needOverlayBuild) {
    Write-Detail 'Overlay source changed (or no previous build found) - running dotnet build...'
    Push-Location $overlayProjectDir
    try {
        dotnet build
        $overlayBuildExitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($overlayBuildExitCode -ne 0) {
        Stop-WithMessage 'dotnet build failed (see the error above).'
    }
    Set-CachedValue -Path $OverlayBuildKeyPath -Value $currentOverlayBuildKey
    $overlayExePath = Find-OverlayExe
} else {
    Write-Detail 'Overlay build unchanged - launching the existing build (skipping dotnet build).'
}

if (($overlayExePath -eq $null) -or (-not (Test-Path $overlayExePath))) {
    Stop-WithMessage 'Could not find RxVerifyOverlay.exe after building. Something is wrong with the overlay build output path.'
}

# dist\cli.js is guaranteed to exist at this point (either the engine
# build above just succeeded, or $needEngineBuild was false specifically
# because it already existed) - safe to check the overlay's saved engine
# path against it now.
Update-StaleEngineSettingsPath -RepoPath $RepoPath

Write-Step 'Launching Rx Verify...'
Start-Process -FilePath $overlayExePath -WorkingDirectory (Split-Path -Path $overlayExePath -Parent)
