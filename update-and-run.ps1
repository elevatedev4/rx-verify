<#
.SYNOPSIS
    One-click update + build + launch for the Rx Verify overlay.

.DESCRIPTION
    Designed to be started from the "Rx Verify" Desktop shortcut created
    by install-shortcut.ps1, or run directly from wherever this repo is
    checked out (see README.md). This is now the ONLY script the
    shortcut needs - it self-heals its own prerequisites, so a shortcut
    created once keeps working even after a Windows reset or on a PC
    that skipped bootstrap-fresh.ps1 entirely. Every run:

      1. Checks that Git, Node.js (major version 20+), and the .NET 8
         SDK are all present - existence/version checks only, no winget
         round-trip, so this costs nothing on the common case where
         everything's already installed. Anything missing is installed
         via winget (the same package IDs/flags bootstrap-fresh.ps1
         uses), PATH is refreshed in this session from the registry, and
         each is re-checked. If something still won't resolve after
         that (rare - some installers need a genuinely fresh console),
         the script stops and says to close/reopen the window and
         re-run - installs already done are kept.
      2. Self-locates the repo from the folder this script lives in
         ($PSScriptRoot) - no hardcoded path. Works identically whether
         the repo is at \claude\rx-verify, \rx-verify, or anywhere else,
         since if this script is running, the repo already exists around
         it.
      3. git fetch origin + git checkout -f -B main origin/main - forces
         the local `main` branch to exactly match GitHub's `main`,
         regardless of local drift (detached HEAD, wrong branch, a
         missing local `main`, a dirty tree, or diverged local commits).
         GitHub is the source of truth on these deploy-and-test
         machines, so this intentionally discards local modifications.
         If the fetch or checkout fails, the script stops with a
         plain-English message naming which step failed.
      4. npm install - ONLY if package-lock.json changed since the last
         successful install (hash cached locally), or node_modules is
         missing (first run). This is the one step that's safe to skip
         when unchanged.
      5. npm run build (the TypeScript matching engine, emits
         dist\cli.js) - ALWAYS runs, every invocation. No staleness
         guesswork.
      6. Before building the overlay: stops any running
         RxVerifyOverlay.exe whose path is under THIS repo's own
         overlay bin directory (never a same-named process from a
         different checkout) - gracefully (CloseMainWindow, then a
         short wait, then Stop-Process -Force if it's still up) - so
         `dotnet build` doesn't fail trying to overwrite a running exe
         (MSB3026). If it can't be stopped, the script stops here and
         says so rather than letting the build fail.
         A same-named process running from elsewhere (an older install
         location, a different drive/folder) is CLOSED too, same
         graceful pattern, per the owner's answer to W-T67 ("make it
         close the current program when the new install is done") - so
         only the freshly built copy ends up running. Unlike this
         checkout's own exe above, a failure to close an old-location
         copy is only a warning, not a hard stop - it was never blocking
         the build to begin with, so the build/launch still proceed
         either way.
      7. dotnet build (the WPF overlay) - ALWAYS runs, every invocation.
         Both builds are incremental under the hood and fast even when
         nothing changed.
      8. Launches the freshly built overlay .exe.

    Any failed step (a missing prerequisite that still won't resolve,
    git fetch/checkout, npm install, npm run build, a running overlay
    that won't stop, dotnet build, or not finding the built .exe) prints
    exactly which step failed and the exact path/command involved, then
    holds the window open with "Press Enter to close" so the error is
    readable even when this was launched via double-click. On success,
    it just launches and exits.

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

# Prerequisite checks shared by the "everything present" fast path and
# the post-install re-check below. Mirrors bootstrap-fresh.ps1's checks
# exactly so both scripts agree on what "present" means. Each body is
# wrapped in try/catch: a binary that resolves via Get-Command but is
# corrupted (bad install, AV quarantine, a missing DLL) throws a
# terminating exception when actually invoked, not a non-zero exit code
# - uncaught, that would crash the whole script before Stop-WithMessage
# ever runs, which on the double-click path means the window just closes
# with no message at all. Routing that through "return $false" instead
# sends it through the same reinstall/re-check/Stop-WithMessage
# machinery as a plain missing tool.
function Test-NodeVersionOk {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { return $false }
    try {
        $nodeVersionRaw = (node --version)
        $nodeMajor = $null
        if ($nodeVersionRaw -match 'v(\d+)\.') {
            $nodeMajor = [int]$Matches[1]
        }
        return (($nodeMajor -ne $null) -and ($nodeMajor -ge 20))
    } catch {
        return $false
    }
}

function Test-Dotnet8Ok {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    try {
        $installedSdks = dotnet --list-sdks
        foreach ($line in $installedSdks) {
            if ($line -like '8.*') { return $true }
        }
        return $false
    } catch {
        return $false
    }
}

# Runs a native command, capturing merged stdout+stderr, WITHOUT letting
# git/npm status text on stderr trip $ErrorActionPreference='Stop' into a
# fake NativeCommandError. The command's exit code is the real signal;
# callers check $LASTEXITCODE afterward. Returns the captured output lines;
# sets the script-scope $script:NativeExitCode for the caller to read.
function Invoke-NativeCapture {
    param([Parameter(Mandatory)][scriptblock]$Command)
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Command 2>&1
        $script:NativeExitCode = $LASTEXITCODE
        return $output
    } finally {
        $ErrorActionPreference = $prevEap
    }
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
# Step 0: make sure Git, Node.js (20+), and the .NET 8 SDK are present
# before doing anything else. The Desktop shortcut runs THIS script
# directly, so it can no longer assume bootstrap-fresh.ps1 ever ran on
# this machine - eg. a Windows reinstall, a new PC someone pointed
# straight at install-shortcut.ps1, or a prerequisite that got
# uninstalled since. When everything's already present (the normal
# case), this is three fast Get-Command/version checks - no winget
# round-trip, so it doesn't slow down the everyday double-click.
# ---------------------------------------------------------------------
$gitOk = [bool](Get-Command git -ErrorAction SilentlyContinue)
$nodeOk = Test-NodeVersionOk
$dotnetOk = Test-Dotnet8Ok

if ($gitOk -and $nodeOk -and $dotnetOk) {
    Write-Detail 'Git, Node.js (20+), and .NET 8 SDK all present - skipping install checks.'
} else {
    Write-Step 'Missing prerequisite(s) detected - installing via winget...'
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Stop-WithMessage 'winget (Windows Package Manager) was not found, so the missing component(s) above cannot be auto-installed. Open the Microsoft Store, install "App Installer", reopen PowerShell, then double-click the shortcut (or re-run this script) again.'
    }
    Write-Host 'Windows may show a few Yes/No install prompts below - click Yes. It may also show a UAC "Do you want to allow this app to make changes to your device?" dialog - click Yes on that too.' -ForegroundColor Yellow

    if (-not $gitOk) {
        Write-Detail 'Git not found - installing via winget (Git.Git)...'
        winget install -e --id Git.Git --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -ne 0) {
            Stop-WithMessage "winget install -e --id Git.Git failed (exit code $LASTEXITCODE). See the winget output above for details."
        }
    }
    if (-not $nodeOk) {
        Write-Detail 'Node.js missing or older than 20 - installing via winget (OpenJS.NodeJS.LTS)...'
        winget install -e --id OpenJS.NodeJS.LTS --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -ne 0) {
            Stop-WithMessage "winget install -e --id OpenJS.NodeJS.LTS failed (exit code $LASTEXITCODE). See the winget output above for details."
        }
    }
    if (-not $dotnetOk) {
        Write-Detail '.NET 8 SDK missing - installing via winget (Microsoft.DotNet.SDK.8)...'
        winget install -e --id Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -ne 0) {
            Stop-WithMessage "winget install -e --id Microsoft.DotNet.SDK.8 failed (exit code $LASTEXITCODE). See the winget output above for details."
        }
    }

    Write-Step 'Refreshing PATH in this session...'
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($null -eq $machinePath) { $machinePath = '' }
    if ($null -eq $userPath) { $userPath = '' }
    $env:Path = $machinePath + ';' + $userPath

    $gitOk = [bool](Get-Command git -ErrorAction SilentlyContinue)
    $nodeOk = Test-NodeVersionOk
    $dotnetOk = Test-Dotnet8Ok

    $stillMissing = @()
    if (-not $gitOk) { $stillMissing += 'git' }
    if (-not $nodeOk) { $stillMissing += 'node (20+)' }
    if (-not $dotnetOk) { $stillMissing += 'dotnet (.NET 8 SDK)' }

    if ($stillMissing.Count -gt 0) {
        $missingList = $stillMissing -join ', '
        Stop-WithMessage "Installed OK, but this window still can't see: $missingList. Close this window, then double-click the Desktop shortcut again (or reopen PowerShell and re-run this script) - the installs already done are kept, so it will pick up where it left off."
    }
    Write-Detail 'git, node, and dotnet all resolve now.'
}

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
$fetchOutput = Invoke-NativeCapture { git fetch origin }
$fetchExitCode = $script:NativeExitCode
$fetchOutput | ForEach-Object { Write-Detail "$_" }
if ($fetchExitCode -ne 0) {
    Stop-WithMessage "git fetch origin failed in $RepoPath. Check the network connection and try again."
}

$checkoutOutput = Invoke-NativeCapture { git checkout -f -B main origin/main }
$checkoutExitCode = $script:NativeExitCode
$checkoutOutput | ForEach-Object { Write-Detail "$_" }
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

# ---------------------------------------------------------------------
# Step 4a: stop a running overlay before rebuilding it. `dotnet build`
# fails with MSB3026 ("Could not copy apphost.exe ... because it is
# being used by another process") if the exe it's about to overwrite is
# currently running - without this, re-running the shortcut while Rx
# Verify is still open turns into a build-failure retry loop instead of
# a clean update. Only ever stops a RxVerifyOverlay.exe whose path is
# under THIS repo's own overlay bin directory ($overlayBinDebugDir,
# built from $RepoPath above) - never a same-named process from a
# different checkout (eg. an owner PC running an old copy from another
# folder/drive while this one updates). A same-named process from
# elsewhere is left running but flagged, since the freshly-built exe
# launched at the end of this script will then run alongside it as a
# second instance.
# ---------------------------------------------------------------------
# .HasExited (like .Path above) can throw - eg. Win32Exception on an
# access-denied/elevation-mismatch process. With $ErrorActionPreference
# = 'Stop' that would crash the whole script before Stop-WithMessage
# ever runs - on the double-click path, a window that just vanishes
# with no message. Treat "can't tell" as "still running": it falls
# through to the Stop-Process -Force attempt and then the final
# re-check below, both of which already report a real failure loudly.
function Test-ProcessStillRunning {
    param($Process)
    try {
        $Process.Refresh()
        return (-not $Process.HasExited)
    } catch {
        return $true
    }
}

# Shared graceful-stop loop (CloseMainWindow -> wait up to 5s ->
# Stop-Process -Force if still up) - used for BOTH this checkout's own
# running exe (which MUST stop, or the build below fails with MSB3026)
# and, per W-T67, any same-named copy running from an older/different
# install location (a nice-to-have close, not a build blocker - see each
# call site's own still-running re-check for how they differ on failure).
function Stop-OverlayProcessList {
    param([Parameter(Mandatory)]$Processes)
    foreach ($proc in $Processes) {
        Write-Detail "Stopping RxVerifyOverlay.exe (PID $($proc.Id))..."
        try {
            $proc.CloseMainWindow() | Out-Null
        } catch {
            # No message loop / already gone - fall through to the
            # wait-then-force-kill below regardless.
        }

        $waited = 0
        while ($waited -lt 5) {
            if (-not (Test-ProcessStillRunning $proc)) { break }
            Start-Sleep -Seconds 1
            $waited++
        }

        if (Test-ProcessStillRunning $proc) {
            try {
                Stop-Process -Id $proc.Id -Force -ErrorAction Stop
                Start-Sleep -Seconds 1
            } catch {
                # Failure is caught by each call site's own still-running
                # re-check below.
            }
        }
    }
}

$overlayProcessName = 'RxVerifyOverlay'
$runningOverlayProcesses = Get-Process -Name $overlayProcessName -ErrorAction SilentlyContinue

$processesToStop = @()
$otherLocationProcesses = @()

foreach ($proc in $runningOverlayProcesses) {
    # .Path (MainModule.FileName under the hood) can throw - eg. access
    # denied for an elevated process while this script runs
    # non-elevated, or a process that exited between Get-Process and
    # here. Treat "couldn't determine" the same as "different location":
    # never stop something we can't positively confirm is our own exe.
    $procPath = $null
    try {
        $procPath = $proc.Path
    } catch {
        $procPath = $null
    }

    if (($procPath -ne $null) -and $procPath.StartsWith($overlayBinDebugDir, [StringComparison]::OrdinalIgnoreCase)) {
        $processesToStop += $proc
    } else {
        $otherLocationProcesses += $proc
    }
}

if ($otherLocationProcesses.Count -gt 0) {
    # W-T67 (owner: "if there's a way to make it close the current
    # program when the new install is done or when the shortcut is
    # clicked, please do that") - was warn-and-leave-running; now
    # actually closed, same graceful pattern as this checkout's own exe
    # below. SOFTER failure mode than that one on purpose: an old-location
    # copy was never blocking this script's own build/launch, so a
    # failure to close it is a warning, not Stop-WithMessage - the script
    # still proceeds either way.
    $otherPids = ($otherLocationProcesses | ForEach-Object { $_.Id }) -join ', '
    Write-Step "Closing the Rx Verify running from the old location... (PID(s): $otherPids)"
    Stop-OverlayProcessList -Processes $otherLocationProcesses

    $stillRunningElsewhere = @($otherLocationProcesses | Where-Object { Test-ProcessStillRunning $_ })
    if ($stillRunningElsewhere.Count -gt 0) {
        $stillElsewherePids = ($stillRunningElsewhere | ForEach-Object { $_.Id }) -join ', '
        Write-Host "Note: couldn't close the old-location Rx Verify (PID(s): $stillElsewherePids) - leaving it running. After this build finishes you may end up with two Rx Verify windows open; close the other one by hand if you only want one." -ForegroundColor Yellow
    } else {
        Write-Detail 'Old-location Rx Verify closed.'
    }
}

if ($processesToStop.Count -gt 0) {
    Write-Step 'Stopping the running Rx Verify so it can be updated...'
    Stop-OverlayProcessList -Processes $processesToStop

    $stillRunning = @(Get-Process -Name $overlayProcessName -ErrorAction SilentlyContinue | Where-Object {
        $stillPath = $null
        try { $stillPath = $_.Path } catch { $stillPath = $null }
        ($stillPath -ne $null) -and $stillPath.StartsWith($overlayBinDebugDir, [StringComparison]::OrdinalIgnoreCase)
    })

    if ($stillRunning.Count -gt 0) {
        $stillPids = ($stillRunning | ForEach-Object { $_.Id }) -join ', '
        Stop-WithMessage "Rx Verify (PID(s): $stillPids) is still running and could not be stopped automatically. Close it by hand - right-click its window/taskbar icon and close, or End Task in Task Manager - then re-run this script."
    }
    Write-Detail 'Rx Verify stopped.'
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
