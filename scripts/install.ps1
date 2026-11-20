<#
.SYNOPSIS
    Rx Verify "stage 0" installer - the one thing a fresh pharmacy PC runs now
    that the GitHub repo is private. NO secrets in this file. Canonical copy:
    scripts/install.ps1 in the rx-verify repo; Manager HQ serves an identical
    copy at https://manager-hq.vercel.app/rxverify/install.ps1.

.DESCRIPTION
    Run it as a downloaded scriptblock (paste into PowerShell):

        & ([scriptblock]::Create((irm https://manager-hq.vercel.app/rxverify/install.ps1))) -ReportKey '<REPORTKEY>'

    Single-quote the key ($ would expand inside double quotes). It then:
      1. installs Git via winget if missing,
      2. fetches the read-only SSH deploy key from HQ with the report key
         (GET /api/rxverify-update-key, Bearer auth) and saves it, current
         user only, in %LocalAppData%\RxVerifyOverlay\ next to GitHub's
         pinned host keys (known_hosts),
      3. saves the report key into %AppData%\RxVerifyOverlay\settings.json,
      4. clones the repo over SSH (port 443) into %USERPROFILE%\claude\rx-verify
         (skipped if a checkout is already there),
      5. runs the clone's own bootstrap-fresh.ps1 (Node/.NET/shortcut/build).
    Kept thin on purpose - the rest of the SSH logic lives in the clone's
    scripts\GitSsh.ps1. Windows PowerShell 5.1 compatible. Uses throw (never
    exit) so a failure cannot close the user's console.
#>
param([Parameter(Mandatory = $true)][string]$ReportKey)

function Invoke-RxVerifyStage0 {
    param([string]$ReportKey)
    $ErrorActionPreference = 'Stop'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $env:GIT_TERMINAL_PROMPT = '0'
    $env:GCM_INTERACTIVE = 'never'
    $repoPath = Join-Path $env:USERPROFILE 'claude\rx-verify'
    $keyDir = Join-Path $env:LOCALAPPDATA 'RxVerifyOverlay'
    $keyPath = Join-Path $keyDir 'deploy_key'
    $hostsPath = Join-Path $keyDir 'known_hosts'
    $enc = New-Object System.Text.UTF8Encoding($false)

    Write-Host 'Checking for Git...' -ForegroundColor Cyan
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
            throw 'winget not found. Install "App Installer" from the Microsoft Store, reopen PowerShell, and run the install command again.'
        }
        Write-Host 'Installing Git via winget (click Yes on any Windows prompt)...' -ForegroundColor Yellow
        winget install -e --id Git.Git --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -ne 0) { throw "winget install Git.Git failed (exit code $LASTEXITCODE)." }
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
        if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
            throw 'Git installed, but this window cannot see it yet. Close PowerShell, open a new window, and run the install command again.'
        }
    }

    Write-Host 'Fetching the read-only update key from HQ...' -ForegroundColor Cyan
    try {
        $resp = Invoke-RestMethod -Uri 'https://manager-hq.vercel.app/api/rxverify-update-key' -Method Get -Headers @{ Authorization = "Bearer $ReportKey" } -TimeoutSec 30
    } catch {
        throw "Could not get the update key from HQ ($($_.Exception.Message)). Check the report key and the network connection, then run the install command again."
    }
    $keyText = [string]$resp.key
    if (($keyText -notmatch '-----BEGIN OPENSSH PRIVATE KEY-----') -or ($keyText -notmatch '-----END OPENSSH PRIVATE KEY-----')) {
        throw 'HQ answered, but not with a usable update key. Tell Will/dev.'
    }
    if (-not (Test-Path $keyDir)) { New-Item -ItemType Directory -Path $keyDir -Force | Out-Null }
    if (-not (Test-Path $keyPath)) { [System.IO.File]::WriteAllText($keyPath, '', $enc) }
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $null = & icacls.exe $keyPath /inheritance:r /grant:r "${me}:F" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict $keyPath to the current user (icacls exit code $LASTEXITCODE)." }
    [System.IO.File]::WriteAllText($keyPath, (($keyText -replace "`r`n", "`n").Trim() + "`n"), $enc)
    $knownHosts = @(
        '[ssh.github.com]:443 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl',
        '[ssh.github.com]:443 ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBEmKSENjQEezOmxkZMy7opKgwFB9nkt5YRrYMjNuG5N87uRgg6CLrbo5wAdT/y6v0mKV0U2w0WZ2YB/++Tpockg=',
        '[ssh.github.com]:443 ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQCj7ndNxQowgcQnjshcLrqPEiiphnt+VTTvDP6mHBL9j1aNUkY4Ue1gvwnGLVlOhGeYrnZaMgRK6+PKCUXaDbC7qtbW8gIkhL7aGCsOr/C56SJMy/BCZfxd1nWzAOxSDPgVsmerOBYfNqltV9/hWCqBywINIR+5dIg6JTJ72pcEpEjcYgXkE2YEFXV1JHnsKgbLWNlhScqb2UmyRkQyytRLtL+38TGxkxCflmO+5Z8CSSNY7GidjMIZ7Q4zMjA2n1nGrlTDkzwDCsw+wqFPGQA179cnfGWOWRVruj16z6XyvxvjJwbz0wQZ75XK5tKSb7FNyeIEs4TT4jk+S4dhPeAUC5y+bDYirYgM4GC7uEnztnZyaVWQ7B381AK4Qdrwt51ZqExKbQpTUNn+EjqoTwvqNj4kqx5QUCI0ThS/YkOxJCXmPUWZbhjpCg56i+2aB6CmK2JGhn57K5mj0MNdBXA4/WnwH6XoPWJzK5Nyu2zB3nAZp+S5hpQs+p1vN1/wsjk='
    )
    [System.IO.File]::WriteAllText($hostsPath, (($knownHosts -join "`n") + "`n"), $enc)

    Write-Host 'Saving the report key (settings.json)...' -ForegroundColor Cyan
    $settingsDir = Join-Path $env:APPDATA 'RxVerifyOverlay'
    $settingsPath = Join-Path $settingsDir 'settings.json'
    if (-not (Test-Path $settingsDir)) { New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null }
    $settings = $null
    if (Test-Path $settingsPath) {
        try { $settings = (Get-Content -Path $settingsPath -Raw) | ConvertFrom-Json } catch { $settings = $null }
        if ($settings -isnot [System.Management.Automation.PSCustomObject]) {
            Copy-Item -Path $settingsPath -Destination ($settingsPath + '.corrupt-' + (Get-Date -Format 'yyyyMMddHHmmss') + '.bak') -Force
            $settings = $null
        }
    }
    if ($null -eq $settings) { $settings = [PSCustomObject]@{} }
    $settings | Add-Member -NotePropertyName 'RxVerifyReportKey' -NotePropertyValue $ReportKey -Force
    ($settings | ConvertTo-Json -Depth 10) | Set-Content -Path $settingsPath -Encoding UTF8

    Write-Host "Checking for the repo at $repoPath..." -ForegroundColor Cyan
    if (Test-Path (Join-Path $repoPath '.git')) {
        Write-Host '  Repo already cloned - skipping the clone.' -ForegroundColor DarkGray
    } else {
        if ((Test-Path $repoPath) -and ((Get-ChildItem -Path $repoPath -Force | Measure-Object).Count -gt 0)) {
            throw "$repoPath exists but is not an rx-verify checkout. Rename or remove that folder, then run the install command again."
        }
        New-Item -ItemType Directory -Path (Split-Path -Path $repoPath -Parent) -Force | Out-Null
        $k = $keyPath -replace '\\', '/'
        $h = $hostsPath -replace '\\', '/'
        $env:GIT_SSH_COMMAND = ('ssh -i "' + $k + '" -o IdentitiesOnly=yes -o BatchMode=yes -o ConnectTimeout=20 -o "UserKnownHostsFile=\"' + $h + '\"" -o StrictHostKeyChecking=yes -p 443')
        try {
            git clone 'ssh://git@ssh.github.com:443/elevatedev4/rx-verify.git' $repoPath
            if ($LASTEXITCODE -ne 0) { throw "git clone over SSH failed (exit code $LASTEXITCODE). See the git output above." }
        } finally {
            Remove-Item Env:\GIT_SSH_COMMAND -ErrorAction SilentlyContinue
        }
    }

    . (Join-Path $repoPath 'scripts\GitSsh.ps1')
    if (-not (Set-RxVerifyRepoSshConfig -RepoPath $repoPath)) {
        Write-Host 'Warning: could not set the repo-local SSH command; update-and-run.ps1 will repair it on first launch.' -ForegroundColor Yellow
    }

    Write-Host 'Handing off to bootstrap-fresh.ps1 (Node, .NET, shortcut, build, launch)...' -ForegroundColor Cyan
    powershell -ExecutionPolicy Bypass -File (Join-Path $repoPath 'bootstrap-fresh.ps1') -ReportKey "$ReportKey"
}

try {
    Invoke-RxVerifyStage0 -ReportKey $ReportKey
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host 'Copy the text above and send it to Will/dev. Nothing destructive happened - run the same install command again once it is fixed.' -ForegroundColor Red
}
