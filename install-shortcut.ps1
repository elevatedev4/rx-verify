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

    -ExecutionPolicy Bypass on the shortcut's own invocation only
    affects that one process - it does not change your machine's
    PowerShell execution policy setting.

    PowerShell 5.1 compatible on purpose (Windows' default).
#>

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
    Read-Host 'Press Enter to close this window'
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

Write-Step "Done. '$shortcutPath' now updates, builds (only what changed), and launches Rx Verify in one double-click."
Read-Host 'Press Enter to close this window'
