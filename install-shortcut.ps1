<#
.SYNOPSIS
    One-time setup: creates a "Rx Verify" Desktop shortcut that runs
    update-and-run.ps1 - after this, updating/building/launching the
    app is a single double-click.

.DESCRIPTION
    Run this once (right-click -> Run with PowerShell, or from a
    PowerShell prompt). It:

      1. Makes sure the repo exists at the canonical path,
         $env:USERPROFILE\claude\rx-verify (clones it, creating the
         \claude parent folder if needed, if this is a brand new machine
         and it isn't there yet - same clone this script and
         update-and-run.ps1 both use, so the "one true copy" this
         workflow manages always ends up in the same place).
      2. Creates (or overwrites - safe to re-run any time) a Desktop
         shortcut named "Rx Verify" that runs:
           powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\claude\rx-verify\update-and-run.ps1"
      3. Makes a best-effort attempt to pin that shortcut to the Windows
         taskbar. Microsoft removed the supported way to do this from a
         script (the shell "Pin to taskbar" verb was blocked starting
         Windows 10 1903, and Windows 11 is stricter still - there is no
         supported per-app pinning API; it's a genuine user action or
         MDM/provisioning-only). This never fails the script either way:
         if the verb exists on this Windows build, it's invoked and you
         get a one-line "pinned" confirmation; if not (the common case on
         current Windows 10/11), you get a one-line instruction to
         right-click the Desktop shortcut and pin it yourself, once.

    -ExecutionPolicy Bypass on the shortcut's own invocation only
    affects that one process - it does not change your machine's
    PowerShell execution policy setting.

    Pass -NoPrompt to skip the "Press Enter to close this window" pauses
    below (both on success and on failure). Only meant for when this
    script is invoked programmatically - bootstrap-fresh.ps1 does this
    to create/refresh the shortcut as part of a fresh-PC bootstrap that
    shouldn't stop and wait for a keypress partway through. Run it
    directly (double-click, or "Run with PowerShell") and the pauses
    stay on, same as always.

    PowerShell 5.1 compatible on purpose (Windows' default).
#>

param(
    [switch]$NoPrompt
)

$ErrorActionPreference = 'Stop'

$RepoUrl = 'https://github.com/elevatedev4/rx-verify.git'
$RepoPath = Join-Path $env:USERPROFILE 'claude\rx-verify'
$LauncherScriptPath = Join-Path $RepoPath 'update-and-run.ps1'

function Write-Step {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Cyan
}

function Stop-WithMessage {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Red
    Write-Host 'Copy the text above (including any error output) and send it to Will/dev. Nothing has been changed or discarded.' -ForegroundColor Red
    if (-not $NoPrompt) { Read-Host 'Press Enter to close this window' }
    exit 1
}

# ---------------------------------------------------------------------
# Step 1: make sure the repo (and update-and-run.ps1 inside it) exists
# at the canonical path before we point a shortcut at it.
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

if (-not (Test-Path $LauncherScriptPath)) {
    Stop-WithMessage "$LauncherScriptPath still doesn't exist after cloning - something is wrong with the repo checkout."
}

# ---------------------------------------------------------------------
# Step 2: create (or overwrite) the Desktop shortcut. WScript.Shell is
# the standard classic-COM way to make a .lnk from PowerShell and has
# worked unchanged since PS2 - no PS7-only cmdlet needed.
# ---------------------------------------------------------------------
Write-Step 'Creating Desktop shortcut "Rx Verify"...'

$desktopPath = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktopPath 'Rx Verify.lnk'
$powershellExePath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'

$wshShell = New-Object -ComObject WScript.Shell
$shortcut = $wshShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $powershellExePath
$shortcut.Arguments = '-ExecutionPolicy Bypass -File "' + $LauncherScriptPath + '"'
$shortcut.WorkingDirectory = $RepoPath
$shortcut.IconLocation = $powershellExePath + ',0'
$shortcut.Description = 'Update and launch Rx Verify'
$shortcut.Save()

# ---------------------------------------------------------------------
# Step 3: best-effort pin to taskbar. There is no supported, documented
# API for an app to pin its own shortcut to the Windows taskbar -
# Microsoft blocked the shell "Pin to taskbar" verb from programmatic
# invocation starting with Windows 10 1903, specifically to stop exactly
# this kind of script from doing it; Windows 11 is stricter still, with
# pinning treated as a genuine user action (or an MDM/provisioning-time
# operation, not something a normal install script can reach). What
# follows is a good-faith attempt using the same Shell.Application COM
# verb-enumeration approach that still works on some older/edge-case
# Windows builds where that verb wasn't fully locked down: ask the shell
# for the shortcut's context-menu verbs and look for one that pins to
# the taskbar (name match is loose/case-insensitive since wording varies
# by locale and Windows build - "Pin to tas&kbar", "Pin to taskbar",
# etc.), invoke it if present. Deliberately NOT a registry/explorer-
# restart hack - those are fragile and can kill Explorer mid-script;
# this only ever calls a documented COM verb-invoke method, and the
# whole thing is wrapped in try/catch so a throw here can never fail
# the overall shortcut-creation script - worst case, the user is told
# to pin it by hand once.
# ---------------------------------------------------------------------
Write-Step 'Attempting to pin "Rx Verify" to the taskbar...'
$pinned = $false
try {
    $shellApp = New-Object -ComObject Shell.Application
    $shortcutFolder = $shellApp.Namespace($desktopPath)
    $shortcutItem = $shortcutFolder.ParseName((Split-Path -Path $shortcutPath -Leaf))
    if ($null -ne $shortcutItem) {
        $pinVerb = $null
        foreach ($verb in $shortcutItem.Verbs()) {
            $verbName = $verb.Name -replace '&', ''
            if ($verbName -match '(?i)taskbar') {
                $pinVerb = $verb
                break
            }
        }
        if ($null -ne $pinVerb) {
            $pinVerb.DoIt()
            $pinned = $true
        }
    }
} catch {
    $pinned = $false
}

if ($pinned) {
    Write-Step 'Pinned "Rx Verify" to the taskbar.'
} else {
    Write-Host "Windows doesn't allow apps to pin for you on this version - right-click the desktop 'Rx Verify' shortcut and choose 'Pin to taskbar' (one time)." -ForegroundColor Yellow
}

Write-Step "Done. '$shortcutPath' now updates, builds fresh, and launches Rx Verify in one double-click."
if (-not $NoPrompt) { Read-Host 'Press Enter to close this window' }
