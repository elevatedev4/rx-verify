<#
.SYNOPSIS
    Fresh-Windows-PC bootstrap for Rx Verify: installs Git, Node.js, and the
    .NET 8 SDK via winget if missing, clones the repo, then hands off to
    update-and-run.ps1.

.DESCRIPTION
    Meant to be run from an interactive PowerShell console by pasting the
    one-liner from README.md, which pipes this script straight into
    Invoke-Expression:

        [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; irm https://raw.githubusercontent.com/elevatedev4/rx-verify/main/bootstrap-fresh.ps1 | iex

    Because it runs via `iex` inside Will's own already-open PowerShell
    console - not launched as its own process the way update-and-run.ps1
    and install-shortcut.ps1 are (via `powershell -File ...`) - two things
    that are safe in those scripts are NOT safe here:

      - $PSScriptRoot is empty/unavailable under `iex` (there is no script
        file on disk to locate). Every path below is built explicitly from
        $env:USERPROFILE instead.
      - `exit` would close Will's actual PowerShell window, not just this
        script, since `iex` runs in his current session rather than a
        child process. Every failure path below uses `throw` instead,
        caught by the try/catch at the bottom, so a failure prints a clear
        message and hands control back to Will's prompt - the window stays
        open either way.

    Steps:

      1. Checks for winget (Get-Command winget). If missing, tells Will to
         install "App Installer" from the Microsoft Store and re-run.
      2. Installs Git, Node.js LTS (needs major version >= 20 - see
         package.json "engines"), and the .NET 8 SDK via winget, each only
         if it isn't already present/new enough - safe to re-run any time.
         Because each is skipped when already present, any non-zero exit
         code winget itself returns is a real failure, not an
         "already installed" false alarm - so every non-zero winget exit
         code stops the script.
      3. If anything was installed, refreshes PATH in this console session
         from the Machine + User environment so newly-installed tools
         resolve without reopening PowerShell, then re-verifies. If a tool
         still doesn't resolve (some installers need a genuinely fresh
         console despite the refresh), tells Will to close and reopen
         PowerShell and paste the same one-liner again - installs already
         done are kept, so it picks up where it left off.
      4. Clones the repo to the canonical path,
         $env:USERPROFILE\claude\rx-verify, if it isn't already there.
         "Already there" is judged by update-and-run.ps1 existing inside
         it, not just the folder existing - a folder with no
         update-and-run.ps1 means a previous clone was interrupted, and
         the script throws with the exact command to remove that broken
         copy rather than silently retrying over it or deleting it
         itself.
      5. Creates/refreshes the "Rx Verify" Desktop shortcut by invoking
         install-shortcut.ps1 from inside the freshly-cloned repo, so a
         fresh PC ends fully set up - not just installed-and-run-once,
         but ready for every future launch to be a single double-click.
         Non-fatal if this step fails (eg. some COM oddity creating the
         .lnk): a warning is printed and the script continues, since the
         app can still be run directly via update-and-run.ps1 below, and
         install-shortcut.ps1 can always be re-run by hand later.
      6. Hands off to update-and-run.ps1 (pull + build + launch) via
         `powershell -ExecutionPolicy Bypass -File ...`, the same command
         the "every run after" workflow in README.md uses. Since fresh PCs
         default to a Restricted execution policy, -ExecutionPolicy Bypass
         is required for that handoff to run at all. update-and-run.ps1
         now also verifies Git/Node/dotnet are present on its own before
         doing anything else, so this handoff is safe even if step 1-4's
         install/PATH-refresh dance above somehow left something not
         resolving yet in this console.

    Windows PowerShell 5.1 compatible on purpose (Windows' default) - no
    PS7-only syntax (ternary, ??, &&/||, Join-Path -AdditionalChildPath,
    etc.).

.NOTES
    SYNTHETIC DATA ONLY applies to this repo as a whole (see README.md) -
    this script itself never touches patient/prescriber data, only
    installs tooling and clones/builds source code.
#>

function Write-Step {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Cyan
}

function Write-Detail {
    param([string]$Message)
    Write-Host "  $Message" -ForegroundColor DarkGray
}

function Invoke-RxVerifyBootstrap {
    # Scoped to THIS function only (PowerShell preference variables are
    # function-scoped) so it never bleeds into Will's interactive session
    # after this script finishes running via `iex`.
    $ErrorActionPreference = 'Stop'

    $RepoUrl = 'https://github.com/elevatedev4/rx-verify.git'
    $ClaudeDir = Join-Path $env:USERPROFILE 'claude'
    $RepoPath = Join-Path $ClaudeDir 'rx-verify'
    $LauncherScriptPath = Join-Path $RepoPath 'update-and-run.ps1'
    $InstallShortcutScriptPath = Join-Path $RepoPath 'install-shortcut.ps1'

    # -------------------------------------------------------------
    # Step 0: winget itself must exist - everything below depends on it.
    # -------------------------------------------------------------
    Write-Step 'Checking for winget (Windows Package Manager)...'
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw 'winget not found. Open the Microsoft Store, install "App Installer", reopen PowerShell, then paste the bootstrap command again.'
    }
    Write-Detail 'winget found.'

    Write-Host 'Windows may show a few Yes/No install prompts below - click Yes. It may also show a UAC "Do you want to allow this app to make changes to your device?" dialog - click Yes on that too.' -ForegroundColor Yellow

    $anyInstalled = $false

    # -------------------------------------------------------------
    # Step 1: Git
    # -------------------------------------------------------------
    Write-Step 'Checking for Git...'
    if (Get-Command git -ErrorAction SilentlyContinue) {
        Write-Detail 'Git already installed - skipping.'
    } else {
        Write-Detail 'Git not found - installing via winget (Git.Git)...'
        winget install -e --id Git.Git --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -ne 0) {
            throw "winget install -e --id Git.Git failed (exit code $LASTEXITCODE). See the winget output above for details."
        }
        $anyInstalled = $true
    }

    # -------------------------------------------------------------
    # Step 2: Node.js - needs major version >= 20 (see package.json
    # "engines"). Installed-but-too-old counts the same as missing.
    # -------------------------------------------------------------
    Write-Step 'Checking for Node.js (need major version 20+)...'
    $nodeOk = $false
    $nodeVersionRaw = $null
    if (Get-Command node -ErrorAction SilentlyContinue) {
        $nodeVersionRaw = (node --version)
        $nodeMajor = $null
        if ($nodeVersionRaw -match 'v(\d+)\.') {
            $nodeMajor = [int]$Matches[1]
        }
        if (($nodeMajor -ne $null) -and ($nodeMajor -ge 20)) {
            $nodeOk = $true
        }
    }
    if ($nodeOk) {
        Write-Detail "Node.js $nodeVersionRaw already installed - skipping."
    } else {
        Write-Detail 'Node.js missing or older than 20 - installing via winget (OpenJS.NodeJS.LTS)...'
        winget install -e --id OpenJS.NodeJS.LTS --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -ne 0) {
            throw "winget install -e --id OpenJS.NodeJS.LTS failed (exit code $LASTEXITCODE). See the winget output above for details."
        }
        $anyInstalled = $true
    }

    # -------------------------------------------------------------
    # Step 3: .NET 8 SDK
    # -------------------------------------------------------------
    Write-Step 'Checking for .NET 8 SDK...'
    $dotnetOk = $false
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $installedSdks = dotnet --list-sdks
        foreach ($line in $installedSdks) {
            if ($line -like '8.*') {
                $dotnetOk = $true
                break
            }
        }
    }
    if ($dotnetOk) {
        Write-Detail '.NET 8 SDK already installed - skipping.'
    } else {
        Write-Detail '.NET 8 SDK missing - installing via winget (Microsoft.DotNet.SDK.8)...'
        winget install -e --id Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -ne 0) {
            throw "winget install -e --id Microsoft.DotNet.SDK.8 failed (exit code $LASTEXITCODE). See the winget output above for details."
        }
        $anyInstalled = $true
    }

    # -------------------------------------------------------------
    # Step 4: if anything was installed, refresh PATH in this console
    # session, then re-verify everything resolves before moving on.
    # -------------------------------------------------------------
    if ($anyInstalled) {
        Write-Step 'Refreshing PATH in this session...'
        $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if ($null -eq $machinePath) { $machinePath = '' }
        if ($null -eq $userPath) { $userPath = '' }
        $env:Path = $machinePath + ';' + $userPath

        $stillMissing = @()
        if (-not (Get-Command git -ErrorAction SilentlyContinue)) { $stillMissing += 'git' }
        if (-not (Get-Command node -ErrorAction SilentlyContinue)) { $stillMissing += 'node' }
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $stillMissing += 'dotnet' }

        if ($stillMissing.Count -gt 0) {
            $missingList = $stillMissing -join ', '
            throw "Installed OK, but this PowerShell window still can't see: $missingList. Close this PowerShell window, reopen a new one, and paste the same bootstrap command again - the installs are kept, so it will pick up where it left off."
        }
        Write-Detail 'git, node, and dotnet all resolve in this session.'
    }

    # -------------------------------------------------------------
    # Step 5: clone if missing. "Already cloned" is judged by
    # $LauncherScriptPath existing (a file only present after a
    # completed checkout), NOT by the .git folder existing - git
    # creates .git almost immediately, before the object transfer or
    # checkout finishes, so a clone interrupted mid-transfer still
    # leaves a .git folder behind. Trusting .git alone would make this
    # script report "already cloned" forever on a broken copy and
    # never recover. If $RepoPath exists but isn't a completed clone,
    # that's a distinct, explicit failure below - this script still
    # never deletes anything on its own (same covenant as
    # update-and-run.ps1), so it tells Will exactly what to remove and
    # how, rather than guessing.
    # -------------------------------------------------------------
    Write-Step "Checking for the repo at $RepoPath..."
    if (Test-Path $LauncherScriptPath) {
        Write-Detail 'Repo already cloned - skipping.'
    } elseif (Test-Path $RepoPath) {
        throw "A partial or broken copy of the repo already exists at $RepoPath - there's no update-and-run.ps1 inside it, which means a previous clone got interrupted before it finished. It holds no local work of yours, just an incomplete download, so it's safe to remove. Run this, then paste the bootstrap one-liner again:`n`n  Remove-Item -Recurse -Force `"$RepoPath`"`n"
    } else {
        if (-not (Test-Path $ClaudeDir)) {
            New-Item -ItemType Directory -Path $ClaudeDir -Force | Out-Null
        }
        Write-Detail "Cloning $RepoUrl to $RepoPath..."
        git clone $RepoUrl $RepoPath
        if ($LASTEXITCODE -ne 0) {
            throw "git clone $RepoUrl $RepoPath failed (exit code $LASTEXITCODE). See the git output above for details."
        }
    }

    if (-not (Test-Path $LauncherScriptPath)) {
        throw "$LauncherScriptPath still doesn't exist after cloning - something is wrong with the repo checkout."
    }

    # -------------------------------------------------------------
    # Step 6: create/refresh the Desktop shortcut so a fresh PC ends
    # fully set up. install-shortcut.ps1 is idempotent (overwrites any
    # existing shortcut, never duplicates) and its own repo-clone check
    # is a no-op here since the repo was just ensured above - safe to
    # run on every bootstrap, including re-runs on an already-set-up
    # PC. -NoPrompt suppresses its interactive "Press Enter to close"
    # pause, which only makes sense when it's run standalone/by hand.
    # Deliberately non-fatal: a failure here shouldn't block getting the
    # app running for the first time (step 7 below still runs either
    # way), and this can always be re-run later (see README.md).
    # -------------------------------------------------------------
    Write-Step 'Creating/refreshing the Desktop shortcut...'
    powershell -ExecutionPolicy Bypass -File "$InstallShortcutScriptPath" -NoPrompt
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Could not create the Desktop shortcut (exit code $LASTEXITCODE) - continuing anyway. Re-run install-shortcut.ps1 later to try again (see README.md)." -ForegroundColor Yellow
    } else {
        Write-Detail 'Desktop shortcut ready.'
    }

    # -------------------------------------------------------------
    # Step 7: hand off to update-and-run.ps1 (pull + build + launch). It
    # handles its own errors (holds the window open on failure), so this
    # is the last thing this script does either way.
    # -------------------------------------------------------------
    Write-Step 'Handing off to update-and-run.ps1 (pull + build + launch)...'
    powershell -ExecutionPolicy Bypass -File "$LauncherScriptPath"
}

try {
    Invoke-RxVerifyBootstrap
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host 'Copy the text above (including any error output) and send it to Will/dev. Nothing destructive has happened - any tools already installed are kept, so pasting the same bootstrap command again later will pick up where it left off. If the error above was about a partial repo copy, run the exact command it gave you first, then paste the bootstrap command again.' -ForegroundColor Red
}
